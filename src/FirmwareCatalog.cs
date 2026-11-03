using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace BoatTool
{
    public class FirmwareSpec
    {
        public BoardRole Role = BoardRole.Unknown;
        public string Fwid = "";
        public string Version = "";
        public int SteerDeg = -1;
        public string AppPath = "";
        public long AppOffset = -1;
        public string BootloaderPath = "";
        public long BootloaderOffset = -1;
        public string PartitionsPath = "";
        public long PartitionsOffset = -1;
        public string BootApp0Path = "";
        public long BootApp0Offset = -1;

        public IEnumerable<KeyValuePair<long, string>> Images()
        {
            yield return new KeyValuePair<long, string>(BootloaderOffset, BootloaderPath);
            yield return new KeyValuePair<long, string>(PartitionsOffset, PartitionsPath);
            yield return new KeyValuePair<long, string>(BootApp0Offset, BootApp0Path);
            yield return new KeyValuePair<long, string>(AppOffset, AppPath);
        }

        // 分段写入并保留 NVS（0x9000 起）——升级不会静默抹掉已登记配对；
        // 与整片 merged.bin 相比也避免每次重写 4MB。
        public string DescribeImages()
        {
            return "bootloader@0x" + BootloaderOffset.ToString("X", CultureInfo.InvariantCulture) +
                   " / 分区表@0x" + PartitionsOffset.ToString("X", CultureInfo.InvariantCulture) +
                   " / OTA选择@0x" + BootApp0Offset.ToString("X", CultureInfo.InvariantCulture) +
                   " / 应用@0x" + AppOffset.ToString("X", CultureInfo.InvariantCulture) +
                   "（保留 NVS 配对区）";
        }
    }

    public class FirmwareCatalog
    {
        public int Schema;
        public string Version = "";
        public int SteerDeg = -1;
        public int AngleMin = -1;
        public int AngleMax = -1;
        public int CenterUs = -1;
        public int PulseMinUs = -1;
        public int PulseMaxUs = -1;
        public int PairProtocol = -1;
        public int TargetRpm = -1;
        public int OverspeedRpm = -1;
        public Dictionary<BoardRole, FirmwareSpec> Roles = new Dictionary<BoardRole, FirmwareSpec>();
        public List<string> Problems = new List<string>();

        public FirmwareSpec ForRole(BoardRole role)
        {
            FirmwareSpec spec;
            return Roles.TryGetValue(role, out spec) ? spec : null;
        }

        public static FirmwareCatalog Load(string path)
        {
            FirmwareCatalog catalog = new FirmwareCatalog();
            if (!File.Exists(path))
            {
                catalog.Problems.Add("缺少固件清单：固件\\固件清单.json");
                return catalog;
            }
            try
            {
                Dictionary<string, object> root = Json.AsObject(Json.Parse(File.ReadAllText(path)));
                string dir = Path.GetDirectoryName(path);
                catalog.Schema = Json.GetInt(root, "schema", 0);
                catalog.Version = Json.GetString(root, "version", "");
                catalog.SteerDeg = Json.GetInt(root, "steer_deg", -1);
                catalog.AngleMin = Json.GetInt(root, "angle_min", -1);
                catalog.AngleMax = Json.GetInt(root, "angle_max", -1);
                catalog.CenterUs = Json.GetInt(root, "center_us", -1);
                catalog.PulseMinUs = Json.GetInt(root, "pulse_min_us", -1);
                catalog.PulseMaxUs = Json.GetInt(root, "pulse_max_us", -1);
                catalog.PairProtocol = Json.GetInt(root, "pair_protocol", -1);
                catalog.TargetRpm = Json.GetInt(root, "target_rpm", -1);
                catalog.OverspeedRpm = Json.GetInt(root, "overspeed_rpm", -1);

                object rolesValue;
                if (root.TryGetValue("roles", out rolesValue))
                {
                    Dictionary<string, object> roles = Json.AsObject(rolesValue);
                    foreach (KeyValuePair<string, object> pair in roles)
                    {
                        BoardRole role = RoleNames.FromCode(pair.Key.ToUpperInvariant());
                        if (role == BoardRole.Unknown) { catalog.Problems.Add("固件清单含未知角色：" + pair.Key); continue; }
                        Dictionary<string, object> obj = Json.AsObject(pair.Value);
                        FirmwareSpec spec = new FirmwareSpec();
                        spec.Role = role;
                        spec.Fwid = Json.GetString(obj, "fwid", "");
                        spec.Version = Json.GetString(obj, "version", catalog.Version);
                        spec.SteerDeg = Json.GetInt(obj, "steer_deg", catalog.SteerDeg);
                        spec.AppPath = Path.Combine(dir, Json.GetString(obj, "app", ""));
                        spec.AppOffset = Json.GetInt(obj, "app_offset", -1);
                        spec.BootloaderPath = Path.Combine(dir, Json.GetString(obj, "bootloader", ""));
                        spec.BootloaderOffset = Json.GetInt(obj, "bootloader_offset", -1);
                        spec.PartitionsPath = Path.Combine(dir, Json.GetString(obj, "partitions", ""));
                        spec.PartitionsOffset = Json.GetInt(obj, "partitions_offset", -1);
                        spec.BootApp0Path = Path.Combine(dir, Json.GetString(obj, "boot_app0", ""));
                        spec.BootApp0Offset = Json.GetInt(obj, "boot_app0_offset", -1);
                        catalog.Roles[role] = spec;
                    }
                }
            }
            catch (Exception ex)
            {
                catalog.Problems.Add("固件清单无法解析：" + ex.Message);
            }
            catalog.SelfCheck();
            return catalog;
        }

        // 清单、镜像与身份串必须互相自证，避免"说明写 20 度、固件其实是旧程序"。
        private void SelfCheck()
        {
            if (Schema < 3) Problems.Add("固件清单版本过旧（schema=" + Schema + "），请使用本包配套文件");
            if (SteerDeg >= 0 && AngleMin >= 0 && AngleMax >= 0 && (AngleMax - AngleMin) != 2 * SteerDeg)
                Problems.Add("固件清单角度范围与转向角不自洽：" + AngleMin + "～" + AngleMax);
            if (TargetRpm <= 0) Problems.Add("固件清单没有声明目标转速 target_rpm");
            foreach (KeyValuePair<BoardRole, FirmwareSpec> pair in Roles)
            {
                FirmwareSpec spec = pair.Value;
                string label = RoleNames.Chinese(spec.Role);
                if (string.IsNullOrEmpty(spec.Fwid)) { Problems.Add(label + "固件缺少 FWID"); continue; }
                // 身份串必须自称本清单声明的角度契约，否则不允许交给用户。
                string expected = "S" + spec.SteerDeg + "-A" + AngleMin + "-" + AngleMax;
                if (AngleMin >= 0 && spec.SteerDeg > 0 && !spec.Fwid.Contains(expected))
                    Problems.Add(label + "固件身份不含清单声明的角度契约（" + expected + "）：" + spec.Fwid);
                if (spec.SteerDeg <= 0) Problems.Add(label + "固件角色缺少转向角声明");
                foreach (KeyValuePair<long, string> image in spec.Images())
                {
                    if (image.Key < 0) { Problems.Add(label + "固件镜像缺少写入地址"); continue; }
                    if (!File.Exists(image.Value)) { Problems.Add(label + "缺少镜像：" + Path.GetFileName(image.Value)); continue; }
                    if (new FileInfo(image.Value).Length == 0) Problems.Add(label + "镜像为空：" + Path.GetFileName(image.Value));
                }
                if (File.Exists(spec.AppPath) && !Hashing.FileContainsAscii(spec.AppPath, spec.Fwid))
                    Problems.Add(label + "应用镜像内不存在清单声明的 FWID，镜像与清单不一致");
            }
            if (!Roles.ContainsKey(BoardRole.Boat)) Problems.Add("固件清单缺少小船固件");
            if (!Roles.ContainsKey(BoardRole.Remote)) Problems.Add("固件清单缺少遥控器固件");

            // 任何一套套装都不能让小船和遥控器共用同一个镜像文件——那是镜像混用的典型症状。
            FirmwareSpec boat = ForRole(BoardRole.Boat);
            FirmwareSpec remote = ForRole(BoardRole.Remote);
            if (boat != null && remote != null)
            {
                if (string.Equals(boat.AppPath, remote.AppPath, StringComparison.OrdinalIgnoreCase))
                    Problems.Add("小船与遥控器指向同一个应用镜像，镜像混用");
                if (string.Equals(boat.BootloaderPath, remote.BootloaderPath, StringComparison.OrdinalIgnoreCase) == false)
                {
                    // 两端 bootloader 可以不同（分别编译），这里只做记录，不视为错误。
                }
            }
        }

        public string Summary
        {
            get
            {
                string speed = TargetRpm > 0 ? "目标 " + TargetRpm + " 转" : "转速未知";
                string overspeed = OverspeedRpm > 0 ? "，超速保护 " + OverspeedRpm + " 转" : "";
                return "固件包 " + Version + "：中位 " + CenterUs + "us（左右各 " + SteerDeg + "°：" +
                       AngleMin + "～" + AngleMax + "），脉宽 " + PulseMinUs + "～" + PulseMaxUs +
                       "us，" + speed + overspeed + "，配对协议 v" + PairProtocol;
            }
        }
    }
}
