using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace BoatTool
{
    // 600 转超速锁存状态。三态明确区分，不能只用一个数值字段表达全部含义：
    //   Enabled  = 固件启用 600 转软件超速锁存停机（r10 的 -OS600）
    //   Disabled = 固件已关闭该锁存（r11 的 -OSOFF），600 不再是停机阈值
    //   Unknown  = 旧身份没有声明，不能猜为开启或关闭
    //
    // 注意：Enabled 也只是软件阈值，不是物理转速上限的保证；
    //       Disabled 同样不保证空载转速不超过 600，也没有电流反馈保证不过载。
    public enum OverspeedLockout
    {
        Unknown = 0,
        Enabled = 1,
        Disabled = 2
    }

    public static class OverspeedLockoutText
    {
        public static string Chinese(OverspeedLockout v)
        {
            switch (v)
            {
                case OverspeedLockout.Enabled: return "600 转软件超速锁存已开启";
                case OverspeedLockout.Disabled: return "600 转超速锁存已关闭";
                default: return "超速锁存状态未声明";
            }
        }

        public static string Code(OverspeedLockout v)
        {
            switch (v)
            {
                case OverspeedLockout.Enabled: return "enabled";
                case OverspeedLockout.Disabled: return "disabled";
                default: return "unknown";
            }
        }

        // 从 FWID 文本推断（只认明确的 OS 标记，没有标记就是未声明）。
        public static OverspeedLockout FromFwid(string fwid)
        {
            if (string.IsNullOrEmpty(fwid)) return OverspeedLockout.Unknown;
            if (fwid.IndexOf("-OSOFF", StringComparison.OrdinalIgnoreCase) >= 0) return OverspeedLockout.Disabled;
            if (fwid.IndexOf("-OS600", StringComparison.OrdinalIgnoreCase) >= 0) return OverspeedLockout.Enabled;
            // 其它 -OS<数字> 形式也不认：不是本轮批准过的两种标记之一。
            return OverspeedLockout.Unknown;
        }

        public static OverspeedLockout Parse(string text)
        {
            if (string.IsNullOrEmpty(text)) return OverspeedLockout.Unknown;
            string t = text.Trim();
            if (string.Equals(t, "off", StringComparison.OrdinalIgnoreCase)) return OverspeedLockout.Disabled;
            if (string.Equals(t, "on", StringComparison.OrdinalIgnoreCase)) return OverspeedLockout.Enabled;
            if (string.Equals(t, "enabled", StringComparison.OrdinalIgnoreCase)) return OverspeedLockout.Enabled;
            if (string.Equals(t, "disabled", StringComparison.OrdinalIgnoreCase)) return OverspeedLockout.Disabled;
            if (string.Equals(t, "unknown", StringComparison.OrdinalIgnoreCase)) return OverspeedLockout.Unknown;
            return OverspeedLockout.Unknown;
        }
    }

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
        // 清单声明的锁存状态；最终以 FWID 为交叉核对依据。
        public OverspeedLockout LockoutDeclared = OverspeedLockout.Unknown;
        // 清单声明的诊断协议契约 ID（如 diag-v1）。空表示未声明 → 只被动记录。
        public string DiagContract = "";
        // 该角色的超速锁定结论（清单声明与 FWID 一致时才有值，矛盾时套装不可用）。
        public OverspeedLockout Lockout = OverspeedLockout.Unknown;

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
        // 历史数值：r11 的 FW_OVERSPEED_RPM 仍为 600，但已不参与停机判断。
        public int OverspeedRpm = -1;
        // 清单声明的锁存状态（"on"/"off"/"unknown"）。缺省为 Unknown。
        public OverspeedLockout LockoutDeclared = OverspeedLockout.Unknown;
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
                catalog.LockoutDeclared = OverspeedLockoutText.Parse(
                    Json.GetString(root, "overspeed_lockout", ""));

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
                        string appRel = Json.GetString(obj, "app", "");
                        if (!IsSafeRelative(appRel))
                            catalog.Problems.Add(RoleNames.Chinese(role) + "应用镜像路径不安全（必须是套装内的相对路径）：" + appRel);
                        spec.AppPath = Path.Combine(dir, appRel);
                        spec.AppOffset = Json.GetInt(obj, "app_offset", -1);
                        spec.BootloaderOffset = Json.GetInt(obj, "bootloader_offset", -1);
                        spec.PartitionsOffset = Json.GetInt(obj, "partitions_offset", -1);
                        string blRel = Json.GetString(obj, "bootloader", "");
                        if (!IsSafeRelative(blRel))
                            catalog.Problems.Add(RoleNames.Chinese(role) + "bootloader 路径不安全（必须是套装内的相对路径）：" + blRel);
                        spec.BootloaderPath = Path.Combine(dir, blRel);
                        string ptRel = Json.GetString(obj, "partitions", "");
                        if (!IsSafeRelative(ptRel))
                            catalog.Problems.Add(RoleNames.Chinese(role) + "分区表路径不安全（必须是套装内的相对路径）：" + ptRel);
                        spec.PartitionsPath = Path.Combine(dir, ptRel);
                        string baRel = Json.GetString(obj, "boot_app0", "");
                        if (!IsSafeRelative(baRel))
                            catalog.Problems.Add(RoleNames.Chinese(role) + "OTA 选择文件路径不安全（必须是套装内的相对路径）：" + baRel);
                        spec.BootApp0Path = Path.Combine(dir, baRel);
                        spec.BootApp0Offset = Json.GetInt(obj, "boot_app0_offset", -1);
                        spec.LockoutDeclared = OverspeedLockoutText.Parse(
                            Json.GetString(obj, "overspeed_lockout", ""));
                        if (spec.LockoutDeclared == OverspeedLockout.Unknown && catalog.LockoutDeclared != OverspeedLockout.Unknown)
                            spec.LockoutDeclared = catalog.LockoutDeclared;
                        spec.DiagContract = Json.GetString(obj, "diag_contract",
                            Json.GetString(root, "diag_contract", ""));
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

        // 相对路径安全检查：非空、非绝对、不含 .. 或 . 段。镜像必须属于本套装目录。
        public static bool IsSafeRelative(string rel)
        {
            if (string.IsNullOrEmpty(rel)) return false;
            if (rel.IndexOf('~') >= 0) return false;
            if (Path.IsPathRooted(rel)) return false;
            string[] parts = rel.Replace('\\', '/').Split('/');
            foreach (string part in parts)
            {
                if (part.Length == 0) continue;
                if (part == "." || part == "..") return false;
            }
            return true;
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

                // 超速锁存三态：以 FWID 里的明确标记为事实，清单声明只能与它一致。
                // 该保护只属于小船动力控制；遥控器没有电机，不做锁存判断。
                OverspeedLockout fromFwid = OverspeedLockoutText.FromFwid(spec.Fwid);
                if (spec.Role == BoardRole.Boat && spec.LockoutDeclared != OverspeedLockout.Unknown)
                {
                    if (fromFwid == OverspeedLockout.Unknown)
                        Problems.Add(label + "清单声明超速锁存为 " + OverspeedLockoutText.Code(spec.LockoutDeclared) +
                                     "，但 FWID 没有明确的 OS 标记：" + spec.Fwid);
                    else if (fromFwid != spec.LockoutDeclared)
                        Problems.Add(label + "清单声明超速锁存 " + OverspeedLockoutText.Code(spec.LockoutDeclared) +
                                     " 与 FWID 实际 " + OverspeedLockoutText.Code(fromFwid) + " 矛盾");
                }
                // 结论一律以 FWID 为准：没有 OS 标记就是“未声明”，不猜。
                spec.Lockout = spec.Role == BoardRole.Boat ? fromFwid : OverspeedLockout.Unknown;
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
                // 以小船角色的实际结论（来自 FWID）为准；遥控器不参与锁存判断。
                OverspeedLockout boatLockout = LockoutDeclared;
                FirmwareSpec boatSpec = ForRole(BoardRole.Boat);
                if (boatSpec != null && boatSpec.Fwid.Length > 0) boatLockout = boatSpec.Lockout;
                string overspeed;
                switch (boatLockout)
                {
                    case OverspeedLockout.Enabled:
                        overspeed = "，" + OverspeedRpm + " 转软件超速锁存已开启";
                        break;
                    case OverspeedLockout.Disabled:
                        overspeed = "，600 转超速锁存已关闭（" + OverspeedRpm + " 仅为历史数值，不参与停机）";
                        break;
                    default:
                        overspeed = "，超速锁存状态未声明";
                        break;
                }
                return "固件包 " + Version + "：中位 " + CenterUs + "us（左右各 " + SteerDeg + "°：" +
                       AngleMin + "～" + AngleMax + "），脉宽 " + PulseMinUs + "～" + PulseMaxUs +
                       "us，" + speed + overspeed + "，配对协议 v" + PairProtocol;
            }
        }
    }
}
