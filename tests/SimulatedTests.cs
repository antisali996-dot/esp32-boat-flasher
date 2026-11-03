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

        // 两套合成套装（测试夹具，不是真实发布镜像）。
        private const string Fwid580Boat = "JAYFWB3-S20-A70-110-U1278-1500-1722-R580-C580pi2";
        private const string Fwid580Remote = "JAYFWR3-S20-A70-110-U1278-1500-1722-D1750-2150";
        private const string Fwid590Boat = "JAYFWB4-S20-A70-110-U1278-1500-1722-R590-OS600-Cpi2-aw-os600";
        private const string Fwid590Remote = "JAYFWR4-S20-A70-110-U1278-1500-1722-D1750-2150";

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
                TestFwidParsing();
                TestVersionPolicy();
                TestPartitionLayout();
                TestActualEraseRanges();
                TestLibraryLoad();
                TestLibraryRejectsMixing();
                TestRoleStoreByMacOnly();
                TestPortSwapSamePortDifferentBoards();
                TestRoleMemoryAndFirstTimeSelection();
                TestNoBackupByDefault();
                TestOptionalBackupStillWorks();
                TestWriteFailureIsNotSuccess();
                TestFlashRechecksIdentity();
                TestSuiteSwitch();
                TestMatchedSuiteSkipsWithOptionToReflash();
                TestDowngradeRequiresExplicitConfirm();
                TestSuiteUsesItsOwnEsptool();
                TestPairBothBoards();
                TestPairRejectsSamePortAndSameMac();
                TestPairPartialFailureAndRetry();
                TestPairSkipsCorrectSide();
                TestPairCompatibleAcrossSpeed();
                TestPairRejectsContractMismatch();
                TestPairRejectsOldFirmware();
                TestSafetyPromptOnlyOnce();
                TestCatalogSelfCheck();
                TestIdentityFromChipForOldBoard();
                TestFlashRequiresReadBack();
                TestFlashKeepsNvsWording();
                TestIncompatiblePartitionRefused();
                TestMissingEsptoolMarksSuiteUnusable();
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

        // ---------- 夹具 ----------

        private static string CatalogFwid(BoardRole role, int rpm)
        {
            if (rpm == 590) return role == BoardRole.Boat ? Fwid590Boat : Fwid590Remote;
            return role == BoardRole.Boat ? Fwid580Boat : Fwid580Remote;
        }

        private static byte[] BuildPartitionTable(bool nvsOverlapsApp, bool noNvs)
        {
            List<byte[]> entries = new List<byte[]>();
            if (!noNvs)
            {
                long nvsOffset = nvsOverlapsApp ? 0x10000 : 0x9000;
                entries.Add(PartitionEntryBytes("nvs", 1, 2, nvsOffset, 0x5000));
            }
            entries.Add(PartitionEntryBytes("otadata", 1, 0, 0xE000, 0x2000));
            entries.Add(PartitionEntryBytes("app0", 0, 0x10, 0x10000, 0x140000));
            entries.Add(PartitionEntryBytes("app1", 0, 0x11, 0x150000, 0x140000));
            entries.Add(PartitionEntryBytes("spiffs", 1, 0x82, 0x290000, 0x160000));

            byte[] data = new byte[0xC00];
            int pos = 0;
            foreach (byte[] entry in entries)
            {
                Buffer.BlockCopy(entry, 0, data, pos, 32);
                pos += 32;
            }
            return data;
        }

        private static byte[] PartitionEntryBytes(string label, int type, int subtype, long offset, long size)
        {
            byte[] entry = new byte[32];
            entry[0] = 0xAA; entry[1] = 0x50;
            entry[2] = (byte)type;
            entry[3] = (byte)subtype;
            Buffer.BlockCopy(BitConverter.GetBytes((uint)offset), 0, entry, 4, 4);
            Buffer.BlockCopy(BitConverter.GetBytes((uint)size), 0, entry, 8, 4);
            byte[] name = Encoding.ASCII.GetBytes(label);
            Buffer.BlockCopy(name, 0, entry, 12, Math.Min(name.Length, 16));
            return entry;
        }

        private static string MakeSuiteDir(string id, string version, int rpm, string boatFwid, string remoteFwid,
                                           bool nvsOverlapsApp = false, bool noNvs = false)
        {
            string dir = Path.Combine(workRoot, "suite-" + Guid.NewGuid().ToString("N"), id);
            string fw = Path.Combine(dir, "固件");
            string tools = Path.Combine(dir, "工具");
            Directory.CreateDirectory(fw);
            Directory.CreateDirectory(tools);
            File.WriteAllText(Path.Combine(tools, "esptool.exe"), "fake esptool for " + id);

            File.WriteAllBytes(Path.Combine(fw, "boat-app.bin"), Encoding.ASCII.GetBytes("BOAT APP " + boatFwid + " END"));
            File.WriteAllBytes(Path.Combine(fw, "remote-app.bin"), Encoding.ASCII.GetBytes("REMOTE APP " + remoteFwid + " END"));
            File.WriteAllBytes(Path.Combine(fw, "boat-bootloader.bin"), new byte[] { 1, 2, 3, 4 });
            File.WriteAllBytes(Path.Combine(fw, "remote-bootloader.bin"), new byte[] { 1, 2, 3, 4 });
            File.WriteAllBytes(Path.Combine(fw, "boot_app0.bin"), new byte[] { 5, 6, 7, 8 });
            byte[] table = BuildPartitionTable(nvsOverlapsApp, noNvs);
            File.WriteAllBytes(Path.Combine(fw, "boat-partitions.bin"), table);
            File.WriteAllBytes(Path.Combine(fw, "remote-partitions.bin"), table);

            string manifest = "{ \"schema\": 3, \"version\": \"" + version + "\", \"steer_deg\": 20, " +
                "\"angle_min\": 70, \"angle_max\": 110, \"center_us\": 1500, " +
                "\"pulse_min_us\": 1278, \"pulse_max_us\": 1722, \"pair_protocol\": 2, " +
                "\"target_rpm\": " + rpm + (rpm == 590 ? ", \"overspeed_rpm\": 600" : "") + ", " +
                "\"roles\": { " +
                "\"BOAT\": { \"fwid\": \"" + boatFwid + "\", \"version\": \"" + version + "\", \"steer_deg\": 20, " +
                "\"app\": \"boat-app.bin\", \"app_offset\": 65536, \"bootloader\": \"boat-bootloader.bin\", \"bootloader_offset\": 4096, " +
                "\"partitions\": \"boat-partitions.bin\", \"partitions_offset\": 32768, " +
                "\"boot_app0\": \"boot_app0.bin\", \"boot_app0_offset\": 57344 }, " +
                "\"REMOTE\": { \"fwid\": \"" + remoteFwid + "\", \"version\": \"" + version + "\", \"steer_deg\": 20, " +
                "\"app\": \"remote-app.bin\", \"app_offset\": 65536, \"bootloader\": \"remote-bootloader.bin\", \"bootloader_offset\": 4096, " +
                "\"partitions\": \"remote-partitions.bin\", \"partitions_offset\": 32768, " +
                "\"boot_app0\": \"boot_app0.bin\", \"boot_app0_offset\": 57344 } } }";
            File.WriteAllText(Path.Combine(fw, "固件清单.json"), manifest, new UTF8Encoding(false));

            string import = "{ \"id\": \"" + id + "\", \"version\": \"" + version + "\", \"target_rpm\": " + rpm +
                ", \"steer_deg\": 20, \"boat_fwid\": \"" + boatFwid + "\", \"remote_fwid\": \"" + remoteFwid +
                "\", \"source\": \"fixture\", \"manifest_sha256\": \"00\" }";
            File.WriteAllText(Path.Combine(dir, "import.json"), import, new UTF8Encoding(false));
            return dir;
        }

        private static string MakeTwoSuiteLibrary()
        {
            string root = Path.Combine(workRoot, "library-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            Directory.Move(MakeSuiteDir("580-20-r3", "20261103-20deg-r3", 580, Fwid580Boat, Fwid580Remote),
                Path.Combine(root, "580-20-r3"));
            Directory.Move(MakeSuiteDir("590-20-r4", "20261103-590rpm-r4", 590, Fwid590Boat, Fwid590Remote),
                Path.Combine(root, "590-20-r4"));
            return root;
        }

        private static AppPaths MakePaths()
        {
            AppPaths paths = new AppPaths();
            paths.AppRoot = workRoot;
            paths.WritableRoot = workRoot;
            paths.ConfigDir = Path.Combine(workRoot, "配置");
            paths.RecordsDir = Path.Combine(workRoot, "记录");
            paths.RoleStorePath = Path.Combine(paths.ConfigDir, "角色记忆.json");
            return paths;
        }

        private static Workflow MakeWorkflow(SimulatedTransport transport, SimUi ui, RoleStore store,
                                             string libraryRoot = null, string suiteId = null)
        {
            AppPaths paths = MakePaths();
            if (store == null) store = new RoleStore(paths.RoleStorePath);
            if (libraryRoot == null) libraryRoot = MakeTwoSuiteLibrary();
            FirmwareLibrary library = FirmwareLibrary.Load(libraryRoot);
            Workflow flow = new Workflow(paths, library, store, transport, new SimClock(), ui);
            if (suiteId != null)
            {
                FirmwareSuite suite = library.Find(suiteId);
                if (suite == null) throw new InvalidOperationException("测试夹具缺少套装 " + suiteId);
                flow.SelectedSuite = suite;
            }
            return flow;
        }

        private static SimBoard NewBoard(string mac, BoardRole role, string fwid, int steer = 20)
        {
            SimBoard board = new SimBoard();
            board.Mac = mac;
            board.Role = role;
            board.SteerDeg = steer;
            board.Fwid = fwid ?? "";
            board.Version = fwid == Fwid590Boat || fwid == Fwid590Remote ? "20261103-590rpm-r4" : "20261103-20deg-r3";
            board.Legacy = string.IsNullOrEmpty(board.Fwid);
            return board;
        }

        private static SimulatedTransport TwoBoards(out SimBoard boat, out SimBoard remote,
                                                    string boatFwid = null, string remoteFwid = null)
        {
            SimulatedTransport transport = new SimulatedTransport();
            boat = NewBoard(BoatMac, BoardRole.Boat, boatFwid != null ? boatFwid : Fwid580Boat);
            remote = NewBoard(RemoteMac, BoardRole.Remote, remoteFwid != null ? remoteFwid : Fwid580Remote);
            transport.Boards.Add(boat);
            transport.Boards.Add(remote);
            transport.Plug("COM8", BoatMac);
            transport.Plug("COM9", RemoteMac);
            return transport;
        }

        // ---------- 1-3. 身份 / FWID / 版本 ----------

        private static void TestIdentityParsing()
        {
            Console.WriteLine("1. 身份行解析");
            BoardIdentity v3 = IdentityParser.FromLine(
                "JAYv3 ROLE=BOAT MAC=24:62:AB:F1:36:64 PEER=24:62:AB:EF:CF:84 PAIR=2 STEER=20 FWID=" + Fwid580Boat + " VER=20261103-20deg-r3");
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

            // 固件端将来可能把身份行升级到 JAYv4/v5；字段语义不变时不能把新固件当成“无身份”。
            BoardIdentity v4 = IdentityParser.FromLine(
                "JAYv4 ROLE=BOAT MAC=24:62:AB:F1:36:64 PEER=24:62:AB:EF:CF:84 PAIR=2 STEER=20 FWID=" + Fwid580Boat + " VER=x");
            Check("兼容更新的 JAYv4 身份行", v4 != null && v4.HasFirmwareId && v4.IdentityLineVersion == 4 &&
                  v4.Mac == BoatMac && v4.SteeringAngle == 20, "JAYv4 行未被正确解析");
            Check("旧格式标记为协议 0", old30 != null && old30.IdentityLineVersion == 0, "旧格式版本标记错误");
            Console.WriteLine();
        }

        private static void TestFwidParsing()
        {
            Console.WriteLine("2. FWID 解析：角色、转速、角度");
            FwidInfo boat580 = FwidInfo.Parse(Fwid580Boat);
            Check("解析 580 小船", boat580.Parsed && boat580.Role == BoardRole.Boat && boat580.TargetRpm == 580 &&
                  boat580.SteerDeg == 20 && boat580.AngleMin == 70 && boat580.AngleMax == 110, "580 小船解析错误");
            FwidInfo boat590 = FwidInfo.Parse(Fwid590Boat);
            Check("解析 590 小船（含超速字段）", boat590.Parsed && boat590.TargetRpm == 590 &&
                  boat590.OverspeedRpm == 600, "590 小船解析错误：" + boat590.TargetRpm + "/" + boat590.OverspeedRpm);
            FwidInfo remote580 = FwidInfo.Parse(Fwid580Remote);
            Check("遥控器 FWID 不被误解析出转速", remote580.Parsed && remote580.Role == BoardRole.Remote &&
                  remote580.TargetRpm < 0, "遥控器被解析出转速 " + remote580.TargetRpm);
            FwidInfo remote590 = FwidInfo.Parse(Fwid590Remote);
            Check("590 遥控器 FWID 同样不误解析", remote590.Parsed && remote590.Role == BoardRole.Remote &&
                  remote590.TargetRpm < 0, "590 遥控器被解析出转速 " + remote590.TargetRpm);
            Check("同角度契约判定一致", FwidInfo.SameSteeringContract(boat580, remote580) &&
                  FwidInfo.SameSteeringContract(boat590, remote580), "同角度契约被判不同");
            FwidInfo old30 = FwidInfo.Parse("JAYFWB3-S30-A60-120-U1278-1500-1722-R580-C580pi2");
            Check("30° 契约被判定不同", !FwidInfo.SameSteeringContract(old30, boat580), "30° 与 20° 被判同一契约");
            Console.WriteLine();
        }

        private static void TestVersionPolicy()
        {
            Console.WriteLine("3. 版本判断：FWID 为准，配对协议版本不作依据");
            FirmwareSpec boat580 = MakeCatalogFor(580).ForRole(BoardRole.Boat);
            FirmwareSpec boat590 = MakeCatalogFor(590).ForRole(BoardRole.Boat);

            BoardIdentity current = IdentityParser.FromLine(
                "JAYv3 ROLE=BOAT MAC=24:62:AB:F1:36:64 PEER=00:00:00:00:00:00 PAIR=2 STEER=20 FWID=" + Fwid580Boat + " VER=x");
            Check("同 FWID 判为最新", VersionPolicy.Check(current, boat580).State == FirmwareState.Current, "最新固件被误判");

            BoardIdentity old30 = IdentityParser.FromLine(
                "JAY ROLE=BOAT MAC=24:62:AB:F0:72:94 PEER=00:00:00:00:00:00 VERSION=2 STEER=20");
            Check("旧 30° 即使 STEER=20 也要升级",
                VersionPolicy.Check(old30, boat580).State == FirmwareState.UpgradeRequired, "旧 30° 被跳过升级");

            BoardIdentity otherFwid = IdentityParser.FromLine(
                "JAYv3 ROLE=BOAT MAC=24:62:AB:F0:72:94 PEER=00:00:00:00:00:00 PAIR=2 STEER=20 FWID=JAYFWB3-S30-A60-120-U1278-1500-1722-R580-C580pi2 VER=x");
            Check("FWID 不一致必须升级", VersionPolicy.Check(otherFwid, boat580).State == FirmwareState.UpgradeRequired,
                "不同 FWID 被当作最新");

            BoardIdentity wrongRole = IdentityParser.FromLine(
                "JAYv3 ROLE=REMOTE MAC=24:62:AB:EF:CF:84 PEER=00:00:00:00:00:00 PAIR=2 STEER=20 FWID=" + Fwid580Remote + " VER=x");
            Check("遥控器程序不能当小船", VersionPolicy.Check(wrongRole, boat580).State == FirmwareState.WrongRole,
                "角色不符未拦截");

            BoardIdentity protocolOnly = IdentityParser.FromLine(
                "JAY ROLE=BOAT MAC=24:62:AB:F1:36:64 PEER=00:00:00:00:00:00 VERSION=2 STEER=20");
            Check("配对协议 v2 不等于最新", VersionPolicy.Check(protocolOnly, boat580).State == FirmwareState.UpgradeRequired,
                "仅凭协议版本被当作最新");

            BoardIdentity board590 = IdentityParser.FromLine(
                "JAYv3 ROLE=BOAT MAC=24:62:AB:F1:36:64 PEER=00:00:00:00:00:00 PAIR=2 STEER=20 FWID=" + Fwid590Boat + " VER=x");
            Check("590 板对 580 套装需切换", VersionPolicy.Check(board590, boat580).State == FirmwareState.UpgradeRequired,
                "590 板被误判为与 580 套装一致");
            Check("590 板对 590 套装一致", VersionPolicy.Check(board590, boat590).State == FirmwareState.Current,
                "590 板与 590 套装不一致");
            Console.WriteLine();
        }

        private static FirmwareCatalog MakeCatalogFor(int rpm)
        {
            FirmwareCatalog catalog = new FirmwareCatalog();
            catalog.Schema = 3;
            catalog.Version = rpm == 590 ? "20261103-590rpm-r4" : "20261103-20deg-r3";
            catalog.SteerDeg = 20;
            catalog.AngleMin = 70;
            catalog.AngleMax = 110;
            catalog.CenterUs = 1500;
            catalog.PulseMinUs = 1278;
            catalog.PulseMaxUs = 1722;
            catalog.PairProtocol = 2;
            catalog.TargetRpm = rpm;
            catalog.OverspeedRpm = rpm == 590 ? 600 : -1;
            foreach (BoardRole role in new BoardRole[] { BoardRole.Boat, BoardRole.Remote })
            {
                FirmwareSpec spec = new FirmwareSpec();
                spec.Role = role;
                spec.Fwid = CatalogFwid(role, rpm);
                spec.Version = catalog.Version;
                spec.SteerDeg = 20;
                catalog.Roles[role] = spec;
            }
            return catalog;
        }

        // ---------- 4. 分区 ----------

        private static void TestPartitionLayout()
        {
            Console.WriteLine("4. 分区布局：保留 NVS 的前提必须成立");
            string okPath = Path.Combine(workRoot, "part-ok.bin");
            File.WriteAllBytes(okPath, BuildPartitionTable(false, false));
            PartitionLayout ok = PartitionLayout.Analyze(okPath, 0x10000, 0x90000);
            Check("正常分区识别出 NVS", ok.Parsed && string.IsNullOrEmpty(ok.Error) && ok.Nvs != null &&
                  ok.Nvs.Offset == 0x9000, "正常分区被判不兼容：" + ok.Error);
            Check("NVS 说明含地址与不写入", ok.DescribeNvs().Contains("0x9000") && ok.DescribeNvs().Contains("不写入"),
                "NVS 说明不准确：" + ok.DescribeNvs());

            string overlap = Path.Combine(workRoot, "part-overlap.bin");
            File.WriteAllBytes(overlap, BuildPartitionTable(true, false));
            PartitionLayout bad = PartitionLayout.Analyze(overlap, 0x10000, 0x90000);
            Check("NVS 与应用区重叠被判不兼容", !string.IsNullOrEmpty(bad.Error), "重叠分区未被拦下");

            string noNvs = Path.Combine(workRoot, "part-nonvs.bin");
            File.WriteAllBytes(noNvs, BuildPartitionTable(false, true));
            PartitionLayout missing = PartitionLayout.Analyze(noNvs, 0x10000, 0x90000);
            Check("缺少 NVS 被判不兼容", !string.IsNullOrEmpty(missing.Error) && missing.Error.Contains("NVS"),
                "缺 NVS 未拦下");

            string overflowPath = Path.Combine(workRoot, "part-overflow.bin");
            File.WriteAllBytes(overflowPath, BuildPartitionTable(false, false));
            PartitionLayout overflow = PartitionLayout.Analyze(overflowPath, 0x10000, 0x200000);
            Check("应用镜像越界被判不兼容", !string.IsNullOrEmpty(overflow.Error), "越界未被拦下");

            PartitionLayout noFile = PartitionLayout.Analyze(Path.Combine(workRoot, "不存在.bin"), 0x10000, 0x1000);
            Check("缺分区表文件被判不兼容", !noFile.Parsed && !string.IsNullOrEmpty(noFile.Error), "缺文件未拦下");
            Console.WriteLine();
        }

        // ---------- 5-6. 固件库 ----------

        private static void TestLibraryLoad()
        {
            Console.WriteLine("5. 固件库：从发布清单读取，不按文件名猜");
            FirmwareLibrary library = FirmwareLibrary.Load(MakeTwoSuiteLibrary());
            Check("加载两套套装", library.Suites.Count == 2, "套装数量错误：" + library.Suites.Count);
            FirmwareSuite s580 = library.Find("580-20-r3");
            FirmwareSuite s590 = library.Find("590-20-r4");
            Check("580 套装数值来自清单", s580 != null && s580.Usable && s580.TargetRpm == 580 &&
                  s580.SteerDeg == 20 && s580.Version == "20261103-20deg-r3",
                "580 套装加载错误：" + (s580 == null ? "null" : string.Join("；", s580.Problems.ToArray())));
            Check("590 套装数值来自清单", s590 != null && s590.Usable && s590.TargetRpm == 590 &&
                  s590.Version == "20261103-590rpm-r4", "590 套装加载错误");
            Check("显示名含转速与角度", s580 != null && s580.Display.Contains("580") && s580.Display.Contains("20"),
                "显示名不含版本要素：" + (s580 == null ? "" : s580.Display));

            Check("按 FWID 找到所属套装", library.FindByFwid(BoardRole.Boat, Fwid590Boat) == s590 &&
                  library.FindByFwid(BoardRole.Boat, Fwid580Boat) == s580, "FWID 归属判断错误");
            Check("未知 FWID 不归属任何套装", library.FindByFwid(BoardRole.Boat, "JAYFWB9-未知") == null,
                "未知 FWID 被错误归属");

            string tampered = MakeSuiteDir("tampered", "20261103-20deg-r3", 580, Fwid580Boat, Fwid580Remote);
            string manifest = Path.Combine(tampered, "固件", "固件清单.json");
            File.WriteAllText(manifest, File.ReadAllText(manifest).Replace("\"target_rpm\": 580", "\"target_rpm\": 700"),
                new UTF8Encoding(false));
            FirmwareSuite badSuite = FirmwareSuite.Load(tampered);
            Check("清单与导入记录不符判为不可用", !badSuite.Usable &&
                  string.Join("；", badSuite.Problems.ToArray()).Contains("目标转速"), "篡改清单未被发现");

            string swapped = MakeSuiteDir("swapped", "20261103-20deg-r3", 580, Fwid580Boat, Fwid580Remote);
            File.WriteAllBytes(Path.Combine(swapped, "固件", "boat-app.bin"), Encoding.ASCII.GetBytes("BOAT APP " + Fwid590Boat));
            FirmwareSuite swappedSuite = FirmwareSuite.Load(swapped);
            Check("小船镜像被换成 590 判为不可用", !swappedSuite.Usable &&
                  string.Join("；", swappedSuite.Problems.ToArray()).Contains("镜像"), "镜像替换未被发现");

            string misleading = MakeSuiteDir("590-20-r4", "20261103-20deg-r3", 580, Fwid580Boat, Fwid580Remote);
            FirmwareSuite mis = FirmwareSuite.Load(misleading);
            Check("目录名不能决定版本", mis.Usable && mis.TargetRpm == 580 && mis.Display.Contains("580"),
                "版本被目录名误导");
            Console.WriteLine();
        }

        private static void TestLibraryRejectsMixing()
        {
            Console.WriteLine("6. 镜像混用拦截");
            string root = Path.Combine(workRoot, "mixed-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            Directory.Move(MakeSuiteDir("a", "20261103-20deg-r3", 580, Fwid580Boat, Fwid580Remote),
                Path.Combine(root, "a"));
            Directory.Move(MakeSuiteDir("b", "20261103-590rpm-r4", 590, Fwid580Boat, Fwid590Remote),
                Path.Combine(root, "b"));
            FirmwareLibrary mixed = FirmwareLibrary.Load(root);
            bool flagged = false;
            foreach (FirmwareSuite suite in mixed.Suites)
                foreach (string problem in suite.Problems)
                    if (problem.Contains("重复")) flagged = true;
            Check("两套共用同一小船 FWID 被标记", flagged, "重复 FWID 未被发现");

            string same = MakeSuiteDir("same", "20261103-20deg-r3", 580, Fwid580Boat, Fwid580Remote);
            string fw = Path.Combine(same, "固件");
            File.WriteAllBytes(Path.Combine(fw, "remote-app.bin"), Encoding.ASCII.GetBytes("BOAT APP " + Fwid580Boat));
            string manifestPath = Path.Combine(fw, "固件清单.json");
            File.WriteAllText(manifestPath,
                File.ReadAllText(manifestPath).Replace("\"app\": \"remote-app.bin\"", "\"app\": \"boat-app.bin\""),
                new UTF8Encoding(false));
            FirmwareSuite sameSuite = FirmwareSuite.Load(same);
            Check("两端指向同一应用镜像被拦下", !sameSuite.Usable, "同镜像未被拦下");
            Console.WriteLine();
        }

        // ---------- 7-9. 角色与串口 ----------

        private static void TestRoleStoreByMacOnly()
        {
            Console.WriteLine("7. 角色记忆只认 MAC，不认串口");
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
            Console.WriteLine("8. 串口换插：两块板轮流插入都显示 COM8");
            SimulatedTransport transport = new SimulatedTransport();
            SimBoard boat = NewBoard(BoatMac, BoardRole.Boat, Fwid580Boat);
            SimBoard remote = NewBoard(RemoteMac, BoardRole.Remote, Fwid580Remote);
            transport.Boards.Add(boat);
            transport.Boards.Add(remote);
            transport.Plug("COM8", BoatMac);
            SimUi ui = new SimUi();
            RoleStore store = new RoleStore(Path.Combine(workRoot, "swap-" + Guid.NewGuid().ToString("N") + ".json"));
            Workflow flow = MakeWorkflow(transport, ui, store, null, "580-20-r3");

            store.Remember(BoatMac, BoardRole.Boat, "COM8", "");
            transport.Plug("COM8", RemoteMac);
            PortScan scan = flow.Scan()[0];
            Check("同一 COM8 换插后读到新 MAC", scan.Identity.Mac == RemoteMac, "换插后读到的仍是旧 MAC");
            Check("换插后角色随 MAC 变为遥控器", scan.DisplayRole == BoardRole.Remote, "换插后角色未随 MAC 更新");

            OperationResult result = flow.FlashCurrent(scan, BoardRole.Boat, false, false);
            Check("换插后误选角色被拒绝", !result.Ok, "误选角色竟然成功");
            Check("误选角色时没有写入", transport.WriteFirmwareCount == 0, "误选角色仍执行了写入");
            Console.WriteLine();
        }

        private static void TestRoleMemoryAndFirstTimeSelection()
        {
            Console.WriteLine("9. 已认识按 MAC 记角色；新板必须用户选择");
            SimulatedTransport transport = new SimulatedTransport();
            SimBoard oldBoat = NewBoard(OldBoatMac, BoardRole.Boat, "");
            SimBoard remote = NewBoard(RemoteMac, BoardRole.Remote, Fwid580Remote);
            transport.Boards.Add(oldBoat);
            transport.Boards.Add(remote);
            transport.Plug("COM8", OldBoatMac);
            SimUi ui = new SimUi();
            RoleStore store = new RoleStore(Path.Combine(workRoot, "first-" + Guid.NewGuid().ToString("N") + ".json"));
            Workflow flow = MakeWorkflow(transport, ui, store, null, "580-20-r3");

            PortScan scan = flow.Scan()[0];
            Check("新板角色显示未登记", scan.RememberedRole == BoardRole.Unknown, "新板被自动登记角色");

            OperationResult noChoice = flow.FlashCurrent(scan, BoardRole.Unknown, false, false);
            Check("不选角色时拒绝烧录", !noChoice.Ok &&
                  noChoice.Error.IndexOf("首次使用", StringComparison.Ordinal) >= 0, "未选择用途仍然烧录");

            OperationResult chosen = flow.FlashCurrent(scan, BoardRole.Boat, false, false);
            Check("用户选定小船后烧录成功", chosen.Ok, "选定角色后烧录失败：" + chosen.Error);
            Check("按 MAC 记住角色", store.RoleOf(OldBoatMac) == BoardRole.Boat, "成功后未记住角色");

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

        // ---------- 10-13. 烧录基础 ----------

        private static void TestNoBackupByDefault()
        {
            Console.WriteLine("10. 普通烧录默认不备份");
            SimBoard boat;
            SimBoard remote;
            SimulatedTransport transport = TwoBoards(out boat, out remote, "", Fwid580Remote);
            SimUi ui = new SimUi();
            RoleStore store = new RoleStore(Path.Combine(workRoot, "nobackup-" + Guid.NewGuid().ToString("N") + ".json"));
            Workflow flow = MakeWorkflow(transport, ui, store, null, "580-20-r3");
            PortScan scan = flow.Scan()[0];

            OperationResult result = flow.FlashCurrent(scan, BoardRole.Boat, false, false);
            Check("默认不备份也完成烧录", result.Ok, "默认烧录失败：" + result.Error);
            Check("默认没有读整片 Flash", transport.ReadFlashCount == 0, "默认烧录仍强制备份");
            Check("写入仍然校验", transport.WriteFirmwareCount == 1, "没有执行写入");
            Check("界面只确认一次", ui.LastFlashPlan != null && ui.SafetyPrompts == 1, "确认次数异常");
            Check("确认信息含串口/MAC/角色", ui.LastFlashPlan != null &&
                  ui.LastFlashPlan.Port == "COM8" && ui.LastFlashPlan.Identity.Mac == BoatMac &&
                  ui.LastFlashPlan.Role == BoardRole.Boat, "确认信息缺失");
            Check("确认框同时给出板上与将写入", ui.LastFlashPlan != null &&
                  ui.LastFlashPlan.BoardFwid == "" && ui.LastFlashPlan.Fwid == Fwid580Boat &&
                  ui.LastFlashPlan.Suite != null && ui.LastFlashPlan.Suite.TargetRpm == 580,
                "板上/将写入信息不完整");
            Console.WriteLine();
        }

        private static void TestOptionalBackupStillWorks()
        {
            Console.WriteLine("11. 可选烧录前备份与单独备份");
            SimBoard boat;
            SimBoard remote;
            SimulatedTransport transport = TwoBoards(out boat, out remote, "", Fwid580Remote);
            SimUi ui = new SimUi();
            Workflow flow = MakeWorkflow(transport, ui,
                new RoleStore(Path.Combine(workRoot, "opt-" + Guid.NewGuid().ToString("N") + ".json")), null, "580-20-r3");
            OperationResult withBackup = flow.FlashCurrent(flow.Scan()[0], BoardRole.Boat, true, false);
            Check("勾选后执行整片备份", withBackup.Ok && transport.ReadFlashCount == 1, "勾选备份未执行");
            Check("备份文件与大小正确", !string.IsNullOrEmpty(transport.LastBackupPath) &&
                  File.Exists(transport.LastBackupPath) &&
                  new FileInfo(transport.LastBackupPath).Length == 4 * 1024 * 1024, "备份文件大小不对");

            SimulatedTransport t2 = new SimulatedTransport();
            SimBoard b2 = NewBoard(OldBoatMac, BoardRole.Boat, Fwid580Boat);
            t2.Boards.Add(b2);
            t2.Plug("COM3", OldBoatMac);
            SimUi ui2 = new SimUi();
            Workflow flow2 = MakeWorkflow(t2, ui2,
                new RoleStore(Path.Combine(workRoot, "bk-" + Guid.NewGuid().ToString("N") + ".json")), null, "580-20-r3");
            OperationResult only = flow2.BackupOnly(flow2.Scan()[0]);
            Check("单独备份不写固件", only.Ok && t2.WriteFirmwareCount == 0 && t2.ReadFlashCount == 1,
                "单独备份失败或误写：" + only.Error);
            Check("单独备份前核对 MAC", ui2.LastBackupPlan != null && ui2.LastBackupPlan.Identity.Mac == OldBoatMac,
                "备份确认缺少 MAC");
            Console.WriteLine();
        }

        private static void TestWriteFailureIsNotSuccess()
        {
            Console.WriteLine("12. 写入失败不得显示成功");
            SimBoard boat;
            SimBoard remote;
            SimulatedTransport transport = TwoBoards(out boat, out remote, "", Fwid580Remote);
            transport.FailNextWrite = 1;
            SimUi ui = new SimUi();
            Workflow flow = MakeWorkflow(transport, ui,
                new RoleStore(Path.Combine(workRoot, "fail-" + Guid.NewGuid().ToString("N") + ".json")), null, "580-20-r3");
            OperationResult result = flow.FlashCurrent(flow.Scan()[0], BoardRole.Boat, false, false);
            Check("写入失败返回失败", !result.Ok &&
                  result.Error.IndexOf("不能视为", StringComparison.Ordinal) >= 0, "写入失败却返回成功");

            SimulatedTransport t2 = new SimulatedTransport();
            SimBoard b2 = NewBoard(OldBoatMac, BoardRole.Boat, "");
            t2.Boards.Add(b2);
            t2.Plug("COM5", OldBoatMac);
            t2.ReportVerification = false;
            Workflow flow2 = MakeWorkflow(t2, new SimUi(),
                new RoleStore(Path.Combine(workRoot, "v-" + Guid.NewGuid().ToString("N") + ".json")), null, "580-20-r3");
            OperationResult noVerify = flow2.FlashCurrent(flow2.Scan()[0], BoardRole.Boat, false, false);
            Check("缺少校验字样也算失败", !noVerify.Ok, "没有校验也返回成功");
            Console.WriteLine();
        }

        private static void TestFlashRechecksIdentity()
        {
            Console.WriteLine("13. 烧录前身份复核（防换插）");
            SimBoard boat;
            SimBoard remote;
            SimulatedTransport transport = TwoBoards(out boat, out remote, "", Fwid580Remote);
            SimUi ui = new SimUi();
            Workflow flow = MakeWorkflow(transport, ui,
                new RoleStore(Path.Combine(workRoot, "recheck-" + Guid.NewGuid().ToString("N") + ".json")), null, "580-20-r3");
            ui.OnConfirmFlash = delegate { transport.Plug("COM8", RemoteMac); };
            OperationResult result = flow.FlashCurrent(flow.Scan()[0], BoardRole.Boat, false, false);
            Check("确认后换插被拦截", !result.Ok && result.Error.IndexOf("写入前", StringComparison.Ordinal) >= 0,
                "换插后仍然写入");
            Check("换插后未执行写入", transport.WriteFirmwareCount == 0, "换插后仍发生写入");
            Console.WriteLine();
        }

        // ---------- 14-17. 套装 ----------

        private static void TestSuiteSwitch()
        {
            Console.WriteLine("14. 两套固件切换：按所选套装的清单镜像写入");
            SimBoard boat;
            SimBoard remote;
            SimulatedTransport transport = TwoBoards(out boat, out remote);
            SimUi ui = new SimUi();
            Workflow flow = MakeWorkflow(transport, ui,
                new RoleStore(Path.Combine(workRoot, "switch-" + Guid.NewGuid().ToString("N") + ".json")), null, "590-20-r4");
            PortScan scan = flow.Scan()[0];
            Check("扫描时提示板上是另一套", scan.Note.Contains("580") && scan.Note.Contains("不同"),
                "未提示跨套装：" + scan.Note);

            OperationResult up = flow.FlashCurrent(scan, BoardRole.Boat, false, false);
            Check("升级到 590 成功", up.Ok, "切换到 590 失败：" + up.Error);
            Check("写入的是 590 的 FWID", boat.Fwid == Fwid590Boat, "写入 FWID 不对：" + boat.Fwid);
            Check("确认框提示跨套装", ui.LastFlashPlan != null && ui.LastFlashPlan.ChangingSuite &&
                  !string.IsNullOrEmpty(ui.LastFlashPlan.CrossSuiteWarning), "跨套装提示缺失");
            Check("跨套装提示含两套名称", ui.LastFlashPlan != null &&
                  ui.LastFlashPlan.CrossSuiteWarning.Contains("580") &&
                  ui.LastFlashPlan.CrossSuiteWarning.Contains("590"), "提示缺少套装名");

            Workflow flow2 = MakeWorkflow(transport, new SimUi(),
                new RoleStore(Path.Combine(workRoot, "switch2-" + Guid.NewGuid().ToString("N") + ".json")), null, "580-20-r3");
            OperationResult down = flow2.FlashCurrent(flow2.Scan()[0], BoardRole.Boat, false, false);
            Check("切回 580 写入正确 FWID", down.Ok && boat.Fwid == Fwid580Boat, "切回失败：" + down.Error);

            string partialRoot = Path.Combine(workRoot, "partial-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(partialRoot);
            Directory.Move(MakeSuiteDir("p", "20261103-20deg-r3", 580, Fwid580Boat, Fwid580Remote),
                Path.Combine(partialRoot, "p"));
            File.Delete(Path.Combine(partialRoot, "p", "固件", "remote-app.bin"));
            FirmwareLibrary partialLib = FirmwareLibrary.Load(partialRoot);
            Check("缺镜像的套装不可用", partialLib.Suites.Count == 1 && !partialLib.Suites[0].Usable,
                "缺镜像套装仍被判可用");
            Console.WriteLine();
        }

        private static void TestMatchedSuiteSkipsWithOptionToReflash()
        {
            Console.WriteLine("15. 已匹配套装：提示无需重刷，也可明确重刷");
            SimBoard boat;
            SimBoard remote;
            SimulatedTransport transport = TwoBoards(out boat, out remote);
            SimUi ui = new SimUi();
            Workflow flow = MakeWorkflow(transport, ui,
                new RoleStore(Path.Combine(workRoot, "matched-" + Guid.NewGuid().ToString("N") + ".json")), null, "580-20-r3");
            PortScan scan = flow.Scan()[0];
            Check("已匹配时备注写明无需重复烧录", scan.Note.Contains("无需重复烧录"), "备注未提示：" + scan.Note);

            OperationResult noReflash = flow.FlashCurrent(scan, BoardRole.Boat, false, false);
            Check("不勾选重刷时不写入", noReflash.Ok && transport.WriteFirmwareCount == 0 &&
                  noReflash.Summary.Contains("无需重复烧录"), "已匹配时仍然写入");
            Check("不勾选重刷时不弹确认框", ui.LastFlashPlan == null, "不该弹确认框");

            OperationResult reflash = flow.FlashCurrent(scan, BoardRole.Boat, false, true);
            Check("明确重刷时执行写入", reflash.Ok && transport.WriteFirmwareCount == 1, "明确重刷未写入：" + reflash.Error);
            Check("重刷确认框标注明确重刷", ui.LastFlashPlan != null && ui.LastFlashPlan.ExplicitReflash,
                "确认框未标注明确重刷");
            Console.WriteLine();
        }

        private static void TestDowngradeRequiresExplicitConfirm()
        {
            Console.WriteLine("16. 误降级：跨速必须明确提示，不能静默");
            SimulatedTransport transport = new SimulatedTransport();
            SimBoard boat = NewBoard(BoatMac, BoardRole.Boat, Fwid590Boat);
            SimBoard remote = NewBoard(RemoteMac, BoardRole.Remote, Fwid590Remote);
            transport.Boards.Add(boat);
            transport.Boards.Add(remote);
            transport.Plug("COM8", BoatMac);
            SimUi ui = new SimUi();
            Workflow flow = MakeWorkflow(transport, ui,
                new RoleStore(Path.Combine(workRoot, "down-" + Guid.NewGuid().ToString("N") + ".json")), null, "580-20-r3");
            PortScan scan = flow.Scan()[0];

            OperationResult result = flow.FlashCurrent(scan, BoardRole.Boat, false, false);
            Check("降级被标记", ui.LastFlashPlan != null && ui.LastFlashPlan.Downgrade, "降级未被标记");
            Check("降级提示写明两个转速", ui.LastFlashPlan != null &&
                  ui.LastFlashPlan.CrossSuiteWarning.Contains("590") &&
                  ui.LastFlashPlan.CrossSuiteWarning.Contains("580"), "降级提示不完整：" +
                  (ui.LastFlashPlan == null ? "" : ui.LastFlashPlan.CrossSuiteWarning));
            Check("确认后降级才写入", result.Ok && boat.Fwid == Fwid580Boat, "降级写入失败");

            SimulatedTransport t2 = new SimulatedTransport();
            SimBoard b2 = NewBoard(BoatMac, BoardRole.Boat, Fwid590Boat);
            t2.Boards.Add(b2);
            t2.Plug("COM8", BoatMac);
            SimUi ui2 = new SimUi();
            ui2.FlashAnswer = false;
            Workflow flow2 = MakeWorkflow(t2, ui2,
                new RoleStore(Path.Combine(workRoot, "down2-" + Guid.NewGuid().ToString("N") + ".json")), null, "580-20-r3");
            OperationResult cancelled = flow2.FlashCurrent(flow2.Scan()[0], BoardRole.Boat, false, false);
            Check("取消降级确认则不写入", !cancelled.Ok && t2.WriteFirmwareCount == 0 &&
                  b2.Fwid == Fwid590Boat, "取消后仍然写入");
            Console.WriteLine();
        }

        private static void TestSuiteUsesItsOwnEsptool()
        {
            Console.WriteLine("17. 每套用自己的烧录组件，不跨套混用");
            SimBoard boat;
            SimBoard remote;
            SimulatedTransport transport = TwoBoards(out boat, out remote, "", Fwid590Remote);
            Workflow flow = MakeWorkflow(transport, new SimUi(),
                new RoleStore(Path.Combine(workRoot, "esptool-" + Guid.NewGuid().ToString("N") + ".json")), null, "590-20-r4");
            flow.FlashCurrent(flow.Scan()[0], BoardRole.Boat, false, false);
            Check("使用所选套装的 esptool", transport.LastEsptool.Contains("590-20-r4"),
                "用了错误的工具路径：" + transport.LastEsptool);
            Console.WriteLine();
        }

        // ---------- 18-24. 配对 ----------

        private static void TestPairBothBoards()
        {
            Console.WriteLine("18. 两块板同时配对：一次完成双方");
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
            Console.WriteLine("19. 配对禁止同一串口 / 同一 MAC");
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

            transport.Plug("COM9", BoatMac);
            PortScan rs = flow.Scan().Find(s => s.Port.Name == "COM9");
            OperationResult sameMac = flow.PairTwoBoards(bs, rs);
            Check("同一 MAC 被拒绝", !sameMac.Ok && transport.WritePairCount == 0,
                "同 MAC 未被拦截");
            Console.WriteLine();
        }

        private static void TestPairPartialFailureAndRetry()
        {
            Console.WriteLine("20. 配对部分失败，说明进度并重试");
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
            int pairsBefore = transport.WritePairCount;
            OperationResult retry = flow.PairTwoBoards(scans.Find(s => s.Port.Name == "COM8"),
                                                       scans.Find(s => s.Port.Name == "COM9"));
            Check("重试后两端都成功", retry.Ok && boat.Peer == RemoteMac && remote.Peer == BoatMac,
                "重试未完成：" + retry.Error);
            Check("重试只写未完成的一端", transport.WritePairCount - pairsBefore == 1,
                "重试写了 " + (transport.WritePairCount - pairsBefore) + " 次");

            SimulatedTransport t2 = new SimulatedTransport();
            SimBoard b2 = NewBoard(BoatMac, BoardRole.Boat, Fwid580Boat);
            SimBoard r2 = NewBoard(RemoteMac, BoardRole.Remote, Fwid580Remote);
            b2.SaveFails = true;
            t2.Boards.Add(b2);
            t2.Boards.Add(r2);
            t2.Plug("COM8", BoatMac);
            t2.Plug("COM9", RemoteMac);
            Workflow flow2 = MakeWorkflow(t2, new SimUi(),
                new RoleStore(Path.Combine(workRoot, "firstfail-" + Guid.NewGuid().ToString("N") + ".json")));
            List<PortScan> s2 = flow2.Scan();
            OperationResult fail = flow2.PairTwoBoards(s2.Find(s => s.Port.Name == "COM8"),
                                                       s2.Find(s => s.Port.Name == "COM9"));
            Check("一端失败仍尝试另一端", t2.WritePairCount == 2, "第一端失败就跳过了第二端");
            Check("失败端保持原样", b2.Peer == "00:00:00:00:00:00" && r2.Peer == BoatMac, "失败端状态异常");
            Check("说明完成 1/2 并列出待处理", fail.Summary.IndexOf("1/2", StringComparison.Ordinal) >= 0 &&
                  fail.Summary.IndexOf("小船", StringComparison.Ordinal) >= 0, "失败说明缺失：" + fail.Summary);
            Console.WriteLine();
        }

        private static void TestPairSkipsCorrectSide()
        {
            Console.WriteLine("21. 已正确的一端跳过写入与重启");
            SimBoard boat;
            SimBoard remote;
            SimulatedTransport transport = TwoBoards(out boat, out remote);
            boat.Peer = RemoteMac;
            SimUi ui = new SimUi();
            Workflow flow = MakeWorkflow(transport, ui,
                new RoleStore(Path.Combine(workRoot, "skip-" + Guid.NewGuid().ToString("N") + ".json")));
            List<PortScan> scans = flow.Scan();
            OperationResult result = flow.PairTwoBoards(scans.Find(s => s.Port.Name == "COM8"),
                                                       scans.Find(s => s.Port.Name == "COM9"));
            Check("只写需要改的一端", result.Ok && transport.WritePairCount == 1,
                "写入了 " + transport.WritePairCount + " 次");
            Check("跳过的一端未被重启", !transport.Events.Contains("pair:" + BoatMac + "->" + RemoteMac),
                "已正确的一端仍被写入");
            Check("确认框标明跳过", ui.LastPairPlan != null && ui.LastPairPlan.SkipBoat && !ui.LastPairPlan.SkipRemote,
                "确认框未标明跳过");
            Check("结果说明跳过", result.Summary.Contains("跳过"), "结果未说明跳过");

            int before = transport.WritePairCount;
            OperationResult again = flow.PairTwoBoards(scans.Find(s => s.Port.Name == "COM8"),
                                                       scans.Find(s => s.Port.Name == "COM9"));
            Check("两端都正确时零写入", again.Ok && transport.WritePairCount == before, "两端已正确仍写入");
            Check("两端一致时不弹改动确认", again.Summary.Contains("没有改写"), "两端一致时提示信息不对");
            Console.WriteLine();
        }

        private static void TestPairCompatibleAcrossSpeed()
        {
            Console.WriteLine("22. 590 小船 + 同一 20° 遥控器可以配对，不强制重刷");
            SimulatedTransport transport = new SimulatedTransport();
            SimBoard boat = NewBoard(BoatMac, BoardRole.Boat, Fwid590Boat);
            SimBoard remote = NewBoard(RemoteMac, BoardRole.Remote, Fwid580Remote);
            transport.Boards.Add(boat);
            transport.Boards.Add(remote);
            transport.Plug("COM8", BoatMac);
            transport.Plug("COM9", RemoteMac);
            SimUi ui = new SimUi();
            Workflow flow = MakeWorkflow(transport, ui,
                new RoleStore(Path.Combine(workRoot, "compat-" + Guid.NewGuid().ToString("N") + ".json")), null, "580-20-r3");
            List<PortScan> scans = flow.Scan();

            OperationResult result = flow.PairTwoBoards(scans.Find(s => s.Port.Name == "COM8"),
                                                       scans.Find(s => s.Port.Name == "COM9"));
            Check("跨速配对允许", result.Ok, "跨速配对被拒绝：" + result.Error);
            Check("跨速配对不写固件", transport.WriteFirmwareCount == 0, "配对时写入了固件");
            Check("跨速配对说明转速差异", result.Summary.Contains("遥控器不控制油门") ||
                  result.Summary.Contains("转向契约一致"), "未解释转速差异");
            Check("兼容说明含转速提示", ui.LastPairPlan != null &&
                  (ui.LastPairPlan.Description.Contains("转向契约一致")), "兼容说明缺失");
            Console.WriteLine();
        }

        private static void TestPairRejectsContractMismatch()
        {
            Console.WriteLine("23. 转向契约/协议版本不同时拒绝配对");
            SimulatedTransport transport = new SimulatedTransport();
            SimBoard boat = NewBoard(BoatMac, BoardRole.Boat, "JAYFWB3-S30-A60-120-U1278-1500-1722-R580-C580pi2", 30);
            SimBoard remote = NewBoard(RemoteMac, BoardRole.Remote, Fwid580Remote);
            transport.Boards.Add(boat);
            transport.Boards.Add(remote);
            transport.Plug("COM8", BoatMac);
            transport.Plug("COM9", RemoteMac);
            Workflow flow = MakeWorkflow(transport, new SimUi(),
                new RoleStore(Path.Combine(workRoot, "contract-" + Guid.NewGuid().ToString("N") + ".json")));
            List<PortScan> scans = flow.Scan();
            OperationResult result = flow.PairTwoBoards(scans.Find(s => s.Port.Name == "COM8"),
                                                       scans.Find(s => s.Port.Name == "COM9"));
            Check("30° 与 20° 拒绝配对", !result.Ok &&
                  result.Error.IndexOf("转向契约", StringComparison.Ordinal) >= 0, "契约不符未被拦下");
            Check("契约不符时不写入", transport.WritePairCount == 0, "契约不符仍写入");

            SimulatedTransport t2 = new SimulatedTransport();
            SimBoard b2 = NewBoard(BoatMac, BoardRole.Boat, Fwid580Boat);
            SimBoard r2 = NewBoard(RemoteMac, BoardRole.Remote, Fwid580Remote);
            r2.PairProtocol = 3;
            t2.Boards.Add(b2);
            t2.Boards.Add(r2);
            t2.Plug("COM8", BoatMac);
            t2.Plug("COM9", RemoteMac);
            Workflow flow2 = MakeWorkflow(t2, new SimUi(),
                new RoleStore(Path.Combine(workRoot, "proto-" + Guid.NewGuid().ToString("N") + ".json")));
            List<PortScan> s2 = flow2.Scan();
            OperationResult proto = flow2.PairTwoBoards(s2.Find(s => s.Port.Name == "COM8"),
                                                       s2.Find(s => s.Port.Name == "COM9"));
            Check("协议版本不同拒绝配对", !proto.Ok &&
                  proto.Error.IndexOf("协议版本", StringComparison.Ordinal) >= 0, "协议不符未被拦下");
            Console.WriteLine();
        }

        private static void TestPairRejectsOldFirmware()
        {
            Console.WriteLine("24. 旧固件不参与配对，提示先升级");
            SimulatedTransport transport = new SimulatedTransport();
            SimBoard boat = NewBoard(BoatMac, BoardRole.Boat, "");
            boat.Legacy = true;
            SimBoard remote = NewBoard(RemoteMac, BoardRole.Remote, Fwid580Remote);
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

            SimulatedTransport t2 = new SimulatedTransport();
            SimBoard b2 = NewBoard(BoatMac, BoardRole.Boat, Fwid580Boat);
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

        // ---------- 25-30. 其它 ----------

        private static void TestSafetyPromptOnlyOnce()
        {
            Console.WriteLine("25. 安全提示同次运行只确认一次");
            SimBoard boat;
            SimBoard remote;
            SimulatedTransport transport = TwoBoards(out boat, out remote, "", Fwid580Remote);
            SimUi ui = new SimUi();
            Workflow flow = MakeWorkflow(transport, ui,
                new RoleStore(Path.Combine(workRoot, "safety-" + Guid.NewGuid().ToString("N") + ".json")), null, "580-20-r3");
            OperationResult first = flow.FlashCurrent(flow.Scan()[0], BoardRole.Boat, false, false);
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
            Console.WriteLine();
        }

        private static void TestCatalogSelfCheck()
        {
            Console.WriteLine("26. 清单自检：角度契约与镜像一致");
            string dir = Path.Combine(workRoot, "selfcheck-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "boat.bin"), "binary " + Fwid580Boat + " tail");
            File.WriteAllText(Path.Combine(dir, "remote.bin"), "binary " + Fwid580Remote + " tail");
            File.WriteAllText(Path.Combine(dir, "boot.bin"), "boot");
            File.WriteAllText(Path.Combine(dir, "part.bin"), "part");
            File.WriteAllText(Path.Combine(dir, "boot_app0.bin"), "app0");

            string manifest = Path.Combine(dir, "固件清单.json");
            File.WriteAllText(manifest, BuildManifest(Fwid580Boat, Fwid580Remote));
            FirmwareCatalog good = FirmwareCatalog.Load(manifest);
            Check("正常清单自检通过", good.Problems.Count == 0, string.Join("；", good.Problems.ToArray()));

            File.WriteAllText(Path.Combine(dir, "boat.bin"), "binary without identity");
            FirmwareCatalog bad = FirmwareCatalog.Load(manifest);
            Check("镜像缺 FWID 被自检拦下", bad.Problems.Count > 0, "镜像与清单不一致却通过自检");

            File.WriteAllText(manifest, BuildManifest(Fwid580Boat, Fwid580Remote)
                .Replace("\"steer_deg\": 20", "\"steer_deg\": 30"));
            File.WriteAllText(Path.Combine(dir, "boat.bin"), "binary " + Fwid580Boat);
            FirmwareCatalog wrongSteer = FirmwareCatalog.Load(manifest);
            Check("清单角度与 FWID 契约不一致被拦下", wrongSteer.Problems.Count > 0, "30 度清单通过自检");
            Console.WriteLine();
        }

        private static void TestIdentityFromChipForOldBoard()
        {
            Console.WriteLine("27. 旧板/空白板不回应身份查询：按芯片 MAC 升级");
            SimulatedTransport transport = new SimulatedTransport();
            SimBoard silent = NewBoard(OldBoatMac, BoardRole.Boat, "");
            silent.Legacy = true;
            silent.SteerDeg = -1;
            transport.Boards.Add(silent);
            transport.Plug("COM8", OldBoatMac);
            transport.SilentPorts.Add("COM8");
            transport.OnWrite = delegate(string port, FirmwareSpec spec, SimBoard b) { transport.SilentPorts.Remove(port); };
            SimUi ui = new SimUi();
            RoleStore store = new RoleStore(Path.Combine(workRoot, "silent-" + Guid.NewGuid().ToString("N") + ".json"));
            Workflow flow = MakeWorkflow(transport, ui, store, null, "580-20-r3");

            PortScan scan = flow.Scan()[0];
            Check("无回应仍读到芯片 MAC", scan.HasChip && scan.Identity.Mac == OldBoatMac, "没有回退到芯片读取");
            Check("无回应显示为旧版", scan.FirmwareText.IndexOf("旧版", StringComparison.Ordinal) >= 0,
                "无回应没有标记旧版：" + scan.FirmwareText);
            Check("无回应角色仍是未知", scan.RememberedRole == BoardRole.Unknown, "无回应板被自动登记角色");

            OperationResult noRole = flow.FlashCurrent(scan, BoardRole.Unknown, false, false);
            Check("无回应板未选角色时拒绝", !noRole.Ok && transport.WriteFirmwareCount == 0, "无回应板被直接烧录");

            OperationResult ok = flow.FlashCurrent(scan, BoardRole.Boat, false, false);
            Check("选定角色后旧板升级成功", ok.Ok, "旧板升级失败：" + ok.Error);
            Check("升级时不强制备份", transport.ReadFlashCount == 0, "旧板升级强制备份");
            Check("升级后按 MAC 记住角色", store.RoleOf(OldBoatMac) == BoardRole.Boat, "升级后未记住角色");
            Console.WriteLine();
        }

        private static void TestFlashRequiresReadBack()
        {
            Console.WriteLine("28. 烧录后必须读回身份，否则不算成功");
            SimulatedTransport transport = new SimulatedTransport();
            SimBoard board = NewBoard(OldBoatMac, BoardRole.Boat, "");
            board.Legacy = true;
            transport.Boards.Add(board);
            transport.Plug("COM8", OldBoatMac);
            Workflow flow = MakeWorkflow(transport, new SimUi(),
                new RoleStore(Path.Combine(workRoot, "readback-" + Guid.NewGuid().ToString("N") + ".json")), null, "580-20-r3");
            PortScan scan = flow.Scan()[0];
            transport.OnWrite = delegate(string port, FirmwareSpec spec, SimBoard b) { b.Legacy = true; b.Fwid = ""; };
            OperationResult result = flow.FlashCurrent(scan, BoardRole.Boat, false, false);
            Check("读回身份不符时返回失败", !result.Ok, "写入成功即被当作完成");
            Check("失败原因说明读回不符", result.Error.IndexOf("读回", StringComparison.Ordinal) >= 0 ||
                  result.Error.IndexOf("不符合本套装", StringComparison.Ordinal) >= 0, "缺少读回说明：" + result.Error);
            Console.WriteLine();
        }

        private static void TestFlashKeepsNvsWording()
        {
            Console.WriteLine("29. 烧录说明分段写入且保留 NVS");
            SimBoard boat;
            SimBoard remote;
            SimulatedTransport transport = TwoBoards(out boat, out remote, "", Fwid580Remote);
            SimUi ui = new SimUi();
            Workflow flow = MakeWorkflow(transport, ui,
                new RoleStore(Path.Combine(workRoot, "nvs-" + Guid.NewGuid().ToString("N") + ".json")), null, "580-20-r3");
            FirmwareSpec spec = flow.SelectedSuite.SpecFor(BoardRole.Boat);
            int count = 0;
            bool coversZero = false;
            foreach (KeyValuePair<long, string> image in spec.Images())
            {
                if (image.Key == 0) coversZero = true;
                count++;
            }
            Check("分段写入四个镜像且不整片覆盖 0x0", count == 4 && !coversZero, "镜像数量异常：" + count);

            flow.FlashCurrent(flow.Scan()[0], BoardRole.Boat, false, false);
            Check("确认框写明保留 NVS", ui.LastFlashPlan != null &&
                  ui.LastFlashPlan.NvsNote.Contains("NVS") && ui.LastFlashPlan.NvsNote.Contains("不写入"),
                "NVS 说明缺失：" + (ui.LastFlashPlan == null ? "" : ui.LastFlashPlan.NvsNote));
            Console.WriteLine();
        }

        private static void TestIncompatiblePartitionRefused()
        {
            Console.WriteLine("30. 分区布局不兼容时拒绝写入");
            string root = Path.Combine(workRoot, "badpart-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            Directory.Move(MakeSuiteDir("bad", "20261103-20deg-r3", 580, Fwid580Boat, Fwid580Remote,
                                        nvsOverlapsApp: true), Path.Combine(root, "bad"));
            FirmwareLibrary library = FirmwareLibrary.Load(root);
            Check("分区不兼容的套装被判不可用", library.Suites.Count == 1 && !library.Suites[0].Usable,
                "分区不兼容仍被判可用：" + string.Join("；", library.Suites[0].Problems.ToArray()));

            SimulatedTransport transport = new SimulatedTransport();
            SimBoard boat = NewBoard(BoatMac, BoardRole.Boat, "");
            boat.Legacy = true;
            transport.Boards.Add(boat);
            transport.Plug("COM8", BoatMac);
            AppPaths paths = MakePaths();
            Workflow flow = new Workflow(paths, library, new RoleStore(paths.RoleStorePath),
                                         transport, new SimClock(), new SimUi());
            flow.SelectedSuite = library.Suites[0];
            PortScan scan = flow.Scan()[0];
            OperationResult result = flow.FlashCurrent(scan, BoardRole.Boat, false, false);
            Check("不兼容时拒绝写入", !result.Ok && transport.WriteFirmwareCount == 0, "不兼容仍写入");
            Check("拒绝原因说明分区或自检", result.Error.Contains("分区布局") || result.Error.Contains("自检"),
                "拒绝说明不清楚：" + result.Error);
            Console.WriteLine();
        }

        // 缺少 esptool 的套装不能用来烧录（read_flash / write_flash 都要它）。
        private static void TestMissingEsptoolMarksSuiteUnusable()
        {
            Console.WriteLine("31. 缺少烧录组件的套装不可用");
            string dir = MakeSuiteDir("noesptool", "20261103-20deg-r3", 580, Fwid580Boat, Fwid580Remote);
            File.Delete(Path.Combine(dir, "工具", "esptool.exe"));
            FirmwareSuite suite = FirmwareSuite.Load(dir);
            Check("缺 esptool 判为不可用", !suite.Usable &&
                  string.Join("；", suite.Problems.ToArray()).Contains("esptool"), "缺 esptool 仍被判可用");

            // 套装未选中时扫描旧板仍应能借到别的套装的 esptool，而不是空路径。
            SimulatedTransport transport = new SimulatedTransport();
            SimBoard silent = NewBoard(OldBoatMac, BoardRole.Boat, "");
            silent.Legacy = true;
            transport.Boards.Add(silent);
            transport.Plug("COM8", OldBoatMac);
            transport.SilentPorts.Add("COM8");
            Workflow flow = MakeWorkflow(transport, new SimUi(), null);
            flow.SelectedSuite = null;
            flow.Scan();
            Check("未选套装时扫描仍借用可用套装的工具",
                transport.LastEsptool.Contains("580-20-r3") || transport.LastEsptool.Contains("590-20-r4"),
                "扫描时工具路径为空：" + transport.LastEsptool);
            Console.WriteLine();
        }

        private static void TestActualEraseRanges()
        {
            string dir = MakeSuiteDir("actualranges", "20261103-20deg-r3", 580, Fwid580Boat, Fwid580Remote);
            FirmwareSuite valid = FirmwareSuite.Load(dir);
            Check("实际四段范围允许标准发布包", valid.Usable, string.Join(";", valid.Problems.ToArray()));
            FirmwareSpec spec = valid.SpecFor(BoardRole.Boat);
            long original = spec.BootloaderOffset;
            spec.BootloaderOffset = 0x9000;
            PartitionLayout bad = PartitionLayout.Analyze(spec.PartitionsPath, spec.AppOffset, new FileInfo(spec.AppPath).Length, spec);
            Check("启动镜像按清单写进NVS被拦截", bad.Error.Contains("NVS"), bad.Error);
            spec.BootloaderOffset = 0x8FFF;
            bad = PartitionLayout.Analyze(spec.PartitionsPath, spec.AppOffset, new FileInfo(spec.AppPath).Length, spec);
            Check("跨扇区擦除触及NVS被拦截", bad.Error.Contains("NVS"), bad.Error);
            spec.BootloaderOffset = spec.PartitionsOffset;
            bad = PartitionLayout.Analyze(spec.PartitionsPath, spec.AppOffset, new FileInfo(spec.AppPath).Length, spec);
            Check("镜像擦除范围彼此重叠被拦截", bad.Error.Contains("重叠"), bad.Error);
            spec.BootloaderOffset = original;
            File.WriteAllBytes(spec.BootloaderPath, new byte[0x9000]);
            bad = PartitionLayout.Analyze(spec.PartitionsPath, spec.AppOffset, new FileInfo(spec.AppPath).Length, spec);
            Check("过长启动镜像触及NVS被拦截", bad.Error.Contains("NVS"), bad.Error);
        }

        private static string BuildManifest(string boatFwid, string remoteFwid)
        {
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
}
