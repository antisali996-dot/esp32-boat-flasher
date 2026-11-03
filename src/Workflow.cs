using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

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
        private readonly FirmwareLibrary library;
        private readonly RoleStore roles;
        private readonly IBoardTransport transport;
        private readonly IClock clock;
        private readonly IUserInteraction ui;
        private bool safetyAcknowledged;
        private FirmwareSuite selectedSuite;
        private bool allowDowngrade;

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

        // 当前选中的固件套装；为空表示没有可用套装，所有写入操作都必须拒绝。
        public FirmwareSuite SelectedSuite
        {
            get { return selectedSuite; }
            set { selectedSuite = value; }
        }

        public FirmwareLibrary Library { get { return library; } }

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
                return "与所选套装一致，无需重复烧录";

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
            return VersionPolicy.Check(scan == null ? null : scan.Identity, spec);
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
        public OperationResult FlashCurrent(PortScan scan, BoardRole chosenRole, bool doBackup, bool forceReflash)
        {
            OperationResult result = new OperationResult();
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
            if (scan == null || scan.Port == null) { result.Error = "没有选择串口"; return result; }
            if (!AcknowledgeSafety()) { result.Error = "已取消：尚未断开动力电池"; return result; }

            string port = scan.Port.Name;
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

            FirmwareCheck check = VersionPolicy.Check(identity, spec);
            if (check.State == FirmwareState.Current && !forceReflash)
            {
                result.Ok = true;
                result.Summary = "这块板已经与所选套装【" + suite.DisplayName + "】完全一致（" + identity.SteeringText +
                                 "，FWID " + identity.FirmwareId + "），无需重复烧录。";
                result.Summary += Environment.NewLine + "如果确实要重刷，请勾选“即使已匹配也重刷”后再点“烧录当前板”。";
                result.Step("固件身份一致：" + identity.FirmwareId);
                roles.Remember(identity.Mac, role, port, "已是最新，未重写");
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
            string boatError = "";
            string remoteError = "";

            if (!boatAlready)
            {
                PairWriteResult boatWrite = transport.WritePair(boatPort, remote.Mac, boat, log);
                if (boatWrite.Ok)
                {
                    done++;
                    boatDone = true;
                    result.Step("小船：已保存并重启读回，对端 " + remote.Mac + "（本机 " + boat.Mac + "）");
                    roles.Remember(boat.Mac, BoardRole.Boat, boatPort, "配对 " + remote.Mac);
                    log.Info("小船配对完成并通过读回核验");
                }
                else
                {
                    boatError = boatWrite.Error;
                    log.Info("小船配对失败：" + boatError);
                }
            }
            else
            {
                result.Step("小船：对端已经是 " + remote.Mac + "，跳过写入与重启");
            }

            if (!remoteAlready)
            {
                PairWriteResult remoteWrite = transport.WritePair(remotePort, boat.Mac, remote, log);
                if (remoteWrite.Ok)
                {
                    done++;
                    remoteDone = true;
                    result.Step("遥控器：已保存并重启读回，对端 " + boat.Mac + "（本机 " + remote.Mac + "）");
                    roles.Remember(remote.Mac, BoardRole.Remote, remotePort, "配对 " + boat.Mac);
                    log.Info("遥控器配对完成并通过读回核验");
                }
                else
                {
                    remoteError = remoteWrite.Error;
                    log.Info("遥控器配对失败：" + remoteError);
                }
            }
            else
            {
                result.Step("遥控器：对端已经是 " + boat.Mac + "，跳过写入与重启");
            }

            if (!boatDone || !remoteDone)
            {
                string pending = "";
                if (!boatDone) pending += "小船";
                if (!remoteDone) pending += (pending.Length > 0 ? "、" : "") + "遥控器";
                string detail = "";
                if (!boatDone && boatError.Length > 0) detail += "小船失败原因：" + boatError + "\n";
                if (!remoteDone && remoteError.Length > 0) detail += "遥控器失败原因：" + remoteError + "\n";
                result.Partial = true;
                result.Error = "尚未完成：还需处理" + pending;
                string progress = "已完成 " + done + "/" + needed;
                if (boatAlready) progress += "，小船本来已正确";
                if (remoteAlready) progress += "，遥控器本来已正确";
                result.Summary = "部分完成（" + progress + "）：" + detail;
                result.Summary += "两端现在可能暂时无法通信。排除连接问题后，再次点击“配对两块板”即可重试；" +
                                  "重试只会处理尚未完成的一端，已正确的一端不会重写。";
                return result;
            }

            // 最终两端再读一次，确认 MAC、角色与对端地址。
            ProbeResult finalBoat = transport.Probe(boatPort, 5000);
            ProbeResult finalRemote = transport.Probe(remotePort, 5000);
            if (!finalBoat.Ok || !finalRemote.Ok)
            {
                result.Partial = true;
                result.Error = "两端写入均已完成，但最终复核时有板子没有回应。";
                result.Summary = "部分完成：本次需要改的一端都已保存并读回，但最终复核失败（" +
                                 (finalBoat.Ok ? "" : "小船无回应；") + (finalRemote.Ok ? "" : "遥控器无回应；") +
                                 "）。请重新连接后点“刷新列表”核对，再视情况重试。";
                return result;
            }
            if (finalBoat.Identity.Peer != remote.Mac || finalRemote.Identity.Peer != boat.Mac ||
                finalBoat.Identity.Mac != boat.Mac || finalRemote.Identity.Mac != remote.Mac)
            {
                result.Partial = true;
                result.Error = "最终复核的对端地址与本次写入不一致。";
                result.Summary = "部分完成：写入已保存，但最终复核不一致。小船对端 " + finalBoat.Identity.PeerText +
                                 "，遥控器对端 " + finalRemote.Identity.PeerText + "。请重试配对。";
                return result;
            }

            result.Ok = true;
            result.Summary = "配对完成：两端对端地址一致，重启与读回核验均已通过。";
            result.Summary += Environment.NewLine + "小船 " + boat.Mac + " ←→ 遥控器 " + remote.Mac;
            if (boatAlready || remoteAlready)
                result.Summary += Environment.NewLine + "本次跳过已正确的一端，只写了需要改的一端。";
            result.Summary += Environment.NewLine + "只修改了保存的对端地址，没有切换固件套装、没有重刷程序。";
            result.Summary += Environment.NewLine + "兼容性：" + compat;
            result.Summary += Environment.NewLine + "MAC 过滤不是加密认证，不能抵御刻意伪造 MAC。";
            result.Summary += Environment.NewLine + "记录目录：" + result.RecordDir;
            return result;
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
