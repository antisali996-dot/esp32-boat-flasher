using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace BoatTool
{
    public enum BoardRole
    {
        Unknown = 0,
        Boat = 1,
        Remote = 2
    }

    public static class RoleNames
    {
        public static string Chinese(BoardRole role)
        {
            switch (role)
            {
                case BoardRole.Boat: return "小船";
                case BoardRole.Remote: return "遥控器";
                default: return "未登记";
            }
        }

        public static string Code(BoardRole role)
        {
            switch (role)
            {
                case BoardRole.Boat: return "BOAT";
                case BoardRole.Remote: return "REMOTE";
                default: return "UNKNOWN";
            }
        }

        public static BoardRole FromCode(string code)
        {
            if (string.Equals(code, "BOAT", StringComparison.OrdinalIgnoreCase)) return BoardRole.Boat;
            if (string.Equals(code, "REMOTE", StringComparison.OrdinalIgnoreCase)) return BoardRole.Remote;
            return BoardRole.Unknown;
        }
    }

    public static class MacText
    {
        private static readonly Regex Pattern = new Regex("^[0-9A-Fa-f]{2}(:[0-9A-Fa-f]{2}){5}$", RegexOptions.Compiled);

        // 芯片 MAC 必须是单播、非全零地址。
        public static bool IsValidChipMac(string mac)
        {
            if (string.IsNullOrEmpty(mac) || !Pattern.IsMatch(mac)) return false;
            string[] parts = mac.Split(':');
            bool anyNonZero = false;
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i] != "00") anyNonZero = true;
            }
            if (!anyNonZero) return false;
            int first = int.Parse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return (first & 1) == 0;
        }

        public static string Normalize(string mac)
        {
            if (string.IsNullOrEmpty(mac)) return "";
            string m = mac.Trim().Replace('-', ':').ToUpperInvariant();
            return IsValidChipMac(m) ? m : "";
        }

        // 对端地址允许全零（表示尚未配对），但仍必须是标准 MAC 文本。
        public static string NormalizePeer(string mac)
        {
            if (string.IsNullOrEmpty(mac)) return "";
            string m = mac.Trim().Replace('-', ':').ToUpperInvariant();
            return Pattern.IsMatch(m) ? m : "";
        }

        public static string OrUnknown(string mac)
        {
            string m = Normalize(mac);
            return m.Length == 0 ? "未知" : m;
        }
    }

    public class BoardIdentity
    {
        public bool Responded;
        public BoardRole Role = BoardRole.Unknown;
        public string Mac = "";
        public string Peer = "";
        public int PairProtocol;
        public int SteeringAngle = -1;
        public string FirmwareId = "";
        public string FirmwareVersion = "";
        public string RawLine = "";
        // 身份行协议版本：JAYv3 → 3、JAYv4 → 4、旧 JAY ROLE= → 0。
        public int IdentityLineVersion = -1;
        public string Error = "";

        public bool HasFirmwareId { get { return !string.IsNullOrEmpty(FirmwareId); } }
        public string MacText { get { return string.IsNullOrEmpty(Mac) ? "未知" : Mac; } }
        public string RoleText { get { return RoleNames.Chinese(Role); } }

        public bool HasChipIdentity { get { return !string.IsNullOrEmpty(Mac); } }

        public string PeerText
        {
            get
            {
                if (string.IsNullOrEmpty(Peer) || Peer == "00:00:00:00:00:00") return "未配对";
                return Peer;
            }
        }

        public string SteeringText
        {
            get
            {
                if (SteeringAngle < 0) return "未知";
                return "左右各 " + SteeringAngle + "°";
            }
        }

        public string FirmwareText
        {
            get
            {
                if (!Responded) return "未知";
                if (!HasFirmwareId) return "旧版（无固件身份）";
                return string.IsNullOrEmpty(FirmwareVersion) ? "有身份串（版本文本缺失）" : FirmwareVersion;
            }
        }

        public static BoardIdentity Unresponsive(string reason)
        {
            BoardIdentity id = new BoardIdentity();
            id.Responded = false;
            id.Error = reason;
            return id;
        }
    }

    public static class IdentityParser
    {
        // 新格式身份行：JAYv3 / JAYv4 ROLE=BOAT MAC=.. [PEER=..] [PAIR=2] [STEER=20] [FWID=..] [VER=..]
        // 版本号只影响前缀文字（固件端可能继续升版），字段语义不变；
        // 不认识的更高版本仍按同一字段解析，避免把新固件误判为“无身份”。
        private static readonly Regex V3Pattern = new Regex(
            "JAYv(\\d+)\\s+ROLE=(BOAT|REMOTE)\\s+MAC=([0-9A-Fa-f:]{17})(?:\\s+PEER=([0-9A-Fa-f:]{17}))?(?:\\s+PAIR=(\\d+))?(?:\\s+STEER=(-?\\d+))?(?:\\s+FWID=(\\S+))?(?:\\s+VER=(\\S+))?",
            RegexOptions.Compiled);

        // 旧格式：JAY ROLE=BOAT MAC=.. PEER=.. VERSION=2 [STEER=20]
        private static readonly Regex LegacyPattern = new Regex(
            "JAY\\s+ROLE=(BOAT|REMOTE)\\s+MAC=([0-9A-Fa-f:]{17})(?:\\s+PEER=([0-9A-Fa-f:]{17}))?(?:\\s+VERSION=(\\d+))?(?:\\s+STEER=(-?\\d+))?",
            RegexOptions.Compiled);

        public static BoardIdentity FromLine(string line)
        {
            if (string.IsNullOrEmpty(line)) return null;
            Match m = V3Pattern.Match(line);
            if (m.Success)
            {
                BoardIdentity id = new BoardIdentity();
                id.Responded = true;
                id.RawLine = line.Trim();
                id.Role = RoleNames.FromCode(m.Groups[2].Value);
                id.Mac = MacText.Normalize(m.Groups[3].Value);
                id.IdentityLineVersion = ParseInt(m.Groups[1].Value);
                if (m.Groups[4].Success) id.Peer = MacText.NormalizePeer(m.Groups[4].Value);
                if (m.Groups[5].Success) id.PairProtocol = ParseInt(m.Groups[5].Value);
                if (m.Groups[6].Success) id.SteeringAngle = ParseInt(m.Groups[6].Value);
                if (m.Groups[7].Success) id.FirmwareId = m.Groups[7].Value;
                if (m.Groups[8].Success) id.FirmwareVersion = m.Groups[8].Value;
                return id.Mac.Length == 0 ? null : id;
            }

            m = LegacyPattern.Match(line);
            if (m.Success)
            {
                BoardIdentity id = new BoardIdentity();
                id.Responded = true;
                id.RawLine = line.Trim();
                id.Role = RoleNames.FromCode(m.Groups[1].Value);
                id.Mac = MacText.Normalize(m.Groups[2].Value);
                if (m.Groups[3].Success) id.Peer = MacText.NormalizePeer(m.Groups[3].Value);
                if (m.Groups[4].Success) id.PairProtocol = ParseInt(m.Groups[4].Value);
                if (m.Groups[5].Success) id.SteeringAngle = ParseInt(m.Groups[5].Value);
                // 旧格式没有 FWID，FirmwareId 保持为空，表示必须升级。
                id.IdentityLineVersion = 0;
                return id.Mac.Length == 0 ? null : id;
            }
            return null;
        }

        // 配对核验专用：只接受带 FWID 的 JAYv4+ 完整行。
        // 旧格式、READ_FAILED、缺字段、半行、无法解析一律拒绝，并给出中文原因。
        public static bool TryParseStrict(string line, out BoardIdentity identity, out string reject)
        {
            identity = null;
            reject = "";
            if (string.IsNullOrEmpty(line)) { reject = "空行"; return false; }
            if (line.IndexOf("MAC=READ_FAILED", StringComparison.Ordinal) >= 0 ||
                line.IndexOf("SELF_MAC=READ_FAILED", StringComparison.Ordinal) >= 0 ||
                line.IndexOf("READ_FAILED", StringComparison.Ordinal) >= 0)
            {
                reject = "板上报告自身 MAC 读取失败（READ_FAILED）";
                return false;
            }
            Match strict = V3Pattern.Match(line);
            if (!strict.Success)
            {
                if (LegacyPattern.IsMatch(line) || line.StartsWith("JAY ROLE=", StringComparison.Ordinal))
                {
                    reject = "只有旧格式身份行（无 FWID），不能用于核验";
                    return false;
                }
                reject = "不是完整的 JAYv4 身份行";
                return false;
            }
            BoardIdentity id = FromLine(line);
            if (id == null || string.IsNullOrEmpty(id.Mac))
            {
                reject = "身份行缺少有效本机 MAC";
                return false;
            }
            if (id.IdentityLineVersion < 4)
            {
                reject = "身份行版本为 JAYv" + id.IdentityLineVersion + "，低于 v4";
                return false;
            }
            if (!id.HasFirmwareId)
            {
                reject = "身份行缺少 FWID";
                return false;
            }
            identity = id;
            return true;
        }

        public static BoardIdentity FromLines(IList<string> lines)
        {
            if (lines == null) return null;
            BoardIdentity first = null;
            for (int i = 0; i < lines.Count; i++)
            {
                BoardIdentity id = FromLine(lines[i]);
                if (id == null) continue;
                if (id.HasFirmwareId) return id; // 优先带 FWID 的新格式
                if (first == null) first = id;
            }
            return first;
        }

        private static int ParseInt(string text)
        {
            int value;
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) ? value : -1;
        }
    }

    public enum FirmwareState
    {
        Current = 0,
        UpgradeRequired = 1,
        WrongRole = 2,
        Unresponsive = 3,
        NoSpec = 4
    }

    public class FirmwareCheck
    {
        public FirmwareState State = FirmwareState.Unresponsive;
        public string Reason = "";
        public string ExpectedFwid = "";

        public string StateText
        {
            get
            {
                switch (State)
                {
                    case FirmwareState.Current: return "最新固件";
                    case FirmwareState.UpgradeRequired: return "需要升级";
                    case FirmwareState.WrongRole: return "角色不符";
                    case FirmwareState.NoSpec: return "缺少固件说明";
                    default: return "未响应";
                }
            }
        }
    }

    public static class VersionPolicy
    {
        // 只有 FWID 与本包固件完全一致才算最新。
        // 旧程序即使写着 STEER=20、配对协议为 2，只要没有 FWID 或 FWID 不同，就必须升级。
        public static FirmwareCheck Check(BoardIdentity id, FirmwareSpec spec)
        {
            FirmwareCheck check = new FirmwareCheck();
            check.ExpectedFwid = spec == null ? "" : spec.Fwid;

            if (id == null || string.IsNullOrEmpty(id.Mac))
            {
                check.State = FirmwareState.Unresponsive;
                check.Reason = "没有读到芯片 MAC" + (id == null || string.IsNullOrEmpty(id.Error) ? "" : "（" + id.Error + "）");
                return check;
            }
            if (spec == null)
            {
                check.State = FirmwareState.NoSpec;
                check.Reason = "工具包没有这块板对应角色的固件说明";
                return check;
            }
            if (!id.Responded)
            {
                // 旧程序或空白板不回应身份查询：无法证明是新固件，一律按需要升级处理。
                check.State = FirmwareState.UpgradeRequired;
                check.Reason = "板上程序没有回应身份查询（旧版或空白板），必须升级到本包固件";
                return check;
            }
            if (id.Role != spec.Role)
            {
                check.State = FirmwareState.WrongRole;
                check.Reason = "板上程序自报角色为" + id.RoleText + "，与预期的" + RoleNames.Chinese(spec.Role) + "不一致";
                return check;
            }
            if (!id.HasFirmwareId)
            {
                check.State = FirmwareState.UpgradeRequired;
                check.Reason = "板上程序没有固件身份（FWID），属于旧版，必须升级";
                return check;
            }
            if (!string.Equals(id.FirmwareId, spec.Fwid, StringComparison.Ordinal))
            {
                check.State = FirmwareState.UpgradeRequired;
                check.Reason = "板上固件身份与本包不一致：板上 " + id.FirmwareId + "，本包 " + spec.Fwid;
                return check;
            }
            if (id.SteeringAngle >= 0 && id.SteeringAngle != spec.SteerDeg)
            {
                check.State = FirmwareState.UpgradeRequired;
                check.Reason = "板上转向角度标记 " + id.SteeringAngle + "° 与本包 " + spec.SteerDeg + "° 不一致";
                return check;
            }
            check.State = FirmwareState.Current;
            check.Reason = "固件身份与本包一致";
            return check;
        }
    }

    public static class Hashing
    {
        public static string Sha256File(string path)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream stream = File.OpenRead(path))
            {
                byte[] hash = sha.ComputeHash(stream);
                StringBuilder text = new StringBuilder(hash.Length * 2);
                for (int i = 0; i < hash.Length; i++) text.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
                return text.ToString();
            }
        }

        public static bool FileContainsAscii(string path, string text)
        {
            byte[] needle = Encoding.ASCII.GetBytes(text);
            using (FileStream stream = File.OpenRead(path))
            {
                const int chunk = 1 << 20;
                byte[] buffer = new byte[chunk + needle.Length];
                int carry = 0;
                while (true)
                {
                    int read = stream.Read(buffer, carry, chunk);
                    if (read <= 0) return false;
                    int total = carry + read;
                    for (int i = 0; i <= total - needle.Length; i++)
                    {
                        bool match = true;
                        for (int j = 0; j < needle.Length; j++)
                        {
                            if (buffer[i + j] != needle[j]) { match = false; break; }
                        }
                        if (match) return true;
                    }
                    carry = Math.Min(needle.Length - 1, total);
                    Buffer.BlockCopy(buffer, total - carry, buffer, 0, carry);
                }
            }
        }
    }

    public class AppPaths
    {
        public string AppRoot;
        // 单套装兼容字段：指向 AppRoot\固件，用于旧结构与单套装自检。
        public string FirmwareDir;
        public string ToolDir;
        public string ManifestPath;
        public string EsptoolPath;
        // 多套装：所有导入的发布包都在这个目录下，每个子目录是一套。
        public string FirmwareLibraryDir;
        // 分类包预设；通用包没有这个文件。
        public string PresetPath;
        public string WritableRoot;
        public string ConfigDir;
        public string RecordsDir;
        public string RoleStorePath;
        public bool Portable;

        public static AppPaths Detect()
        {
            AppPaths paths = new AppPaths();
            paths.AppRoot = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            paths.FirmwareLibraryDir = Path.Combine(paths.AppRoot, "固件库");
            paths.PresetPath = Path.Combine(paths.AppRoot, "preset.json");
            paths.FirmwareDir = Path.Combine(paths.AppRoot, "固件");
            paths.ToolDir = Path.Combine(paths.AppRoot, "工具");
            paths.ManifestPath = Path.Combine(paths.FirmwareDir, "固件清单.json");
            paths.EsptoolPath = Path.Combine(paths.ToolDir, "esptool.exe");

            if (CanWrite(paths.AppRoot))
            {
                paths.WritableRoot = paths.AppRoot;
                paths.Portable = true;
            }
            else
            {
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                paths.WritableRoot = Path.Combine(local, "ESP32BoatTool");
                paths.Portable = false;
            }
            paths.ConfigDir = Path.Combine(paths.WritableRoot, "配置");
            paths.RecordsDir = Path.Combine(paths.WritableRoot, "记录");
            paths.RoleStorePath = Path.Combine(paths.ConfigDir, "角色记忆.json");
            return paths;
        }

        private static bool CanWrite(string dir)
        {
            try
            {
                string probe = Path.Combine(dir, ".write-probe-" + Guid.NewGuid().ToString("N"));
                File.WriteAllText(probe, "probe");
                File.Delete(probe);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public void EnsureDirectories()
        {
            Directory.CreateDirectory(ConfigDir);
            Directory.CreateDirectory(RecordsDir);
        }

        public string NewSession(string action)
        {
            EnsureDirectories();
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string safeAction = action;
            char[] invalid = Path.GetInvalidFileNameChars();
            foreach (char c in invalid) safeAction = safeAction.Replace(c, '-');
            string dir = Path.Combine(RecordsDir, stamp + "-" + safeAction);
            int n = 2;
            while (Directory.Exists(dir))
            {
                dir = Path.Combine(RecordsDir, stamp + "-" + safeAction + "-" + n);
                n++;
            }
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public class Logger
    {
        private readonly object gate = new object();
        private readonly Action<string> sink;
        private readonly string filePath;

        public Logger(string filePath, Action<string> sink)
        {
            this.filePath = filePath;
            this.sink = sink;
        }

        public void Info(string message)
        {
            Write("[" + DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "] " + message);
        }

        public void Raw(string message)
        {
            Write("    工具输出：" + message);
        }

        private void Write(string line)
        {
            lock (gate)
            {
                if (!string.IsNullOrEmpty(filePath))
                {
                    try { File.AppendAllText(filePath, line + Environment.NewLine, new UTF8Encoding(false)); }
                    catch { }
                }
                if (sink != null)
                {
                    try { sink(line); }
                    catch { }
                }
            }
        }
    }
}
