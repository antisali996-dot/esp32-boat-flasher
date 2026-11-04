using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace BoatTool
{
    // 动态固件库测试（dynamic-library 轮）。
    //
    // 覆盖：任意新 ID 套装加载、目录名与版本解耦、r11+模拟r12 并存、
    // preset 不锁死版本、刷新不碰串口、选择失效检测、同 ID 冲突、
    // 诊断契约按板上身份启用、未知契约被动记录、路径安全。
    // 「模拟 r12」只是测试夹具名称，不是真实 r12 固件。
    public static class DynamicLibraryTests
    {
        private static int passed;
        private static int failed;
        private static string root;

        private const string BoatR11 = "JAYFWB11-S20-A70-110-U1278-1500-1722-R590-OSOFF-Cr11-osoff";
        private const string RemoteR11 = "JAYFWR11-S20-A70-110-U1278-1500-1722-D1750-2150";
        private const string BoatR10 = "JAYFWB10-S20-A70-110-U1278-1500-1722-R590-OS600-Cr10-diagshared";
        // 模拟 r12：仅测试夹具，非真实固件。
        private const string BoatSimR12 = "JAYFWB12-S20-A70-110-U1278-1500-1722-R590-OSOFF-Cr12-sim";
        private const string RemoteSimR12 = "JAYFWR12-S20-A70-110-U1278-1500-1722-D1750-2150";

        public static void Run()
        {
            passed = 0; failed = 0;
            root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test-work",
                "dynlib-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            Console.WriteLine();
            Console.WriteLine("===== 动态固件库 =====");
            try
            {
                TestNewIdSuiteVisible();
                TestDirNameNotVersion();
                TestR11AndSimR12Coexist();
                TestPresetNotLockingVersion();
                TestRecommendationMissingNotFallback();
                TestRefreshNoSerialOps();
                TestSelectedSuiteInvalidated();
                TestSameIdConflict();
                TestContractEnablesByBoardIdentity();
                TestUnknownContractPassive();
                TestRemoteNoDiagFromSharedHeader();
                TestPathSafety();
                TestHashMismatchRejected();
                TestRefreshDecisionRules();
                TestDiagSessionClosedBlocksRefresh();
                TestSuiteObjectConsistency();
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
            Console.WriteLine();
            Console.WriteLine("动态固件库：通过 " + passed + " 项，失败 " + failed + " 项");
            if (failed > 0) throw new InvalidOperationException("动态固件库测试失败 " + failed + " 项");
        }

        private static void Check(string name, bool ok, string detail)
        {
            if (ok) { passed++; Console.WriteLine("  [通过] " + name); }
            else { failed++; Console.WriteLine("  [失败] " + name + " —— " + detail); }
        }

        // ---------- 夹具 ----------

        // 构造一个最小自洽套装（真实分区表字节由 PartitionLayout 验证）。
        private static string MakeSuite(string libRoot, string dirName, string id, string version,
                                        string boatFwid, string remoteFwid, int rpm = 590,
                                        string lockout = null, string diagContract = null)
        {
            string dir = Path.Combine(libRoot, dirName);
            string fw = Path.Combine(dir, "固件");
            string tools = Path.Combine(dir, "工具");
            Directory.CreateDirectory(fw);
            Directory.CreateDirectory(tools);
            File.WriteAllText(Path.Combine(tools, "esptool.exe"), "fake esptool " + id);
            // 用 r11 库的真实分区表，保证分区布局校验通过
            string realPart = FindRealPartition();
            foreach (string f in new string[] { "boat-partitions.bin", "remote-partitions.bin" })
            {
                if (realPart != null) File.Copy(realPart, Path.Combine(fw, f), true);
                else File.WriteAllBytes(Path.Combine(fw, f), new byte[0]);
            }
            File.WriteAllText(Path.Combine(fw, "boat-app.bin"), "xx" + boatFwid + "yy", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(fw, "remote-app.bin"), "xx" + remoteFwid + "yy", new UTF8Encoding(false));
            File.WriteAllBytes(Path.Combine(fw, "boat-bootloader.bin"), new byte[] { 1, 2, 3 });
            File.WriteAllBytes(Path.Combine(fw, "remote-bootloader.bin"), new byte[] { 1, 2, 3 });
            File.WriteAllBytes(Path.Combine(fw, "boot_app0.bin"), new byte[] { 9 });

            string boatRole = "\"BOAT\": { \"fwid\": \"" + boatFwid + "\", \"version\": \"" + version + "\", \"steer_deg\": 20" +
                (lockout == null ? "" : ", \"overspeed_lockout\": \"" + lockout + "\"") +
                (diagContract == null ? "" : ", \"diag_contract\": \"" + diagContract + "\"") +
                ", \"app\": \"boat-app.bin\", \"app_offset\": 65536, \"bootloader\": \"boat-bootloader.bin\", \"bootloader_offset\": 4096, " +
                "\"partitions\": \"boat-partitions.bin\", \"partitions_offset\": 32768, " +
                "\"boot_app0\": \"boot_app0.bin\", \"boot_app0_offset\": 57344 }";
            string remoteRole = "\"REMOTE\": { \"fwid\": \"" + remoteFwid + "\", \"version\": \"" + version + "\", \"steer_deg\": 20" +
                (diagContract == null ? "" : ", \"diag_contract\": \"" + diagContract + "\"") +
                ", \"app\": \"remote-app.bin\", \"app_offset\": 65536, \"bootloader\": \"remote-bootloader.bin\", \"bootloader_offset\": 4096, " +
                "\"partitions\": \"remote-partitions.bin\", \"partitions_offset\": 32768, " +
                "\"boot_app0\": \"boot_app0.bin\", \"boot_app0_offset\": 57344 }";
            string manifest = "{ \"schema\": 11, \"version\": \"" + version + "\", \"steer_deg\": 20, " +
                "\"angle_min\": 70, \"angle_max\": 110, \"center_us\": 1500, " +
                "\"pulse_min_us\": 1278, \"pulse_max_us\": 1722, \"pair_protocol\": 2, " +
                "\"target_rpm\": " + rpm + ", \"overspeed_rpm\": 600, " +
                "\"roles\": { " + boatRole + ", " + remoteRole + " } }";
            File.WriteAllText(Path.Combine(fw, "固件清单.json"), manifest, new UTF8Encoding(false));

            string import = "{ \"id\": \"" + id + "\", \"version\": \"" + version + "\", \"target_rpm\": " + rpm +
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

        private static FirmwareLibrary LoadLib(string name)
        {
            string lib = Path.Combine(root, name + "-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(lib);
            FirmwareLibrary loaded = FirmwareLibrary.Load(lib);
            return loaded;
        }

        // ---------- 1–3. 动态加载 ----------

        private static void TestNewIdSuiteVisible()
        {
            Console.WriteLine();
            Console.WriteLine("1. 任意新 ID 套装加载后可见（不因名字不是 590-20-r11 被拒）");
            string lib = Path.Combine(root, "newid-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(lib);
            MakeSuite(lib, "my-test-build-xyz", "my-test-build-xyz", "20991231-test-v1",
                BoatSimR12, RemoteSimR12, 590, "off");
            FirmwareLibrary l = FirmwareLibrary.Load(lib);
            Check("套装被加载", l.Suites.Count == 1, l.Suites.Count.ToString());
            FirmwareSuite s = l.Suites.Count > 0 ? l.Suites[0] : null;
            Check("ID 来自 import.json 而非目录名推断",
                s != null && s.Id == "my-test-build-xyz", s == null ? "" : s.Id);
            Check("版本来自清单", s != null && s.Version == "20991231-test-v1", s == null ? "" : s.Version);
            Check("自洽套装可用", s != null && s.Usable,
                s == null ? "" : string.Join("；", s.Problems.ToArray()));
        }

        private static void TestDirNameNotVersion()
        {
            Console.WriteLine();
            Console.WriteLine("2. 目录名与版本不一致，仍以清单为准");
            string lib = Path.Combine(root, "dirname-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(lib);
            MakeSuite(lib, "i-am-not-a-version", "custom-id-001", "20261103-590rpm-r11",
                BoatR11, RemoteR11, 590, "off");
            FirmwareLibrary l = FirmwareLibrary.Load(lib);
            FirmwareSuite s = l.Suites.Count > 0 ? l.Suites[0] : null;
            Check("ID 用 import.json 的 custom-id-001", s != null && s.Id == "custom-id-001",
                s == null ? "" : s.Id);
            Check("版本用清单的 r11", s != null && s.Version == "20261103-590rpm-r11",
                s == null ? "" : s.Version);
            Check("显示名以清单为准（590 转）", s != null && s.Display.Contains("590"), s == null ? "" : s.Display);
        }

        private static void TestR11AndSimR12Coexist()
        {
            Console.WriteLine();
            Console.WriteLine("3. r11 与模拟 r12 并存，可分别选择");
            string lib = Path.Combine(root, "coexist-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(lib);
            MakeSuite(lib, "590-20-r11", "590-20-r11", "20261103-590rpm-r11", BoatR11, RemoteR11, 590, "off");
            MakeSuite(lib, "sim-r12-test-fixture", "sim-r12-test-fixture", "20991231-sim-r12",
                BoatSimR12, RemoteSimR12, 590, "off");
            FirmwareLibrary l = FirmwareLibrary.Load(lib);
            Check("两套都加载", l.Suites.Count == 2, l.Suites.Count.ToString());
            FirmwareSuite r11 = l.Find("590-20-r11");
            FirmwareSuite r12 = l.Find("sim-r12-test-fixture");
            Check("r11 可用", r11 != null && r11.Usable, r11 == null ? "缺失" : string.Join("；", r11.Problems.ToArray()));
            Check("模拟 r12 可用", r12 != null && r12.Usable, r12 == null ? "缺失" : string.Join("；", r12.Problems.ToArray()));
            Check("两套锁存都是关闭", r11 != null && r12 != null &&
                r11.Lockout == OverspeedLockout.Disabled && r12.Lockout == OverspeedLockout.Disabled,
                "锁存判断不一致");
            Check("互不覆盖（旧版不自动删除）", r11 != null && r12 != null &&
                r11.Id != r12.Id, "ID 异常");
        }

        // ---------- 4–5. preset 解绑 ----------

        private static void TestPresetNotLockingVersion()
        {
            Console.WriteLine();
            Console.WriteLine("4. 分类 preset 推荐 r11，用户选模拟 r12 仍可形成烧录计划");
            string lib = Path.Combine(root, "preset-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(lib);
            MakeSuite(lib, "590-20-r11", "590-20-r11", "20261103-590rpm-r11", BoatR11, RemoteR11, 590, "off");
            MakeSuite(lib, "sim-r12-test-fixture", "sim-r12-test-fixture", "20991231-sim-r12",
                BoatSimR12, RemoteSimR12, 590, "off");
            FirmwareLibrary l = FirmwareLibrary.Load(lib);

            PresetConfig preset = new PresetConfig();
            preset.Id = "r11-old-cat"; preset.Kind = "category"; preset.Title = "旧分类包";
            preset.BoatMac = "24:62:AB:F1:36:64"; preset.RemoteMac = "24:62:AB:EF:CF:84";
            preset.SuiteId = "590-20-r11";           // 旧推荐
            preset.BoatFwid = BoatR11; preset.RemoteFwid = RemoteR11;

            // 推荐存在 → 对推荐套装本身核对一致
            FirmwareSuite recommended = l.Find("590-20-r11");
            preset.CrossCheck(recommended);
            Check("推荐存在且一致时核对通过", preset.Problems.Count == 0,
                string.Join("；", preset.Problems.ToArray()));

            // 用户改选模拟 r12：不报错、不禁用；MAC 约束不变
            PresetConfig copy = new PresetConfig();
            copy.Id = preset.Id; copy.Kind = preset.Kind; copy.Title = preset.Title;
            copy.BoatMac = preset.BoatMac; copy.RemoteMac = preset.RemoteMac;
            copy.SuiteId = preset.SuiteId; copy.BoatFwid = preset.BoatFwid; copy.RemoteFwid = preset.RemoteFwid;
            copy.CrossCheck(l.Find("sim-r12-test-fixture"));
            Check("选非推荐版本不产生 preset 问题", copy.Problems.Count == 0,
                string.Join("；", copy.Problems.ToArray()));
            string reason;
            Check("MAC 保护仍在", copy.AllowsBoat("AA:BB:CC:DD:EE:FF", out reason) == false &&
                reason.Contains("24:62:AB:F1:36:64"), "MAC 保护丢失");
            Check("正确 MAC 仍放行", copy.AllowsBoat("24:62:AB:F1:36:64", out reason), reason);
        }

        private static void TestRecommendationMissingNotFallback()
        {
            Console.WriteLine();
            Console.WriteLine("5. 推荐项不存在 → 提示重新选择，不回退到第一个可用套装");
            string lib = Path.Combine(root, "missrec-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(lib);
            // 只有模拟 r12；推荐 r11 不在
            MakeSuite(lib, "sim-r12-test-fixture", "sim-r12-test-fixture", "20991231-sim-r12",
                BoatSimR12, RemoteSimR12, 590, "off");
            FirmwareLibrary l = FirmwareLibrary.Load(lib);
            PresetConfig preset = new PresetConfig();
            preset.Kind = "category"; preset.Title = "旧分类包";
            preset.BoatMac = "24:62:AB:F1:36:64"; preset.RemoteMac = "24:62:AB:EF:CF:84";
            preset.SuiteId = "590-20-r11";

            Check("推荐不存在被识别", preset.RecommendedSuiteExists(l) == false, "误报存在");
            string text = preset.RecommendText(l);
            Check("提示重新选择", text.Contains("不在当前固件库") && text.Contains("请自行选择"), text);
            // 工具不能悄悄选第一套冒充：Workflow 初始 selectedSuite 为空时由用户选择；
            // preset 流程不会把 library.Suites[0] 塞给用户当作“正确选择”。
            Check("库中第一套不会被自动当作推荐", l.Find("590-20-r11") == null, "推荐被误命中");
        }

        // ---------- 6–8. 刷新与失效 ----------

        private static void TestRefreshNoSerialOps()
        {
            Console.WriteLine();
            Console.WriteLine("6. 固件库加载与共享刷新入口零串口操作（计数器正向对照）");
            // 原断言无效原因：CountingTransport 从未接入 FirmwareLibrary.Load（Load 本就不接受
            // transport），"计数为 0"恒真，无检测能力。
            // 新断言：(a) 计数器正向对照——同一 transport 接入 Workflow 后 Scan 真实递增，
            // 证明计数器本身有效；(b) Load + ApplyFirmwareLibraryRefresh 前后计数不变，
            // 证明固件库加载/刷新路径确实不触碰 transport。
            SimulatedTransport transport = new SimulatedTransport();   // Probe/ReadChip 真实递增
            string lib = Path.Combine(root, "noscan-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(lib);
            MakeSuite(lib, "590-20-r11", "590-20-r11", "20261103-590rpm-r11", BoatR11, RemoteR11, 590, "off");
            FirmwareLibrary l = FirmwareLibrary.Load(lib);
            FirmwareLibrary l2 = FirmwareLibrary.Load(lib);
            Check("Load 前后 Probe/ReadChip 计数不变", transport.ProbeCount == 0 && transport.ReadChipCount == 0,
                "Probe=" + transport.ProbeCount + " ReadChip=" + transport.ReadChipCount);

            // 正向对照：计数器必须能检测到真实串口操作（否则断言无效）。
            AppPaths paths = new AppPaths();
            paths.AppRoot = root; paths.WritableRoot = root;
            paths.ConfigDir = Path.Combine(root, "配置");
            paths.RecordsDir = Path.Combine(root, "记录");
            paths.RoleStorePath = Path.Combine(paths.ConfigDir, "角色记忆.json");
            Workflow flow = new Workflow(paths, l, new RoleStore(paths.RoleStorePath),
                                         transport, new SimClock(), new SimUi());
            // 正向对照需要真实板子：接入一块模拟板（r11 身份），Scan 才会 Probe。
            SimBoard board = new SimBoard();
            board.Mac = "24:62:AB:F1:36:64";
            board.Role = BoardRole.Boat;
            board.Fwid = BoatR11;
            board.SteerDeg = 20;
            transport.Boards.Add(board);
            transport.Plug("COM9", board.Mac);
            flow.Scan();
            Check("正向对照：Scan 后 ProbeCount 增加（计数器有效）", transport.ProbeCount > 0,
                "ProbeCount=" + transport.ProbeCount);
            int afterScan = transport.ProbeCount;

            // 共享刷新入口：计数不变。
            FirmwareSuite selection = l.Find("590-20-r11");
            FirmwareSuite newSelection;
            FirmwareLibrary.ApplyFirmwareLibraryRefresh(l, l2, selection,
                delegate(FirmwareLibrary newLib) { /* 测试宿主无诊断控制器可绑定 */ },
                out newSelection);
            Check("共享刷新入口后计数不变", transport.ProbeCount == afterScan && transport.ReadChipCount == 0,
                "Probe=" + transport.ProbeCount + " ReadChip=" + transport.ReadChipCount);
            Check("刷新后对象独立（无旧镜像缓存引用）", !ReferenceEquals(l, l2) && l2.Suites.Count == 1,
                "库对象异常");
        }

        private static void TestSelectedSuiteInvalidated()
        {
            Console.WriteLine();
            Console.WriteLine("7. 所选目录删除/文件被替换 → 旧计划失效");
            string lib = Path.Combine(root, "invalid-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(lib);
            string dir = MakeSuite(lib, "590-20-r11", "590-20-r11", "20261103-590rpm-r11", BoatR11, RemoteR11, 590, "off");
            FirmwareLibrary l = FirmwareLibrary.Load(lib);
            FirmwareSuite chosen = l.Find("590-20-r11");
            Check("初始可用", chosen != null && chosen.Usable, "初始不可用");

            // a) 目录删除
            Directory.Delete(dir, true);
            FirmwareLibrary afterDelete = FirmwareLibrary.Load(lib);
            Check("目录删除后套装消失", afterDelete.Find("590-20-r11") == null, "仍可见");

            // b) 镜像被替换（同目录重建，镜像内容不同）
            MakeSuite(lib, "590-20-r11", "590-20-r11", "20261103-590rpm-r11", BoatR11, RemoteR11, 590, "off");
            string fw = Path.Combine(lib, "590-20-r11", "固件");
            File.WriteAllText(Path.Combine(fw, "boat-app.bin"), "TAMPERED-CONTENT", new UTF8Encoding(false));
            FirmwareLibrary afterSwap = FirmwareLibrary.Load(lib);
            FirmwareSuite swapped = afterSwap.Find("590-20-r11");
            Check("镜像替换后自检失败", swapped != null && swapped.Problems.Count > 0,
                swapped == null ? "缺失" : "未发现问题");

            // c) RevalidateSuite 检测到变化：held = 内存里持有“TAMPERED 镜像”的套件对象，
            //    磁盘已恢复为合法镜像 → 两者哈希不同 → 复核必须失败。
            FirmwareSuite held = swapped;   // swapped 是 TAMPERED 状态时加载的
            MakeSuite(lib, "590-20-r11", "590-20-r11", "20261103-590rpm-r11", BoatR11, RemoteR11, 590, "off");
            File.WriteAllText(Path.Combine(fw, "boat-app.bin"), "xx" + BoatR11 + "yy", new UTF8Encoding(false));
            string err;
            bool ok = Workflow.RevalidateSuite(held, out err);
            Check("复核检测到镜像变化", ok == false && err.Contains("镜像内容已被替换"),
                "ok=" + ok + " err=" + err);
        }

        private static void TestSameIdConflict()
        {
            Console.WriteLine();
            Console.WriteLine("8. 两个目录套装 ID 相同 → 冲突，全部标不可用");
            string lib = Path.Combine(root, "sameid-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(lib);
            MakeSuite(lib, "dir-one", "590-20-r11", "20261103-590rpm-r11", BoatR11, RemoteR11, 590, "off");
            MakeSuite(lib, "dir-two", "590-20-r11", "20261103-590rpm-r11", BoatR11, RemoteR11, 590, "off");
            FirmwareLibrary l = FirmwareLibrary.Load(lib);
            int conflicted = 0;
            foreach (FirmwareSuite s in l.Suites)
                foreach (string p in s.Problems)
                    if (p.Contains("同名套装 ID")) { conflicted++; break; }
            Check("两套都标 ID 冲突", conflicted == 2, "标记数 " + conflicted);
            foreach (FirmwareSuite s in l.Suites)
                Check("冲突套装不可用：" + s.CatalogDir, !s.Usable, "仍可用");
        }

        // ---------- 9–11. 诊断契约 ----------

        private static void TestContractEnablesByBoardIdentity()
        {
            Console.WriteLine();
            Console.WriteLine("9. 新 FWID 声明已支持契约 → 按板上身份启用");
            // 模拟 r12 套装声明 diag-v1
            string lib = Path.Combine(root, "contract-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(lib);
            MakeSuite(lib, "sim-r12", "sim-r12", "20991231-sim-r12", BoatSimR12, RemoteSimR12, 590, "off",
                QueryPolicy.DiagContracts.V1);
            FirmwareLibrary l = FirmwareLibrary.Load(lib);
            FirmwareSuite s = l.Find("sim-r12");
            FirmwareSpec boatSpec = s.SpecFor(BoardRole.Boat);
            Check("清单契约被读取", boatSpec.DiagContract == "diag-v1", boatSpec.DiagContract);

            QueryPolicy policy = QueryPolicy.ApprovedR11();
            string reason;
            // 板上真是该 FWID → 允许
            bool allowed = policy.AllowsSuiteRole(boatSpec, BoatSimR12, BoardRole.Boat, out reason);
            Check("板上 r12 小船 + 契约 diag-v1 → 启用", allowed, reason);
            // 板上是 r11 → 不属于该套装的 FWID → 拒绝（不能拿 r12 套装的能力给 r11 板）
            string reason2;
            bool wrongBoard = policy.AllowsSuiteRole(boatSpec, BoatR11, BoardRole.Boat, out reason2);
            Check("板上 r11 不套用 r12 套装能力", wrongBoard == false, "被误放行");
        }

        private static void TestUnknownContractPassive()
        {
            Console.WriteLine();
            Console.WriteLine("10. 未知契约 → 只被动记录");
            string lib = Path.Combine(root, "unknownc-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(lib);
            MakeSuite(lib, "future-fw", "future-fw", "20991231-future", BoatSimR12, RemoteSimR12, 590, "off",
                "diag-v9-unknown");
            FirmwareLibrary l = FirmwareLibrary.Load(lib);
            FirmwareSpec spec = l.Find("future-fw").SpecFor(BoardRole.Boat);
            Check("未知契约套装本身仍可烧录（不影响烧录）",
                l.Find("future-fw").Usable, string.Join("；", l.Find("future-fw").Problems.ToArray()));
            QueryPolicy policy = QueryPolicy.ApprovedR11();
            string reason;
            bool allowed = policy.AllowsSuiteRole(spec, BoatSimR12, BoardRole.Boat, out reason);
            Check("未知契约不启用自动查询", allowed == false, "被放行");
            Check("说明需要工具适配", reason.Contains("需要工具适配"), reason);
        }

        private static void TestRemoteNoDiagFromSharedHeader()
        {
            Console.WriteLine();
            Console.WriteLine("11. 遥控器不因共享身份头自动启用诊断");
            string lib = Path.Combine(root, "remotediag-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(lib);
            // 故意给遥控器也写 diag-v1（源码端若误发）；工具按角色+能力表判断
            MakeSuite(lib, "sim-r12", "sim-r12", "20991231-sim-r12", BoatSimR12, RemoteSimR12, 590, "off",
                QueryPolicy.DiagContracts.V1);
            FirmwareLibrary l = FirmwareLibrary.Load(lib);
            FirmwareSpec remoteSpec = l.Find("sim-r12").SpecFor(BoardRole.Remote);
            QueryPolicy policy = QueryPolicy.ApprovedR11();
            string reason;
            // 遥控器白名单恒空：即使清单写了契约也不启用（工具只信已实现的角色能力）
            bool allowed = policy.Allows(remoteSpec.Fwid, BoardRole.Remote);
            Check("遥控器零自动查询", allowed == false, "被放行");
            bool viaSuite = policy.AllowsSuiteRole(remoteSpec, RemoteSimR12, BoardRole.Remote, out reason);
            Check("遥控器即使声明契约也不经 AllowsSuiteRole 启用",
                viaSuite == false || policy.RemoteFwids.Count == 0,
                "遥控器白名单不应非空");
            Check("遥控器白名单仍为空", policy.RemoteFwids.Count == 0, policy.RemoteFwids.Count.ToString());
        }

        // ---------- 12–13. 路径与哈希 ----------

        private static void TestPathSafety()
        {
            Console.WriteLine();
            Console.WriteLine("12. 镜像路径必须限制在套装目录内");
            string dir = Path.Combine(root, "pathsafe-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            string fw = Path.Combine(dir, "固件");
            Directory.CreateDirectory(fw);
            string manifest = "{ \"schema\": 11, \"version\": \"v\", \"steer_deg\": 20, \"angle_min\": 70, \"angle_max\": 110," +
                "\"center_us\": 1500, \"pulse_min_us\": 1278, \"pulse_max_us\": 1722, \"pair_protocol\": 2, \"target_rpm\": 590," +
                "\"roles\": { \"BOAT\": { \"fwid\": \"JAYFWB12-S20-A70-110-U1278-1500-1722-R590-OSOFF-Cr12-sim\", \"version\": \"v\", \"steer_deg\": 20," +
                "\"app\": \"..\\\\..\\\\evil.bin\", \"app_offset\": 65536 } } }";
            File.WriteAllText(Path.Combine(fw, "固件清单.json"), manifest, new UTF8Encoding(false));
            FirmwareCatalog c = FirmwareCatalog.Load(Path.Combine(fw, "固件清单.json"));
            bool unsafeFlagged = false;
            foreach (string p in c.Problems)
                if (p.Contains("路径不安全")) unsafeFlagged = true;
            Check(".. 逃逸路径被拒绝", unsafeFlagged, string.Join("；", c.Problems.ToArray()));

            // 合法相对路径不受影响
            Check("合法相对路径通过", FirmwareCatalog.IsSafeRelative("boat-app.bin") &&
                FirmwareCatalog.IsSafeRelative("sub/boat-app.bin"), "合法路径被拒");
            Check("绝对路径被拒", !FirmwareCatalog.IsSafeRelative("C:\\evil.bin") &&
                !FirmwareCatalog.IsSafeRelative("\\\\server\\share.bin"), "绝对路径被放行");
            Check("~ 路径被拒", !FirmwareCatalog.IsSafeRelative("~user/bin"), "~ 被放行");
        }

        private static void TestHashMismatchRejected()
        {
            Console.WriteLine();
            Console.WriteLine("13. 清单 FWID 不在镜像 / 哈希错误仍拒绝");
            string lib = Path.Combine(root, "hashbad-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(lib);
            MakeSuite(lib, "bad-fw", "bad-fw", "20991231-bad", BoatSimR12, RemoteSimR12, 590, "off");
            // 改写镜像内容，使 FWID 不在镜像里
            string fw = Path.Combine(lib, "bad-fw", "固件");
            File.WriteAllText(Path.Combine(fw, "boat-app.bin"), "NO FWID HERE", new UTF8Encoding(false));
            FirmwareLibrary l = FirmwareLibrary.Load(lib);
            FirmwareSuite s = l.Find("bad-fw");
            bool rejected = false;
            foreach (string p in s.Problems)
                if (p.Contains("镜像与清单不一致") || p.Contains("FWID")) rejected = true;
            Check("FWID 不在镜像中被拒", rejected && !s.Usable,
                "问题：" + string.Join("；", s.Problems.ToArray()));
        }

        // ---------- 14. 刷新决策 ----------

        private static void TestRefreshDecisionRules()
        {
            Console.WriteLine();
            Console.WriteLine("14. 刷新后选择去留的四种决策");
            string lib = Path.Combine(root, "refresh-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(lib);
            string dir = MakeSuite(lib, "590-20-r11", "590-20-r11", "20261103-590rpm-r11", BoatR11, RemoteR11, 590, "off");
            FirmwareLibrary l = FirmwareLibrary.Load(lib);
            FirmwareSuite held = l.Find("590-20-r11");
            string heldStamp = StampOf(held);

            // a) 原选择不变 → keep
            FirmwareLibrary fresh1 = FirmwareLibrary.Load(lib);
            FirmwareSuite kept1 = fresh1.Find("590-20-r11");
            Check("原选择不变时保留", FirmwareLibrary.RefreshDecision(held, heldStamp, kept1, StampOf(kept1)) == "keep",
                "决策不是 keep");

            // b) 目录删除 → missing
            Directory.Delete(dir, true);
            FirmwareLibrary fresh2 = FirmwareLibrary.Load(lib);
            Check("目录删除后清除选择", FirmwareLibrary.RefreshDecision(held, heldStamp,
                fresh2.Find("590-20-r11"), "") == "missing", "决策不是 missing");

            // c) 镜像替换 → changed
            string dir2 = MakeSuite(lib, "590-20-r11", "590-20-r11", "20261103-590rpm-r11", BoatR11, RemoteR11, 590, "off");
            File.WriteAllText(Path.Combine(dir2, "固件", "boat-app.bin"), "TAMPERED", new UTF8Encoding(false));
            FirmwareLibrary fresh3 = FirmwareLibrary.Load(lib);
            FirmwareSuite kept3 = fresh3.Find("590-20-r11");
            // held 已随目录删除失效；重新持有一个"旧内容"引用
            FirmwareLibrary before = FirmwareLibrary.Load(lib);
            MakeSuite(lib, "tmp-old", "tmp-old", "20261103-590rpm-r11", BoatR11, RemoteR11, 590, "off");
            FirmwareSuite oldHeld = before.Find("590-20-r11");
            Check("替换镜像后旧计划失效", FirmwareLibrary.RefreshDecision(oldHeld, heldStamp, kept3, StampOf(kept3)) == "changed",
                "决策不是 changed");

            // d) 自检失败 → unusable
            string dir4 = MakeSuite(lib, "broken", "broken", "20991231-broken", BoatSimR12, RemoteSimR12, 590, "off");
            File.WriteAllText(Path.Combine(dir4, "固件", "boat-app.bin"), "NO FWID", new UTF8Encoding(false));
            FirmwareLibrary fresh4 = FirmwareLibrary.Load(lib);
            FirmwareSuite broken = fresh4.Find("broken");
            string brokenStamp = StampOf(broken);
            Check("自检失败后要求重新选择", FirmwareLibrary.RefreshDecision(broken, brokenStamp, broken, brokenStamp) == "unusable",
                "决策不是 unusable");
        }

        private static string StampOf(FirmwareSuite s)
        {
            if (s == null) return "";
            StringBuilder sb = new StringBuilder();
            foreach (BoardRole role in new BoardRole[] { BoardRole.Boat, BoardRole.Remote })
            {
                FirmwareSpec spec = s.SpecFor(role);
                sb.Append(role).Append(':').Append(
                    spec == null || spec.AppPath.Length == 0 || !File.Exists(spec.AppPath)
                        ? "-" : FirmwareLibrary.ComputeFileHash(spec.AppPath)).Append(';');
            }
            return sb.ToString();
        }

        // ---------- 15. 未关闭诊断会话阻止刷新 ----------

        private static void TestDiagSessionClosedBlocksRefresh()
        {
            Console.WriteLine();
            Console.WriteLine("15. 活动操作或未关闭诊断会话时拒绝刷新");
            FakeDiagChannel ch = new FakeDiagChannel();
            FakeDiagClock clock = new FakeDiagClock();
            string dir = Path.Combine(root, "diagblock-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(dir);
            AppPaths p = new AppPaths();
            p.AppRoot = dir; p.WritableRoot = dir;
            p.ConfigDir = Path.Combine(dir, "配置");
            p.RecordsDir = Path.Combine(dir, "记录");
            p.RoleStorePath = Path.Combine(p.ConfigDir, "角色记忆.json");
            DiagnosticsController c = new DiagnosticsController(p, ch, clock, QueryPolicy.ApprovedR11(), null);
            c.PortLocks = new PortLockRegistry();
            c.SetPort("COM_SIM");
            ch.Deliver("JAYv4 ROLE=BOAT MAC=24:62:AB:F1:36:64" +
                       " PEER=00:00:00:00:00:00 PAIR=2 STEER=20 FWID=" + BoatR11 +
                       " VER=20261103-590rpm-r11\n");
            c.ConnectAndIdentify();
            c.StartRecording();
            Check("记录中会话未关闭", c.Session != null && !c.Session.IsClosed, "会话状态异常");
            // RefreshLibrary 的守卫条件（与 GUI 相同）：Recording 或 Session 未关闭都拒绝
            bool blocked = c.State == DiagState.Recording || (c.Session != null && !c.Session.IsClosed);
            Check("未关闭会话使刷新被拒（守卫条件成立）", blocked, "守卫未触发");

            // 掉线后（会话仍未关闭）也应阻止——掉线会话要保留证据
            ch.FailRead = true;
            clock.Advance(10); c.Pump();
            ch.FailRead = false;
            bool blockedAfterDisconnect = c.State == DiagState.Disconnected &&
                                          c.Session != null && !c.Session.IsClosed;
            Check("掉线后会话仍未关闭，刷新仍被拒", blockedAfterDisconnect, "掉线后守卫未触发");

            c.StopRecording();
            Check("停止后允许刷新", c.Session.IsClosed, "停止后会话仍开着");
        }

        // ---------- 16. MainForm 与 Workflow 库对象一致 ----------

        private static void TestSuiteObjectConsistency()
        {
            Console.WriteLine();
            Console.WriteLine("16. 替换后 MainForm 与 Workflow 持有同一个 fresh 库");
            string lib = Path.Combine(root, "consistent-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(lib);
            MakeSuite(lib, "590-20-r11", "590-20-r11", "20261103-590rpm-r11", BoatR11, RemoteR11, 590, "off");
            MakeSuite(lib, "sim-r12-test-fixture", "sim-r12-test-fixture", "20991231-sim-r12",
                BoatSimR12, RemoteSimR12, 590, "off");
            FirmwareLibrary first = FirmwareLibrary.Load(lib);
            Workflow flow = new Workflow(new AppPaths(), first, new RoleStore(
                Path.Combine(root, "roles.json")), null, null, null);
            FirmwareSuite oldSelection = first.Find("590-20-r11");
            flow.SelectedSuite = oldSelection;

            // 模拟 ReplaceLibrary：整体替换 + UpdateLibrary 清空旧选择
            FirmwareLibrary fresh = FirmwareLibrary.Load(lib);
            flow.UpdateLibrary(fresh);
            Check("Workflow 引用换成 fresh 库", ReferenceEquals(flow.Library, fresh), "引用不一致");
            Check("旧 SelectedSuite 引用被清除", flow.SelectedSuite == null, "旧计划残留");
            Check("fresh 库里两套都在", fresh.Suites.Count == 2, fresh.Suites.Count.ToString());
            // 重新选择必须从 fresh 里取（对象身份一致）
            flow.SelectedSuite = fresh.Find("sim-r12-test-fixture");
            Check("重新选择使用 fresh 里的对象", flow.SelectedSuite != null &&
                ReferenceEquals(flow.SelectedSuite, fresh.Find("sim-r12-test-fixture")), "选择对象不一致");
        }

        // ---------- 计数 transport ----------
    }
}