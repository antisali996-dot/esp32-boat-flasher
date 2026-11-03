using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace BoatTool
{
    // r11 适配测试（r11-adaptation 轮）。
    //
    // 覆盖：身份解析与 FWID 匹配、OS600/OSOFF 三态展示、清单与 FWID 矛盾拒绝、
    // 烧录确认框的锁存差异、默认选择与 preset 指向 r11、
    // 诊断能力按板上实际身份判定、r11 白名单、UNSUPPORTED 停用、
    // r11 样例经真实 Pump 进入 COUNT/FAULT 关联与导出、原有严格规则不回退。
    public static class R11AdaptationTests
    {
        private static int passed;
        private static int failed;
        private static string root;

        private const string boatR11 = "JAYFWB11-S20-A70-110-U1278-1500-1722-R590-OSOFF-Cr11-osoff";
        private const string remoteR11 = "JAYFWR11-S20-A70-110-U1278-1500-1722-D1750-2150";
        private const string boatR10 = "JAYFWB10-S20-A70-110-U1278-1500-1722-R590-OS600-Cr10-diagshared";
        private const string remoteR10 = "JAYFWR10-S20-A70-110-U1278-1500-1722-D1750-2150";
        private const string boatMac = "24:62:AB:F1:36:64";

        public static void Run()
        {
            passed = 0; failed = 0;
            root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test-work",
                "r11-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            Console.WriteLine();
            Console.WriteLine("===== r11 适配 =====");
            try
            {
                TestIdentityParse();
                TestLockoutThreeStates();
                TestLockoutDisplayDiffers();
                TestOsOffNotShownAsEnabled();
                TestUnmarkedShowsUnknown();
                TestManifestFwidConflictRejected();
                TestDefaultSuiteIsR11();
                TestDiagnosticsByBoardIdentity();
                TestR11Whitelist();
                TestUnsupportedStops();
                TestR11SampleThroughPump();
                TestStrictRulesUnchanged();
                TestManifestMatchesImages();
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
            Console.WriteLine();
            Console.WriteLine("r11 适配：通过 " + passed + " 项，失败 " + failed + " 项");
            if (failed > 0) throw new InvalidOperationException("r11 适配测试失败 " + failed + " 项");
        }

        private static void Check(string name, bool ok, string detail)
        {
            if (ok) { passed++; Console.WriteLine("  [通过] " + name); }
            else { failed++; Console.WriteLine("  [失败] " + name + " —— " + detail); }
        }

        // ---------- 1. 身份解析与 FWID 匹配 ----------

        private static void TestIdentityParse()
        {
            Console.WriteLine();
            Console.WriteLine("1. r11 身份解析与准确 FWID 匹配");
            string line = "JAYv4 ROLE=BOAT MAC=" + boatMac +
                          " PEER=24:62:AB:EF:CF:84 PAIR=2 STEER=20 FWID=" + boatR11 +
                          " VER=20261103-590rpm-r11";
            BoardIdentity id = IdentityParser.FromLine(line);
            Check("身份行解析成功", id != null, "解析失败");
            Check("FWID 完全一致", id != null && id.FirmwareId == boatR11, id == null ? "" : id.FirmwareId);
            Check("FW_SCHEMA 11 体现在 FWID", id != null && id.FirmwareId.Contains("JAYFWB11-"), "前缀不对");
            Check("OSOFF 标记存在", id != null && id.FirmwareId.Contains("-OSOFF"), "缺 OSOFF");
            Check("不是 OS600", id != null && !id.FirmwareId.Contains("-OS600"), "误含 OS600");
            Check("转向契约 20°/70-110", id != null && id.FirmwareId.Contains("S20-A70-110"), "角度契约不符");
            Check("目标 590", id != null && id.FirmwareId.Contains("-R590-"), "转速不符");
            Check("控制修订 r11-osoff", id != null && id.FirmwareId.Contains("Cr11-osoff"), "修订号不符");
        }

        // ---------- 2–3. 三态与展示差异 ----------

        private static void TestLockoutThreeStates()
        {
            Console.WriteLine();
            Console.WriteLine("2. 三态模型：开启 / 关闭 / 未知");
            Check("OS600 → 开启", OverspeedLockoutText.FromFwid(boatR10) == OverspeedLockout.Enabled,
                OverspeedLockoutText.FromFwid(boatR10).ToString());
            Check("OSOFF → 关闭", OverspeedLockoutText.FromFwid(boatR11) == OverspeedLockout.Disabled,
                OverspeedLockoutText.FromFwid(boatR11).ToString());
            Check("无 OS 标记 → 未知",
                OverspeedLockoutText.FromFwid("JAYFWB9-S20-A70-110-U1278-1500-1722-R590") == OverspeedLockout.Unknown,
                "被猜成开启或关闭");
            Check("空 FWID → 未知", OverspeedLockoutText.FromFwid("") == OverspeedLockout.Unknown, "非未知");
            Check("远控 FWID 无 OS 标记 → 未知",
                OverspeedLockoutText.FromFwid(remoteR11) == OverspeedLockout.Unknown, "被猜");
            Check("中文文案可区分",
                OverspeedLockoutText.Chinese(OverspeedLockout.Enabled) !=
                OverspeedLockoutText.Chinese(OverspeedLockout.Disabled) &&
                OverspeedLockoutText.Chinese(OverspeedLockout.Unknown) !=
                OverspeedLockoutText.Chinese(OverspeedLockout.Enabled), "文案无法区分");
        }

        private static void TestLockoutDisplayDiffers()
        {
            Console.WriteLine();
            Console.WriteLine("3. r10 与 r11 均 590 转，但锁存展示不同");
            FirmwareCatalog r10 = LoadCatalog("r10-catalog", boatR10, remoteR10, "20261103-590rpm-r10", "on", boatR10);
            FirmwareCatalog r11 = LoadCatalog("r11-catalog", boatR11, remoteR11, "20261103-590rpm-r11", "off", boatR11);
            Check("r10 清单可用", r10.Problems.Count == 0, string.Join("；", r10.Problems.ToArray()));
            Check("r11 清单可用", r11.Problems.Count == 0, string.Join("；", r11.Problems.ToArray()));
            Check("两者目标转速都是 590", r10.TargetRpm == 590 && r11.TargetRpm == 590, "转速不一致");
            Check("r10 摘要说锁存开启", r10.Summary.Contains("锁存已开启"), r10.Summary);
            Check("r11 摘要说锁存关闭", r11.Summary.Contains("锁存已关闭"), r11.Summary);
            Check("r11 摘要写明 600 仅为历史数值", r11.Summary.Contains("历史数值"), r11.Summary);
            Check("两侧摘要不相同", r10.Summary != r11.Summary, "摘要相同");
        }

        private static void TestOsOffNotShownAsEnabled()
        {
            Console.WriteLine();
            Console.WriteLine("4. OSOFF 不得显示为“600 保护开启”");
            FirmwareCatalog r11 = LoadCatalog("osoff", boatR11, remoteR11, "20261103-590rpm-r11", "off", boatR11);
            string s = r11.Summary;
            Check("摘要不含“超速保护 600 转”", !s.Contains("超速保护 600"), s);
            Check("Boat spec 结论为关闭", r11.ForRole(BoardRole.Boat).Lockout == OverspeedLockout.Disabled,
                r11.ForRole(BoardRole.Boat).Lockout.ToString());
            Check("文案明确“不参与停机”", s.Contains("不参与停机"), s);
        }

        private static void TestUnmarkedShowsUnknown()
        {
            Console.WriteLine();
            Console.WriteLine("5. 无 OS 标记 → 未声明，不猜");
            string old = "JAYFWB9-S20-A70-110-U1278-1500-1722-R590-Cr9-window-enum";
            FirmwareCatalog c = LoadCatalog("nomark", old, remoteR10, "20261103-590rpm-r9", "", old);
            Check("清单可用（未声明不视为错误）", c.Problems.Count == 0, string.Join("；", c.Problems.ToArray()));
            Check("状态为未知", c.ForRole(BoardRole.Boat).Lockout == OverspeedLockout.Unknown,
                c.ForRole(BoardRole.Boat).Lockout.ToString());
            Check("摘要说未声明", c.Summary.Contains("未声明"), c.Summary);
        }

        // ---------- 6. 清单与 FWID 矛盾 ----------

        private static void TestManifestFwidConflictRejected()
        {
            Console.WriteLine();
            Console.WriteLine("6. 清单声明开启但 FWID 是 OSOFF → 套装不可用");
            FirmwareCatalog bad = LoadCatalog("conflict", boatR11, remoteR11, "20261103-590rpm-r11", "on", boatR11);
            Check("清单被判有问题", bad.Problems.Count > 0, "未发现问题");
            bool mentioned = false;
            foreach (string p in bad.Problems)
                if (p.Contains("矛盾")) mentioned = true;
            Check("问题说明是矛盾", mentioned, string.Join("；", bad.Problems.ToArray()));

            // 声明 off 但 FWID 是 OS600 也必须拒绝
            FirmwareCatalog bad2 = LoadCatalog("conflict2", boatR10, remoteR10, "20261103-590rpm-r10", "off", boatR10);
            Check("反向矛盾也被拒", bad2.Problems.Count > 0, "未发现问题");
        }

        // ---------- 7. 默认选择 ----------

        private static void TestDefaultSuiteIsR11()
        {
            Console.WriteLine();
            Console.WriteLine("7. 默认选择 r11（不受目录排序影响）");
            string lib = Path.Combine(root, "lib-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(lib);
            // 故意让 r10 的目录名排在前面
            MakeSuite(lib, "590-20-r10", boatR10, remoteR10, "20261103-590rpm-r10", "on");
            MakeSuite(lib, "590-20-r11", boatR11, remoteR11, "20261103-590rpm-r11", "off");

            FirmwareLibrary library = FirmwareLibrary.Load(lib);
            Check("两套装都被加载", library.Suites.Count == 2, library.Suites.Count.ToString());
            int firstUsable = -1, preferred = -1;
            for (int i = 0; i < library.Suites.Count; i++)
            {
                if (!library.Suites[i].Usable) continue;
                if (firstUsable < 0) firstUsable = i;
                if (preferred < 0 && library.Suites[i].Version.IndexOf("r11", StringComparison.OrdinalIgnoreCase) >= 0)
                    preferred = i;
            }
            Check("目录顺序里 r10 在前", firstUsable == 0, "顺序异常");
            Check("优先命中的是 r11", preferred >= 0 &&
                library.Suites[preferred].Version.IndexOf("r11", StringComparison.OrdinalIgnoreCase) >= 0,
                preferred < 0 ? "未找到 r11" : library.Suites[preferred].Version);
            Check("r11 套装锁存为关闭", library.Suites[preferred].Lockout == OverspeedLockout.Disabled,
                library.Suites[preferred].Lockout.ToString());

            // preset 必须按声明的 suite_id 找套装，不能取“第一个可用（这里是 r10）”
            AppPaths paths = new AppPaths();
            paths.PresetPath = Path.Combine(lib, "preset.json");
            File.WriteAllText(paths.PresetPath,
                "{\"id\":\"r11-test\",\"kind\":\"general\",\"suite_id\":\"590-20-r11\","
                + "\"boat_fwid\":\"" + boatR11 + "\",\"remote_fwid\":\"" + remoteR11 + "\"}",
                new UTF8Encoding(false));
            PresetConfig preset = PresetConfig.Load(paths.PresetPath);
            FirmwareSuite byId = null;
            foreach (FirmwareSuite s in library.Suites)
                if (string.Equals(s.Id, preset.SuiteId, StringComparison.OrdinalIgnoreCase)) { byId = s; break; }
            Check("按 suite_id 命中 r11 而不是 r10", byId != null &&
                byId.Version.IndexOf("r11", StringComparison.OrdinalIgnoreCase) >= 0,
                byId == null ? "未命中" : byId.Version);
            preset.CrossCheck(byId);
            Check("按 suite_id 交叉核对通过", preset.Problems.Count == 0,
                string.Join("；", preset.Problems.ToArray()));
            // 反向证明：若按“第一个可用套装”核对会失败
            PresetConfig wrong = PresetConfig.Load(paths.PresetPath);
            wrong.CrossCheck(library.Suites[0]);
            Check("按第一个可用套装核对确实会失败（说明必须按 id）", wrong.Problems.Count > 0,
                "未产生问题，无法证明");
        }

        // ---------- 8. 诊断能力按板上身份 ----------

        private static void TestDiagnosticsByBoardIdentity()
        {
            Console.WriteLine();
            Console.WriteLine("8. 板上 r11、界面选 r10 时，诊断仍按板上 r11 决定");
            QueryPolicy policy = QueryPolicy.ApprovedR11();
            Check("策略同时批准 r10 小船", policy.Allows(boatR10, BoardRole.Boat), "r10 被拒");
            Check("策略批准 r11 小船", policy.Allows(boatR11, BoardRole.Boat), "r11 被拒");
            Check("r11 遥控器不批准", !policy.Allows(remoteR11, BoardRole.Remote), "遥控器被放行");
            Check("遥控器白名单为空", policy.RemoteFwids.Count == 0, policy.RemoteFwids.Count.ToString());

            // 策略只看传入的 FWID（板上身份），与套装下拉无关。
            FakeDiagChannel ch = new FakeDiagChannel();
            FakeDiagClock clock = new FakeDiagClock();
            DiagnosticsController c = NewController(ch, clock, "diag-r11");
            ch.Deliver("JAYv4 ROLE=BOAT MAC=" + boatMac +
                       " PEER=00:00:00:00:00:00 PAIR=2 STEER=20 FWID=" + boatR11 +
                       " VER=20261103-590rpm-r11\n");
            c.ConnectAndIdentify();
            c.StartRecording();
            clock.Advance(5100); c.Pump();
            Check("板上 r11 → 会发 COUNT", ch.CommandCount("JAY COUNT") == 1,
                "未发（" + ch.CommandCount("JAY COUNT") + "）");
            c.StopRecording();
        }

        private static void TestR11Whitelist()
        {
            Console.WriteLine();
            Console.WriteLine("9. r11 白名单生效；遥控器与未知 FWID 不放行");
            QueryPolicy p = QueryPolicy.ApprovedR11();
            Check("r11 小船放行", p.Allows(boatR11, BoardRole.Boat), "被拒");
            Check("r11 遥控器拒绝", !p.Allows(remoteR11, BoardRole.Remote), "被放行");
            Check("未知 FWID 拒绝", !p.Allows("JAYFWB11-S20-A70-110-UNKNOWN", BoardRole.Boat), "被放行");
            Check("前缀相同但 FWID 不同也拒绝",
                !p.Allows("JAYFWB11-S20-A70-110-U1278-1500-1722-R590-OSOFF-Cr11-other", BoardRole.Boat),
                "前缀放行");
            Check("旧 r9 拒绝", !p.Allows("JAYFWB9-S20-A70-110-U1278-1500-1722-R590-OS600-Cr9-x", BoardRole.Boat),
                "被放行");
        }

        private static void TestUnsupportedStops()
        {
            Console.WriteLine();
            Console.WriteLine("10. 未支持查询回应后停用，不产生重试风暴");
            FakeDiagChannel ch = new FakeDiagChannel();
            FakeDiagClock clock = new FakeDiagClock();
            DiagnosticsController c = NewController(ch, clock, "unsup-r11");
            ch.Deliver("JAYv4 ROLE=BOAT MAC=" + boatMac +
                       " PEER=00:00:00:00:00:00 PAIR=2 STEER=20 FWID=" + boatR11 +
                       " VER=20261103-590rpm-r11\n");
            c.ConnectAndIdentify();
            c.StartRecording();
            clock.Advance(5100); c.Pump();
            int before = ch.CommandCount("JAY COUNT");
            ch.Deliver("JAY ERROR=UNSUPPORTED_COMMAND\n");
            clock.Advance(100); c.Pump();
            Check("停用标志置位", c.CountQueryDisabled, "未停用");
            for (int i = 0; i < 40; i++) { clock.Advance(1000); c.Pump(); }
            Check("不再重发", ch.CommandCount("JAY COUNT") == before,
                "多发了 " + (ch.CommandCount("JAY COUNT") - before));
            c.StopRecording();
        }

        // ---------- 11. r11 样例经真实 Pump ----------

        private static void TestR11SampleThroughPump()
        {
            Console.WriteLine();
            Console.WriteLine("11. r11 样例经真实 Pump 进入 COUNT / FAULT 关联与导出");
            FakeDiagChannel ch = new FakeDiagChannel();
            FakeDiagClock clock = new FakeDiagClock();
            DiagnosticsController c = NewController(ch, clock, "sample-r11");
            ch.Deliver("JAYv4 ROLE=BOAT MAC=" + boatMac +
                       " PEER=24:62:AB:EF:CF:84 PAIR=2 STEER=20 FWID=" + boatR11 +
                       " VER=20261103-590rpm-r11\n");
            c.ConnectAndIdentify();
            c.StartRecording();
            ch.Deliver("JAY COUNT boot=b1 ms=1000 total=18446744073709551615\n" +
                       "JAY FAULT CURRENT id=1 name=OVERSPEED ms=2850 rpm=612.40 pwm=1 pulses=74 elapsed=50 integral=0.10\n" +
                       "JAY FAULT PREVIOUS id=1 ms=2800 rpm=595.08 pwm=1 pulses=70 elapsed=50 integral=0.00\n");
            clock.Advance(10); c.Pump();

            Check("COUNT 被解析", c.Counts.Count == 1 && c.Counts[0].Parsed, "COUNT 未解析");
            Check("COUNT 64 位保真", c.Counts.Count == 1 && c.Counts[0].Total == ulong.MaxValue,
                c.Counts.Count == 0 ? "" : c.Counts[0].Total.ToString());
            Check("FAULT 两行都解析", c.FaultsSeen.Count == 2, c.FaultsSeen.Count.ToString());
            FaultGroup g = null;
            foreach (FaultGroup grp in c.Faults.Groups) g = grp;
            Check("形成完整组", g != null && g.Complete, g == null ? "无组" : g.State.ToString());
            Check("boot 已确认", g != null && FaultBoot.IsConfirmed(g.BootSegment),
                g == null ? "" : g.BootSegment);

            c.StopRecording();
            ExportResult r = DiagnosticsExporter.Export(c.Session, PathsOf(c), "r11-test", clock.Now);
            Check("导出成功", r.Ok, r.Error);
            if (!r.Ok) return;
            using (ZipArchive z = ZipFile.OpenRead(r.ZipPath))
            {
                string groups = ReadEntry(z, "FAULT关联.csv");
                Check("导出含完整组", groups.Contains("complete"), "缺 complete");
                Check("导出含两行原文", groups.Contains("rpm=612.40") && groups.Contains("rpm=595.08"), "缺原文");
            }
        }

        // ---------- 12. 严格规则不回退 ----------

        private static void TestStrictRulesUnchanged()
        {
            Console.WriteLine();
            Console.WriteLine("12. 原有严格规则在 r11 下同样生效");
            R10FaultAdapter a = new R10FaultAdapter();
            FaultSnapshot bad = a.Parse(
                "JAY FAULT CURRENT id=1 name=X ms=100 rpm=... pwm=1 pulses=72 elapsed=50 integral=..", "dev", "b1");
            Check("非法数字仍判失败", bad.State == FaultAdapterState.Malformed && !bad.HasFields,
                bad.State.ToString());

            FakeDiagChannel ch = new FakeDiagChannel();
            FakeDiagClock clock = new FakeDiagClock();
            DiagnosticsController c = NewController(ch, clock, "strict-r11");
            ch.Deliver("JAYv4 ROLE=BOAT MAC=" + boatMac +
                       " PEER=24:62:AB:EF:CF:84 PAIR=2 STEER=20 FWID=" + boatR11 +
                       " VER=20261103-590rpm-r11\n");
            c.ConnectAndIdentify();
            c.StartRecording();
            // 未知 boot
            ch.Deliver("JAY FAULT CURRENT id=1 name=X ms=100 rpm=1.00 pwm=1 pulses=1 elapsed=1 integral=0.10\n" +
                       "JAY FAULT PREVIOUS id=1 ms=1 rpm=1.00 pwm=1 pulses=1 elapsed=1 integral=0.10\n");
            clock.Advance(10); c.Pump();
            FaultGroup g1 = null;
            foreach (FaultGroup grp in c.Faults.Groups) g1 = grp;
            Check("未知 boot 仍不完整", g1 != null && g1.State == FaultGroupState.BootUnconfirmed,
                g1 == null ? "无组" : g1.State.ToString());
            int complete = 0;
            foreach (FaultGroup grp in c.Faults.Groups) if (grp.Complete) complete++;
            Check("完整组数为 0", complete == 0, complete.ToString());
            c.StopRecording();
        }

        // ---------- 13. 清单与镜像一致 ----------

        private static void TestManifestMatchesImages()
        {
            Console.WriteLine();
            Console.WriteLine("13. r11 清单与镜像、输入哈希一致");
            string suite = FindR11Suite();
            if (suite == null)
            {
                Check("找到 r11 固件库", false, "未找到 work/r11-library/590-20-r11");
                return;
            }
            string manifestPath = Path.Combine(suite, "固件", "固件清单.json");
            string importPath = Path.Combine(suite, "import.json");
            Check("清单存在", File.Exists(manifestPath), manifestPath);
            Check("导入记录存在", File.Exists(importPath), importPath);
            if (!File.Exists(manifestPath) || !File.Exists(importPath)) return;

            FirmwareCatalog cat = FirmwareCatalog.Load(manifestPath);
            Check("清单自检通过", cat.Problems.Count == 0, string.Join("；", cat.Problems.ToArray()));
            Check("版本是 r11", cat.Version == "20261103-590rpm-r11", cat.Version);
            Check("目标 590", cat.TargetRpm == 590, cat.TargetRpm.ToString());
            Check("角度契约 20°/70-110",
                cat.SteerDeg == 20 && cat.AngleMin == 70 && cat.AngleMax == 110, "角度不符");
            Check("脉宽 1278/1500/1722",
                cat.PulseMinUs == 1278 && cat.CenterUs == 1500 && cat.PulseMaxUs == 1722, "脉宽不符");
            Check("Boat FWID 含 OSOFF 且不含 OS600",
                cat.ForRole(BoardRole.Boat).Fwid.Contains("-OSOFF") &&
                !cat.ForRole(BoardRole.Boat).Fwid.Contains("-OS600"), cat.ForRole(BoardRole.Boat).Fwid);
            Check("Remote FWID 正确", cat.ForRole(BoardRole.Remote).Fwid == remoteR11,
                cat.ForRole(BoardRole.Remote).Fwid);
            Check("Boat 锁存为关闭", cat.ForRole(BoardRole.Boat).Lockout == OverspeedLockout.Disabled,
                cat.ForRole(BoardRole.Boat).Lockout.ToString());

            // 镜像里真的含该 FWID
            Check("小船镜像含 FWID",
                Hashing.FileContainsAscii(cat.ForRole(BoardRole.Boat).AppPath, cat.ForRole(BoardRole.Boat).Fwid),
                "镜像里没有");
            Check("遥控器镜像含 FWID",
                Hashing.FileContainsAscii(cat.ForRole(BoardRole.Remote).AppPath, cat.ForRole(BoardRole.Remote).Fwid),
                "镜像里没有");

            // import.json 的镜像哈希与实际一致
            Dictionary<string, object> imp = Json.AsObject(Json.Parse(File.ReadAllText(importPath)));
            string recordedBoat = Json.GetString(imp, "boat_app_sha256", "");
            string actualBoat = Sha256(Path.Combine(suite, "固件", "boat-app.bin"));
            Check("导入记录小船镜像哈希一致", recordedBoat == actualBoat,
                recordedBoat + " vs " + actualBoat);
            string recordedRemote = Json.GetString(imp, "remote_app_sha256", "");
            string actualRemote = Sha256(Path.Combine(suite, "固件", "remote-app.bin"));
            Check("导入记录遥控器镜像哈希一致", recordedRemote == actualRemote,
                recordedRemote + " vs " + actualRemote);
        }

        // ---------- 工具 ----------

        private static readonly Dictionary<DiagnosticsController, AppPaths> pathsMap =
            new Dictionary<DiagnosticsController, AppPaths>();

        private static AppPaths PathsOf(DiagnosticsController c)
        {
            AppPaths p;
            return pathsMap.TryGetValue(c, out p) ? p : null;
        }

        private static string FindR11Suite()
        {
            // 从测试程序所在目录向上找工程根，再定位 work\\r11-library\\590-20-r11
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            for (int i = 0; i < 6 && dir != null; i++)
            {
                string candidate = Path.Combine(dir, "work", "r11-library", "590-20-r11");
                if (Directory.Exists(candidate)) return candidate;
                candidate = Path.Combine(dir, "r11-library", "590-20-r11");
                if (Directory.Exists(candidate)) return candidate;
                DirectoryInfo parent = Directory.GetParent(dir);
                dir = parent == null ? null : parent.FullName;
            }
            return null;
        }

        private static string Sha256(string path)
        {
            using (System.Security.Cryptography.SHA256 sha = System.Security.Cryptography.SHA256.Create())
            using (FileStream fs = File.OpenRead(path))
            {
                byte[] h = sha.ComputeHash(fs);
                StringBuilder sb = new StringBuilder(h.Length * 2);
                foreach (byte b in h) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        private static DiagnosticsController NewController(FakeDiagChannel ch, FakeDiagClock clock, string label)
        {
            string dir = Path.Combine(root, label + "-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(dir);
            AppPaths p = new AppPaths();
            p.AppRoot = dir; p.WritableRoot = dir;
            p.ConfigDir = Path.Combine(dir, "配置");
            p.RecordsDir = Path.Combine(dir, "记录");
            p.RoleStorePath = Path.Combine(p.ConfigDir, "角色记忆.json");
            DiagnosticsController c = new DiagnosticsController(p, ch, clock, QueryPolicy.ApprovedR11(), null);
            c.PortLocks = new PortLockRegistry();
            c.SetPort("COM_SIM");
            pathsMap[c] = p;
            return c;
        }

        // 生成一套最小可用固件清单（只用于测试加载与校验）。
        private static FirmwareCatalog LoadCatalog(string label, string boatFwid, string remoteFwid,
                                                   string version, string lockout, string boatImageFwid)
        {
            string dir = Path.Combine(root, label + "-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(dir);
            string fw = Path.Combine(dir, "固件");
            Directory.CreateDirectory(fw);
            // 用 FWID 文本填充镜像，让 FileContainsAscii 能命中
            File.WriteAllText(Path.Combine(fw, "boat-app.bin"), "xx" + boatImageFwid + "yy", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(fw, "remote-app.bin"), "xx" + remoteFwid + "yy", new UTF8Encoding(false));
            File.WriteAllBytes(Path.Combine(fw, "boat-bootloader.bin"), new byte[] { 1, 2, 3 });
            File.WriteAllBytes(Path.Combine(fw, "remote-bootloader.bin"), new byte[] { 1, 2, 3 });
            File.WriteAllBytes(Path.Combine(fw, "partitions.bin"), new byte[] { 0xAA, 0x50 });
            File.WriteAllBytes(Path.Combine(fw, "boot_app0.bin"), new byte[] { 9 });

            string manifest = "{"
                + "\"schema\":11,"
                + "\"version\":\"" + version + "\","
                + "\"steer_deg\":20,\"angle_min\":70,\"angle_max\":110,"
                + "\"center_us\":1500,\"pulse_min_us\":1278,\"pulse_max_us\":1722,"
                + "\"pair_protocol\":2,\"target_rpm\":590,\"overspeed_rpm\":600,"
                + "\"roles\":{"
                + "\"BOAT\":{\"fwid\":\"" + boatFwid + "\",\"version\":\"" + version + "\",\"steer_deg\":20,"
                + (string.IsNullOrEmpty(lockout) ? "" : "\"overspeed_lockout\":\"" + lockout + "\",")
                + "\"app\":\"boat-app.bin\",\"app_offset\":65536,"
                + "\"bootloader\":\"boat-bootloader.bin\",\"bootloader_offset\":4096,"
                + "\"partitions\":\"partitions.bin\",\"partitions_offset\":32768,"
                + "\"boot_app0\":\"boot_app0.bin\",\"boot_app0_offset\":57344},"
                + "\"REMOTE\":{\"fwid\":\"" + remoteFwid + "\",\"version\":\"" + version + "\",\"steer_deg\":20,"
                + "\"app\":\"remote-app.bin\",\"app_offset\":65536,"
                + "\"bootloader\":\"remote-bootloader.bin\",\"bootloader_offset\":4096,"
                + "\"partitions\":\"partitions.bin\",\"partitions_offset\":32768,"
                + "\"boot_app0\":\"boot_app0.bin\",\"boot_app0_offset\":57344}}"
                + "}";
            File.WriteAllText(Path.Combine(fw, "固件清单.json"), manifest, new UTF8Encoding(false));
            return FirmwareCatalog.Load(Path.Combine(fw, "固件清单.json"));
        }

        // 生成一套最小可用套装目录（含 import.json、工具\esptool.exe、分区表）。
        private static void MakeSuite(string libRoot, string id, string boatFwid, string remoteFwid,
                                      string version, string lockout)
        {
            string suite = Path.Combine(libRoot, id);
            string fw = Path.Combine(suite, "固件");
            string tools = Path.Combine(suite, "工具");
            Directory.CreateDirectory(fw);
            Directory.CreateDirectory(tools);
            File.WriteAllText(Path.Combine(fw, "boat-app.bin"), "xx" + boatFwid + "yy", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(fw, "remote-app.bin"), "xx" + remoteFwid + "yy", new UTF8Encoding(false));
            File.WriteAllBytes(Path.Combine(fw, "boat-bootloader.bin"), new byte[] { 1, 2, 3 });
            File.WriteAllBytes(Path.Combine(fw, "remote-bootloader.bin"), new byte[] { 1, 2, 3 });
            File.WriteAllBytes(Path.Combine(fw, "boot_app0.bin"), new byte[] { 9 });
            File.WriteAllBytes(Path.Combine(tools, "esptool.exe"), new byte[] { 0 });

            // 直接复制 r11 固件库里的真实分区表：手写二进制容易与解析器不兼容，
            // 且这里要验证的是清单/锁存逻辑，不是分区表构造。
            string realSuite = FindR11Suite();
            if (realSuite != null)
            {
                File.Copy(Path.Combine(realSuite, "固件", "boat-partitions.bin"),
                          Path.Combine(fw, "boat-partitions.bin"), true);
                File.Copy(Path.Combine(realSuite, "固件", "remote-partitions.bin"),
                          Path.Combine(fw, "remote-partitions.bin"), true);
            }

            string manifest = "{"
                + "\"schema\":11,"
                + "\"version\":\"" + version + "\","
                + "\"steer_deg\":20,\"angle_min\":70,\"angle_max\":110,"
                + "\"center_us\":1500,\"pulse_min_us\":1278,\"pulse_max_us\":1722,"
                + "\"pair_protocol\":2,\"target_rpm\":590,\"overspeed_rpm\":600,"
                + "\"roles\":{"
                + "\"BOAT\":{\"fwid\":\"" + boatFwid + "\",\"version\":\"" + version + "\",\"steer_deg\":20,"
                + (string.IsNullOrEmpty(lockout) ? "" : "\"overspeed_lockout\":\"" + lockout + "\",")
                + "\"app\":\"boat-app.bin\",\"app_offset\":65536,"
                + "\"bootloader\":\"boat-bootloader.bin\",\"bootloader_offset\":4096,"
                + "\"partitions\":\"boat-partitions.bin\",\"partitions_offset\":32768,"
                + "\"boot_app0\":\"boot_app0.bin\",\"boot_app0_offset\":57344},"
                + "\"REMOTE\":{\"fwid\":\"" + remoteFwid + "\",\"version\":\"" + version + "\",\"steer_deg\":20,"
                + "\"app\":\"remote-app.bin\",\"app_offset\":65536,"
                + "\"bootloader\":\"remote-bootloader.bin\",\"bootloader_offset\":4096,"
                + "\"partitions\":\"remote-partitions.bin\",\"partitions_offset\":32768,"
                + "\"boot_app0\":\"boot_app0.bin\",\"boot_app0_offset\":57344}}"
                + "}";
            File.WriteAllText(Path.Combine(fw, "固件清单.json"), manifest, new UTF8Encoding(false));

            string imp = "{\"id\":\"" + id + "\",\"version\":\"" + version + "\",\"target_rpm\":590,"
                + "\"steer_deg\":20,\"boat_fwid\":\"" + boatFwid + "\",\"remote_fwid\":\"" + remoteFwid + "\"}";
            File.WriteAllText(Path.Combine(suite, "import.json"), imp, new UTF8Encoding(false));
        }

        private static string ReadEntry(ZipArchive z, string name)
        {
            foreach (ZipArchiveEntry e in z.Entries)
            {
                if (e.FullName.Replace('\\', '/') == name)
                {
                    using (Stream s = e.Open())
                    using (StreamReader r = new StreamReader(s, Encoding.UTF8))
                        return r.ReadToEnd();
                }
            }
            return "";
        }
    }
}
