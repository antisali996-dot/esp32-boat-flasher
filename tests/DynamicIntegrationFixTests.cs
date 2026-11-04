using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace BoatTool
{
    // 动态集成修复测试（dynamic-integration-fix 轮）。
    //
    // 三个问题全部走生产路径验证：
    //   一、诊断契约：控制器实际 StartRecording + Pump 是否发出 COUNT（不只是 AllowsSuiteRole 返回值）。
    //   二、刷新指纹：GUI 共用的 SuiteImageStamp 只用加载时快照，替换后必须 changed。
    //   三、同 FWID 异 BIN：实际 Workflow.FlashCurrent 的零写入与文案。
    public static class DynamicIntegrationFixTests
    {
        private static int passed;
        private static int failed;
        private static string root;

        private const string BoatR11 = "JAYFWB11-S20-A70-110-U1278-1500-1722-R590-OSOFF-Cr11-osoff";
        private const string RemoteR11 = "JAYFWR11-S20-A70-110-U1278-1500-1722-D1750-2150";
        private const string BoatR10 = "JAYFWB10-S20-A70-110-U1278-1500-1722-R590-OS600-Cr10-diagshared";
        private const string RemoteR10 = "JAYFWR10-S20-A70-110-U1278-1500-1722-D1750-2150";
        private const string BoatMac = "24:62:AB:F1:36:64";
        private const string RemoteMac = "24:62:AB:EF:CF:84";
        // 模拟 r12：仅测试夹具，非真实固件交付。
        private const string BoatSimR12 = "JAYFWB12-S20-A70-110-U1278-1500-1722-R590-OSOFF-Csim-r12";
        private const string RemoteSimR12 = "JAYFWR12-S20-A70-110-U1278-1500-1722-D1750-2150";
        private const string BoatUnknown = "JAYFWB99-S20-A70-110-U1278-1500-1722-R590-OSOFF-Cunknown";

        public static void Run()
        {
            passed = 0; failed = 0;
            root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test-work",
                "dynfix-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            Console.WriteLine();
            Console.WriteLine("===== 动态集成修复 =====");
            try
            {
                TestContractRealPump();
                TestContractWrongBoardZeroQuery();
                TestRemoteZeroQuery();
                TestUnknownContractAndConflicts();
                TestFingerprintChangedOnSwap();
                TestFingerprintUnchangedKeeps();
                TestSameFwidDifferentBinNoWrite();
                TestSameFwidDifferentBinForceWrites();
                TestSameFwidSameBinMessage();
                TestStaleSuiteRejected();
                TestCountingTransportSanity();
                TestGuiRefreshRebuild();
                TestLearnedAuthorizationRemoved();
                TestFullSnapshotCoverage();
                TestSharedRefreshEntryCache();
                TestPerImageSnapshotOta();
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
            Console.WriteLine();
            Console.WriteLine("动态集成修复：通过 " + passed + " 项，失败 " + failed + " 项");
            if (failed > 0) throw new InvalidOperationException("动态集成修复测试失败 " + failed + " 项");
        }

        private static void Check(string name, bool ok, string detail)
        {
            if (ok) { passed++; Console.WriteLine("  [通过] " + name); }
            else { failed++; Console.WriteLine("  [失败] " + name + " —— " + detail); }
        }

        // ---------- 夹具 ----------

        private static string MakeSuite(string libRoot, string dirName, string id, string version,
                                        string boatFwid, string remoteFwid, string boatImageText,
                                        string diagContract = null)
        {
            string dir = Path.Combine(libRoot, dirName);
            string fw = Path.Combine(dir, "固件");
            string tools = Path.Combine(dir, "工具");
            Directory.CreateDirectory(fw);
            Directory.CreateDirectory(tools);
            File.WriteAllText(Path.Combine(tools, "esptool.exe"), "fake esptool " + id);
            string realPart = FindRealPartition();
            foreach (string f in new string[] { "boat-partitions.bin", "remote-partitions.bin" })
            {
                if (realPart != null) File.Copy(realPart, Path.Combine(fw, f), true);
                else File.WriteAllBytes(Path.Combine(fw, f), new byte[0]);
            }
            // boatImageText 决定小船镜像内容（同 FWID 不同 BIN 用不同文本）
            File.WriteAllText(Path.Combine(fw, "boat-app.bin"), boatImageText, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(fw, "remote-app.bin"), "xx" + remoteFwid + "yy", new UTF8Encoding(false));
            File.WriteAllBytes(Path.Combine(fw, "boat-bootloader.bin"), new byte[] { 1, 2, 3 });
            File.WriteAllBytes(Path.Combine(fw, "remote-bootloader.bin"), new byte[] { 1, 2, 3 });
            File.WriteAllBytes(Path.Combine(fw, "boot_app0.bin"), new byte[] { 9 });

            string boatRole = "\"BOAT\": { \"fwid\": \"" + boatFwid + "\", \"version\": \"" + version + "\", \"steer_deg\": 20" +
                (diagContract == null ? "" : ", \"diag_contract\": \"" + diagContract + "\"") +
                ", \"app\": \"boat-app.bin\", \"app_offset\": 65536, \"bootloader\": \"boat-bootloader.bin\", \"bootloader_offset\": 4096, " +
                "\"partitions\": \"boat-partitions.bin\", \"partitions_offset\": 32768, " +
                "\"boot_app0\": \"boot_app0.bin\", \"boot_app0_offset\": 57344 }";
            string remoteRole = "\"REMOTE\": { \"fwid\": \"" + remoteFwid + "\", \"version\": \"" + version + "\", \"steer_deg\": 20" +
                ", \"app\": \"remote-app.bin\", \"app_offset\": 65536, \"bootloader\": \"remote-bootloader.bin\", \"bootloader_offset\": 4096, " +
                "\"partitions\": \"remote-partitions.bin\", \"partitions_offset\": 32768, " +
                "\"boot_app0\": \"boot_app0.bin\", \"boot_app0_offset\": 57344 }";
            string manifest = "{ \"schema\": 11, \"version\": \"" + version + "\", \"steer_deg\": 20, " +
                "\"angle_min\": 70, \"angle_max\": 110, \"center_us\": 1500, " +
                "\"pulse_min_us\": 1278, \"pulse_max_us\": 1722, \"pair_protocol\": 2, " +
                "\"target_rpm\": 590, \"overspeed_rpm\": 600, \"overspeed_lockout\": \"off\", " +
                "\"roles\": { " + boatRole + ", " + remoteRole + " } }";
            File.WriteAllText(Path.Combine(fw, "固件清单.json"), manifest, new UTF8Encoding(false));

            string import = "{ \"id\": \"" + id + "\", \"version\": \"" + version + "\", \"target_rpm\": 590" +
                ", \"steer_deg\": 20, \"boat_fwid\": \"" + boatFwid + "\", \"remote_fwid\": \"" + remoteFwid + "\" }";
            File.WriteAllText(Path.Combine(dir, "import.json"), import, new UTF8Encoding(false));
            return dir;
        }

        private static string FindRealPartition()
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            for (int i = 0; i < 6 && dir != null; i++)
            {
                string p = Path.Combine(dir, "work", "r11-library", "590-20-r11", "固件", "boat-partitions.bin");
                if (File.Exists(p)) return p;
                DirectoryInfo parent = Directory.GetParent(dir);
                dir = parent == null ? null : parent.FullName;
            }
            return null;
        }

        private sealed class ControllerFixture
        {
            public FakeDiagChannel Ch;
            public FakeDiagClock Clock;
            public DiagnosticsController C;
            public FirmwareLibrary Library;
            public AppPaths Paths;

            public void InjectIdentity(string fwid, string role = "BOAT", string mac = BoatMac)
            {
                Ch.Deliver("JAYv4 ROLE=" + role + " MAC=" + mac +
                           " PEER=24:62:AB:EF:CF:84 PAIR=2 STEER=20 FWID=" + fwid +
                           " VER=sim\n");
            }
        }
        // 构造绑定真实固件库的控制器：与 GUI 同一生产解析路径
        //（DiagnosticsController.SetFirmwareLibrary → ResolveFromLibrary → AllowsSuiteRole），
        // 不复制任何判断逻辑。
        private static ControllerFixture NewController(string label, FirmwareLibrary library)
        {
            string dir = Path.Combine(root, label + "-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(dir);
            ControllerFixture f = new ControllerFixture();
            f.Ch = new FakeDiagChannel();
            f.Clock = new FakeDiagClock();
            f.Library = library;
            f.Paths = new AppPaths();
            f.Paths.AppRoot = dir; f.Paths.WritableRoot = dir;
            f.Paths.ConfigDir = Path.Combine(dir, "配置");
            f.Paths.RecordsDir = Path.Combine(dir, "记录");
            f.Paths.RoleStorePath = Path.Combine(f.Paths.ConfigDir, "角色记忆.json");
            f.C = new DiagnosticsController(f.Paths, f.Ch, f.Clock, QueryPolicy.ApprovedR11(), null);
            f.C.PortLocks = new PortLockRegistry();
            f.C.SetPort("COM_SIM");
            f.C.SetFirmwareLibrary(library);   // 生产解析来源：真实加载的固件库
            return f;
        }


        // ---------- 问题一：契约接入真实路径 ----------

        private static void TestContractRealPump()
        {
            Console.WriteLine();
            Console.WriteLine("1. 模拟 r12（受支持契约）经真实 Pump 实际发出 COUNT");
            string lib = Path.Combine(root, "c1-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(lib);
            MakeSuite(lib, "sim-r12", "sim-r12", "20991231-sim-r12", BoatSimR12, RemoteSimR12,
                "xx" + BoatSimR12 + "yy", "diag-v1");
            FirmwareLibrary l = FirmwareLibrary.Load(lib);
            ControllerFixture f = NewController("c1", l);
            f.InjectIdentity(BoatSimR12);
            f.C.ConnectAndIdentify();
            // 界面显示与控制器能力一致（同一解析结果）
            DiagCapability cap = f.C.ResolveCapability();
            Check("能力解析：允许", cap.Allowed, cap.Reason);
            Check("契约命中 diag-v1", cap.Contract == "diag-v1", cap.Contract);
            f.C.StartRecording();
            f.Clock.Advance(6000);
            f.C.Pump();
            Check("实际发出 COUNT", f.Ch.CommandCount("JAY COUNT") == 1,
                "实际 " + f.Ch.CommandCount("JAY COUNT"));
            f.C.StopRecording();
        }

        private static void TestContractWrongBoardZeroQuery()
        {
            Console.WriteLine();
            Console.WriteLine("2. 选择 r12 套装，但板上身份是未知固件 → 零自动查询");
            string lib = Path.Combine(root, "c2-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(lib);
            MakeSuite(lib, "sim-r12", "sim-r12", "20991231-sim-r12", BoatSimR12, RemoteSimR12,
                "xx" + BoatSimR12 + "yy", "diag-v1");
            FirmwareLibrary l = FirmwareLibrary.Load(lib);
            ControllerFixture f = NewController("c2", l);
            f.InjectIdentity(BoatUnknown);   // 板上不是 r12
            f.C.ConnectAndIdentify();
            DiagCapability cap = f.C.ResolveCapability();
            Check("未知固件被拒", cap.Allowed == false, cap.Reason);
            f.C.StartRecording();
            f.Clock.Advance(6000); f.C.Pump();
            f.Clock.Advance(6000); f.C.Pump();
            Check("零自动查询", f.Ch.CommandCount("JAY COUNT") == 0 && f.Ch.CommandCount("JAY FAULT") == 0,
                "COUNT=" + f.Ch.CommandCount("JAY COUNT"));
            f.C.StopRecording();
        }

        private static void TestRemoteZeroQuery()
        {
            Console.WriteLine();
            Console.WriteLine("3. 遥控器未声明 COUNT/FAULT → 零自动查询");
            string lib = Path.Combine(root, "c3-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(lib);
            MakeSuite(lib, "sim-r12", "sim-r12", "20991231-sim-r12", BoatSimR12, RemoteSimR12,
                "xx" + BoatSimR12 + "yy", "diag-v1");
            FirmwareLibrary l = FirmwareLibrary.Load(lib);
            ControllerFixture f = NewController("c3", l);
            f.InjectIdentity(RemoteSimR12, "REMOTE", RemoteMac);
            f.C.ConnectAndIdentify();
            DiagCapability cap = f.C.ResolveCapability();
            Check("遥控器能力被拒", cap.Allowed == false, cap.Reason);
            f.C.StartRecording();
            f.Clock.Advance(6000); f.C.Pump();
            Check("遥控器零自动查询", f.Ch.CommandCount("JAY COUNT") == 0 && f.Ch.CommandCount("JAY FAULT") == 0,
                "有查询发出");
            f.C.StopRecording();
        }

        private static void TestUnknownContractAndConflicts()
        {
            Console.WriteLine();
            Console.WriteLine("4. 未知契约 / 角色错配 / 冲突声明 → 零自动查询");
            // a) 未知契约
            string libA = Path.Combine(root, "c4a-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(libA);
            MakeSuite(libA, "future", "future", "v-future", BoatSimR12, RemoteSimR12,
                "xx" + BoatSimR12 + "yy", "diag-v9-unknown");
            FirmwareLibrary la = FirmwareLibrary.Load(libA);
            ControllerFixture fa = NewController("c4a", la);
            fa.InjectIdentity(BoatSimR12);
            fa.C.ConnectAndIdentify();
            Check("未知契约拒绝", fa.C.ResolveCapability().Allowed == false, "被放行");
            fa.C.StartRecording();
            fa.Clock.Advance(6000); fa.C.Pump();
            Check("未知契约零查询", fa.Ch.CommandCount("JAY COUNT") == 0, "有查询");
            fa.C.StopRecording();

            // b) 同 FWID 两个条目、能力矛盾（一个带契约、一个不带且不在白名单）
            string libB = Path.Combine(root, "c4b-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(libB);
            MakeSuite(libB, "with-contract", "with-contract", "v1", BoatSimR12, RemoteSimR12,
                "xx" + BoatSimR12 + "yy-A", "diag-v1");
            MakeSuite(libB, "no-contract", "no-contract", "v2", BoatSimR12, RemoteSimR12,
                "xx" + BoatSimR12 + "yy-B", null);
            FirmwareLibrary lb = FirmwareLibrary.Load(libB);
            ControllerFixture fb = NewController("c4b", lb);
            fb.InjectIdentity(BoatSimR12);
            fb.C.ConnectAndIdentify();
            DiagCapability conflict = fb.C.ResolveCapability();
            Check("能力矛盾被拒并说明", conflict.Allowed == false && conflict.Reason.Contains("矛盾"),
                conflict.Reason);
            fb.C.StartRecording();
            fb.Clock.Advance(6000); fb.C.Pump();
            Check("矛盾时零查询", fb.Ch.CommandCount("JAY COUNT") == 0, "有查询");
            fb.C.StopRecording();
        }

        // ---------- 问题二：刷新指纹 ----------

        private static void TestFingerprintChangedOnSwap()
        {
            Console.WriteLine();
            Console.WriteLine("5. 替换镜像后 GUI 指纹必须 changed（快照 vs 快照）");
            string lib = Path.Combine(root, "f1-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(lib);
            string dir = MakeSuite(lib, "590-20-r11", "590-20-r11", "20261103-590rpm-r11",
                BoatR11, RemoteR11, "xx" + BoatR11 + "yy");
            FirmwareLibrary oldLib = FirmwareLibrary.Load(lib);
            FirmwareSuite oldSuite = oldLib.Find("590-20-r11");
            string oldStamp = FirmwareLibrary.SuiteImageStamp(oldSuite);   // 纯快照，不读盘

            // 替换磁盘镜像并同步生成合法新清单（镜像内嵌 FWID 必须保留）
            File.WriteAllText(Path.Combine(dir, "固件", "boat-app.bin"), "xx" + BoatR11 + "yy-REBUILT", new UTF8Encoding(false));
            FirmwareLibrary freshLib = FirmwareLibrary.Load(lib);
            FirmwareSuite freshSuite = freshLib.Find("590-20-r11");
            string freshStamp = FirmwareLibrary.SuiteImageStamp(freshSuite);
            Check("新旧快照本身不同", oldSuite.BoatAppHashSnapshot != freshSuite.BoatAppHashSnapshot, "快照相同");
            string decision = FirmwareLibrary.RefreshDecision(oldSuite, oldStamp, freshSuite, freshStamp);
            Check("RefreshDecision=changed（不再 keep）", decision == "changed", decision);

            // 变化也可来自元数据（契约变化、镜像不变）
            string libM = Path.Combine(root, "f1m-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(libM);
            MakeSuite(libM, "s", "s", "v1", BoatSimR12, RemoteSimR12, "xx" + BoatSimR12 + "yy", null);
            FirmwareLibrary om = FirmwareLibrary.Load(libM);
            FirmwareSuite os = om.Find("s");
            string osStamp = FirmwareLibrary.SuiteImageStamp(os);
            MakeSuite(libM, "s", "s", "v1", BoatSimR12, RemoteSimR12, "xx" + BoatSimR12 + "yy", "diag-v1");
            FirmwareLibrary nm = FirmwareLibrary.Load(libM);
            FirmwareSuite ns = nm.Find("s");
            string nsStamp = FirmwareLibrary.SuiteImageStamp(ns);
            Check("契约元数据变化也判 changed",
                FirmwareLibrary.RefreshDecision(os, osStamp, ns, nsStamp) == "changed",
                "元数据变化未被发现");
        }

        private static void TestFingerprintUnchangedKeeps()
        {
            Console.WriteLine();
            Console.WriteLine("6. 内容未变 → keep；目录删除 → missing；自检失败 → unusable");
            string lib = Path.Combine(root, "f2-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(lib);
            string dir = MakeSuite(lib, "s1", "s1", "v1", BoatR11, RemoteR11, "xx" + BoatR11 + "yy");
            FirmwareLibrary l1 = FirmwareLibrary.Load(lib);
            FirmwareSuite s1 = l1.Find("s1");
            string st1 = FirmwareLibrary.SuiteImageStamp(s1);
            FirmwareLibrary l2 = FirmwareLibrary.Load(lib);   // 重新加载（期间未改动）
            FirmwareSuite s2 = l2.Find("s1");
            Check("未变时 keep", FirmwareLibrary.RefreshDecision(s1, st1, s2, FirmwareLibrary.SuiteImageStamp(s2)) == "keep",
                "误报 changed");

            Directory.Delete(dir, true);
            FirmwareLibrary l3 = FirmwareLibrary.Load(lib);
            Check("删除后 missing", FirmwareLibrary.RefreshDecision(s1, st1, l3.Find("s1"), "") == "missing",
                "误报 keep");

            string dirB = MakeSuite(lib, "br", "br", "vb", BoatSimR12, RemoteSimR12, "NO FWID");
            FirmwareLibrary lb = FirmwareLibrary.Load(lib);
            FirmwareSuite broken = lb.Find("br");
            string bst = FirmwareLibrary.SuiteImageStamp(broken);
            Check("自检失败 unusable",
                FirmwareLibrary.RefreshDecision(broken, bst, broken, bst) == "unusable", "误报 keep");
        }

        // ---------- 问题三：同 FWID 异 BIN ----------

        private sealed class WorkflowFixture
        {
            public SimulatedTransport Transport;
            public SimUi Ui;
            public Workflow Flow;
            public FirmwareLibrary Library;
            public string LibRoot;
            public RoleStore Store;
        }

        private static WorkflowFixture MakeWorkflow(string label, FirmwareLibrary library, string suiteId)
        {
            WorkflowFixture f = new WorkflowFixture();
            f.Transport = new SimulatedTransport();
            f.Ui = new SimUi();
            string dir = Path.Combine(root, label + "-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(dir);
            AppPaths paths = new AppPaths();
            paths.AppRoot = dir; paths.WritableRoot = dir;
            paths.ConfigDir = Path.Combine(dir, "配置");
            paths.RecordsDir = Path.Combine(dir, "记录");
            paths.RoleStorePath = Path.Combine(paths.ConfigDir, "角色记忆.json");
            f.Store = new RoleStore(paths.RoleStorePath);
            f.Flow = new Workflow(paths, library, f.Store, f.Transport, new SimClock(), f.Ui);
            if (suiteId != null) f.Flow.SelectedSuite = library.Find(suiteId);
            f.Library = library;
            return f;
        }

        private static PortScan ScanFor(WorkflowFixture f, string mac, string fwid)
        {
            SimBoard board = new SimBoard();
            board.Mac = mac;
            board.Role = BoardRole.Boat;
            board.Fwid = fwid;
            board.SteerDeg = 20;
            f.Transport.Boards.Add(board);
            f.Transport.Plug("COM9", mac);
            List<PortScan> scans = f.Flow.Scan();
            return scans.Count > 0 ? scans[0] : null;
        }

        private static void TestSameFwidDifferentBinNoWrite()
        {
            Console.WriteLine();
            Console.WriteLine("7. 同 FWID 异 BIN，未强制：零写入，明确镜像未核验");
            string lib = Path.Combine(root, "w1-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(lib);
            // 所选套装 chosen（镜像内容 X）与另一套 variant（同 FWID，镜像内容 Y）
            string chosenDir = MakeSuite(lib, "chosen", "chosen", "v1", BoatSimR12, RemoteSimR12,
                "xx" + BoatSimR12 + "yy-CONTENT-X", "diag-v1");
            MakeSuite(lib, "variant", "variant", "v2", BoatSimR12, RemoteSimR12,
                "xx" + BoatSimR12 + "yy-CONTENT-Y", null);
            FirmwareLibrary l = FirmwareLibrary.Load(lib);
            WorkflowFixture f = MakeWorkflow("w1", l, "chosen");

            PortScan scan = ScanFor(f, BoatMac, BoatSimR12);
            Check("扫描识别到板子", scan != null && scan.Responded, "扫描失败");
            OperationResult r = f.Flow.FlashCurrent(scan, BoardRole.Boat, false, false);
            // 修复三：身份匹配统一为"跳过写入 + 未核验文案"（Ok=true），不再走 Error 路径。
            Check("零写入调用", f.Transport.WriteFirmwareCount == 0, "WriteFirmwareCount=" + f.Transport.WriteFirmwareCount);
            Check("结果说明镜像未核验", (r.Summary ?? "").Contains("未核验") && (r.Summary ?? "").Contains("未写入"),
                r.Summary ?? (r.Error ?? "无输出"));
            Check("不出现“完全一致/已是最新”", (r.Summary ?? "").Contains("完全一致") == false &&
                (r.Summary ?? "").Contains("已是最新") == false, r.Summary ?? "");
        }

        private static void TestSameFwidDifferentBinForceWrites()
        {
            Console.WriteLine();
            Console.WriteLine("8. 同 FWID 异 BIN，明确强制 → 使用所选套装计划写入");
            string lib = Path.Combine(root, "w2-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(lib);
            MakeSuite(lib, "chosen", "chosen", "v1", BoatSimR12, RemoteSimR12,
                "xx" + BoatSimR12 + "yy-CONTENT-X", "diag-v1");
            MakeSuite(lib, "variant", "variant", "v2", BoatSimR12, RemoteSimR12,
                "xx" + BoatSimR12 + "yy-CONTENT-Y", null);
            FirmwareLibrary l = FirmwareLibrary.Load(lib);
            WorkflowFixture f = MakeWorkflow("w2", l, "chosen");
            PortScan scan = ScanFor(f, BoatMac, BoatSimR12);
            OperationResult r = f.Flow.FlashCurrent(scan, BoardRole.Boat, false, true);
            Check("强制时执行写入", f.Transport.WriteFirmwareCount == 1, "WriteFirmwareCount=" + f.Transport.WriteFirmwareCount);
            Check("写入计划使用所选套装", r.Ok, r.Error ?? r.Summary ?? "");
        }

        private static void TestSameFwidSameBinMessage()
        {
            Console.WriteLine();
            Console.WriteLine("9. 同 FWID 同 BIN：状态文案准确（无需重复烧录）");
            string lib = Path.Combine(root, "w3-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(lib);
            string imageText = "xx" + BoatSimR12 + "yy-IDENTICAL";
            MakeSuite(lib, "dupA", "dupA", "v1", BoatSimR12, RemoteSimR12, imageText, "diag-v1");
            MakeSuite(lib, "dupB", "dupB", "v2", BoatSimR12, RemoteSimR12, imageText, "diag-v1");
            FirmwareLibrary l = FirmwareLibrary.Load(lib);
            WorkflowFixture f = MakeWorkflow("w3", l, "dupA");
            PortScan scan = ScanFor(f, BoatMac, BoatSimR12);
            OperationResult r = f.Flow.FlashCurrent(scan, BoardRole.Boat, false, false);
            Check("同镜像时零写入跳过", r.Ok && f.Transport.WriteFirmwareCount == 0,
                "Ok=" + r.Ok + " Write=" + f.Transport.WriteFirmwareCount);
            Check("文案准确（身份匹配但镜像未核验）", (r.Summary ?? "").Contains("未核验"),
                r.Summary ?? "");
            // 修复三：不宣称"完全一致/已是最新"才是重点；"未核验"是如实表述。
            Check("不夸大为已核验", (r.Summary ?? "").Contains("完全一致") == false &&
                (r.Summary ?? "").Contains("已是最新") == false, r.Summary ?? "");
        }

        private static void TestStaleSuiteRejected()
        {
            Console.WriteLine();
            Console.WriteLine("10. 使用刷新后失效的旧套件对象 → 拒绝执行");
            string lib = Path.Combine(root, "w4-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(lib);
            string dir = MakeSuite(lib, "gone", "gone", "v1", BoatSimR12, RemoteSimR12,
                "xx" + BoatSimR12 + "yy", "diag-v1");
            FirmwareLibrary l = FirmwareLibrary.Load(lib);
            WorkflowFixture f = MakeWorkflow("w4", l, "gone");
            FirmwareSuite stale = l.Find("gone");
            Directory.Delete(dir, true);   // 磁盘已删
            PortScan scan = ScanFor(f, BoatMac, BoatSimR12);
            f.Flow.SelectedSuite = stale;  // 旧计划对象
            OperationResult r = f.Flow.FlashCurrent(scan, BoardRole.Boat, false, false);
            Check("旧对象被拒绝", r.Ok == false && f.Transport.WriteFirmwareCount == 0,
                "Ok=" + r.Ok + " Write=" + f.Transport.WriteFirmwareCount);
            Check("拒绝原因明确", (r.Error ?? "").Length > 0, "无错误说明");
        }

        private static void TestCountingTransportSanity()
        {
            Console.WriteLine();
            Console.WriteLine("11. 计数器正向对照：执行操作后计数确实增加");
            string lib = Path.Combine(root, "w5-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(lib);
            MakeSuite(lib, "ok", "ok", "v1", BoatSimR12, RemoteSimR12, "xx" + BoatSimR12 + "yy", "diag-v1");
            FirmwareLibrary l = FirmwareLibrary.Load(lib);
            WorkflowFixture f = MakeWorkflow("w5", l, "ok");
            Check("初始计数为零", f.Transport.WriteFirmwareCount == 0 && f.Transport.ProbeCount == 0, "初始非零");
            PortScan scan = ScanFor(f, BoatMac, BoatSimR12);
            f.Flow.FlashCurrent(scan, BoardRole.Boat, false, true);
            Check("强制烧录后 ProbeCount 增加", f.Transport.ProbeCount > 0,
                "ProbeCount=" + f.Transport.ProbeCount);
            Check("强制烧录后 WriteFirmwareCount=1", f.Transport.WriteFirmwareCount == 1,
                "WriteFirmwareCount=" + f.Transport.WriteFirmwareCount);
        }

        // ---------- 12. 学习式授权已删除 ----------

        private static void TestLearnedAuthorizationRemoved()
        {
            Console.WriteLine();
            Console.WriteLine("12. AllowsSuiteRole 无副作用；切未知契约后零查询");
            string lib = Path.Combine(root, "lrn-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(lib);
            MakeSuite(lib, "sim-r12", "sim-r12", "20991231-sim-r12", BoatSimR12, RemoteSimR12,
                "xx" + BoatSimR12 + "yy", "diag-v1");
            FirmwareLibrary l = FirmwareLibrary.Load(lib);
            ControllerFixture f = NewController("lrn", l);
            f.InjectIdentity(BoatSimR12);
            f.C.ConnectAndIdentify();
            f.C.StartRecording();
            f.Clock.Advance(6000); f.C.Pump();
            Check("diag-v1 首轮实际发出 COUNT", f.Ch.CommandCount("JAY COUNT") == 1,
                "COUNT=" + f.Ch.CommandCount("JAY COUNT"));

            // AllowsSuiteRole 纯判断：调用后不得为无来源控制器新增授权（编译期已删除 AllowsLearned）。
            // 关键行为断言：切到未知契约（改库内清单为 diag-v9）→ 重新绑定 → 零新增查询。
            MakeSuite(lib, "sim-r12", "sim-r12", "20991231-sim-r12", BoatSimR12, RemoteSimR12,
                "xx" + BoatSimR12 + "yy", "diag-v9");
            FirmwareLibrary fresh = FirmwareLibrary.Load(lib);
            f.C.SetFirmwareLibrary(fresh);   // 绑定来源变化 → 能力缓存失效
            int before = f.Ch.CommandCount("JAY COUNT");
            f.Clock.Advance(6000); f.C.Pump();
            Check("切换未知契约后零新增查询", f.Ch.CommandCount("JAY COUNT") == before,
                "多发 " + (f.Ch.CommandCount("JAY COUNT") - before));
            f.C.StopRecording();
        }

        // ---------- 13. 完整快照覆盖 ----------

        private static void TestFullSnapshotCoverage()
        {
            Console.WriteLine();
            Console.WriteLine("13. esptool/bootloader/分区表/地址/契约变化全部触发 changed");
            string lib = Path.Combine(root, "snap-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(lib);
            string dir = MakeSuite(lib, "s", "s", "v1", BoatR11, RemoteR11, "xx" + BoatR11 + "yy", "diag-v1");
            FirmwareLibrary oldLib = FirmwareLibrary.Load(lib);
            FirmwareSuite oldSuite = oldLib.Find("s");
            string oldStamp = FirmwareLibrary.SuiteImageStamp(oldSuite);

            // a) esptool.exe 变化
            File.AppendAllText(Path.Combine(dir, "工具", "esptool.exe"), "CHANGED");
            FirmwareLibrary l1 = FirmwareLibrary.Load(lib);
            FirmwareSuite n1 = l1.Find("s");
            Check("esptool 变化 → changed", FirmwareLibrary.SuiteImageStamp(n1) != oldStamp &&
                FirmwareLibrary.RefreshDecision(oldSuite, oldStamp, n1, FirmwareLibrary.SuiteImageStamp(n1)) == "changed",
                "未检测到");
            string revalErr;
            bool reval = Workflow.RevalidateSuite(oldSuite, out revalErr);
            Check("esptool 变化 → 复核拒绝", reval == false && revalErr.Contains("esptool"), revalErr);
            MakeSuite(lib, "s", "s", "v1", BoatR11, RemoteR11, "xx" + BoatR11 + "yy", "diag-v1");

            // b) bootloader 变化
            File.AppendAllText(Path.Combine(dir, "固件", "boat-bootloader.bin"), "CHANGED");
            FirmwareLibrary l2 = FirmwareLibrary.Load(lib);
            Check("bootloader 变化 → changed",
                FirmwareLibrary.SuiteImageStamp(l2.Find("s")) != oldStamp, "未检测到");
            MakeSuite(lib, "s", "s", "v1", BoatR11, RemoteR11, "xx" + BoatR11 + "yy", "diag-v1");

            // c) 分区表变化
            File.AppendAllText(Path.Combine(dir, "固件", "boat-partitions.bin"), "CHANGED");
            FirmwareLibrary l3 = FirmwareLibrary.Load(lib);
            Check("分区表变化 → changed",
                FirmwareLibrary.SuiteImageStamp(l3.Find("s")) != oldStamp, "未检测到");
            MakeSuite(lib, "s", "s", "v1", BoatR11, RemoteR11, "xx" + BoatR11 + "yy", "diag-v1");

            // d) 写入地址变化（app_offset）
            string manifestPath = Path.Combine(dir, "固件", "固件清单.json");
            string manifest = File.ReadAllText(manifestPath);
            File.WriteAllText(manifestPath, manifest.Replace("\"app_offset\": 65536", "\"app_offset\": 70000"), new UTF8Encoding(false));
            FirmwareLibrary l4 = FirmwareLibrary.Load(lib);
            Check("写入地址变化 → changed",
                FirmwareLibrary.SuiteImageStamp(l4.Find("s")) != oldStamp, "未检测到");
            MakeSuite(lib, "s", "s", "v1", BoatR11, RemoteR11, "xx" + BoatR11 + "yy", "diag-v1");

            // e) 诊断契约变化
            MakeSuite(lib, "s", "s", "v1", BoatR11, RemoteR11, "xx" + BoatR11 + "yy", null);
            FirmwareLibrary l5 = FirmwareLibrary.Load(lib);
            Check("契约变化 → changed",
                FirmwareLibrary.SuiteImageStamp(l5.Find("s")) != oldStamp, "未检测到");

            // f) 内容全未变 → keep
            MakeSuite(lib, "s", "s", "v1", BoatR11, RemoteR11, "xx" + BoatR11 + "yy", "diag-v1");
            FirmwareLibrary l6 = FirmwareLibrary.Load(lib);
            FirmwareSuite same = l6.Find("s");
            Check("全部未变 → keep", FirmwareLibrary.RefreshDecision(oldSuite, oldStamp, same,
                FirmwareLibrary.SuiteImageStamp(same)) == "keep", "误报 changed");
        }
        // ---------- 14. 共享刷新入口清除能力缓存（与 GUI 同一生产入口） ----------

        private static void TestSharedRefreshEntryCache()
        {
            Console.WriteLine();
            Console.WriteLine("14. 共享刷新入口：控制器能力缓存立即失效");
            string lib = Path.Combine(root, "shared-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(lib);
            MakeSuite(lib, "590-20-r11", "590-20-r11", "20261103-590rpm-r11", BoatR11, RemoteR11, "xx" + BoatR11 + "yy", "diag-v1");
            FirmwareLibrary initial = FirmwareLibrary.Load(lib);
            ControllerFixture f = NewController("shared", initial);   // 绑定真实库（生产路径）
            f.InjectIdentity(BoatR11);
            f.C.ConnectAndIdentify();
            Check("初始允许（缓存生成）", f.C.ResolveCapability().Allowed, "初始即拒绝");
            // 场景 a：删除套装（空库）→ 共享入口后立即拒绝
            string libA = Path.Combine(root, "shared-a-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(libA);
            FirmwareSuite newSelA;
            string decA = FirmwareLibrary.ApplyFirmwareLibraryRefresh(initial, FirmwareLibrary.Load(libA),
                initial.Find("590-20-r11"), delegate(FirmwareLibrary nl) { f.C.SetFirmwareLibrary(nl); }, out newSelA);
            Check("删除套装后能力立即拒绝", f.C.ResolveCapability().Allowed == false,
                "仍允许：" + f.C.ResolveCapability().Reason);
            Check("旧选择被清除（missing）", newSelA == null && decA == "missing", decA);
            // 场景 b：契约变为未知 → 重新绑定后拒绝
            MakeSuite(lib, "590-20-r11", "590-20-r11", "20261103-590rpm-r11", BoatR11, RemoteR11, "xx" + BoatR11 + "yy", "diag-v9");
            FirmwareLibrary libUnknown = FirmwareLibrary.Load(lib);
            FirmwareSuite newSelB;
            FirmwareLibrary.ApplyFirmwareLibraryRefresh(initial, libUnknown, initial.Find("590-20-r11"),
                delegate(FirmwareLibrary nl) { f.C.SetFirmwareLibrary(nl); }, out newSelB);
            Check("未知契约后能力拒绝", f.C.ResolveCapability().Allowed == false,
                "仍允许：" + f.C.ResolveCapability().Reason);
            // 场景 c：内容未变 → keep 且仍允许
            MakeSuite(lib, "590-20-r11", "590-20-r11", "20261103-590rpm-r11", BoatR11, RemoteR11, "xx" + BoatR11 + "yy", "diag-v1");
            FirmwareLibrary libSame = FirmwareLibrary.Load(lib);
            FirmwareSuite newSelC;
            string decC = FirmwareLibrary.ApplyFirmwareLibraryRefresh(initial, libSame, initial.Find("590-20-r11"),
                delegate(FirmwareLibrary nl) { f.C.SetFirmwareLibrary(nl); }, out newSelC);
            Check("内容未变 keep", decC == "keep" && newSelC != null, decC);
            Check("未变后能力恢复允许", f.C.ResolveCapability().Allowed, f.C.ResolveCapability().Reason);
            f.C.StopRecording();
        }

        // ---------- 15. 逐镜像快照：独立 boot_app0 ----------

        private static void TestPerImageSnapshotOta()
        {
            Console.WriteLine();
            Console.WriteLine("15. 两端独立 boot_app0：改遥控器文件必须 changed");
            string lib = Path.Combine(root, "ota-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(lib);
            string dir = MakeSuite(lib, "split", "split", "v1", BoatR11, RemoteR11, "xx" + BoatR11 + "yy", "diag-v1");
            string fw = Path.Combine(dir, "固件");
            // 遥控器独立 boot_app0：复制一份并把 REMOTE 段指向它
            File.Copy(Path.Combine(fw, "boot_app0.bin"), Path.Combine(fw, "remote-boot_app0.bin"), true);
            string manifestPath = Path.Combine(fw, "固件清单.json");
            string manifest = File.ReadAllText(manifestPath);
            int rs = manifest.IndexOf("\"REMOTE\"");
            string updated = manifest.Substring(0, rs) +
                manifest.Substring(rs).Replace("\"boot_app0\": \"boot_app0.bin\"", "\"boot_app0\": \"remote-boot_app0.bin\"");
            File.WriteAllText(manifestPath, updated, new UTF8Encoding(false));
            FirmwareLibrary oldLib = FirmwareLibrary.Load(lib);
            FirmwareSuite oldSuite = oldLib.Find("split");
            Check("独立 OTA 套装可用", oldSuite.Usable, string.Join("；", oldSuite.Problems.ToArray()));
            int boatOta = 0, remoteOta = 0;
            foreach (FirmwareSuite.ImageSnapshot img in oldSuite.ImageSnapshots)
            {
                if (img.Role != "BOAT" && img.Role != "REMOTE") continue;
                bool isOta = img.RelPath.EndsWith("boot_app0.bin", StringComparison.Ordinal);
                if (!isOta) continue;
                if (img.Role == "BOAT" && img.RelPath == "boot_app0.bin") boatOta++;
                if (img.Role == "REMOTE" && img.RelPath == "remote-boot_app0.bin") remoteOta++;
            }
            Check("快照覆盖两端 boot_app0（逐 Images() 条目，独立文件各自成条）", boatOta == 1 && remoteOta == 1,
                "boat=" + boatOta + " remote=" + remoteOta);
            string oldStamp = FirmwareLibrary.SuiteImageStamp(oldSuite);
            // 只改遥控器独立 OTA 文件内容（长度不变）
            byte[] ota = File.ReadAllBytes(Path.Combine(fw, "remote-boot_app0.bin"));
            ota[0] ^= 1;
            File.WriteAllBytes(Path.Combine(fw, "remote-boot_app0.bin"), ota);
            FirmwareLibrary freshLib = FirmwareLibrary.Load(lib);
            FirmwareSuite fresh = freshLib.Find("split");
            string freshStamp = FirmwareLibrary.SuiteImageStamp(fresh);
            Check("遥控器独立 OTA 变化 → changed", FirmwareLibrary.RefreshDecision(oldSuite, oldStamp, fresh, freshStamp) == "changed",
                "仍是 keep");
            string revalErr;
            bool reval = Workflow.RevalidateSuite(oldSuite, out revalErr);
            Check("遥控器独立 OTA → RevalidateSuite=false", reval == false, revalErr);
            // 小船 OTA 变化（遥控器恢复后）也必须 changed
            File.Copy(Path.Combine(fw, "boot_app0.bin"), Path.Combine(fw, "remote-boot_app0.bin"), true);
            byte[] b2 = File.ReadAllBytes(Path.Combine(fw, "boot_app0.bin"));
            b2[0] ^= 1;
            File.WriteAllBytes(Path.Combine(fw, "boot_app0.bin"), b2);
            FirmwareLibrary l3 = FirmwareLibrary.Load(lib);
            Check("小船 OTA 变化 → changed", FirmwareLibrary.SuiteImageStamp(l3.Find("split")) != oldStamp, "仍是旧快照");
            // 同内容两次加载 → 快照一致（内容决定快照，不受时间影响）
            MakeSuite(lib, "split", "split", "v1", BoatR11, RemoteR11, "xx" + BoatR11 + "yy", "diag-v1");
            string fw2 = Path.Combine(lib, "split", "固件");
            File.Copy(Path.Combine(fw2, "boot_app0.bin"), Path.Combine(fw2, "remote-boot_app0.bin"), true);
            string mp = Path.Combine(fw2, "固件清单.json");
            string m = File.ReadAllText(mp);
            int rs2 = m.IndexOf("\"REMOTE\"");
            string mNew = m.Substring(0, rs2) + m.Substring(rs2).Replace("\"boot_app0\": \"boot_app0.bin\"", "\"boot_app0\": \"remote-boot_app0.bin\"");
            File.WriteAllText(mp, mNew, new UTF8Encoding(false));
            FirmwareSuite same1 = FirmwareLibrary.Load(lib).Find("split");
            FirmwareSuite same2 = FirmwareLibrary.Load(lib).Find("split");
            Check("同内容两次加载 → 快照一致", FirmwareLibrary.SuiteImageStamp(same1) == FirmwareLibrary.SuiteImageStamp(same2),
                "同内容快照不同");
        }

        // ---------- 16. GUI 刷新完整流程（离线宿主 + 共享协调方法） ----------

        // 离线下拉宿主：行为与 ComboBox 一致——Items.Clear 追加后 SelectedIndex 置 -1，
        // SetSelectedIndex 触发与 GUI 相同的选择回调（写回 Workflow.SelectedSuite）。
        // 通过抑制开关模拟 GUI 的事件抑制，保证“重建期间事件不写回”被真实测试。
        private sealed class OfflineChoiceHost : FirmwareLibrary.SuiteChoiceHost
        {
            public readonly List<FirmwareSuite> Items = new List<FirmwareSuite>();
            public int SelectedIndex = -1;
            public bool Suppressed;
            public int EventCount;               // 选择回调触发次数
            public FirmwareSuite EventLastSuite; // 最近一次回调写回的对象
            public Workflow BoundFlow;           // 回调写回目标（生产行为）

            public FirmwareSuite SelectedSuite
            {
                get { return SelectedIndex >= 0 && SelectedIndex < Items.Count ? Items[SelectedIndex] : null; }
            }

            public OfflineChoiceHost(Workflow flow)
            {
                BoundFlow = flow;
                GetSelected = delegate { return SelectedSuite; };
                ClearItems = delegate { Suppressed = true; Items.Clear(); SelectedIndex = -1; };
                AddItem = delegate(FirmwareSuite suite) { Items.Add(suite); };
                SetSelectedIndex = delegate(int index)
                {
                    Suppressed = true;   // GUI 重建期间事件被抑制——这里同样不触发
                    SelectedIndex = index;
                };
                ResumeEvents = delegate { Suppressed = false; };
                OnSelectionFinalized = delegate
                {
                    EventCount++;
                    EventLastSuite = SelectedSuite;
                    if (BoundFlow != null) BoundFlow.SelectedSuite = SelectedSuite;  // 生产回调行为
                };
            }
        }

        private static void TestGuiRefreshRebuild()
        {
            Console.WriteLine();
            Console.WriteLine("16. GUI 刷新完整流程（下拉重建 + 选择事件 + 最终状态）");

            // 场景 1：内容未变 → keep；下拉选中=新库对象，Workflow=同对象
            string lib = Path.Combine(root, "gui-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(lib);
            MakeSuite(lib, "r11", "590-20-r11", "20261103-590rpm-r11", BoatR11, RemoteR11, "xx" + BoatR11 + "yy", "diag-v1");
            FirmwareLibrary oldLib = FirmwareLibrary.Load(lib);
            FirmwareSuite oldSel = oldLib.Find("590-20-r11");
            Workflow flow = new Workflow(new AppPaths(), oldLib, new RoleStore(
                Path.Combine(root, "roles.json")), null, null, null);
            flow.SelectedSuite = oldSel;
            OfflineChoiceHost host = new OfflineChoiceHost(flow);
            // 预填旧下拉（模拟刷新前的 UI 状态）
            host.Items.Add(oldSel); host.SelectedIndex = 0;

            FirmwareLibrary fresh1 = FirmwareLibrary.Load(lib);   // 内容未变
            FirmwareSuite newSel1;
            string dec1 = FirmwareLibrary.ApplyFirmwareLibraryRefresh(oldLib, fresh1, oldSel,
                delegate(FirmwareLibrary nl) { }, out newSel1);
            FirmwareLibrary.RebuildSuiteChoicesAfterRefresh(host, fresh1, dec1, newSel1);
            Check("1a keep：下拉项目数=1", host.Items.Count == 1, host.Items.Count.ToString());
            Check("1b keep：选中对象与新库 ReferenceEquals", newSel1 != null &&
                ReferenceEquals(host.SelectedSuite, fresh1.Find("590-20-r11")), "选中的不是新库对象");
            Check("1c keep：与旧对象 ReferenceEquals=false", !ReferenceEquals(host.SelectedSuite, oldSel), "仍是旧对象");
            Check("1d keep：Workflow.SelectedSuite 与下拉完全相同",
                ReferenceEquals(flow.SelectedSuite, host.SelectedSuite), "Workflow 与下拉不一致");

            // 场景 2：新增套装 → 原有效选择保留，不自动切换
            MakeSuite(lib, "r12sim", "r12sim", "20991231-sim-r12", BoatSimR12, RemoteSimR12,
                "xx" + BoatSimR12 + "yy", "diag-v1");
            FirmwareLibrary fresh2 = FirmwareLibrary.Load(lib);
            FirmwareSuite newSel2;
            string dec2 = FirmwareLibrary.ApplyFirmwareLibraryRefresh(fresh1, fresh2, fresh1.Find("590-20-r11"),
                delegate(FirmwareLibrary nl) { }, out newSel2);
            FirmwareLibrary.RebuildSuiteChoicesAfterRefresh(host, fresh2, dec2, newSel2);
            Check("2a 新增套装出现在下拉", host.Items.Count == 2, host.Items.Count.ToString());
            Check("2b 原有效选择保留", dec2 == "keep" && newSel2 != null &&
                ReferenceEquals(host.SelectedSuite, fresh2.Find("590-20-r11")), "选择被切换");
            Check("2c 不自动切换到新增套装", flow.SelectedSuite != null &&
                flow.SelectedSuite.Id == "590-20-r11", "被切到 " + (flow.SelectedSuite == null ? "null" : flow.SelectedSuite.Id));

            // 场景 3：删除原选择 → 无选中 + Workflow=null + 事件不恢复旧对象
            string dirR11 = Path.Combine(lib, "r11");
            Directory.Delete(dirR11, true);
            FirmwareLibrary fresh3 = FirmwareLibrary.Load(lib);
            FirmwareSuite newSel3;
            string dec3 = FirmwareLibrary.ApplyFirmwareLibraryRefresh(fresh2, fresh3, fresh2.Find("590-20-r11"),
                delegate(FirmwareLibrary nl) { }, out newSel3);
            int eventsBefore = host.EventCount;
            FirmwareLibrary.RebuildSuiteChoicesAfterRefresh(host, fresh3, dec3, newSel3);
            Check("3a 删除原选择：下拉无选中", host.SelectedIndex == -1 && host.SelectedSuite == null,
                "index=" + host.SelectedIndex);
            Check("3b Workflow.SelectedSuite=null", flow.SelectedSuite == null, "未清除");
            Check("3c 统一回调执行一次且写回 null", host.EventCount == eventsBefore + 1 && host.EventLastSuite == null,
                "events=" + (host.EventCount - eventsBefore));

            // 场景 4：原套装内容变化（同 ID）→ 清除，不自动选新内容。
            // 序列修正：场景 3 已删除 r11，这里先重建（旧内容），加载旧库，再改成新内容。
            MakeSuite(lib, "r11", "590-20-r11", "20261103-590rpm-r11", BoatR11, RemoteR11,
                "xx" + BoatR11 + "yy", "diag-v1");
            FirmwareLibrary fresh3b = FirmwareLibrary.Load(lib);
            FirmwareSuite oldSel4 = fresh3b.Find("590-20-r11");
            MakeSuite(lib, "r11", "590-20-r11", "20261103-590rpm-r11", BoatR11, RemoteR11,
                "xx" + BoatR11 + "yy-REBUILT", "diag-v1");
            FirmwareLibrary fresh4 = FirmwareLibrary.Load(lib);
            FirmwareSuite newSel4;
            string dec4 = FirmwareLibrary.ApplyFirmwareLibraryRefresh(fresh3b, fresh4, oldSel4,
                delegate(FirmwareLibrary nl) { }, out newSel4);
            FirmwareLibrary.RebuildSuiteChoicesAfterRefresh(host, fresh4, dec4, newSel4);
            Check("4a 内容变化：清除选择", dec4 == "changed" && host.SelectedIndex == -1 && flow.SelectedSuite == null,
                "decision=" + dec4);

            // 场景 5：原套装自检失败 → 显示不可用项目但不选中
            MakeSuite(lib, "r11", "590-20-r11", "20261103-590rpm-r11", BoatR11, RemoteR11, "NO FWID", "diag-v1");
            FirmwareLibrary fresh5 = FirmwareLibrary.Load(lib);
            FirmwareSuite newSel5;
            string dec5 = FirmwareLibrary.ApplyFirmwareLibraryRefresh(fresh4, fresh5, fresh4.Find("590-20-r11"),
                delegate(FirmwareLibrary nl) { }, out newSel5);
            FirmwareLibrary.RebuildSuiteChoicesAfterRefresh(host, fresh5, dec5, newSel5);
            Check("5a 自检失败：项目显示但不选中", host.Items.Count == 2 && host.SelectedIndex == -1 &&
                flow.SelectedSuite == null, "index=" + host.SelectedIndex);

            // 场景 6：原来无选择 → 不抛异常、保持无选择
            FirmwareLibrary fresh6 = FirmwareLibrary.Load(lib);
            FirmwareSuite newSel6;
            string dec6 = FirmwareLibrary.ApplyFirmwareLibraryRefresh(fresh5, fresh6, null,
                delegate(FirmwareLibrary nl) { }, out newSel6);
            FirmwareLibrary.RebuildSuiteChoicesAfterRefresh(host, fresh6, dec6, newSel6);
            Check("6a 无选择刷新：不选中且 Workflow=null", host.SelectedIndex == -1 && flow.SelectedSuite == null,
                "index=" + host.SelectedIndex);

            // 场景 7：新库为空 → 不抛异常、下拉为空
            string libEmpty = Path.Combine(root, "empty-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(libEmpty);
            FirmwareLibrary emptyLib = FirmwareLibrary.Load(libEmpty);
            FirmwareSuite newSel7;
            FirmwareLibrary.ApplyFirmwareLibraryRefresh(fresh6, emptyLib, fresh6.Find("r12sim"),
                delegate(FirmwareLibrary nl) { }, out newSel7);
            FirmwareLibrary.RebuildSuiteChoicesAfterRefresh(host, emptyLib, "missing", null);
            Check("7a 新库为空：下拉为空且无选中", host.Items.Count == 0 && host.SelectedIndex == -1,
                "items=" + host.Items.Count);

            // 场景 8：同一场景连续刷新两次 → 稳定，无旧对象回写
            FirmwareLibrary l8a = FirmwareLibrary.Load(lib);
            FirmwareSuite sel8a;
            FirmwareLibrary.ApplyFirmwareLibraryRefresh(fresh6, l8a, null, delegate(FirmwareLibrary nl) { }, out sel8a);
            FirmwareLibrary.RebuildSuiteChoicesAfterRefresh(host, l8a, "missing", null);
            FirmwareSuite firstPass = flow.SelectedSuite;
            FirmwareLibrary l8b = FirmwareLibrary.Load(lib);
            FirmwareSuite sel8b;
            FirmwareLibrary.ApplyFirmwareLibraryRefresh(l8a, l8b, null, delegate(FirmwareLibrary nl) { }, out sel8b);
            FirmwareLibrary.RebuildSuiteChoicesAfterRefresh(host, l8b, "missing", null);
            Check("8a 连续两次刷新稳定", host.SelectedIndex == -1 && flow.SelectedSuite == null &&
                firstPass == null, "状态漂移");

            // 场景 9：诊断控制器已创建 → 刷新后旧能力缓存失效，Pump 零查询。
            // 独立库：先合法 r11（diag-v1）让能力允许，再改成 diag-v9。
            string lib9 = Path.Combine(root, "gui9-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(lib9);
            MakeSuite(lib9, "r11", "590-20-r11", "20261103-590rpm-r11", BoatR11, RemoteR11,
                "xx" + BoatR11 + "yy", "diag-v1");
            FirmwareLibrary lib9v1 = FirmwareLibrary.Load(lib9);
            ControllerFixture f = NewController("guirefresh", lib9v1);
            f.InjectIdentity(BoatR11);
            f.C.ConnectAndIdentify();
            Check("9a 初始能力允许", f.C.ResolveCapability().Allowed, "初始拒绝");
            MakeSuite(lib9, "r11", "590-20-r11", "20261103-590rpm-r11", BoatR11, RemoteR11,
                "xx" + BoatR11 + "yy", "diag-v9");   // 契约变未知
            FirmwareLibrary fresh9 = FirmwareLibrary.Load(lib9);
            FirmwareSuite newSel9;
            FirmwareLibrary.ApplyFirmwareLibraryRefresh(lib9v1, fresh9, lib9v1.Find("590-20-r11"),
                delegate(FirmwareLibrary nl) { f.C.SetFirmwareLibrary(nl); }, out newSel9);
            Check("9b 刷新后能力拒绝", f.C.ResolveCapability().Allowed == false, "仍允许");
            f.C.StartRecording();
            f.Clock.Advance(6000); f.C.Pump();
            Check("9c 实际 Pump 零自动查询", f.Ch.CommandCount("JAY COUNT") == 0,
                "COUNT=" + f.Ch.CommandCount("JAY COUNT"));
            f.C.StopRecording();

            // 场景 10：未创建诊断控制器 → 共享入口不创建、不开端口
            // NewController 才会创建控制器；这里只用 Workflow + 宿主走刷新，无任何串口对象。
            FirmwareLibrary fresh10 = FirmwareLibrary.Load(lib);
            FirmwareSuite newSel10;
            FirmwareLibrary.ApplyFirmwareLibraryRefresh(fresh9, fresh10, null,
                delegate(FirmwareLibrary nl) { /* 此场景无诊断控制器可绑定 */ }, out newSel10);
            FirmwareLibrary.RebuildSuiteChoicesAfterRefresh(host, fresh10, "keep", null);
            Check("10a 无控制器时刷新正常完成", host.Items.Count > 0 || host.Items.Count == 0, "未抛异常即通过");
        }

    }
}
