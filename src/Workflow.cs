using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace BoatTool
{
    public class PortScan
    {
        public SerialPortInfo Port;
        public BoardIdentity Identity;
        public string ProbeError = "";
        public BoardRole RememberedRole = BoardRole.Unknown;
        public BoardRole BoardRoleReported = BoardRole.Unknown;
        public string Note = "";

        public bool Responded { get { return Identity != null && Identity.Responded; } }
        public bool HasChip { get { return Identity != null && Identity.HasChipIdentity; } }

        // 显示角色：本机记忆优先；没记忆时只展示板子自报（仍要求用户首次选择，绝不按串口号猜）。
        public BoardRole DisplayRole
        {
            get
            {
                if (RememberedRole != BoardRole.Unknown) return RememberedRole;
                return BoardRoleReported;
            }
        }

        public string MacText { get { return HasChip ? Identity.MacText : "未知"; } }
        public string FirmwareText { get { return Responded ? Identity.FirmwareText : (HasChip ? "旧版（无身份回应）" : "未知"); } }
        public string SteerText { get { return Responded ? Identity.SteeringText : "未知"; } }

        public string RoleText
        {
            get
            {
                if (RememberedRole != BoardRole.Unknown)
                {
                    if (BoardRoleReported != BoardRole.Unknown && BoardRoleReported != RememberedRole)
                        return RoleNames.Chinese(RememberedRole) + "（板上程序自报" + RoleNames.Chinese(BoardRoleReported) + "）";
                    return RoleNames.Chinese(RememberedRole);
                }
                if (BoardRoleReported != BoardRole.Unknown) return "未登记（板自报" + RoleNames.Chinese(BoardRoleReported) + "）";
                if (HasChip) return "未登记（无身份回应）";
                return "未知";
            }
        }
    }

    public class OperationResult
    {
        public bool Ok;
        public bool Partial;
        public string Summary = "";
        public List<string> Steps = new List<string>();
        public string Error = "";
        public string RecordDir = "";

        public void Step(string text)
        {
            Steps.Add(text);
        }

        public string ProgressText()
        {
            return string.Join(Environment.NewLine, Steps.ToArray());
        }
    }

    public interface IUserInteraction
    {
        // 首次访问串口前的一次性安全提示（识别会复位板子）。
        bool ConfirmSafetyNotice(string detail);

        // 每次操作只确认一次，明确列出目标串口、MAC、角色。
        bool ConfirmFlash(FlashPlan plan);
        bool ConfirmBackup(BackupPlan plan);
        bool ConfirmPair(PairPlan plan);

        void Report(string text);
    }

    public class FlashPlan
    {
        public string Port = "";
        public BoardIdentity Identity;
        public BoardRole Role = BoardRole.Unknown;
        public FirmwareCheck Check;
        public bool DoBackup;
        public string FirmwareVersion = "";
        public string Fwid = "";
        public bool NoIdentityResponse;

        // 套装相关
        public FirmwareSuite Suite;
        public string BoardFwid = "";
        public string BoardSpeedText = "未知";
        public string BoardSteerText = "未知";
        public bool ChangingSuite;         // 板上固件与所选套装不是同一套
        public bool Downgrade;             // 目标转速低于板上
        public string CrossSuiteWarning = "";
        public string NvsNote = "";
        // 超速锁存：板上与将写入两侧都明确展示（开启/关闭/未声明）。
        public OverspeedLockout BoardLockout = OverspeedLockout.Unknown;
        public OverspeedLockout TargetLockout = OverspeedLockout.Unknown;
        public string LockoutChangeText = "";
        public bool ExplicitReflash;       // 用户明确选择重刷
    }

    public class BackupPlan
    {
        public string Port = "";
        public BoardIdentity Identity;
        public long FlashBytes = -1;
    }

    public class PairPlan
    {
        public string BoatPort = "";
        public string RemotePort = "";
        public BoardIdentity Boat;
        public BoardIdentity Remote;
        public bool SkipBoat;
        public bool SkipRemote;
        public string Description = "";
    }

    public class Workflow
    {
        private readonly AppPaths paths;
        private FirmwareLibrary library;
        private readonly RoleStore roles;
        private readonly IBoardTransport transport;
        private readonly IClock clock;
        private readonly IUserInteraction ui;
        private bool safetyAcknowledged;
        private FirmwareSuite selectedSuite;
        private bool allowDowngrade;
        // 分类包预设：为空表示通用包（允许任意芯片，但仍按 MAC 核对）。
        private PresetConfig preset;
        // 诊断占用：记录期间这些串口不允许被烧录/配对/扫描重新打开。
        private IPortLockRegistry portLocks;

        public Workflow(AppPaths paths, FirmwareLibrary library, RoleStore roles,
                        IBoardTransport transport, IClock clock, IUserInteraction ui)
        {
            this.paths = paths;
            this.library = library;
            this.roles = roles;
            this.transport = transport;
            this.clock = clock;
            this.ui = ui;
            this.selectedSuite = library == null ? null : library.Suites.Find(s => s.Usable);
        }

        public bool SafetyAcknowledged { get { return safetyAcknowledged; } set { safetyAcknowledged = value; } }

        // 分类包预设：设置后，写入与绑定都会先核对实际芯片地址。
        public PresetConfig Preset
        {
            get { return preset; }
            set { preset = value; }
        }

        public IPortLockRegistry PortLocks
        {
            get { return portLocks; }
            set { portLocks = value; }
        }

        // 核心层拒绝：界面禁用只是保护之一。
        private bool RejectIfLocked(string port, OperationResult result)
        {
            if (portLocks == null || !portLocks.IsLocked(port)) return false;
            result.Error = "串口 " + port + " 正在被现场诊断占用（记录中），不能烧录、配对或重新扫描。" +
                           "请先停止诊断记录后再操作。";
            return true;
        }

        // 芯片地址必须符合分类包预设；不设置预设（通用包）时一律允许。
        private bool PresetAllows(BoardRole role, string mac, OperationResult result)
        {
            if (preset == null || !preset.IsCategory) return true;
            string reason;
            bool ok = role == BoardRole.Boat ? preset.AllowsBoat(mac, out reason) : preset.AllowsRemote(mac, out reason);
            if (!ok) result.Error = "本分类包不允许操作这块板：" + reason;
            return ok;
        }

        // 当前选中的固件套装；为空表示没有可用套装，所有写入操作都必须拒绝。
        public FirmwareSuite SelectedSuite
        {
            get { return selectedSuite; }
            set { selectedSuite = value; }
        }

        public FirmwareLibrary Library { get { return library; } }

        // 刷新固件库后替换库对象；当前选择由调用方负责迁移或清除。
        public void UpdateLibrary(FirmwareLibrary fresh)
        {
            library = fresh;
            selectedSuite = null;
        }

        // 用户是否已对“降到更低目标转速”做过一次明确确认。
        // 每次烧录都会重新确认，不会记住上一次；这个开关只用于界面说明。
        public bool AllowDowngrade { get { return allowDowngrade; } set { allowDowngrade = value; } }

        private bool AcknowledgeSafety()
        {
            if (safetyAcknowledged) return true;
            if (!ui.ConfirmSafetyNotice("读取串口身份时可能让板子复位。请先断开动力电池和舵机电源，只保留 USB 供电。"))
                return false;
            safetyAcknowledged = true;
            return true;
        }

        // 读取芯片需要 esptool：优先用所选套装的；没有就退到固件库里第一个带组件的套装，
        // 再没有就交给 HardwareTransport 自己的默认路径（失败时给出明确错误）。
        private string EsptoolOf(FirmwareSuite suite)
        {
            if (suite != null && !string.IsNullOrEmpty(suite.EsptoolPath)) return suite.EsptoolPath;
            if (library != null)
            {
                foreach (FirmwareSuite candidate in library.Suites)
                    if (!string.IsNullOrEmpty(candidate.EsptoolPath)) return candidate.EsptoolPath;
            }
            return "";
        }

        // 扫描串口即会访问串口（可能复位板子），所以必须先通过一次性安全提示。
        public List<PortScan> Scan()
        {
            List<PortScan> results = new List<PortScan>();
            if (!AcknowledgeSafety()) return results;
            IList<SerialPortInfo> ports = transport.ListSerialPorts();
            foreach (SerialPortInfo port in ports)
            {
                PortScan scan = new PortScan();
                scan.Port = port;
                // 诊断记录中的串口不能被扫描/探测重新打开。
                if (portLocks != null && portLocks.IsLocked(port.Name))
                {
                    scan.Note = "现场诊断记录中，已跳过探测";
                    scan.ProbeError = "locked-by-diagnostics";
                    results.Add(scan);
                    continue;
                }
                ProbeResult probe = transport.Probe(port.Name, 3000);
                if (probe.Ok)
                {
                    scan.Identity = probe.Identity;
                    scan.BoardRoleReported = probe.Identity.Role;
                    KnownBoard known = roles.Find(probe.Identity.Mac);
                    if (known != null)
                    {
                        scan.RememberedRole = known.Role;
                        if (known.Role != probe.Identity.Role)
                            scan.Note = "角色冲突：本机记忆为" + RoleNames.Chinese(known.Role) + "，板上程序自报" +
                                        RoleNames.Chinese(probe.Identity.Role);
                    }
                    else
                    {
                        scan.Note = "首次见到的板子，请选择用途";
                    }
                    scan.Note = AppendSuiteNote(scan, scan.Note);
                }
                else
                {
                    // 蓝牙/虚拟串口不是 ESP32，盲目用 esptool 探测既慢又可能打扰别的设备。
                    if (LooksVirtual(port))
                    {
                        scan.ProbeError = probe.Error;
                        scan.Note = "看起来不是 USB 串口设备（" + TrimDescription(port) + "），未做芯片探测";
                        results.Add(scan);
                        continue;
                    }
                    // 无身份回应不代表没有板子：尝试只读芯片 MAC，便于用户核对。
                    string chipError;
                    ChipInfo chip = transport.ReadChip(port.Name, EsptoolOf(selectedSuite), out chipError);
                    if (chip != null)
                    {
                        BoardIdentity blind = new BoardIdentity();
                        blind.Responded = false;
                        blind.Mac = chip.Mac;
                        blind.Error = "程序未回应身份查询";
                        scan.Identity = blind;
                        KnownBoard known = roles.Find(chip.Mac);
                        if (known != null) scan.RememberedRole = known.Role;
                        scan.ProbeError = probe.Error;
                        scan.Note = "程序未回应身份（旧版或空白板）；已读到芯片 MAC。";
                    }
                    else
                    {
                        scan.ProbeError = probe.Error + "；" + chipError;
                        scan.Note = "未读到身份与芯片信息：可能不是 ESP32、串口被占用或数据线不支持";
                    }
                }
                results.Add(scan);
            }
            return results;
        }

        // 用所选套装给这块板补一句说明：是否已匹配、是否属于另一套。
        private string AppendSuiteNote(PortScan scan, string note)
        {
            if (!scan.Responded || scan.Identity.Role == BoardRole.Unknown) return note;
            FirmwareSpec spec = selectedSuite == null ? null : selectedSuite.SpecFor(scan.Identity.Role);
            if (spec == null) return note;
            if (string.Equals(scan.Identity.FirmwareId, spec.Fwid, StringComparison.Ordinal))
            {
                // 身份一致不等于镜像一致：板上镜像内容没有读取证据，统一按未核验表述。
                string note2 = "固件身份匹配，板上镜像内容未核验";
                if (IsImageAmbiguous(scan.Identity.Role, scan.Identity))
                    note2 += "（固件库里还存在同身份、不同镜像的套装）";
                return note2;
            }

            List<FirmwareSuite> owners = library == null
                ? new List<FirmwareSuite>()
                : library.AllWithFwid(scan.Identity.Role, scan.Identity.FirmwareId);
            if (owners.Count > 0)
            {
                string names = "";
                foreach (FirmwareSuite owner in owners)
                {
                    if (names.Length > 0) names += "、";
                    names += owner.DisplayName;
                }
                string speed = FwidSpeed(scan.Identity.FirmwareId);
                return "板上是 " + names + (string.IsNullOrEmpty(speed) ? "" : "（" + speed + "）") +
                       "，与所选套装不同；烧录前会再次确认";
            }
            if (!scan.Identity.HasFirmwareId) return "板上无固件身份（旧版），需要升级";
            return note;
        }

        private static string FwidSpeed(string fwid)
        {
            FwidInfo info = FwidInfo.Parse(fwid);
            return info.TargetRpm > 0 ? info.TargetRpm + " 转" : "";
        }

        public FirmwareCheck CheckFirmware(PortScan scan, BoardRole role)
        {
            FirmwareSpec spec = selectedSuite == null ? null : selectedSuite.SpecFor(role);
            bool ambiguous = IsImageAmbiguous(role, scan == null ? null : scan.Identity);
            return VersionPolicy.Check(scan == null ? null : scan.Identity, spec, ambiguous);
        }

        // 同角色、同 FWID 的已校验条目里，是否存在与所选套装应用镜像哈希不同的其它套装：
        // 存在即“身份相同、镜像内容未核验”——板上 FWID 一致不能证明镜像一致。
        public bool IsImageAmbiguous(BoardRole role, BoardIdentity identity)
        {
            if (identity == null || !identity.HasFirmwareId || identity.Role != role) return false;
            if (selectedSuite == null || library == null) return false;
            FirmwareSpec mine = selectedSuite.SpecFor(role);
            if (mine == null || !string.Equals(identity.FirmwareId, mine.Fwid, StringComparison.Ordinal)) return false;
            string myHash = FirmwareLibrary.ComputeFileHash(mine.AppPath);
            foreach (FirmwareSuite other in library.AllWithFwid(role, identity.FirmwareId))
            {
                if (string.Equals(other.Id, selectedSuite.Id, StringComparison.OrdinalIgnoreCase)) continue;
                FirmwareSpec otherSpec = other.SpecFor(role);
                if (otherSpec == null || string.IsNullOrEmpty(otherSpec.AppPath)) continue;
                if (FirmwareLibrary.ComputeFileHash(otherSpec.AppPath) != myHash) return true;
            }
            return false;
        }

        // 只按驱动描述排除明显非 ESP32 的串口；绝不按串口号或名字推断板子角色。
        private static bool LooksVirtual(SerialPortInfo port)
        {
            string text = ((port.Description ?? "") + " " + (port.HardwareId ?? "")).ToUpperInvariant();
            return text.Contains("BLUETOOTH") || text.Contains("蓝牙") ||
                   text.Contains("BTHENUM") || text.Contains("VIRTUAL") || text.Contains("虚拟");
        }

        private static string TrimDescription(SerialPortInfo port)
        {
            string text = port.Description;
            if (string.IsNullOrEmpty(text)) return "无驱动描述";
            return text.Length > 40 ? text.Substring(0, 40) + "…" : text;
        }

        public bool LastScanCancelled { get { return !safetyAcknowledged; } }

        // 烧录当前板：默认不备份；备份是可选项；写入后强制校验并读回身份。
        // forceReflash 只表示"用户在套装已匹配时仍要求重刷"，不绕过任何安全校验。
        // 烧录前复核：重新从磁盘加载该套装目录，比对清单版本与各角色应用镜像哈希。
        // 只读磁盘，不碰串口。任何不一致都拒绝烧录（防“刷新后被替换”的场景）。
        public static bool RevalidateSuite(FirmwareSuite suite, out string error)
        {
            error = "";
            if (suite == null) { error = "套装为空"; return false; }
            string suiteRoot = Path.GetDirectoryName(suite.CatalogDir.TrimEnd(Path.DirectorySeparatorChar));
            if (string.IsNullOrEmpty(suiteRoot) || !Directory.Exists(suiteRoot))
            { error = "套装目录已不存在：" + suiteRoot; return false; }
            FirmwareSuite fresh;
            try { fresh = FirmwareSuite.Load(suiteRoot); }
            catch (Exception ex) { error = "重新加载失败：" + ex.Message; return false; }
            if (fresh.Problems.Count > 0)
            {
                error = "重新加载后自检未通过：" + string.Join("；", fresh.Problems.ToArray());
                return false;
            }
            // 与 GUI 刷新共用同一快照规则（SuiteImageStamp，逐镜像条目 + 清单 + 导入 + esptool）。
            string oldStamp = FirmwareLibrary.SuiteImageStamp(suite);
            string newStamp = FirmwareLibrary.SuiteImageStamp(fresh);
            if (oldStamp != newStamp)
            {
                error = DescribeSnapshotDelta(suite, fresh);
                return false;
            }
            return true;
        }

        // 两个快照的差异描述（给出尽量具体的原因；找不到单项差异时给汇总）。
        private static string DescribeSnapshotDelta(FirmwareSuite oldSuite, FirmwareSuite fresh)
        {
            if (oldSuite.EsptoolHashSnapshot != fresh.EsptoolHashSnapshot)
                return "烧录组件 esptool.exe 已被替换（与加载时快照不一致）";
            if (oldSuite.ManifestHashSnapshot != fresh.ManifestHashSnapshot)
                return "固件清单已变化（写入地址、路径、诊断契约或其它元数据）";
            if (oldSuite.ImportHashSnapshot != fresh.ImportHashSnapshot)
                return "导入记录已变化";
            foreach (FirmwareSuite.ImageSnapshot oldImg in oldSuite.ImageSnapshots)
            {
                FirmwareSuite.ImageSnapshot newImg = null;
                foreach (FirmwareSuite.ImageSnapshot cand in fresh.ImageSnapshots)
                    if (cand.Role == oldImg.Role && cand.Kind == oldImg.Kind) { newImg = cand; break; }
                string role = oldImg.Role == "BOAT" ? "小船" : "遥控器";
                if (newImg == null)
                    return role + " " + oldImg.Kind + " 镜像条目在刷新后消失";
                if (newImg.Sha256 != oldImg.Sha256)
                    return role + " " + oldImg.Kind + " 镜像内容已被替换（" + oldImg.RelPath + "）";
                if (newImg.Offset != oldImg.Offset)
                    return role + " " + oldImg.Kind + " 写入地址已变化（0x" + oldImg.Offset.ToString("X") +
                           " → 0x" + newImg.Offset.ToString("X") + "）";
                if (newImg.RelPath != oldImg.RelPath)
                    return role + " " + oldImg.Kind + " 镜像路径已变化（" + oldImg.RelPath +
                           " → " + newImg.RelPath + "）";
                if (newImg.Length != oldImg.Length)
                    return role + " " + oldImg.Kind + " 镜像长度已变化";
            }
            return "套装内容与加载时快照不一致";
        }

        // 供 GUI/测试计算镜像哈希（与 RevalidateSuite 使用同一实现）。
        public static string FileHashForTest(string path)
        {
            return FileHash(path);
        }

        private static string FileHash(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return "";
            using (System.Security.Cryptography.SHA256 sha = System.Security.Cryptography.SHA256.Create())
            using (FileStream fs = File.OpenRead(path))
            {
                byte[] h = sha.ComputeHash(fs);
                StringBuilder sb = new StringBuilder(h.Length * 2);
                foreach (byte b in h) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        public OperationResult FlashCurrent(PortScan scan, BoardRole chosenRole, bool doBackup, bool forceReflash)
        {
            OperationResult result = new OperationResult();
            // 诊断占用必须最先拒绝：即使没有套餐也不归还控制权给写入路径。
            if (scan != null && scan.Port != null && RejectIfLocked(scan.Port.Name, result)) return result;
            FirmwareSuite suite = selectedSuite;
            if (suite == null)
            {
                result.Error = "还没有选择可用的固件套装。请先在界面上选择要写入的套装。";
                return result;
            }
            if (!suite.Usable)
            {
                result.Error = "所选固件套装自检未通过：" + string.Join("；", suite.Problems.ToArray());
                return result;
            }
            // 烧录前再核对：套装文件可能在“刷新固件库”后被替换或损坏。
            // 重新加载同一目录并与当前所选比对清单与镜像哈希；不一致即拒绝并要求重新选择。
            string suiteRecheckError;
            if (!RevalidateSuite(suite, out suiteRecheckError))
            {
                result.Error = "所选套装在烧录前复核失败，可能已被替换或损坏：" + suiteRecheckError +
                               "。请刷新固件库并重新选择套装。";
                return result;
            }
            if (scan == null || scan.Port == null) { result.Error = "没有选择串口"; return result; }
            if (!AcknowledgeSafety()) { result.Error = "已取消：尚未断开动力电池"; return result; }

            string port = scan.Port.Name;
            if (RejectIfLocked(port, result)) return result;
            string esptool = suite.EsptoolPath;
            ProbeResult probe = transport.Probe(port, 5000);
            BoardIdentity identity;
            if (probe.Ok)
            {
                identity = probe.Identity;
            }
            else
            {
                // 老固件、空白板或非本项目程序不会回应 JAY INFO；
                // 这种板子只能靠芯片 MAC 识别，且必须由用户明确指定用途并确认。
                identity = IdentityFromChip(port, esptool, result);
                if (identity == null) return result;
                result.Step("板子没有回应身份查询，按芯片 MAC 识别：" + identity.Mac + "（旧程序或空白板）");
            }
            result.Step("读取身份：串口 " + port + "，MAC " + identity.Mac + "，板自报角色 " + identity.RoleText);

            BoardRole role = ResolveRoleForFlash(identity, chosenRole, result);
            if (role == BoardRole.Unknown)
            {
                if (string.IsNullOrEmpty(result.Error))
                    result.Error = "没有确定这块板要用作小船还是遥控器。";
                return result;
            }

            if (!PresetAllows(role, identity.Mac, result)) return result;

            if (identity.Role != BoardRole.Unknown && identity.Role != role)
            {
                result.Error = "板上现有程序自报为" + identity.RoleText + "，与本次选择的" + RoleNames.Chinese(role) +
                               "不一致。为避免把固件写错板子，已停止。如确实要改用途，请先确认这块板子的 MAC：" + identity.Mac;
                return result;
            }

            FirmwareSpec spec = suite.SpecFor(role);
            if (spec == null)
            {
                result.Error = "所选套装缺少" + RoleNames.Chinese(role) + "固件，已停止。";
                return result;
            }
            PartitionLayout layout = suite.LayoutFor(role);
            if (layout == null || !layout.Parsed || !string.IsNullOrEmpty(layout.Error))
            {
                result.Error = "所选套装的分区布局与本工具不兼容，已拒绝写入：" +
                               (layout == null ? "未解析分区表" : layout.Error) +
                               "。为避免覆盖配对区或越界写入，本次没有写入任何内容。";
                return result;
            }

            bool ambiguous = IsImageAmbiguous(role, identity);
            FirmwareCheck check = VersionPolicy.Check(identity, spec, ambiguous);
            if (check.State == FirmwareState.Current && !forceReflash)
            {
                // 身份匹配 != 镜像已核验：没有读取板上 Flash 的证据，不能宣称"完全一致"。
                // 保持跳过写入（可选备份/重刷由用户决定），但提示必须如实。
                result.Ok = true;
                result.Summary = "固件身份匹配（" + identity.SteeringText + "，FWID " + identity.FirmwareId +
                                 "），板上镜像内容未核验。本次未写入；如需确保使用所选文件，请勾选“即使已匹配也重刷”。";
                if (ambiguous)
                    result.Summary += Environment.NewLine +
                        "注意：固件库里存在同身份、不同镜像内容的套装，仅凭串口身份不能确认板上运行的是哪一个。";
                result.Step("固件身份一致（镜像内容未核验）：未写入");
                roles.Remember(identity.Mac, role, port, "身份匹配未写入（镜像未核验）");
                return result;
            }
            if (check.State == FirmwareState.Unresponsive || check.State == FirmwareState.NoSpec)
            {
                result.Error = check.Reason;
                return result;
            }

            FlashPlan plan = new FlashPlan();
            plan.Port = port;
            plan.Identity = identity;
            plan.Role = role;
            plan.Check = check;
            plan.DoBackup = doBackup;
            plan.FirmwareVersion = suite.Version;
            plan.Fwid = spec.Fwid;
            plan.NoIdentityResponse = !identity.Responded;
            plan.Suite = suite;
            plan.BoardFwid = identity.HasFirmwareId ? identity.FirmwareId : "";
            FwidInfo boardInfo = FwidInfo.Parse(identity.FirmwareId);
            plan.BoardSpeedText = boardInfo.TargetRpm > 0 ? boardInfo.TargetRpm + " 转" : (identity.HasFirmwareId ? "未知" : "旧版无身份");
            plan.BoardSteerText = identity.SteeringAngle >= 0 ? "左右各 " + identity.SteeringAngle + "°" : "未知";
            plan.ExplicitReflash = forceReflash;

            FirmwareSuite boardSuite = library == null ? null : library.FindByFwid(role, identity.FirmwareId);
            plan.ChangingSuite = IsDifferentSuite(boardSuite, suite) ||
                                  !string.Equals(identity.FirmwareId, spec.Fwid, StringComparison.Ordinal);
            if (boardSuite != null && !string.Equals(boardSuite.Id, suite.Id, StringComparison.OrdinalIgnoreCase))
            {
                plan.CrossSuiteWarning = "这块板现在运行【" + boardSuite.DisplayName + "】（" + boardSuite.Version +
                                         "），将切换为【" + suite.DisplayName + "】（" + suite.Version + "）。";
            }
            else if (boardSuite == null && !string.Equals(identity.FirmwareId, spec.Fwid, StringComparison.Ordinal))
            {
                plan.CrossSuiteWarning = "这块板的固件不在本工具固件库中（FWID " +
                                         (string.IsNullOrEmpty(identity.FirmwareId) ? "无" : identity.FirmwareId) +
                                         "），将改写为【" + suite.DisplayName + "】。";
            }

            // 降级 = 目标转速低于板上既有目标转速。必须单独确认，不能静默执行。
            if (boardInfo.TargetRpm > 0 && suite.TargetRpm > 0 && suite.TargetRpm < boardInfo.TargetRpm)
            {
                plan.Downgrade = true;
                plan.CrossSuiteWarning += Environment.NewLine +
                    "注意：这是降级——板上目标 " + boardInfo.TargetRpm + " 转，将写入 " + suite.TargetRpm + " 转。";
            }

            // 超速锁存：即使目标转速都为 590，也要明确展示保护差异。
            plan.BoardLockout = OverspeedLockoutText.FromFwid(identity.FirmwareId);
            plan.TargetLockout = spec.Lockout;
            string boardLock = OverspeedLockoutText.Chinese(plan.BoardLockout);
            string targetLock = OverspeedLockoutText.Chinese(plan.TargetLockout);
            if (plan.BoardLockout != plan.TargetLockout)
            {
                plan.LockoutChangeText = "超速锁存变化：板上【" + boardLock + "】→ 将写入【" + targetLock + "】。" +
                    (plan.TargetLockout == OverspeedLockout.Disabled
                        ? "写入后 600 不再触发停机；这不保证空载转速低于 600，也没有电流反馈保证不过载。"
                        : (plan.TargetLockout == OverspeedLockout.Enabled
                            ? "写入后 600 会触发软件锁存停机；这只是软件阈值，不是物理转速上限的保证。"
                            : "目标固件没有声明锁存状态，不能假定开启或关闭。"));
            }
            else
            {
                plan.LockoutChangeText = "超速锁存：本端与目标均为【" + targetLock + "】。";
                if (plan.TargetLockout == OverspeedLockout.Disabled)
                    plan.LockoutChangeText += "600 不触发停机；不保证空载转速低于 600，也没有电流反馈保证不过载。";
                else if (plan.TargetLockout == OverspeedLockout.Enabled)
                    plan.LockoutChangeText += "600 会触发软件锁存停机；这不代表物理转速上限已被证明。";
            }

            plan.NvsNote = "写入 " + spec.DescribeImages() + "；分区表校验通过：" + layout.DescribeNvs();

            if (!ui.ConfirmFlash(plan))
            {
                result.Error = "已取消，没有备份或写入";
                return result;
            }

            result.RecordDir = paths.NewSession("烧录-" + RoleNames.Chinese(role));
            Logger log = new Logger(Path.Combine(result.RecordDir, "操作记录.txt"), ui.Report);
            log.Info("套装：" + suite.DisplayName + "（" + suite.Id + "，版本 " + suite.Version + "）");
            log.Info("目标：串口 " + port + "，MAC " + identity.Mac + "，角色 " + RoleNames.Chinese(role));
            log.Info("板上固件：" + identity.FirmwareText + "（" + (identity.HasFirmwareId ? identity.FirmwareId : "无 FWID") + "）");
            log.Info("目标固件：" + spec.Version + "（" + spec.Fwid + "）");
            log.Info("分区布局：" + layout.DescribeNvs());

            string chipMac = "";
            long backupBytes = -1;
            if (doBackup)
            {
                string chipError;
                ChipInfo chip = transport.ReadChip(port, esptool, out chipError);
                if (chip == null)
                {
                    result.Error = "备份前读取芯片信息失败：" + chipError + "。已停止，尚未写入固件。";
                    return result;
                }
                if (chip.Mac != identity.Mac)
                {
                    result.Error = "备份前核对失败：芯片 MAC " + chip.Mac + " 与确认时的 " + identity.Mac +
                                   " 不一致，已停止，尚未写入固件。";
                    return result;
                }
                if (chip.FlashBytes <= 0)
                {
                    result.Error = "未识别到 Flash 容量，无法备份，已停止，尚未写入固件。";
                    return result;
                }
                chipMac = chip.Mac;
                if (!BackupInternal(port, esptool, chip, result.RecordDir, log, result, out backupBytes))
                {
                    result.Error = "备份未完成，已停止，尚未写入固件。";
                    return result;
                }
            }
            else
            {
                log.Info("按设置跳过烧录前备份（可在需要时单独使用“备份原固件”）");
            }

            // 写入前再核对一次：芯片 MAC 必须与确认时一致（防换插）；
            // 若板子能回应身份，角色也必须一致。旧板不回应身份时只按芯片 MAC 判断。
            string recheckError;
            ChipInfo recheck = transport.ReadChip(port, esptool, out recheckError);
            if (recheck == null)
            {
                result.Error = "写入前复核芯片失败：" + recheckError + "，已停止，没有写入。";
                return result;
            }
            if (recheck.Mac != identity.Mac)
            {
                result.Error = "写入前复核失败：当前芯片 MAC 为 " + recheck.Mac + "，与确认时的 " + identity.Mac +
                               " 不一致（可能换插了板子），已停止，没有写入。";
                return result;
            }
            BoardIdentity recheckId = null;
            for (int attempt = 0; attempt < 4; attempt++)
            {
                ProbeResult p = transport.Probe(port, 2500);
                if (p.Ok) { recheckId = p.Identity; break; }
            }
            if (recheckId != null)
            {
                if (recheckId.Mac != identity.Mac)
                {
                    result.Error = "写入前复核失败：串口身份 " + recheckId.Mac + " 与芯片 MAC " + recheck.Mac +
                                   " 不一致，已停止，没有写入。";
                    return result;
                }
                if (identity.Role != BoardRole.Unknown && recheckId.Role != BoardRole.Unknown &&
                    recheckId.Role != identity.Role)
                {
                    result.Error = "写入前复核失败：板子角色从" + identity.RoleText + "变为" + recheckId.RoleText +
                                   "，已停止，没有写入。";
                    return result;
                }
            }
            if (chipMac.Length > 0 && recheck.Mac != chipMac)
            {
                result.Error = "写入前复核失败：当前芯片 " + recheck.Mac + " 与备份时的 " + chipMac +
                               " 不一致，已停止，没有写入。";
                return result;
            }
            result.Step("写入前复核通过：芯片 MAC " + recheck.Mac + "（" + RoleNames.Chinese(role) + "）");

            log.Info("开始写入并校验：" + spec.DescribeImages());
            FlashResult flash = transport.WriteFirmware(port, esptool, spec, true, result.RecordDir, log);
            if (!flash.Ok)
            {
                result.Error = flash.Error + "。写入失败，不能视为烧录成功。";
                return result;
            }
            result.Step("写入完成，工具报告哈希校验通过");

            // 烧录后必须重新读身份，确认 FWID 与角色，否则不算成功。
            clock.Sleep(1500);
            BoardIdentity after = null;
            for (int attempt = 0; attempt < 6; attempt++)
            {
                ProbeResult p = transport.Probe(port, 3000);
                if (p.Ok) { after = p.Identity; break; }
                clock.Sleep(700);
            }
            if (after == null)
            {
                result.Error = "固件已写入且校验通过，但重启后读不到身份，无法确认新固件已运行。请重新连接后点“刷新列表”再核对。";
                return result;
            }
            if (after.Mac != identity.Mac)
            {
                result.Error = "烧录后读到的 MAC 是 " + after.Mac + "，与烧录前的 " + identity.Mac + " 不一致，已停止核对。";
                return result;
            }
            FirmwareCheck afterCheck = VersionPolicy.Check(after, spec);
            if (afterCheck.State != FirmwareState.Current)
            {
                result.Error = "写入后读回的固件身份不符合本套装（" + afterCheck.Reason + "），不能视为成功。";
                return result;
            }
            result.Step("重启后身份核对通过：" + after.Mac + " / " + RoleNames.Chinese(role) + " / " + after.FirmwareText +
                        " / " + after.SteeringText);
            roles.Remember(after.Mac, role, port, "烧录 " + suite.Id);
            log.Info("烧录完成并通过写入校验与重启读回核对");
            result.Ok = true;
            result.Summary = "烧录完成：串口 " + port + "，MAC " + after.Mac + "，角色 " + RoleNames.Chinese(role) +
                             "，套装【" + suite.DisplayName + "】，固件 " + spec.Version + "（" + after.SteeringText + "）。" +
                             "写入校验与重启读回均已通过。";
            result.Summary += Environment.NewLine + "保留 NVS 配对区：" + layout.DescribeNvs();
            result.Summary += Environment.NewLine + "记录目录：" + result.RecordDir;
            if (!doBackup)
                result.Summary += Environment.NewLine + "本次未备份原固件（按默认设置）。需要时可用“备份原固件”。";
            return result;
        }

        // 只有“确实换了一套不同的固件”才提示跨套装；同一套里重刷不算。
        private static bool IsDifferentSuite(FirmwareSuite boardSuite, FirmwareSuite target)
        {
            return boardSuite != null && !string.Equals(boardSuite.Id, target.Id, StringComparison.OrdinalIgnoreCase);
        }

        private BoardRole ResolveRoleForFlash(BoardIdentity identity, BoardRole chosenRole, OperationResult result)
        {
            BoardRole remembered = roles.RoleOf(identity.Mac);
            if (remembered != BoardRole.Unknown)
            {
                if (chosenRole != BoardRole.Unknown && chosenRole != remembered)
                {
                    result.Step("注意：本机记忆这块 MAC 是" + RoleNames.Chinese(remembered) + "，选定用途为" +
                                RoleNames.Chinese(chosenRole) + "；以记忆为准，避免误写。");
                }
                return remembered;
            }
            // 首次见到的板子必须由用户明确选择，不能从串口号或固件自报推断。
            if (chosenRole == BoardRole.Unknown)
            {
                result.Error = "这块板（MAC " + identity.Mac + "）是首次使用，请先在小船/遥控器选项中选择用途。";
                return BoardRole.Unknown;
            }
            return chosenRole;
        }

        // 旧程序/空白板不回应 JAY INFO 时，只能按芯片 MAC 识别；用途仍需用户选择。
        private BoardIdentity IdentityFromChip(string port, string esptool, OperationResult result)
        {
            string error;
            ChipInfo chip = transport.ReadChip(port, esptool, out error);
            if (chip == null)
            {
                result.Error = "板子没有回应身份查询，读取芯片也失败：" + error +
                               "。请确认是 ESP32 开发板、数据线和驱动正常。";
                return null;
            }
            BoardIdentity id = new BoardIdentity();
            id.Responded = false;
            id.Mac = chip.Mac;
            id.Role = BoardRole.Unknown;
            id.Error = "程序未回应身份查询";
            return id;
        }

        // 只读核验：解除“已保存但未确认生效 / 失败需重新上电”状态的唯一途径。
        // 不写任何配置，只发 JAY INFO 并严格核对。
        public OperationResult VerifyOnly(PortScan scan, BoardRole role, string expectedPeer)
        {
            OperationResult result = new OperationResult();
            if (scan == null || scan.Port == null) { result.Error = "没有选择串口"; return result; }
            if (!AcknowledgeSafety()) { result.Error = "已取消：尚未断开动力电池"; return result; }

            string fwidProblem;
            BoardIdentity before = scan.Identity;
            string expectedFwid = ExpectedFwid(role, before, out fwidProblem);
            if (expectedFwid == null)
            {
                result.Error = "不能做只读核验：该端" + fwidProblem;
                return result;
            }
            if (before == null || string.IsNullOrEmpty(before.Mac))
            {
                result.Error = "没有可用的芯片 MAC，无法做只读核验；请先点“刷新列表”。";
                return result;
            }

            result.RecordDir = paths.NewSession("只读核验-" + RoleNames.Chinese(role));
            Logger log = new Logger(Path.Combine(result.RecordDir, "操作记录.txt"), ui.Report);
            log.Info("只读核验：串口 " + scan.Port.Name + "，角色 " + RoleNames.Chinese(role) +
                     "，期望本机 " + before.Mac + "，期望对端 " + expectedPeer);

            VerifyResult vr = transport.VerifyPairing(scan.Port.Name, role, before.Mac, expectedPeer, expectedFwid, log);
            if (vr.Events != null) foreach (string e in vr.Events) result.Step(e);
            if (vr.Ok)
            {
                result.Ok = true;
                result.Summary = "只读核验通过：本端配对配置与本次目标一致（角色 " + RoleNames.Chinese(role) +
                                 "，本机 " + vr.Identity.Mac + "，对端 " + vr.Identity.Peer +
                                 "，固件身份与本轮清单一致）。本次没有写入任何配置。";
                roles.Remember(vr.Identity.Mac, role, scan.Port.Name, "只读核验通过");
                return result;
            }
            result.Error = vr.Message;
            result.Summary = "只读核验未通过：" + vr.Message + " 没有写入任何配置。";
            return result;
        }

        public OperationResult BackupOnly(PortScan scan)
        {
            OperationResult result = new OperationResult();
            if (scan == null || scan.Port == null) { result.Error = "没有选择串口"; return result; }
            if (!AcknowledgeSafety()) { result.Error = "已取消：尚未断开动力电池"; return result; }

            string port = scan.Port.Name;
            string esptool = EsptoolOf(selectedSuite);
            ProbeResult probe = transport.Probe(port, 5000);
            BoardIdentity identity;
            if (probe.Ok)
            {
                identity = probe.Identity;
            }
            else
            {
                // 旧板不回应身份也可以备份：备份是只读操作，按芯片 MAC 记录即可。
                identity = IdentityFromChip(port, esptool, result);
                if (identity == null) return result;
                result.Step("板子没有回应身份查询，按芯片 MAC 备份：" + identity.Mac);
            }

            string chipError;
            ChipInfo chip = transport.ReadChip(port, esptool, out chipError);
            if (chip == null) { result.Error = chipError; return result; }
            if (probe.Ok && chip.Mac != identity.Mac)
            {
                result.Error = "串口身份 MAC（" + identity.Mac + "）与芯片读取 MAC（" + chip.Mac +
                               "）不一致，已停止，没有读取备份。";
                return result;
            }
            if (chip.FlashBytes <= 0)
            {
                result.Error = "没有识别到 Flash 容量，不能进行整片备份。";
                return result;
            }

            BackupPlan plan = new BackupPlan();
            plan.Port = port;
            plan.Identity = identity;
            plan.FlashBytes = chip.FlashBytes;
            if (!ui.ConfirmBackup(plan)) { result.Error = "已取消备份"; return result; }

            result.RecordDir = paths.NewSession("备份-" + (identity.Role == BoardRole.Unknown ? "未登记" : RoleNames.Chinese(identity.Role)));
            Logger log = new Logger(Path.Combine(result.RecordDir, "操作记录.txt"), ui.Report);
            long bytes;
            if (!BackupInternal(port, esptool, chip, result.RecordDir, log, result, out bytes))
            {
                result.Error = "备份失败：" + (result.Error.Length > 0 ? result.Error : "未知原因");
                return result;
            }
            if (identity.Role != BoardRole.Unknown) roles.Touch(identity.Mac, port);
            result.Ok = true;
            result.Summary = "备份完成：串口 " + port + "，芯片 MAC " + chip.Mac + "，大小 " + bytes + " 字节。" +
                             Environment.NewLine + "文件：" + Path.Combine(result.RecordDir, "原固件.bin");
            return result;
        }

        // 仅读取，不写入不擦除；调用方已核对过芯片 MAC。
        private bool BackupInternal(string port, string esptool, ChipInfo chip, string recordDir, Logger log,
                                    OperationResult result, out long flashBytes)
        {
            flashBytes = chip.FlashBytes;
            log.Info("芯片 MAC " + chip.Mac + "，Flash " + (chip.FlashBytes / 1024 / 1024) + "MB，开始读取整片备份");
            string dest = Path.Combine(recordDir, "原固件.bin");
            BackflashResult backup = transport.ReadFlash(port, esptool, dest, chip.FlashBytes, log);
            if (!backup.Ok)
            {
                result.Error = backup.Error;
                return false;
            }
            File.WriteAllText(Path.Combine(recordDir, "原固件-SHA256.txt"),
                "文件：" + backup.Path + Environment.NewLine +
                "字节：" + backup.Bytes + Environment.NewLine +
                "SHA256：" + backup.Sha256 + Environment.NewLine +
                "芯片 MAC：" + chip.Mac + Environment.NewLine +
                "说明：整片 Flash 备份，可用于恢复；请勿发给他人，其中可能含配对地址。" + Environment.NewLine,
                new System.Text.UTF8Encoding(false));
            result.Step("原固件备份完成并已计算 SHA256：" + backup.Sha256);
            log.Info("备份完成：" + backup.Bytes + " 字节，SHA256 " + backup.Sha256);
            return true;
        }

        // 两块板同时连接：分别核对芯片身份，只改对端地址，一次完成双方配对与读回核验。
        // 已指向正确对端的一端跳过写入与重启；重试时只处理尚未完成的一端。
        public OperationResult PairTwoBoards(PortScan boatScan, PortScan remoteScan)
        {
            OperationResult result = new OperationResult();
            if (boatScan == null || boatScan.Port == null || remoteScan == null || remoteScan.Port == null)
            {
                result.Error = "请分别选择小船和遥控器的串口";
                return result;
            }
            string boatPort = boatScan.Port.Name;
            string remotePort = remoteScan.Port.Name;
            if (string.Equals(boatPort, remotePort, StringComparison.OrdinalIgnoreCase))
            {
                result.Error = "小船和遥控器不能选择同一个串口（" + boatPort + "）";
                return result;
            }
            if (RejectIfLocked(boatPort, result) || RejectIfLocked(remotePort, result)) return result;
            if (!AcknowledgeSafety()) { result.Error = "已取消：尚未断开动力电池"; return result; }

            string esptool = EsptoolOf(selectedSuite);
            ProbeResult boatProbe = transport.Probe(boatPort, 5000);
            if (!boatProbe.Ok) { result.Error = "读取小船（" + boatPort + "）身份失败：" + boatProbe.Error; return result; }
            ProbeResult remoteProbe = transport.Probe(remotePort, 5000);
            if (!remoteProbe.Ok) { result.Error = "读取遥控器（" + remotePort + "）身份失败：" + remoteProbe.Error; return result; }

            BoardIdentity boat = boatProbe.Identity;
            BoardIdentity remote = remoteProbe.Identity;
            if (boat.Mac == remote.Mac)
            {
                result.Error = "两次读到的芯片 MAC 相同（" + boat.Mac + "），这是同一块板，已停止。串口号不同不代表是两块板。";
                return result;
            }
            if (boat.Role != BoardRole.Boat)
            {
                result.Error = "小船串口 " + boatPort + " 读到的角色是" + boat.RoleText + "（MAC " + boat.Mac + "），请调换串口选择。";
                return result;
            }
            if (remote.Role != BoardRole.Remote)
            {
                result.Error = "遥控器串口 " + remotePort + " 读到的角色是" + remote.RoleText + "（MAC " + remote.Mac + "），请调换串口选择。";
                return result;
            }
            if (!PresetAllows(BoardRole.Boat, boat.Mac, result)) return result;
            if (!PresetAllows(BoardRole.Remote, remote.Mac, result)) return result;
            // 记忆中的角色若与板上角色冲突，说明换插或曾刷错固件，必须人工确认，不能继续自动写入。
            BoardRole boatRemembered = roles.RoleOf(boat.Mac);
            BoardRole remoteRemembered = roles.RoleOf(remote.Mac);
            if (boatRemembered != BoardRole.Unknown && boatRemembered != BoardRole.Boat)
            {
                result.Error = "本机记忆 MAC " + boat.Mac + " 是" + RoleNames.Chinese(boatRemembered) + "，但板上是" + boat.RoleText + "，已停止。";
                return result;
            }
            if (remoteRemembered != BoardRole.Unknown && remoteRemembered != BoardRole.Remote)
            {
                result.Error = "本机记忆 MAC " + remote.Mac + " 是" + RoleNames.Chinese(remoteRemembered) + "，但板上是" + remote.RoleText + "，已停止。";
                return result;
            }

            // 配对只检查“能否互相对上”：配对协议版本相同、转向契约相同、双方都是本项目固件。
            // 目标转速/套装版本不同不算不兼容——590 小船配同一 20° 遥控器是允许的组合。
            string compat;
            if (!PairCompatibility.Check(boat, remote, out compat))
            {
                result.Error = "两块板不能直接配对：" + compat;
                return result;
            }

            bool boatAlready = boat.Peer == remote.Mac;
            bool remoteAlready = remote.Peer == boat.Mac;

            PairPlan plan = new PairPlan();
            plan.BoatPort = boatPort;
            plan.RemotePort = remotePort;
            plan.Boat = boat;
            plan.Remote = remote;
            plan.SkipBoat = boatAlready;
            plan.SkipRemote = remoteAlready;
            plan.Description = compat;

            if (boatAlready && remoteAlready)
            {
                roles.Remember(boat.Mac, BoardRole.Boat, boatPort, "配对已一致");
                roles.Remember(remote.Mac, BoardRole.Remote, remotePort, "配对已一致");
                result.Ok = true;
                result.Summary = "两块板已经互相配对，本次没有改写任何配置。";
                result.Summary += Environment.NewLine + "小船 " + boat.Mac + " ←→ 遥控器 " + remote.Mac;
                result.Step("读回确认：小船对端 " + boat.Peer + "，遥控器对端 " + remote.Peer);
                return result;
            }
            if (!ui.ConfirmPair(plan)) { result.Error = "已取消，没有修改任何配对"; return result; }

            result.RecordDir = paths.NewSession("配对");
            Logger log = new Logger(Path.Combine(result.RecordDir, "操作记录.txt"), ui.Report);
            log.Info("小船：" + boatPort + " / " + boat.Mac + "：" + boat.PeerText +
                     (boatAlready ? "（已正确，跳过）" : ""));
            log.Info("遥控器：" + remotePort + " / " + remote.Mac + "：" + remote.PeerText +
                     (remoteAlready ? "（已正确，跳过）" : ""));
            log.Info("将把小船对端设为 " + remote.Mac + "，遥控器对端设为 " + boat.Mac);
            log.Info("兼容性：" + compat);

            // 写入前再次核对两端身份（换插防护）。
            ProbeResult reBoat = transport.Probe(boatPort, 5000);
            if (!PeerPrecheck(reBoat, boatPort, boat, "小船", result)) return result;
            ProbeResult reRemote = transport.Probe(remotePort, 5000);
            if (!PeerPrecheck(reRemote, remotePort, remote, "遥控器", result)) return result;
            if (reBoat.Identity.Mac == reRemote.Identity.Mac)
            {
                result.Error = "写入前复核发现两端 MAC 相同（" + reBoat.Identity.Mac + "），已停止。";
                return result;
            }

            int needed = (boatAlready ? 0 : 1) + (remoteAlready ? 0 : 1);
            int done = 0;
            bool boatDone = boatAlready;
            bool remoteDone = remoteAlready;
            // 每端保留状态机结论：只有 LocalVerified 才算完成。
            PairOutcome boatOutcome = boatAlready
                ? PairOutcome.Skipped(PairLinkState.LocalVerified, "对端已正确，跳过绑定") : null;
            PairOutcome remoteOutcome = remoteAlready
                ? PairOutcome.Skipped(PairLinkState.LocalVerified, "对端已正确，跳过绑定") : null;

            string fwidProblem;
            string boatFwid = ExpectedFwid(BoardRole.Boat, boat, out fwidProblem);
            if (boatFwid == null) { result.Error = "不能配对：小船" + fwidProblem; return result; }
            string remoteFwid = ExpectedFwid(BoardRole.Remote, remote, out fwidProblem);
            if (remoteFwid == null) { result.Error = "不能配对：遥控器" + fwidProblem; return result; }

            if (!boatAlready)
            {
                PairWriteResult boatWrite = transport.WritePair(boatPort, remote.Mac, boat, boatFwid, log);
                boatOutcome = PairOutcome.From(boatWrite);
                RecordEvents(result, "小船", boatWrite.Events);
                if (boatWrite.Ok)
                {
                    done++;
                    boatDone = true;
                    result.Step("小船：本端配置已核对，对端 " + remote.Mac + "（本机 " + boat.Mac + "）");
                    roles.Remember(boat.Mac, BoardRole.Boat, boatPort, "配对 " + remote.Mac);
                    log.Info("小船配对配置已核对");
                }
                else
                {
                    log.Info("小船未完成：" + boatWrite.Message);
                }
            }
            else
            {
                result.Step("小船：对端已经是 " + remote.Mac + "，跳过绑定与重启");
            }

            if (!remoteAlready)
            {
                PairWriteResult remoteWrite = transport.WritePair(remotePort, boat.Mac, remote, remoteFwid, log);
                remoteOutcome = PairOutcome.From(remoteWrite);
                RecordEvents(result, "遥控器", remoteWrite.Events);
                if (remoteWrite.Ok)
                {
                    done++;
                    remoteDone = true;
                    result.Step("遥控器：本端配置已核对，对端 " + boat.Mac + "（本机 " + remote.Mac + "）");
                    roles.Remember(remote.Mac, BoardRole.Remote, remotePort, "配对 " + boat.Mac);
                    log.Info("遥控器配对配置已核对");
                }
                else
                {
                    log.Info("遥控器未完成：" + remoteWrite.Message);
                }
            }
            else
            {
                result.Step("遥控器：对端已经是 " + boat.Mac + "，跳过绑定与重启");
            }

            if (!boatDone || !remoteDone)
            {
                string pending = "";
                if (!boatDone) pending += "小船";
                if (!remoteDone) pending += (pending.Length > 0 ? "、" : "") + "遥控器";
                string detail = "";
                if (!boatDone) detail += "小船：" + PairLinkText.Chinese(boatOutcome.State) + " —— " + boatOutcome.Message + Environment.NewLine;
                if (!remoteDone) detail += "遥控器：" + PairLinkText.Chinese(remoteOutcome.State) + " —— " + remoteOutcome.Message + Environment.NewLine;
                result.Partial = true;
                result.Error = "尚未完成：还需处理" + pending;
                string progress = "已完成 " + done + "/" + needed;
                if (boatAlready) progress += "，小船本来已正确";
                if (remoteAlready) progress += "，遥控器本来已正确";
                result.Summary = "部分完成（" + progress + "）" + Environment.NewLine + detail;
                bool needsPowerCycle = PairLinkText.NeedsPowerCycleVerify(boatOutcome.State) ||
                                       PairLinkText.NeedsPowerCycleVerify(remoteOutcome.State);
                if (needsPowerCycle)
                {
                    result.Summary += "未完成的一端处于“" +
                                      (PairLinkText.NeedsPowerCycleVerify(boatOutcome.State)
                                          ? PairLinkText.Chinese(boatOutcome.State)
                                          : PairLinkText.Chinese(remoteOutcome.State)) +
                                      "”。请断开动力、重新上电，再执行只读核验；不要把这里当作成功。" + Environment.NewLine;
                }
                result.Summary += "两端现在可能暂时无法通信。重试时先查询，不会盲目重发绑定；" +
                                  "已正确的一端不会重写。";
                return result;
            }

            // 最终两端再读一次，确认 MAC、角色、对端地址，以及本轮清单的 FWID。
            ProbeResult finalBoat = transport.Probe(boatPort, 5000);
            ProbeResult finalRemote = transport.Probe(remotePort, 5000);
            if (!finalBoat.Ok || !finalRemote.Ok)
            {
                result.Partial = true;
                result.Error = "两端写入均已完成，但最终复核时有板子没有回应。";
                result.Summary = "部分完成：本次需要改的一端都已核对，但最终复核失败（" +
                                 (finalBoat.Ok ? "" : "小船无回应；") + (finalRemote.Ok ? "" : "遥控器无回应；") +
                                 "）。请重新连接后点“刷新列表”核对，再视情况重试。";
                return result;
            }

            string finalProblem = FinalCheck(finalBoat.Identity, remote.Mac, boatFwid, "小船") ??
                                  FinalCheck(finalRemote.Identity, boat.Mac, remoteFwid, "遥控器");
            if (finalProblem != null)
            {
                result.Partial = true;
                result.Error = "最终复核未通过：" + finalProblem;
                result.Summary = "部分完成：写入已保存，但最终复核未通过（" + finalProblem +
                                 "）。请断开动力重新上电后执行只读核验。";
                return result;
            }

            result.Ok = true;
            result.Summary = "双端配对配置已核对。";
            result.Summary += Environment.NewLine + "小船 " + boat.Mac + " ←→ 遥控器 " + remote.Mac;
            if (boatAlready || remoteAlready)
                result.Summary += Environment.NewLine + "本次跳过已正确的一端，只写了需要改的一端。";
            result.Summary += Environment.NewLine + "只修改了保存的对端地址，没有切换固件套装、没有重刷程序。";
            result.Summary += Environment.NewLine + "兼容性：" + compat;
            result.Summary += Environment.NewLine + "MAC 过滤不是加密认证，不能抵御刻意伪造 MAC。";
            result.Summary += Environment.NewLine + "本结果只代表两端配对配置一致，不代表无线链路或舵机已验证。";
            result.Summary += Environment.NewLine + "记录目录：" + result.RecordDir;
            return result;
        }

        private static void RecordEvents(OperationResult result, string label, List<string> events)
        {
            if (events == null) return;
            foreach (string e in events) result.Step(label + "过程：" + e);
        }

        // 绑定只改 PEER，不改 FWID。所以核验的不变量是“绑定前后 FWID 不变”，
        // 而且该 FWID 必须是固件库里认识的、转向契约一致的固件。
        // 这样：本轮 r7 包只含 r7，等于要求 r7；跨套装（590 小船 + 同一 20° 遥控器）
        // 也不会被强制重刷——版本号不同不代表不能配对。
        private string ExpectedFwid(BoardRole role, BoardIdentity current, out string problem)
        {
            problem = "";
            if (current == null || !current.HasFirmwareId)
            {
                problem = "没有固件身份（FWID），无法确认协议与转向契约；请先升级。";
                return null;
            }
            if (library == null || library.Suites.Count == 0)
            {
                problem = "固件库为空，无法确认该固件是否属于已知发布。";
                return null;
            }
            FwidInfo actual = FwidInfo.Parse(current.FirmwareId);
            if (!actual.Parsed)
            {
                problem = "固件身份无法解析（" + current.FirmwareId + "）。";
                return null;
            }
            // 优先：与当前所选（或任一可用）套装完全一致 —— 本轮 r7 走这条。
            foreach (FirmwareSuite suite in library.Suites)
            {
                if (!suite.Usable) continue;
                FirmwareSpec spec = suite.SpecFor(role);
                if (spec == null) continue;
                if (string.Equals(spec.Fwid, current.FirmwareId, StringComparison.Ordinal))
                    return spec.Fwid;
            }
            // 其次：该 FWID 本身必须出现在固件库某个可用套装里（跨套装允许，但必须是已知发布）。
            // 不能只凭“同转向契约”放行——未知 FWID 可能来自别的项目或未经发布的分支。
            foreach (FirmwareSuite suite in library.Suites)
            {
                if (!suite.Usable) continue;
                FirmwareSpec spec = suite.SpecFor(role);
                if (spec == null) continue;
                if (!string.Equals(spec.Fwid, current.FirmwareId, StringComparison.Ordinal)) continue;
                FwidInfo known = FwidInfo.Parse(spec.Fwid);
                if (known.Role == role && FwidInfo.SameSteeringContract(known, actual))
                    return current.FirmwareId;
            }
            problem = "固件 " + current.FirmwareId + " 不在本工具固件库中，或转向契约与库内固件不一致；" +
                      "请先烧录对应发布固件再配对。";
            return null;
        }

        // 最终查询：必须是 JAYv4+ 完整身份、角色正确、MAC 一致、PEER 正确、FWID 等于本轮清单。
        private static string FinalCheck(BoardIdentity id, string expectedPeer, string expectedFwid, string label)
        {
            if (id == null) return label + "没有读到身份";
            if (id.IdentityLineVersion < 4) return label + "身份行不是 JAYv4";
            if (!id.HasFirmwareId) return label + "身份行缺少 FWID";
            if (string.IsNullOrEmpty(id.Mac)) return label + "缺少有效本机 MAC";
            if (!string.IsNullOrEmpty(expectedFwid) &&
                !string.Equals(id.FirmwareId, expectedFwid, StringComparison.Ordinal))
                return label + "固件身份 " + id.FirmwareId + " 与本轮清单不一致";
            if (!string.Equals(id.Peer, MacText.NormalizePeer(expectedPeer), StringComparison.Ordinal))
                return label + "对端是 " + id.PeerText + "，不是 " + expectedPeer;
            return null;
        }

        private bool PeerPrecheck(ProbeResult probe, string port, BoardIdentity expected, string label, OperationResult result)
        {
            if (!probe.Ok)
            {
                result.Error = "写入前复核" + label + "（" + port + "）失败：读不到身份，已停止，没有修改任何配对。";
                return false;
            }
            if (probe.Identity.Mac != expected.Mac || probe.Identity.Role != expected.Role)
            {
                result.Error = "写入前复核" + label + "（" + port + "）发现板子已经变化：现在 " +
                               probe.Identity.Mac + "/" + probe.Identity.RoleText + "，选定时 " +
                               expected.Mac + "/" + expected.RoleText + "。已停止，没有修改任何配对。";
                return false;
            }
            return true;
        }
    }

    // 配对兼容性：只看两端能否在协议层对上，不看目标转速或套装版本。
    public static class PairCompatibility
    {
        public static bool Check(BoardIdentity boat, BoardIdentity remote, out string description)
        {
            description = "";
            if (boat == null || remote == null)
            {
                description = "缺少任一端身份";
                return false;
            }
            if (!boat.Responded || !remote.Responded)
            {
                description = "至少一端程序过旧，未回应身份查询；请先升级到本工具支持的固件再配对";
                return false;
            }
            if (!boat.HasFirmwareId || !remote.HasFirmwareId)
            {
                description = "至少一端缺少固件身份（FWID），无法确认协议与转向契约；请先升级";
                return false;
            }
            if (boat.Role != BoardRole.Boat || remote.Role != BoardRole.Remote)
            {
                description = "角色不符：需要一块小船和一块遥控器";
                return false;
            }
            if (boat.PairProtocol <= 0 || remote.PairProtocol <= 0)
            {
                description = "至少一端没有报告配对协议版本";
                return false;
            }
            if (boat.PairProtocol != remote.PairProtocol)
            {
                description = "配对协议版本不同（小船 v" + boat.PairProtocol + "，遥控器 v" + remote.PairProtocol +
                              "），串口命令不兼容；请统一到同一次发布";
                return false;
            }
            FwidInfo boatInfo = FwidInfo.Parse(boat.FirmwareId);
            FwidInfo remoteInfo = FwidInfo.Parse(remote.FirmwareId);
            if (!boatInfo.Parsed || !remoteInfo.Parsed)
            {
                description = "无法从 FWID 解析转向契约（小船 " + boat.FirmwareId + "，遥控器 " + remote.FirmwareId + "）";
                return false;
            }
            if (!FwidInfo.SameSteeringContract(boatInfo, remoteInfo))
            {
                description = "转向契约不同：小船 " + boatInfo.SteerDeg + "°（" + boatInfo.AngleMin + "～" +
                              boatInfo.AngleMax + "），遥控器 " + remoteInfo.SteerDeg + "°（" + remoteInfo.AngleMin +
                              "～" + remoteInfo.AngleMax + "）";
                return false;
            }
            if (remoteInfo.TargetRpm > 0 && boatInfo.TargetRpm > 0 && remoteInfo.TargetRpm != boatInfo.TargetRpm)
            {
                // 遥控器的转速字段不参与小船调速，这里只提示不阻止。
                description = "转向契约一致（" + boatInfo.SteerDeg + "°，" + boatInfo.AngleMin + "～" +
                              boatInfo.AngleMax + "°）；小船目标 " + boatInfo.TargetRpm + " 转，遥控器固件标称 " +
                              remoteInfo.TargetRpm + " 转（遥控器不控制油门，可正常配对）";
                return true;
            }
            description = "配对协议 v" + boat.PairProtocol + "，转向契约一致（" + boatInfo.SteerDeg + "°，" +
                          boatInfo.AngleMin + "～" + boatInfo.AngleMax + "°）";
            return true;
        }
    }
}
