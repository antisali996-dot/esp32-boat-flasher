using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace BoatTool
{
    // 从固件身份串（FWID）解析出的可控参数，仅用于显示与跨套装比较。
    // FWID 是固件自己上报的字符串，解析失败只影响显示，不用于放行写入。
    public class FwidInfo
    {
        public string Raw = "";
        public BoardRole Role = BoardRole.Unknown;
        public int SteerDeg = -1;
        public int AngleMin = -1;
        public int AngleMax = -1;
        public int TargetRpm = -1;
        public int OverspeedRpm = -1;
        public bool Parsed;

        private static readonly Regex RolePattern = new Regex("^JAYFW([BR])", RegexOptions.Compiled);
        private static readonly Regex SteerPattern = new Regex("S(\\d+)-A(\\d+)-(\\d+)", RegexOptions.Compiled);
        // 只在转向段（-U…）之后找转速，避免把遥控器前缀 “JAYFWR4” 里的 R4 当成 4 转。
        private static readonly Regex RpmPattern = new Regex("-R(\\d+)-", RegexOptions.Compiled);
        private static readonly Regex OverspeedPattern = new Regex("-OS(\\d+)-", RegexOptions.Compiled);

        public static FwidInfo Parse(string fwid)
        {
            FwidInfo info = new FwidInfo();
            info.Raw = fwid ?? "";
            if (string.IsNullOrEmpty(fwid)) return info;
            Match role = RolePattern.Match(fwid);
            if (role.Success) info.Role = role.Groups[1].Value == "B" ? BoardRole.Boat : BoardRole.Remote;
            Match steer = SteerPattern.Match(fwid);
            if (steer.Success)
            {
                info.SteerDeg = ParseInt(steer.Groups[1].Value);
                info.AngleMin = ParseInt(steer.Groups[2].Value);
                info.AngleMax = ParseInt(steer.Groups[3].Value);
            }

            // 遥控器 FWID 在角度/脉宽之后直接是 -D<dead>；小船后面才是 -R<rpm>。
            // 小船没有 -D，用整串匹配（-R580- 这样的模式不会误伤 JAYFWB3 前缀）。
            int deadIndex = fwid.IndexOf("-D", StringComparison.Ordinal);
            string tail = deadIndex >= 0 ? fwid.Substring(deadIndex) : fwid;
            Match rpm = RpmPattern.Match(tail);
            if (rpm.Success)
            {
                int value = ParseInt(rpm.Groups[1].Value);
                // RPM 至少是三位数；更小的值一律视为解析噪声。
                if (value >= 100) info.TargetRpm = value;
            }
            Match os = OverspeedPattern.Match(tail);
            if (os.Success) info.OverspeedRpm = ParseInt(os.Groups[1].Value);

            info.Parsed = info.Role != BoardRole.Unknown && info.SteerDeg > 0 && info.AngleMin > 0 && info.AngleMax > 0;
            return info;
        }

        private static int ParseInt(string text)
        {
            int value;
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) ? value : -1;
        }

        // 转向契约：左右角度与范围都相同才视为同一个转向契约。
        public static bool SameSteeringContract(FwidInfo a, FwidInfo b)
        {
            if (a == null || b == null || !a.Parsed || !b.Parsed) return false;
            return a.SteerDeg == b.SteerDeg && a.AngleMin == b.AngleMin && a.AngleMax == b.AngleMax;
        }
    }

    // ESP32 分区表解析：只用于判断“保留 NVS”这条承诺是否成立。
    public class PartitionEntry
    {
        public string Label = "";
        public int Type;
        public int Subtype;
        public long Offset = -1;
        public long Size = -1;

        public long End { get { return Offset < 0 || Size <= 0 ? -1 : Offset + Size; } }

        public bool Overlaps(long start, long end)
        {
            if (Offset < 0 || Size <= 0) return true; // 解析不出范围时按“可能重叠”处理
            if (end <= start) return false;
            return Offset < end && start < End;
        }

        public string Describe()
        {
            if (Offset < 0) return "无法解析";
            return Label + " 0x" + Offset.ToString("X", CultureInfo.InvariantCulture) +
                   " +0x" + Size.ToString("X", CultureInfo.InvariantCulture);
        }
    }

    public class PartitionLayout
    {
        public bool Parsed;
        public string Error = "";
        public List<PartitionEntry> Entries = new List<PartitionEntry>();
        public PartitionEntry Nvs;

        // 本工具实际写入的四段（与清单里的地址对应）。
        public static PartitionLayout Analyze(string partitionsPath, long appOffset, long appImageBytes, FirmwareSpec actualSpec = null)
        {
            PartitionLayout layout = new PartitionLayout();
            if (!File.Exists(partitionsPath))
            {
                layout.Error = "缺少分区表镜像：" + Path.GetFileName(partitionsPath);
                return layout;
            }
            byte[] data;
            try { data = File.ReadAllBytes(partitionsPath); }
            catch (Exception ex) { layout.Error = "分区表读取失败：" + ex.Message; return layout; }

            const int entrySize = 32;
            for (int pos = 0; pos + entrySize <= data.Length; pos += entrySize)
            {
                // ESP-IDF 分区表是小端：小写字节 0xAA 0x50 读成 16 位整数是 0x50AA。
                int magic = data[pos] | (data[pos + 1] << 8);
                if (magic != 0x50AA) break;
                PartitionEntry entry = new PartitionEntry();
                entry.Type = data[pos + 2];
                entry.Subtype = data[pos + 3];
                entry.Offset = BitConverter.ToUInt32(data, pos + 4);
                entry.Size = BitConverter.ToUInt32(data, pos + 8);
                int labelLen = 0;
                while (labelLen < 16 && data[pos + 12 + labelLen] != 0) labelLen++;
                entry.Label = System.Text.Encoding.ASCII.GetString(data, pos + 12, labelLen);
                layout.Entries.Add(entry);
            }

            if (layout.Entries.Count == 0)
            {
                layout.Error = "分区表镜像里没有可识别的分区条目（不是有效的 ESP32 分区表）";
                return layout;
            }
            foreach (PartitionEntry entry in layout.Entries)
            {
                if (entry.Type == 1 && entry.Subtype == 2) { layout.Nvs = entry; break; }
            }
            layout.Parsed = true;

            if (layout.Nvs == null)
            {
                layout.Error = "分区表里没有 NVS 分区，无法保证保留配对地址";
                return layout;
            }
            if (layout.Nvs.Size <= 0)
            {
                layout.Error = "NVS 分区大小无效，无法保证保留配对地址";
                return layout;
            }

            // 1) 按实际清单地址、文件大小及4KB擦除扇区计算；不能只检查固定的启动范围。
            long appStart = appOffset, appEnd = appOffset + appImageBytes;
            if (actualSpec != null)
            {
                List<KeyValuePair<long, long>> ranges = new List<KeyValuePair<long, long>>();
                foreach (KeyValuePair<long, string> image in actualSpec.Images())
                {
                    if (image.Key < 0 || !File.Exists(image.Value))
                    { layout.Error = "实际写入镜像地址或文件无效"; return layout; }
                    long length = new FileInfo(image.Value).Length;
                    if (length <= 0 || image.Key > long.MaxValue - length - 4095)
                    { layout.Error = "实际写入镜像大小或范围无效"; return layout; }
                    long start = image.Key / 4096 * 4096;
                    long end = (image.Key + length + 4095) / 4096 * 4096;
                    foreach (PartitionEntry entry in layout.Entries)
                    {
                        if (entry.Type == 1 && entry.Subtype == 2 && entry.Overlaps(start, end))
                        { layout.Error = "实际写入或擦除范围与 NVS 配对区重叠"; return layout; }
                    }
                    foreach (KeyValuePair<long, long> prior in ranges)
                    {
                        if (start < prior.Value && prior.Key < end)
                        { layout.Error = "实际写入镜像的擦除扇区互相重叠"; return layout; }
                    }
                    ranges.Add(new KeyValuePair<long, long>(start, end));
                }
            }
            else
            {
                // 旧直接调用的兼容路径；发布套装必须传入 actualSpec。
                if (layout.Nvs.Overlaps(0x1000, 0x9000) || layout.Nvs.Overlaps(0xE000, 0x10000) ||
                    layout.Nvs.Overlaps(appStart, appEnd))
                    layout.Error = "NVS 与默认写入范围重叠";
            }

            // 2) 应用镜像必须落在某个 app 分区内。
            bool covered = false;
            foreach (PartitionEntry entry in layout.Entries)
            {
                if (entry.Type != 0) continue;
                if (entry.Offset <= appStart && appEnd <= entry.End) { covered = true; break; }
            }
            if (!covered)
                layout.Error = "应用程序镜像（0x" + appStart.ToString("X", CultureInfo.InvariantCulture) +
                               " 起 " + appImageBytes + " 字节）超出分区表里任何 app 分区，写入会越界";
            return layout;
        }

        public string DescribeNvs()
        {
            if (Nvs == null) return "未知";
            return "NVS " + Nvs.Describe() + "（本工具不写入此区）";
        }
    }
}
