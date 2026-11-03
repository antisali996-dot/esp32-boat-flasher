using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace BoatTool
{
    public static class SimulatedTests
    {
        private static int passed;
        private static int failed;
        private static string workRoot;

        private const string BoatMac = "24:62:AB:F1:36:64";
        private const string OldBoatMac = "24:62:AB:F0:72:94";
        private const string RemoteMac = "24:62:AB:EF:CF:84";

        public static int Run(string[] args)
        {
            workRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test-work", "boattool-sim-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workRoot);
            Console.WriteLine("模拟测试目录：" + workRoot);
            Console.WriteLine("（全部使用模拟串口与模拟板子，不打开任何真实串口，不连接设备）");
            Console.WriteLine();

            try
            {
                TestIdentityParsing();
                TestVersionPolicy();
                TestRoleStoreByMacOnly();
                TestPortSwapSamePortDifferentBoards();
                TestRoleMemoryAndFirstTimeSelection();
                TestNoBackupByDefault();
                TestOptionalBackupStillWorks();
                TestWriteFailureIsNotSuccess();
                TestFlashRechecksIdentity();
                TestPairBothBoards();
                TestPairRejectsSamePortAndSameMac();
                TestPairPartialFailureAndRetry();
                TestPairRejectsOldFirmware();
                TestSafetyPromptOnlyOnce();
                TestCatalogSelfCheck();
                TestIdentityFromChipForOldBoard();
                TestFlashRequiresReadBack();
                TestFlashKeepsNvsWording();
            }
            finally
            {
                try { Directory.Delete(workRoot, true); } catch { }
            }

            Console.WriteLine();
            Console.WriteLine("通过 " + passed + " 项，失败 " + failed + " 项");
            return failed == 0 ? 0 : 1;
        }

        private static void Check(string name, bool condition, string detail)
        {
            if (condition) { passed++; Console.WriteLine("  [通过] " + name); }
            else { failed++; Console.WriteLine("  [失败] " + name + " —— " + detail); }
        }

        private static string CatalogFwid(BoardRole role)
        {
            return role == BoardRole.Boat
                ? "JAYFWB3-S20-A70-110-U1278-1500-1722-R580-C580pi2"
                : "JAYFWR3-S20-A70-110-U1278-1500-1722-D1750-2150";
        }

        private static FirmwareCatalog MakeCatalog()
        {
            string dir = Path.Combine(workRoot, "firmware-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            FirmwareCatalog catalog = new FirmwareCatalog();
            catalog.Schema = 3;
            catalog.Version = "20261103-20deg-r3";
            catalog.SteerDeg = 20;
            catalog.AngleMin = 70;
            catalog.AngleMax = 110;
            catalog.CenterUs = 1500;
            catalog.PulseMinUs = 1278;
            catalog.PulseMaxUs = 1722;
            catalog.PairProtocol = 2;
            catalog.TargetRpm = 580;
            foreach (BoardRole role in new BoardRole[] { BoardRole.Boat, BoardRole.Remote })
            {
                FirmwareSpec spec = new FirmwareSpec();
                spec.Role = role;
                spec.Fwid = CatalogFwid(role);
                spec.Version = catalog.Version;
                spec.SteerDeg = 20;
                spec.BootloaderOffset = 0x1000;
                spec.BootloaderPath = Path.Combine(dir, "boot.bin");
                spec.PartitionsOffset = 0x8000;
                spec.PartitionsPath = Path.Combine(dir, "part.bin");
                spec.BootApp0Offset = 0xE000;
                spec.BootApp0Path = Path.Combine(dir, "boot_app0.bin");
                spec.AppOffset = 0x10000;
                spec.AppPath = Path.Combine(dir, role == BoardRole.Boat ? "boat.bin" : "remote.bin");
                catalog.Roles[role] = spec;
            }
            return catalog;
        }

        private static Workflow MakeWorkflow(SimulatedTransport transport, SimUi ui, RoleStore store)
        {
            AppPaths paths = new AppPaths();
            paths.AppRoot = workRoot;
            paths.WritableRoot = workRoot;
            paths.ConfigDir = Path.Combine(workRoot, "配置");
            paths.RecordsDir = Path.Combine(workRoot, "记录");
            paths.RoleStorePath = Path.Combine(paths.ConfigDir, "角色记忆.json");
            if (store == null) store = new RoleStore(paths.RoleStorePath);
            return new Workflow(paths, MakeCatalog(), store, transport, new SimClock(), ui);
        }

        private static SimBoard NewBoard(string mac, BoardRole role, bool current = true)
        {
            SimBoard board = new SimBoard();
            board.Mac = mac;
            board.Role = role;
            board.SteerDeg = current ? 20 : 30;
            board.Fwid = current ? CatalogFwid(role) : "";
            board.Version = current ? "20261103-20deg-r3" : "";
            board.Legacy = !current;
            return board;
        }

        private static SimulatedTransport TwoBoards(out SimBoard boat, out SimBoard remote)
        {
            SimulatedTransport transport = new SimulatedTransport();
            boat = NewBoard(BoatMac, BoardRole.Boat);
            remote = NewBoard(RemoteMac, BoardRole.Remote);
            transport.Boards.Add(boat);
            transport.Boards.Add(remote);
            transport.Plug("COM8", BoatMac);
            transport.Plug("COM9", RemoteMac);
            return transport;
        }

        private static void TestIdentityParsing()
        {
            Console.WriteLine("1. 身份行解析");
            BoardIdentity v3 = IdentityParser.FromLine(
                "JAYv3 ROLE=BOAT MAC=24:62:AB:F1:36:64 PEER=24:62:AB:EF:CF:84 PAIR=2 STEER=20 FWID=JAYFWB3-S20-A70-110-U1278-1500-1722-R580-C580pi2 VER=20261103-20deg-r3");
            Check("解析 v3 身份", v3 != null && v3.Mac == BoatMac && v3.Role == BoardRole.Boat &&
                  v3.Peer == RemoteMac && v3.SteeringAngle == 20 && v3.HasFirmwareId && v3.PairProtocol == 2,
                "v3 解析结果不符合预期");
            Check("v3 固件版本文本", v3 != null && v3.FirmwareVersion == "20261103-20deg-r3", "版本文本错误");

            BoardIdentity old30 = IdentityParser.FromLine(
                "JAY ROLE=BOAT MAC=24:62:AB:F0:72:94 PEER=24:62:AB:EF:CF:84 VERSION=2");
            Check("解析旧固件无 FWID", old30 != null && !old30.HasFirmwareId && old30.SteeringAngle < 0,
                "旧固件被误判为有身份");

            BoardIdentity legacyMarked = IdentityParser.FromLine(
                "JAY ROLE=BOAT MAC=24:62:AB:F0:72:94 PEER=24:62:AB:EF:CF:84 VERSION=2 STEER=20");
            Check("旧固件自称 STEER=20 仍无 FWID", legacyMarked != null && legacyMarked.SteeringAngle == 20 &&
                  !legacyMarked.HasFirmwareId, "自带 STEER=20 的旧行必须有 STEER 但没有 FWID");

            BoardIdentity multi = IdentityParser.FromLines(new List<string>
            {
                "JAY ROLE=BOAT MAC=24:62:AB:F1:36:64 PEER=00:00:00:00:00:00 VERSION=2 STEER=20",
                "JAYv3 ROLE=BOAT MAC=24:62:AB:F1:36:64 PEER=00:00:00:00:00:00 PAIR=2 STEER=20 FWID=X VER=Y"
            });
            Check("多行优先选带 FWID 的行", multi != null && multi.HasFirmwareId, "没有优先选择 v3 行");

            Check("拒绝非法 MAC", IdentityParser.FromLine("JAY ROLE=BOAT MAC=25:62:AB:F1:36:64 PEER=00:00:00:00:00:00") == null,
                "多播地址被当成合法芯片 MAC");
            Check("MAC 规范化", MacText.Normalize("24:62:ab:f1:36:64") == BoatMac, "大小写规范化失败");
            Console.WriteLine();
        }

        private static void TestVersionPolicy()
        {
            Console.WriteLine("2. 版本判断：FWID 为准，配对协议版本不作依据");
            FirmwareSpec boatSpec = MakeCatalog().ForRole(BoardRole.Boat);

            BoardIdentity current = IdentityParser.FromLine(
                "JAYv3 ROLE=BOAT MAC=24:62:AB:F1:36:64 PEER=00:00:00:00:00:00 PAIR=2 STEER=20 FWID=" +
                CatalogFwid(BoardRole.Boat) + " VER=20261103-20deg-r3");
            Check("同 FWID 判为最新", VersionPolicy.Check(current, boatSpec).State == FirmwareState.Current,
                "最新固件被误判");

            BoardIdentity old30 = IdentityParser.FromLine(
                "JAY ROLE=BOAT MAC=24:62:AB:F0:72:94 PEER=00:00:00:00:00:00 VERSION=2 STEER=20");
            Check("旧30度即使 STEER=20 也要升级",
                VersionPolicy.Check(old30, boatSpec).State == FirmwareState.UpgradeRequired, "旧30度被跳过升级");

            BoardIdentity otherFwid = IdentityParser.FromLine(
                "JAYv3 ROLE=BOAT MAC=24:62:AB:F0:72:94 PEER=00:00:00:00:00:00 PAIR=2 STEER=20 FWID=JAYFWB3-S30-A60-120-U1278-1500-1722-R580-C580pi2 VER=x");
            Check("FWID 不一致必须升级", VersionPolicy.Check(otherFwid, boatSpec).State == FirmwareState.UpgradeRequired,
                "不同 FWID 被当作最新");

            BoardIdentity wrongRole = IdentityParser.FromLine(
                "JAYv3 ROLE=REMOTE MAC=24:62:AB:EF:CF:84 PEER=00:00:00:00:00:00 PAIR=2 STEER=20 FWID=" +
                CatalogFwid(BoardRole.Remote) + " VER=x");
            Check("遥控器程序不能当小船", VersionPolicy.Check(wrongRole, boatSpec).State == FirmwareState.WrongRole,
                "角色不符未拦截");

            BoardIdentity steerMismatch = IdentityParser.FromLine(
                "JAYv3 ROLE=BOAT MAC=24:62:AB:F1:36:64 PEER=00:00:00:00:00:00 PAIR=2 STEER=30 FWID=" +
                CatalogFwid(BoardRole.Boat) + " VER=x");
            Check("STEER 标记与包不一致需升级",
                VersionPolicy.Check(steerMismatch, boatSpec).State == FirmwareState.UpgradeRequired, "角度标记不符未拦截");

            BoardIdentity protocolOnly = IdentityParser.FromLine(
                "JAY ROLE=BOAT MAC=24:62:AB:F1:36:64 PEER=00:00:00:00:00:00 VERSION=2 STEER=20");
            Check("配对协议 v2 不等于最新", VersionPolicy.Check(protocolOnly, boatSpec).State == FirmwareState.UpgradeRequired,
                "仅凭协议版本被当作最新");
            Console.WriteLine();
        }

        private static void TestRoleStoreByMacOnly()
        {
            Console.WriteLine("3. 角色记忆只认 MAC，不认串口");
            string path = Path.Combine(workRoot, "角色记忆-" + Guid.NewGuid().ToString("N") + ".json");
            RoleStore store = new RoleStore(path);
            store.Remember(BoatMac, BoardRole.Boat, "COM8", "测试");
            store.Remember(RemoteMac, BoardRole.Remote, "COM8", "测试");
            Check("同一串口可记忆两块不同板", store.RoleOf(BoatMac) == BoardRole.Boat &&
                  store.RoleOf(RemoteMac) == BoardRole.Remote, "同串口记忆冲突");

            RoleStore reloaded = new RoleStore(path);
            Check("记忆可持久化", reloaded.RoleOf(BoatMac) == BoardRole.Boat &&
                  reloaded.RoleOf(RemoteMac) == BoardRole.Remote, "重新加载后角色丢失");
            Check("未登记的 MAC 不猜角色", reloaded.RoleOf("24:62:AB:00:00:99") == BoardRole.Unknown,
                "未知 MAC 被猜成某个角色");

            File.WriteAllText(path, "{ 这不是 JSON }");
            RoleStore broken = new RoleStore(path);
            Check("记忆文件损坏时不猜角色", broken.RoleOf(BoatMac) == BoardRole.Unknown &&
                  !string.IsNullOrEmpty(broken.LoadError), "损坏文件仍返回角色");
            Console.WriteLine();
        }

        private static void TestPortSwapSamePortDifferentBoards()
        {
            Console.WriteLine("4. 串口换插：两块板轮流插入都显示 COM8");
            SimulatedTransport transport = new SimulatedTransport();
            SimBoard boat = NewBoard(BoatMac, BoardRole.Boat);
            SimBoard remote = NewBoard(RemoteMac, BoardRole.Remote);
            transport.Boards.Add(boat);
            transport.Boards.Add(remote);
            transport.Plug("COM8", BoatMac);
            SimUi ui = new SimUi();
            RoleStore store = new RoleStore(Path.Combine(workRoot, "swap-" + Guid.NewGuid().ToString("N") + ".json"));
            Workflow flow = MakeWorkflow(transport, ui, store);

            SimulationTestHelper helper = new SimulationTestHelper();
            // 船先被记住，然后换插遥控器到同一个 COM8，用“烧录当前板”时必须按 MAC 识别为遥控器。
            store.Remember(BoatMac, BoardRole.Boat, "COM8", "");
            transport.Plug("COM8", RemoteMac);
            PortScan scan = flow.Scan()[0];
            Check("同一 COM8 换插后读到的新 MAC", scan.Identity.Mac == RemoteMac, "换插后读到的仍是旧 MAC");
            Check("换插后显示记忆角色为遥控器", scan.DisplayRole == BoardRole.Remote, "换插后角色未随 MAC 更新");

            // 用户误把角色选成小船：必须以 MAC 记忆为准，不允许把遥控器当小船写。
            string recordBefore = string.Join(",", transport.Events.ToArray());
            OperationResult result = flow.FlashCurrent(scan, BoardRole.Boat, false);
            Check("换插后误选角色被拒绝", !result.Ok, "误选角色竟然成功");
            Check("误选角色时没有写入", transport.WriteFirmwareCount == 0, "误选角色仍执行了写入");
            Console.WriteLine();
        }

        private static void TestRoleMemoryAndFirstTimeSelection()
        {
            Console.WriteLine("5. 已认识按 MAC 记角色；新板必须用户选择");
            SimulatedTransport transport = new SimulatedTransport();
            SimBoard oldBoat = NewBoard(OldBoatMac, BoardRole.Boat, false);
            SimBoard remote = NewBoard(RemoteMac, BoardRole.Remote);
            transport.Boards.Add(oldBoat);
            transport.Boards.Add(remote);
            transport.Plug("COM8", OldBoatMac);
            SimUi ui = new SimUi();
            RoleStore store = new RoleStore(Path.Combine(workRoot, "first-" + Guid.NewGuid().ToString("N") + ".json"));
            Workflow flow = MakeWorkflow(transport, ui, store);

            PortScan scan = flow.Scan()[0];
            Check("新板角色显示未知/未登记", scan.RememberedRole == BoardRole.Unknown, "新板被自动登记角色");

            OperationResult noChoice = flow.FlashCurrent(scan, BoardRole.Unknown, false);
            Check("不选角色时拒绝烧录", !noChoice.Ok &&
                  noChoice.Error.IndexOf("首次使用", StringComparison.Ordinal) >= 0, "未选择用途仍然烧录");

            OperationResult chosen = flow.FlashCurrent(scan, BoardRole.Boat, false);
            Check("用户选定小船后烧录成功", chosen.Ok, "选定角色后烧录失败：" + chosen.Error);
            Check("按 MAC 记住角色", store.RoleOf(OldBoatMac) == BoardRole.Boat, "成功后未记住角色");

            // 换插同一块板到另一个串口，角色仍按 MAC 保持。
            transport.Plug("COM12", OldBoatMac);
            transport.Unplug("COM8");
            List<PortScan> rescan = flow.Scan();
            PortScan moved = rescan.Find(s => s.Port.Name == "COM12");
            Check("换到新串口后角色仍按 MAC 记忆", moved != null && moved.RememberedRole == BoardRole.Boat,
                "换串口后角色丢失");
            PortScan vacated = rescan.Find(s => s.Port.Name == "COM8");
            Check("原串口不再显示旧角色", vacated != null && !vacated.Responded &&
                  vacated.RememberedRole == BoardRole.Unknown, "空串口仍挂着旧板身份");
            Console.WriteLine();
        }

        private static void TestNoBackupByDefault()
        {
            Console.WriteLine("6. 普通烧录默认不备份");
            SimBoard boat;
            SimBoard remote;
            SimulatedTransport transport = TwoBoards(out boat, out remote);
            boat.Fwid = "";
            boat.Legacy = true;
            boat.SteerDeg = 30;
            SimUi ui = new SimUi();
            RoleStore store = new RoleStore(Path.Combine(workRoot, "nobackup-" + Guid.NewGuid().ToString("N") + ".json"));
            Workflow flow = MakeWorkflow(transport, ui, store);
            PortScan scan = flow.Scan()[0];

            OperationResult result = flow.FlashCurrent(scan, BoardRole.Boat, false);
            Check("默认不备份也完成烧录", result.Ok, "默认烧录失败：" + result.Error);
            Check("默认没有读整片 Flash", transport.ReadFlashCount == 0, "默认烧录仍强制备份");
            Check("写入仍然校验", transport.WriteFirmwareCount == 1, "没有执行写入");
            Check("界面只确认一次", ui.LastFlashPlan != null && ui.SafetyPrompts == 1, "确认次数异常");
            Check("提示里包含串口/MAC/角色", ui.LastFlashPlan != null &&
                  ui.LastFlashPlan.Port == "COM8" && ui.LastFlashPlan.Identity.Mac == BoatMac &&
                  ui.LastFlashPlan.Role == BoardRole.Boat, "确认信息缺失");
            Console.WriteLine();
        }

        private static void TestOptionalBackupStillWorks()
        {
            Console.WriteLine("7. 可选烧录前备份仍可单独使用");
            SimBoard boat;
            SimBoard remote;
            SimulatedTransport transport = TwoBoards(out boat, out remote);
            boat.Fwid = "";
            boat.Legacy = true;
            SimUi ui = new SimUi();
            Workflow flow = MakeWorkflow(transport, ui,
                new RoleStore(Path.Combine(workRoot, "opt-" + Guid.NewGuid().ToString("N") + ".json")));
            OperationResult withBackup = flow.FlashCurrent(flow.Scan()[0], BoardRole.Boat, true);
            Check("勾选后执行整片备份", withBackup.Ok && transport.ReadFlashCount == 1, "勾选备份未执行");
            Check("备份文件与校验值留档", !string.IsNullOrEmpty(transport.LastBackupPath) &&
                  File.Exists(transport.LastBackupPath) && new FileInfo(transport.LastBackupPath).Length == 4 * 1024 * 1024,
                "备份文件大小不对");

            SimulatedTransport t2 = new SimulatedTransport();
            SimBoard b2 = NewBoard(OldBoatMac, BoardRole.Boat);
            t2.Boards.Add(b2);
            t2.Plug("COM3", OldBoatMac);
            SimUi ui2 = new SimUi();
            Workflow flow2 = MakeWorkflow(t2, ui2, new RoleStore(Path.Combine(workRoot, "bk-" + Guid.NewGuid().ToString("N") + ".json")));
            OperationResult only = flow2.BackupOnly(flow2.Scan()[0]);
            Check("单独备份不写固件", only.Ok && t2.WriteFirmwareCount == 0 && t2.ReadFlashCount == 1,
                "单独备份失败或误写：" + only.Error);
            Check("单独备份前核对 MAC", ui2.LastBackupPlan != null && ui2.LastBackupPlan.Identity.Mac == OldBoatMac,
                "备份确认缺少 MAC");
            Console.WriteLine();
        }

        private static void TestWriteFailureIsNotSuccess()
        {
            Console.WriteLine("8. 写入失败不得显示成功");
            SimBoard boat;
            SimBoard remote;
            SimulatedTransport transport = TwoBoards(out boat, out remote);
            boat.Legacy = true;
            boat.Fwid = "";
            transport.FailNextWrite = 1;
            SimUi ui = new SimUi();
            Workflow flow = MakeWorkflow(transport, ui,
                new RoleStore(Path.Combine(workRoot, "fail-" + Guid.NewGuid().ToString("N") + ".json")));
            OperationResult result = flow.FlashCurrent(flow.Scan()[0], BoardRole.Boat, false);
            Check("写入失败返回失败", !result.Ok && result.Error.IndexOf("不能视为", StringComparison.Ordinal) >= 0,
                "写入失败却返回成功");

            SimulatedTransport t2 = new SimulatedTransport();
            SimBoard b2 = NewBoard(OldBoatMac, BoardRole.Boat);
            b2.Legacy = true;
            b2.Fwid = "";
            t2.Boards.Add(b2);
            t2.Plug("COM5", OldBoatMac);
            t2.ReportVerification = false;
            Workflow flow2 = MakeWorkflow(t2, new SimUi(), new RoleStore(Path.Combine(workRoot, "v-" + Guid.NewGuid().ToString("N") + ".json")));
            OperationResult noVerify = flow2.FlashCurrent(flow2.Scan()[0], BoardRole.Boat, false);
            Check("缺少校验字样也算失败", !noVerify.Ok, "没有校验也返回成功");
            Console.WriteLine();
        }

        private static void TestFlashRechecksIdentity()
        {
            Console.WriteLine("9. 烧录前身份复核（防换插）");
            SimBoard boat;
            SimBoard remote;
            SimulatedTransport transport = TwoBoards(out boat, out remote);
            boat.Legacy = true;
            boat.Fwid = "";
            SimUi ui = new SimUi();
            Workflow flow = MakeWorkflow(transport, ui,
                new RoleStore(Path.Combine(workRoot, "recheck-" + Guid.NewGuid().ToString("N") + ".json")));
            // 用户在确认对话框期间把 COM8 换成了遥控器。
            ui.OnConfirmFlash = delegate { transport.Plug("COM8", RemoteMac); };
            OperationResult result = flow.FlashCurrent(flow.Scan()[0], BoardRole.Boat, false);
            Check("确认后换插被拦截", !result.Ok && result.Error.IndexOf("写入前", StringComparison.Ordinal) >= 0,
                "换插后仍然写入");
            Check("换插后未执行写入", transport.WriteFirmwareCount == 0, "换插后仍发生写入");
            Console.WriteLine();
        }

        private static void TestPairBothBoards()
        {
            Console.WriteLine("10. 两块板同时配对：一次完成双方");
            SimBoard boat;
            SimBoard remote;
            SimulatedTransport transport = TwoBoards(out boat, out remote);
            SimUi ui = new SimUi();
            RoleStore store = new RoleStore(Path.Combine(workRoot, "pair-" + Guid.NewGuid().ToString("N") + ".json"));
            Workflow flow = MakeWorkflow(transport, ui, store);
            List<PortScan> scans = flow.Scan();
            PortScan bs = scans.Find(s => s.Port.Name == "COM8");
            PortScan rs = scans.Find(s => s.Port.Name == "COM9");
            OperationResult result = flow.PairTwoBoards(bs, rs);
            Check("双板配对成功", result.Ok, "配对失败：" + result.Error);
            Check("只改地址不重刷", transport.WriteFirmwareCount == 0 && transport.WritePairCount == 2, "配对时重刷了固件");
            Check("两端地址互指", boat.Peer == RemoteMac && remote.Peer == BoatMac, "配对地址不正确");
            Check("配对前排除了备份", transport.ReadFlashCount == 0, "配对过程做了备份");
            Check("配对后记住两端角色", store.RoleOf(BoatMac) == BoardRole.Boat && store.RoleOf(RemoteMac) == BoardRole.Remote,
                "配对后角色未记录");
            Check("只确认一次配对", ui.LastPairPlan != null && ui.LastPairPlan.BoatPort == "COM8" &&
                  ui.LastPairPlan.RemotePort == "COM9", "配对确认信息缺失");
            Console.WriteLine();
        }

        private static void TestPairRejectsSamePortAndSameMac()
        {
            Console.WriteLine("11. 配对禁止同一串口 / 同一 MAC");
            SimBoard boat;
            SimBoard remote;
            SimulatedTransport transport = TwoBoards(out boat, out remote);
            SimUi ui = new SimUi();
            Workflow flow = MakeWorkflow(transport, ui,
                new RoleStore(Path.Combine(workRoot, "same-" + Guid.NewGuid().ToString("N") + ".json")));
            List<PortScan> scans = flow.Scan();
            PortScan bs = scans.Find(s => s.Port.Name == "COM8");
            OperationResult samePort = flow.PairTwoBoards(bs, bs);
            Check("同一串口被拒绝", !samePort.Ok && transport.WritePairCount == 0, "同串口仍写入");

            // 两块不同串口但其实是同一块芯片（例如用集线器或同 MAC 克隆）。
            transport.Plug("COM9", BoatMac);
            PortScan rs = flow.Scan().Find(s => s.Port.Name == "COM9");
            OperationResult sameMac = flow.PairTwoBoards(bs, rs);
            Check("同一 MAC 被拒绝", !sameMac.Ok && sameMac.Error.IndexOf("同一块板", StringComparison.Ordinal) >= 0,
                "同 MAC 未被拦截");
            Check("同 MAC 时没有写入", transport.WritePairCount == 0, "同 MAC 仍写入配对");
            Console.WriteLine();
        }

        private static void TestPairPartialFailureAndRetry()
        {
            Console.WriteLine("12. 配对部分失败，说明进度并允许重试");
            SimBoard boat;
            SimBoard remote;
            SimulatedTransport transport = TwoBoards(out boat, out remote);
            remote.SaveNoAck = true;
            SimUi ui = new SimUi();
            Workflow flow = MakeWorkflow(transport, ui,
                new RoleStore(Path.Combine(workRoot, "partial-" + Guid.NewGuid().ToString("N") + ".json")));
            List<PortScan> scans = flow.Scan();
            OperationResult first = flow.PairTwoBoards(scans.Find(s => s.Port.Name == "COM8"),
                                                       scans.Find(s => s.Port.Name == "COM9"));
            Check("部分失败被标记", !first.Ok && first.Partial, "部分失败未标记");
            Check("说明完成到哪一步", first.Summary.IndexOf("部分完成", StringComparison.Ordinal) >= 0 &&
                  first.Summary.IndexOf("1/2", StringComparison.Ordinal) >= 0, "进度说明缺失：" + first.Summary);
            Check("小船已写入、遥控器未写入", boat.Peer == RemoteMac &&
                  remote.Peer == "00:00:00:00:00:00", "部分失败时地址状态异常");

            remote.SaveNoAck = false;
            OperationResult retry = flow.PairTwoBoards(scans.Find(s => s.Port.Name == "COM8"),
                                                       scans.Find(s => s.Port.Name == "COM9"));
            Check("重试后两端都成功", retry.Ok && boat.Peer == RemoteMac && remote.Peer == BoatMac,
                "重试未完成：" + retry.Error);

            // 第一端就失败时不改动第二端。
            SimulatedTransport t2 = new SimulatedTransport();
            SimBoard b2 = NewBoard(BoatMac, BoardRole.Boat);
            SimBoard r2 = NewBoard(RemoteMac, BoardRole.Remote);
            b2.SaveFails = true;
            t2.Boards.Add(b2);
            t2.Boards.Add(r2);
            t2.Plug("COM8", BoatMac);
            t2.Plug("COM9", RemoteMac);
            Workflow flow2 = MakeWorkflow(t2, new SimUi(),
                new RoleStore(Path.Combine(workRoot, "firstfail-" + Guid.NewGuid().ToString("N") + ".json")));
            List<PortScan> s2 = flow2.Scan();
            OperationResult fail = flow2.PairTwoBoards(s2.Find(s => s.Port.Name == "COM8"), s2.Find(s => s.Port.Name == "COM9"));
            Check("小船失败时遥控器不改动", !fail.Ok && r2.Peer == "00:00:00:00:00:00" &&
                  t2.WritePairCount == 1, "小船失败后仍写入遥控器");
            Check("小船失败时说明已完成 0/2", fail.Summary.IndexOf("0/2", StringComparison.Ordinal) >= 0,
                "失败进度说明缺失：" + fail.Summary);
            Console.WriteLine();
        }

        private static void TestPairRejectsOldFirmware()
        {
            Console.WriteLine("13. 旧固件不参与配对，提示先升级");
            SimBoard boat = NewBoard(BoatMac, BoardRole.Boat);
            boat.Legacy = true;
            boat.Fwid = "";
            SimBoard remote = NewBoard(RemoteMac, BoardRole.Remote);
            SimulatedTransport transport = new SimulatedTransport();
            transport.Boards.Add(boat);
            transport.Boards.Add(remote);
            transport.Plug("COM8", BoatMac);
            transport.Plug("COM9", RemoteMac);
            Workflow flow = MakeWorkflow(transport, new SimUi(),
                new RoleStore(Path.Combine(workRoot, "oldpair-" + Guid.NewGuid().ToString("N") + ".json")));
            List<PortScan> scans = flow.Scan();
            OperationResult result = flow.PairTwoBoards(scans.Find(s => s.Port.Name == "COM8"),
                                                       scans.Find(s => s.Port.Name == "COM9"));
            Check("旧固件配对被拒绝", !result.Ok && transport.WritePairCount == 0, "旧固件仍然被配对");
            Check("提示需要升级", result.Error.IndexOf("升级", StringComparison.Ordinal) >= 0, "没有提示升级");

            // 一个固件过旧、另一边没回应时也不能当作成功。
            SimulatedTransport t2 = new SimulatedTransport();
            SimBoard b2 = NewBoard(BoatMac, BoardRole.Boat);
            t2.Boards.Add(b2);
            t2.Plug("COM8", BoatMac);
            t2.AddPort("COM9");
            Workflow flow2 = MakeWorkflow(t2, new SimUi(),
                new RoleStore(Path.Combine(workRoot, "noresp-" + Guid.NewGuid().ToString("N") + ".json")));
            List<PortScan> s2 = flow2.Scan();
            OperationResult noResp = flow2.PairTwoBoards(s2.Find(s => s.Port.Name == "COM8"),
                                                        s2.Find(s => s.Port.Name == "COM9"));
            Check("无回应不当作成功", !noResp.Ok && noResp.Error.IndexOf("遥控器", StringComparison.Ordinal) >= 0,
                "无回应被当成成功");
            Console.WriteLine();
        }

        private static void TestSafetyPromptOnlyOnce()
        {
            Console.WriteLine("14. 安全提示同次运行只确认一次");
            SimBoard boat;
            SimBoard remote;
            SimulatedTransport transport = TwoBoards(out boat, out remote);
            boat.Legacy = true;
            boat.Fwid = "";
            SimUi ui = new SimUi();
            Workflow flow = MakeWorkflow(transport, ui,
                new RoleStore(Path.Combine(workRoot, "safety-" + Guid.NewGuid().ToString("N") + ".json")));
            OperationResult first = flow.FlashCurrent(flow.Scan()[0], BoardRole.Boat, false);
            OperationResult second = flow.BackupOnly(flow.Scan()[0]);
            Check("两次操作只提示一次", ui.SafetyPrompts == 1, "安全提示重复出现：" + ui.SafetyPrompts);
            Check("两次操作都执行", first.Ok && second.Ok, "第二次操作失败");

            SimUi refusal = new SimUi();
            refusal.SafetyAnswer = false;
            SimulatedTransport t2 = TwoBoards(out boat, out remote);
            Workflow flow2 = MakeWorkflow(t2, refusal,
                new RoleStore(Path.Combine(workRoot, "refuse-" + Guid.NewGuid().ToString("N") + ".json")));
            List<PortScan> noScan = flow2.Scan();
            Check("拒绝安全提示则不扫描串口", noScan.Count == 0 && flow2.LastScanCancelled && t2.ProbeCount == 0,
                "拒绝后仍访问了串口");
            OperationResult refused = flow2.FlashCurrent(null, BoardRole.Boat, false);
            Check("拒绝安全提示则不操作", !refused.Ok && t2.WriteFirmwareCount == 0, "拒绝后仍写入");
            Console.WriteLine();
        }

        private static void TestCatalogSelfCheck()
        {
            Console.WriteLine("15. 固件清单自检：镜像内必须真的含有声明的 FWID");
            string dir = Path.Combine(workRoot, "selfcheck-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string fwid = CatalogFwid(BoardRole.Boat);
            File.WriteAllText(Path.Combine(dir, "boat.bin"), "binary " + fwid + " tail");
            File.WriteAllText(Path.Combine(dir, "remote.bin"), "binary " + CatalogFwid(BoardRole.Remote) + " tail");
            File.WriteAllText(Path.Combine(dir, "boot.bin"), "boot");
            File.WriteAllText(Path.Combine(dir, "part.bin"), "part");
            File.WriteAllText(Path.Combine(dir, "boot_app0.bin"), "app0");

            string manifest = Path.Combine(dir, "固件清单.json");
            File.WriteAllText(manifest, BuildManifest(fwid, CatalogFwid(BoardRole.Remote)));
            FirmwareCatalog good = FirmwareCatalog.Load(manifest);
            Check("正常清单自检通过", good.Problems.Count == 0, string.Join("；", good.Problems.ToArray()));
            Check("清单解析角度契约", good.SteerDeg == 20 && good.AngleMin == 70 && good.AngleMax == 110 &&
                  good.PulseMinUs == 1278 && good.PulseMaxUs == 1722, "清单数值解析错误");

            File.WriteAllText(Path.Combine(dir, "boat.bin"), "binary without identity");
            FirmwareCatalog bad = FirmwareCatalog.Load(manifest);
            Check("镜像缺 FWID 被自检拦下", bad.Problems.Count > 0, "镜像与清单不一致却通过自检");

            File.WriteAllText(manifest, BuildManifest(fwid, CatalogFwid(BoardRole.Remote)).Replace("\"steer_deg\": 20", "\"steer_deg\": 30"));
            File.WriteAllText(Path.Combine(dir, "boat.bin"), "binary " + fwid);
            FirmwareCatalog wrongSteer = FirmwareCatalog.Load(manifest);
            Check("清单角度不是20度被拦下", wrongSteer.Problems.Count > 0, "30度清单通过自检");
            Console.WriteLine();
        }

        private static void TestIdentityFromChipForOldBoard()
        {
            Console.WriteLine("16. 旧板/空白板不回应身份查询：按芯片 MAC 升级，仍需用户选角色");
            SimulatedTransport transport = new SimulatedTransport();
            SimBoard silent = NewBoard(OldBoatMac, BoardRole.Boat, false);
            silent.Legacy = true;
            silent.Fwid = "";
            silent.SteerDeg = -1;
            transport.Boards.Add(silent);
            transport.Plug("COM8", OldBoatMac);
            transport.SilentPorts.Add("COM8");
            // 写入新固件后，板子开始回应身份查询（真实行为）。
            transport.OnWrite = delegate(string port, FirmwareSpec spec, SimBoard b) { transport.SilentPorts.Remove(port); };
            SimUi ui = new SimUi();
            RoleStore store = new RoleStore(Path.Combine(workRoot, "silent-" + Guid.NewGuid().ToString("N") + ".json"));
            Workflow flow = MakeWorkflow(transport, ui, store);

            PortScan scan = flow.Scan()[0];
            Check("无回应仍读到芯片 MAC", scan.HasChip && scan.Identity.Mac == OldBoatMac, "没有回退到芯片读取");
            Check("无回应显示为旧版", scan.FirmwareText.IndexOf("旧版", StringComparison.Ordinal) >= 0,
                "无回应没有标记旧版：" + scan.FirmwareText);
            Check("无回应角色仍是未知", scan.RememberedRole == BoardRole.Unknown, "无回应板被自动登记角色");

            OperationResult noRole = flow.FlashCurrent(scan, BoardRole.Unknown, false);
            Check("无回应板未选角色时拒绝", !noRole.Ok && transport.WriteFirmwareCount == 0, "无回应板被直接烧录");

            OperationResult ok = flow.FlashCurrent(scan, BoardRole.Boat, false);
            Check("选定角色后旧板升级成功", ok.Ok, "旧板升级失败：" + ok.Error);
            Check("升级时不强制备份", transport.ReadFlashCount == 0, "旧板升级强制备份");
            Check("升级后按 MAC 记住角色", store.RoleOf(OldBoatMac) == BoardRole.Boat, "升级后未记住角色");
            Console.WriteLine();
        }

        private static void TestFlashRequiresReadBack()
        {
            Console.WriteLine("17. 烧录后必须读回身份，否则不算成功");
            SimulatedTransport transport = new SimulatedTransport();
            SimBoard board = NewBoard(OldBoatMac, BoardRole.Boat);
            board.Legacy = true;
            board.Fwid = "";
            transport.Boards.Add(board);
            transport.Plug("COM8", OldBoatMac);
            Workflow flow = MakeWorkflow(transport, new SimUi(),
                new RoleStore(Path.Combine(workRoot, "readback-" + Guid.NewGuid().ToString("N") + ".json")));
            PortScan scan = flow.Scan()[0];
            // 写入接口报告成功，但板子重启后仍是旧程序（模拟写入的镜像没生效）。
            transport.OnWrite = delegate(string port, FirmwareSpec spec, SimBoard b) { b.Legacy = true; b.Fwid = ""; };
            OperationResult result = flow.FlashCurrent(scan, BoardRole.Boat, false);
            Check("读回身份不符时返回失败", !result.Ok, "写入成功即被当作完成");
            Check("失败原因说明读回不符", result.Error.IndexOf("读回", StringComparison.Ordinal) >= 0 ||
                  result.Error.IndexOf("不符合本包", StringComparison.Ordinal) >= 0, "缺少读回说明：" + result.Error);
            Console.WriteLine();
        }

        private static void TestFlashKeepsNvsWording()
        {
            Console.WriteLine("18. 烧录计划显示分段写入且保留 NVS");
            SimulatedTransport transport = new SimulatedTransport();
            SimBoard board = NewBoard(OldBoatMac, BoardRole.Boat);
            board.Legacy = true;
            board.Fwid = "";
            transport.Boards.Add(board);
            transport.Plug("COM8", OldBoatMac);
            SimUi ui = new SimUi();
            Workflow flow = MakeWorkflow(transport, ui,
                new RoleStore(Path.Combine(workRoot, "nvs-" + Guid.NewGuid().ToString("N") + ".json")));
            FirmwareSpec spec = flow.SpecFor(BoardRole.Boat);
            bool hasNvs = spec.DescribeImages().IndexOf("NVS", StringComparison.Ordinal) >= 0;
            Check("镜像说明包含保留 NVS", hasNvs, "没有说明保留配对区");
            int count = 0;
            foreach (KeyValuePair<long, string> image in spec.Images())
            {
                if (image.Key == 0) { count = -999; break; }
                count++;
            }
            Check("分段写入四个镜像且不整片覆盖 0x0", count == 4, "镜像数量异常：" + count);
            Console.WriteLine();
        }

        private static string BuildManifest(string boatFwid, string remoteFwid)        {
            return "{ \"schema\": 3, \"version\": \"20261103-20deg-r3\", \"steer_deg\": 20, " +
                   "\"angle_min\": 70, \"angle_max\": 110, \"center_us\": 1500, " +
                   "\"pulse_min_us\": 1278, \"pulse_max_us\": 1722, \"pair_protocol\": 2, \"target_rpm\": 580, " +
                   "\"roles\": { " +
                   "\"BOAT\": { \"fwid\": \"" + boatFwid + "\", \"version\": \"20261103-20deg-r3\", \"steer_deg\": 20, " +
                   "\"app\": \"boat.bin\", \"app_offset\": 65536, \"bootloader\": \"boot.bin\", \"bootloader_offset\": 4096, " +
                   "\"partitions\": \"part.bin\", \"partitions_offset\": 32768, " +
                   "\"boot_app0\": \"boot_app0.bin\", \"boot_app0_offset\": 57344 }, " +
                   "\"REMOTE\": { \"fwid\": \"" + remoteFwid + "\", \"version\": \"20261103-20deg-r3\", \"steer_deg\": 20, " +
                   "\"app\": \"remote.bin\", \"app_offset\": 65536, \"bootloader\": \"boot.bin\", \"bootloader_offset\": 4096, " +
                   "\"partitions\": \"part.bin\", \"partitions_offset\": 32768, " +
                   "\"boot_app0\": \"boot_app0.bin\", \"boot_app0_offset\": 57344 } } }";
        }
    }

    internal class SimulationTestHelper
    {
    }
}
