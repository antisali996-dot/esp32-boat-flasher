using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace BoatTool
{
    // 套装选择“摘要与实际选择一致”回归（round5）。
    //
    // 测试对象是生产实现本身：直接构造 MainForm，然后只用真实入口驱动——
    //   * 构造 → FillSuites（首次初始化默认选择）
    //   * suiteChoice.SelectedIndex 变化 → OnSuiteChanged（普通手动选择）
    //   * 反射调用私有 RefreshLibrary()（“刷新固件库”按钮的同一个处理函数）
    // 测试不复制任何界面逻辑，只读取界面控件与 Workflow 的实际状态。
    // 修复前（OnSuiteChanged 漏调 UpdateSuiteInfo）本组会失败；修复后通过。
    public static class SuiteSelectionGuiTests
    {
        private static int passed;
        private static int failed;
        private static string root;

        // 与正式库一致的版本/FWID（r10 保留 OS600，r11 为 OSOFF）。
        private const string VerR11 = "20261103-590rpm-r11";
        private const string VerR10 = "20261103-590rpm-r10";
        private const string BoatR11 = "JAYFWB11-S20-A70-110-U1278-1500-1722-R590-OSOFF-Cr11-osoff";
        private const string RemoteR11 = "JAYFWR11-S20-A70-110-U1278-1500-1722-D1750-2150";
        private const string BoatR10 = "JAYFWB10-S20-A70-110-U1278-1500-1722-R590-OS600-Cr10-diagshared";
        private const string RemoteR10 = "JAYFWR10-S20-A70-110-U1278-1500-1722-D1750-2150";

        public static void Run()
        {
            passed = 0; failed = 0;
            root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test-work",
                "suitegui-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string oldScan = Environment.GetEnvironmentVariable("BOATTOOL_NO_AUTOSCAN");
            // 无设备模式：连串口名都不枚举。真实 GUI 验证也使用同一个开关。
            Environment.SetEnvironmentVariable("BOATTOOL_NO_AUTOSCAN", "1");
            Console.WriteLine();
            Console.WriteLine("===== 套装摘要一致性（GUI 生产入口）=====");
            try
            {
                RunSta(delegate
                {
                    TestFirstInit();
                    TestManualSwitch();
                    TestRefreshThenManualSwitch();
                    TestClearSelection();
                    TestUnusableSuite();
                    TestRefreshRemovesOrChangesSelection();
                    TestRebuildSuppression();
                });
            }
            finally
            {
                Environment.SetEnvironmentVariable("BOATTOOL_NO_AUTOSCAN", oldScan);
                try { Directory.Delete(root, true); } catch { }
            }
            Console.WriteLine();
            Console.WriteLine("套装摘要一致性：通过 " + passed + " 项，失败 " + failed + " 项");
            if (failed > 0) throw new InvalidOperationException("套装摘要一致性测试失败 " + failed + " 项");
        }

        private static void Check(string name, bool ok, string detail)
        {
            if (ok) { passed++; Console.WriteLine("  [通过] " + name); }
            else { failed++; Console.WriteLine("  [失败] " + name + " —— " + detail); }
        }

        // WinForms 控件创建需要 STA；测试进程本身是 MTA 控制台。
        private static void RunSta(Action action)
        {
            Exception error = null;
            Thread t = new Thread(delegate() { try { action(); } catch (Exception ex) { error = ex; } });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();
            if (error != null) throw new InvalidOperationException("GUI 线程失败：" + error.Message, error);
        }

        // ---------- 夹具 ----------

        private sealed class CountingTransport : IBoardTransport
        {
            public int ListPorts;
            public int ProbeCalls;
            public int ReadChipCalls;
            public int ReadFlashCalls;
            public int WriteFirmwareCalls;
            public int WritePairCalls;
            public int VerifyPairCalls;

            public int Total
            {
                get
                {
                    return ListPorts + ProbeCalls + ReadChipCalls + ReadFlashCalls +
                           WriteFirmwareCalls + WritePairCalls + VerifyPairCalls;
                }
            }

            public IList<SerialPortInfo> ListSerialPorts()
            {
                ListPorts++;
                return new List<SerialPortInfo>();   // 不返回任何串口
            }

            public ProbeResult Probe(string port, int timeoutMs)
            {
                ProbeCalls++;
                return ProbeResult.Fail("测试环境不访问串口");
            }

            public ChipInfo ReadChip(string port, string esptoolPath, out string error)
            {
                ReadChipCalls++; error = "测试环境不访问串口";
                return null;
            }

            public BackflashResult ReadFlash(string port, string esptoolPath, string destPath, long bytes, Logger log)
            {
                ReadFlashCalls++;
                return new BackflashResult { Ok = false, Error = "测试环境不访问串口" };
            }

            public FlashResult WriteFirmware(string port, string esptoolPath, FirmwareSpec spec, bool verify,
                                             string logDir, Logger log)
            {
                WriteFirmwareCalls++;
                return new FlashResult { Ok = false, Error = "测试环境不写入" };
            }

            public PairWriteResult WritePair(string port, string peerMac, BoardIdentity expected, string expectedFwid,
                                             Logger log)
            {
                WritePairCalls++;
                return new PairWriteResult { Ok = false, Error = "测试环境不写入" };
            }

            public VerifyResult VerifyPairing(string port, BoardRole role, string expectedMac, string expectedPeer,
                                              string expectedFwid, Logger log)
            {
                VerifyPairCalls++;
                return new VerifyResult { Ok = false, Message = "测试环境不访问串口" };
            }
        }

        private sealed class FormFixture
        {
            public MainForm Form;
            public Workflow Flow;
            public ComboBox SuiteChoice;
            public Label SuiteInfo;
            public Button FlashButton;
            public FirmwareLibrary Library;
            public CountingTransport Transport;
            public string LibDir;

            public FirmwareSuite SelectedSuite
            {
                get
                {
                    if (SuiteChoice.SelectedIndex < 0) return null;
                    object item = SuiteChoice.SelectedItem;
                    return item == null ? null : (FirmwareSuite)Field(item, "Suite");
                }
            }
        }

        // 每次测试独立库目录，避免互相污染。
        private static string NewLibDir(string label)
        {
            string dir = Path.Combine(root, label + "-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static FormFixture NewForm(string libDir)
        {
            FirmwareLibrary library = FirmwareLibrary.Load(libDir);
            CountingTransport transport = new CountingTransport();
            string cfgDir = Path.Combine(root, "cfg-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(cfgDir);
            AppPaths paths = new AppPaths();
            paths.AppRoot = libDir;
            paths.WritableRoot = cfgDir;
            paths.ConfigDir = cfgDir;
            paths.RecordsDir = Path.Combine(cfgDir, "记录");
            paths.RoleStorePath = Path.Combine(cfgDir, "角色记忆.json");
            paths.FirmwareLibraryDir = libDir;

            FormFixture f = new FormFixture();
            f.Library = library;
            f.Transport = transport;
            f.LibDir = libDir;
            // 构造即执行 BuildUi + FillSuites（首次初始化真实入口）。
            f.Form = new MainForm(paths, library, new RoleStore(paths.RoleStorePath), transport);
            f.Flow = (Workflow)Field(f.Form, "workflow");
            f.SuiteChoice = (ComboBox)Field(f.Form, "suiteChoice");
            f.SuiteInfo = (Label)Field(f.Form, "suiteInfo");
            f.FlashButton = (Button)Field(f.Form, "flashButton");
            return f;
        }

        private static object Field(object target, string name)
        {
            FieldInfo fi = target.GetType().GetField(name,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (fi == null) throw new InvalidOperationException("找不到字段：" + name);
            return fi.GetValue(target);
        }

        private static void Refresh(FormFixture f)
        {
            MethodInfo mi = typeof(MainForm).GetMethod("RefreshLibrary",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (mi == null) throw new InvalidOperationException("找不到 RefreshLibrary");
            mi.Invoke(f.Form, null);
        }

        private static string Summary(FormFixture f) { return f.SuiteInfo.Text ?? ""; }

        private static int IndexOf(ComboBox box, string suiteId)
        {
            for (int i = 0; i < box.Items.Count; i++)
            {
                FirmwareSuite s = (FirmwareSuite)Field(box.Items[i], "Suite");
                if (string.Equals(s.Id, suiteId, StringComparison.OrdinalIgnoreCase)) return i;
            }
            return -1;
        }

        private static void DisposeQuietly(FormFixture f)
        {
            try { if (f.Form != null) f.Form.Dispose(); } catch { }
        }

        // 建一套合法固件（测试夹具，非交付固件）。
        private static string MakeSuite(string libRoot, string dirName, string id, string version,
                                        string boatFwid, string remoteFwid)
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
            File.WriteAllText(Path.Combine(fw, "boat-app.bin"), "boat-" + id + "-" + boatFwid, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(fw, "remote-app.bin"), "remote-" + id + "-" + remoteFwid, new UTF8Encoding(false));
            File.WriteAllBytes(Path.Combine(fw, "boat-bootloader.bin"), new byte[] { 1, 2, 3 });
            File.WriteAllBytes(Path.Combine(fw, "remote-bootloader.bin"), new byte[] { 1, 2, 3 });
            File.WriteAllBytes(Path.Combine(fw, "boot_app0.bin"), new byte[] { 9 });

            string boatLock = boatFwid.IndexOf("-OSOFF", StringComparison.OrdinalIgnoreCase) >= 0 ? "off" : "on";
            string boatRole = "\"BOAT\": { \"fwid\": \"" + boatFwid + "\", \"version\": \"" + version + "\", \"steer_deg\": 20" +
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
                "\"target_rpm\": 590, \"overspeed_rpm\": 600, \"overspeed_lockout\": \"" + boatLock + "\", " +
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

        // 同时核对五件事：下拉选中对象、Workflow、摘要版本、小船 FWID、遥控器 FWID。
        private static void CheckSelection(FormFixture f, string label, string suiteId,
                                           string version, string boatFwid, string remoteFwid)
        {
            FirmwareSuite selected = f.SelectedSuite;
            Check("[" + label + "] 下拉选中 " + suiteId,
                selected != null && selected.Id == suiteId,
                selected == null ? "无选中" : selected.Id);
            Check("[" + label + "] Workflow.SelectedSuite 与下拉是同一对象",
                selected != null && ReferenceEquals(f.Flow.SelectedSuite, selected),
                f.Flow.SelectedSuite == null ? "Workflow=null" : f.Flow.SelectedSuite.Id);
            Check("[" + label + "] 摘要版本为 " + version,
                Summary(f).Contains("版本 " + version), Summary(f));
            Check("[" + label + "] 摘要小船 FWID 为 " + boatFwid,
                Summary(f).Contains(boatFwid), Summary(f));
            Check("[" + label + "] 摘要遥控器 FWID 为 " + remoteFwid,
                Summary(f).Contains(remoteFwid), Summary(f));
        }

        // 断言摘要不残留上一套的版本/FWID。
        private static void CheckNoStale(FormFixture f, string label, string otherBoatFwid, string otherRemoteFwid)
        {
            Check("[" + label + "] 摘要不含其它套装的小船 FWID", !Summary(f).Contains(otherBoatFwid), Summary(f));
            Check("[" + label + "] 摘要不含其它套装的遥控器 FWID", !Summary(f).Contains(otherRemoteFwid), Summary(f));
        }

        // ---------- 1. 首次初始化 ----------

        private static void TestFirstInit()
        {
            Console.WriteLine();
            Console.WriteLine("1. 首次初始化（构造 → 默认选中 → 摘要一致）");
            string libDir = NewLibDir("init");
            // 目录排序把 r10 放前面，验证默认仍选 r11。
            MakeSuite(libDir, "a-590-20-r10", "590-20-r10", VerR10, BoatR10, RemoteR10);
            MakeSuite(libDir, "b-590-20-r11", "590-20-r11", VerR11, BoatR11, RemoteR11);

            FormFixture f = NewForm(libDir);
            try
            {
                CheckSelection(f, "首次初始化", "590-20-r11", VerR11, BoatR11, RemoteR11);
                Check("[首次初始化] 烧录按钮可用", f.FlashButton.Enabled, "按钮被禁用");
                Check("[首次初始化] 摘要不是空提示",
                    !Summary(f).Contains("未选择套装") && !Summary(f).Contains("没有可用套装"), Summary(f));
                Check("[首次初始化] 没有创建诊断控制器", Field(f.Form, "diag") == null, "diag 被创建");
                Check("[首次初始化] 零串口访问", f.Transport.Total == 0, "访问次数 " + f.Transport.Total);
            }
            finally { DisposeQuietly(f); }
        }

        // ---------- 2. 普通切换 r11 → r10 → r11 ----------

        private static void TestManualSwitch()
        {
            Console.WriteLine();
            Console.WriteLine("2. 普通手动切换（r11 → r10 → r11，每步核对下拉/Workflow/摘要/FWID）");
            string libDir = NewLibDir("switch");
            MakeSuite(libDir, "a-590-20-r10", "590-20-r10", VerR10, BoatR10, RemoteR10);
            MakeSuite(libDir, "b-590-20-r11", "590-20-r11", VerR11, BoatR11, RemoteR11);

            FormFixture f = NewForm(libDir);
            try
            {
                CheckSelection(f, "初始", "590-20-r11", VerR11, BoatR11, RemoteR11);

                int r10 = IndexOf(f.SuiteChoice, "590-20-r10");
                Check("[r11→r10] 下拉里有 r10", r10 >= 0, "找不到 r10");
                f.SuiteChoice.SelectedIndex = r10;   // 真实 SelectedIndexChanged → OnSuiteChanged
                CheckSelection(f, "r11→r10", "590-20-r10", VerR10, BoatR10, RemoteR10);
                CheckNoStale(f, "r11→r10", BoatR11, RemoteR11);

                int r11 = IndexOf(f.SuiteChoice, "590-20-r11");
                f.SuiteChoice.SelectedIndex = r11;
                CheckSelection(f, "r10→r11", "590-20-r11", VerR11, BoatR11, RemoteR11);
                CheckNoStale(f, "r10→r11", BoatR10, RemoteR10);

                Check("[普通切换] 零串口访问", f.Transport.Total == 0, "访问次数 " + f.Transport.Total);
                Check("[普通切换] 没有创建诊断控制器", Field(f.Form, "diag") == null, "diag 被创建");
            }
            finally { DisposeQuietly(f); }
        }

        // ---------- 3. 刷新后手动切换 ----------

        private static void TestRefreshThenManualSwitch()
        {
            Console.WriteLine();
            Console.WriteLine("3. 刷新保留 r11 后手动选择 r10（摘要必须立即跟着变）");
            string libDir = NewLibDir("refreshswitch");
            MakeSuite(libDir, "a-590-20-r10", "590-20-r10", VerR10, BoatR10, RemoteR10);
            MakeSuite(libDir, "b-590-20-r11", "590-20-r11", VerR11, BoatR11, RemoteR11);

            FormFixture f = NewForm(libDir);
            try
            {
                CheckSelection(f, "刷新前", "590-20-r11", VerR11, BoatR11, RemoteR11);
                Refresh(f);
                FirmwareLibrary current = (FirmwareLibrary)Field(f.Form, "library");
                FirmwareSuite afterRefresh = f.SelectedSuite;
                Check("[刷新后] 保持 r11 选中", afterRefresh != null && afterRefresh.Id == "590-20-r11",
                    afterRefresh == null ? "无选中" : afterRefresh.Id);
                Check("[刷新后] 选中对象来自新库（不是旧库对象）",
                    afterRefresh != null && ReferenceEquals(afterRefresh, current.Find("590-20-r11")), "仍是旧对象");
                Check("[刷新后] Workflow 与下拉一致",
                    afterRefresh != null && ReferenceEquals(f.Flow.SelectedSuite, afterRefresh), "不一致");
                CheckSelection(f, "刷新后", "590-20-r11", VerR11, BoatR11, RemoteR11);

                int r10 = IndexOf(f.SuiteChoice, "590-20-r10");
                f.SuiteChoice.SelectedIndex = r10;
                CheckSelection(f, "刷新后选 r10", "590-20-r10", VerR10, BoatR10, RemoteR10);
                CheckNoStale(f, "刷新后选 r10", BoatR11, RemoteR11);
                Check("[刷新后选 r10] 烧录按钮可用", f.FlashButton.Enabled, "按钮被禁用");
                Check("[刷新后切换] 零串口访问", f.Transport.Total == 0, "访问次数 " + f.Transport.Total);
            }
            finally { DisposeQuietly(f); }
        }

        // ---------- 4. 清除选择 ----------

        private static void TestClearSelection()
        {
            Console.WriteLine();
            Console.WriteLine("4. 清除选择（摘要显示未选择、不残留 FWID、禁止烧录）");
            string libDir = NewLibDir("clear");
            MakeSuite(libDir, "a-590-20-r10", "590-20-r10", VerR10, BoatR10, RemoteR10);
            MakeSuite(libDir, "b-590-20-r11", "590-20-r11", VerR11, BoatR11, RemoteR11);

            FormFixture f = NewForm(libDir);
            try
            {
                CheckSelection(f, "清除前", "590-20-r11", VerR11, BoatR11, RemoteR11);
                f.SuiteChoice.SelectedIndex = -1;    // 真实选择事件 → 统一实现
                Check("[清除后] 下拉无选中", f.SelectedSuite == null, "仍有选中");
                Check("[清除后] Workflow.SelectedSuite=null", f.Flow.SelectedSuite == null, "未清除");
                Check("[清除后] 摘要显示未选择", Summary(f).Contains("未选择套装"), Summary(f));
                CheckNoStale(f, "清除后", BoatR11, RemoteR11);
                CheckNoStale(f, "清除后", BoatR10, RemoteR10);
                Check("[清除后] 禁止烧录", !f.FlashButton.Enabled, "烧录按钮仍可用");
                Check("[清除后] 零串口访问", f.Transport.Total == 0, "访问次数 " + f.Transport.Total);
            }
            finally { DisposeQuietly(f); }
        }

        // ---------- 5. 不可用套装 ----------

        private static void TestUnusableSuite()
        {
            Console.WriteLine();
            Console.WriteLine("5. 选中不可用套装（摘要给原因、禁止烧录）");
            string libDir = NewLibDir("unusable");
            MakeSuite(libDir, "a-590-20-r11", "590-20-r11", VerR11, BoatR11, RemoteR11);
            // FWID 不合法 → 自检失败，但仍在列表中显示（带原因）。
            MakeSuite(libDir, "b-broken", "broken", "v-broken", "NO FWID", RemoteR11);

            FormFixture f = NewForm(libDir);
            try
            {
                FirmwareLibrary lib = (FirmwareLibrary)Field(f.Form, "library");
                FirmwareSuite broken = lib.Find("broken");
                Check("[不可用] 夹具确实不可用", broken != null && !broken.Usable, "夹具意外可用");
                CheckSelection(f, "初始", "590-20-r11", VerR11, BoatR11, RemoteR11);

                int idx = IndexOf(f.SuiteChoice, "broken");
                Check("[不可用] 不可用套装仍显示在下拉里", idx >= 0, "下拉里找不到");
                f.SuiteChoice.SelectedIndex = idx;
                Check("[不可用] 摘要显示不可用原因", Summary(f).Contains("这套不可用") && Summary(f).Contains("禁止烧录"),
                    Summary(f));
                Check("[不可用] Workflow 指向该套装（不是悄悄回退）",
                    f.Flow.SelectedSuite != null && f.Flow.SelectedSuite.Id == "broken",
                    f.Flow.SelectedSuite == null ? "null" : f.Flow.SelectedSuite.Id);
                Check("[不可用] 禁止烧录", !f.FlashButton.Enabled, "烧录按钮仍可用");
                Check("[不可用] 摘要不残留 r11 的 FWID", !Summary(f).Contains(BoatR11), Summary(f));
                Check("[不可用] 零串口访问", f.Transport.Total == 0, "访问次数 " + f.Transport.Total);
            }
            finally { DisposeQuietly(f); }
        }

        // ---------- 6. 刷新内容变化 / 删除原套装 ----------

        private static void TestRefreshRemovesOrChangesSelection()
        {
            Console.WriteLine();
            Console.WriteLine("6. 刷新删除/改动原套装（保持无选择，摘要不被旧事件恢复）");

            // 6a 删除原套装
            string libDir = NewLibDir("missing");
            MakeSuite(libDir, "a-590-20-r10", "590-20-r10", VerR10, BoatR10, RemoteR10);
            string r11Dir = MakeSuite(libDir, "b-590-20-r11", "590-20-r11", VerR11, BoatR11, RemoteR11);
            FormFixture f = NewForm(libDir);
            try
            {
                CheckSelection(f, "删除前", "590-20-r11", VerR11, BoatR11, RemoteR11);
                Directory.Delete(r11Dir, true);
                Refresh(f);
                Check("[删除] 下拉无选中", f.SuiteChoice.SelectedIndex == -1, "index=" + f.SuiteChoice.SelectedIndex);
                Check("[删除] Workflow.SelectedSuite=null", f.Flow.SelectedSuite == null, "未清除");
                Check("[删除] 摘要显示未选择", Summary(f).Contains("未选择套装"), Summary(f));
                CheckNoStale(f, "删除", BoatR11, RemoteR11);
                Check("[删除] 禁止烧录", !f.FlashButton.Enabled, "烧录按钮仍可用");
                Check("[删除] 没有自动选中剩下的 r10", f.SelectedSuite == null,
                    f.SelectedSuite == null ? "" : f.SelectedSuite.Id);
                Check("[删除] 零串口访问", f.Transport.Total == 0, "访问次数 " + f.Transport.Total);
            }
            finally { DisposeQuietly(f); }

            // 6b 原套装内容变化（同 ID 同版本，镜像被替换）
            string libDir2 = NewLibDir("changed");
            MakeSuite(libDir2, "a-590-20-r10", "590-20-r10", VerR10, BoatR10, RemoteR10);
            string r11Dir2 = MakeSuite(libDir2, "b-590-20-r11", "590-20-r11", VerR11, BoatR11, RemoteR11);
            FormFixture f2 = NewForm(libDir2);
            try
            {
                CheckSelection(f2, "改动前", "590-20-r11", VerR11, BoatR11, RemoteR11);
                // 只改镜像字节，清单/版本不变。
                File.WriteAllText(Path.Combine(r11Dir2, "固件", "boat-app.bin"), "replaced image", new UTF8Encoding(false));
                Refresh(f2);
                Check("[改动] 下拉无选中", f2.SuiteChoice.SelectedIndex == -1, "index=" + f2.SuiteChoice.SelectedIndex);
                Check("[改动] Workflow.SelectedSuite=null", f2.Flow.SelectedSuite == null, "未清除");
                Check("[改动] 摘要显示未选择", Summary(f2).Contains("未选择套装"), Summary(f2));
                CheckNoStale(f2, "改动", BoatR11, RemoteR11);
                Check("[改动] 禁止烧录", !f2.FlashButton.Enabled, "烧录按钮仍可用");
            }
            finally { DisposeQuietly(f2); }
        }

        // ---------- 7. 重建事件抑制 ----------

        private static void TestRebuildSuppression()
        {
            Console.WriteLine();
            Console.WriteLine("7. 重建期间事件被抑制，结束后统一使用新库对象");
            string libDir = NewLibDir("suppress");
            MakeSuite(libDir, "a-590-20-r10", "590-20-r10", VerR10, BoatR10, RemoteR10);
            MakeSuite(libDir, "b-590-20-r11", "590-20-r11", VerR11, BoatR11, RemoteR11);

            // 7a 守卫本身：抑制期间真实改变选中项，不得写回 Workflow / 摘要。
            FormFixture g = NewForm(libDir);
            try
            {
                CheckSelection(g, "守卫前", "590-20-r11", VerR11, BoatR11, RemoteR11);
                string before = Summary(g);
                FirmwareSuite beforeSuite = g.Flow.SelectedSuite;
                int r10 = IndexOf(g.SuiteChoice, "590-20-r10");

                SetSuppress(g, true);
                g.SuiteChoice.SelectedIndex = r10;          // 下拉真的变了，但事件必须被抑制
                Check("[守卫] 抑制期间 Workflow 不变",
                    ReferenceEquals(g.Flow.SelectedSuite, beforeSuite), "被写回了");
                Check("[守卫] 抑制期间摘要不变", Summary(g) == before, Summary(g));
                Check("[守卫] 抑制期间烧录按钮未被重算", g.FlashButton.Enabled, "按钮被改动");

                SetSuppress(g, false);
                Check("[守卫] 恢复后下拉仍停在 r10",
                    g.SelectedSuite != null && g.SelectedSuite.Id == "590-20-r10",
                    g.SelectedSuite == null ? "无选中" : g.SelectedSuite.Id);

                int r11 = IndexOf(g.SuiteChoice, "590-20-r11");
                g.SuiteChoice.SelectedIndex = r11;           // 事件生效 → 摘要转 r11
                CheckSelection(g, "守卫恢复后选 r11", "590-20-r11", VerR11, BoatR11, RemoteR11);
                g.SuiteChoice.SelectedIndex = r10;           // 再切回 r10
                CheckSelection(g, "守卫恢复后选 r10", "590-20-r10", VerR10, BoatR10, RemoteR10);
            }
            finally { DisposeQuietly(g); }

            // 7b 刷新全过程：重建产生的事件不得写回；写回只发生在最后的统一回调。
            FormFixture f = NewForm(libDir);
            try
            {
                CheckSelection(f, "重建前", "590-20-r11", VerR11, BoatR11, RemoteR11);
                FirmwareSuite oldSuite = f.SelectedSuite;
                string preRefreshSummary = Summary(f);

                // 探针挂在生产处理器之后：每次下拉事件都能看到“处理器执行后”的状态。
                // 若重建期间没有抑制，Items.Clear() 造成的那次事件会把摘要写成“未选择套装。”
                // 并把 Workflow 写回 null；SetSelectedIndex 造成的事件会把新对象写回。
                List<string> seenSummary = new List<string>();
                List<object> seenFlow = new List<object>();
                List<int> seenIndex = new List<int>();
                EventHandler probe = delegate
                {
                    seenSummary.Add(Summary(f));
                    seenFlow.Add(f.Flow.SelectedSuite);
                    seenIndex.Add(f.SuiteChoice.SelectedIndex);
                };
                f.SuiteChoice.SelectedIndexChanged += probe;
                Refresh(f);
                f.SuiteChoice.SelectedIndexChanged -= probe;

                Check("[抑制] 重建确实产生了下拉事件（抑制是必要的）", seenIndex.Count >= 1,
                    "一次事件都没有");
                bool summaryTouched = false;
                foreach (string s in seenSummary) if (s != preRefreshSummary) summaryTouched = true;
                Check("[抑制] 重建期间摘要没有被改写", !summaryTouched,
                    "重建中途摘要变成了：" + (seenSummary.Count == 0 ? "" : seenSummary[0]));
                bool wroteBack = false;
                object lastFlow = seenFlow.Count == 0 ? null : seenFlow[seenFlow.Count - 1];
                if (lastFlow != null) wroteBack = true;
                Check("[抑制] 重建期间 Workflow 没有被事件写回", !wroteBack,
                    lastFlow == null ? "" : "中途被写成 " + ((FirmwareSuite)lastFlow).Id);

                FirmwareLibrary current = (FirmwareLibrary)Field(f.Form, "library");
                FirmwareSuite after = f.SelectedSuite;
                Check("[抑制] 结束后选中新库对象",
                    after != null && ReferenceEquals(after, current.Find("590-20-r11")), "不是新库对象");
                Check("[抑制] 旧库对象没有回到 Workflow",
                    f.Flow.SelectedSuite == null || !ReferenceEquals(f.Flow.SelectedSuite, oldSuite), "仍是旧对象");
                Check("[抑制] 统一回调把 Workflow 写成新库对象",
                    after != null && ReferenceEquals(f.Flow.SelectedSuite, after), "回调未生效");
                CheckSelection(f, "重建后", "590-20-r11", VerR11, BoatR11, RemoteR11);
                Check("[抑制] 零串口访问", f.Transport.Total == 0, "访问次数 " + f.Transport.Total);
                Check("[抑制] 没有创建诊断控制器", Field(f.Form, "diag") == null, "diag 被创建");
            }
            finally { DisposeQuietly(f); }
        }

        private static void SetSuppress(FormFixture f, bool value)
        {
            FieldInfo fi = typeof(MainForm).GetField("suppressSuiteSelectionEvent",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (fi == null) throw new InvalidOperationException("找不到 suppressSuiteSelectionEvent");
            fi.SetValue(f.Form, value);
        }
    }
}
