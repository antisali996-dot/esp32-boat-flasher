using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;

namespace BoatTool
{
    // FAULT 字段数值合法性测试（fault-number-fix 轮）。
    //
    // 全部经 FakeDiagChannel → DiagnosticsController.Pump 注入实际文本；
    // 只有纯数值边界才直接调用适配器（仍属于同一条实现路径）。
    //
    // 依据：已批准 r10 快照 boat/fault_evidence.h
    //   MeasureRecord { uint32_t ms; float rpm; int32_t pwm; uint32_t pulses;
    //                   uint32_t elapsed; float integral; }
    // 输出格式（boat/diagnostic_commands.h → FaultEvidence::formatCurrent/formatPrevious）：
    //   id=%lu ms=%lu rpm=%.2f pwm=%ld pulses=%lu elapsed=%lu integral=%.2f
    public static class FaultNumberTests
    {
        private static int passed;
        private static int failed;
        private static string root;

        private const string BoatFwid = "JAYFWB10-S20-A70-110-U1278-1500-1722-R590-OS600-Cr10-diagshared";
        private const string BoatMac = "24:62:AB:F1:36:64";

        // 正式格式（rpm/integral 固定两位小数）
        private const string GoodCur =
            "JAY FAULT CURRENT id=1 name=OVERSPEED ms=2850 rpm=612.40 pwm=1 pulses=74 elapsed=50 integral=0.10";
        private const string GoodPrev =
            "JAY FAULT PREVIOUS id=1 ms=2800 rpm=595.08 pwm=1 pulses=70 elapsed=50 integral=0.00";

        public static void Run()
        {
            passed = 0; failed = 0;
            root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test-work",
                "faultnum-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            Console.WriteLine();
            Console.WriteLine("===== FAULT 字段数值合法性 =====");
            try
            {
                TestDotDotDot();
                TestMultipleDots();
                TestEmptyMinusNaNInfinity();
                TestValidDecimalsAndZero();
                TestNegativeRules();
                TestIntegerBoundaries();
                TestNonDigitChars();
                TestInvariantCulture();
                TestInvalidCurrentWithValidPrevious();
                TestValidCurrentWithInvalidPrevious();
                TestInvalidDoesNotOverwriteExisting();
                TestLegalResendAfterInvalid();
                TestInvalidPersistedAndExported();
                TestConflictRulesUnchanged();
                TestRawBytesUnchanged();
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
            Console.WriteLine();
            Console.WriteLine("FAULT 数值校验：通过 " + passed + " 项，失败 " + failed + " 项");
            if (failed > 0) throw new InvalidOperationException("FAULT 数值校验测试失败 " + failed + " 项");
        }

        private static void Check(string name, bool ok, string detail)
        {
            if (ok) { passed++; Console.WriteLine("  [通过] " + name); }
            else { failed++; Console.WriteLine("  [失败] " + name + " —— " + detail); }
        }

        // ---------- 夹具 ----------

        private sealed class Fixture
        {
            public FakeDiagChannel Ch;
            public FakeDiagClock Clock;
            public DiagnosticsController C;
            public AppPaths Paths;
        }

        private static Fixture Start(string label)
        {
            string dir = Path.Combine(root, label + "-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(dir);
            Fixture f = new Fixture();
            f.Ch = new FakeDiagChannel();
            f.Clock = new FakeDiagClock();
            f.Paths = new AppPaths();
            f.Paths.AppRoot = dir; f.Paths.WritableRoot = dir;
            f.Paths.ConfigDir = Path.Combine(dir, "配置");
            f.Paths.RecordsDir = Path.Combine(dir, "记录");
            f.Paths.RoleStorePath = Path.Combine(f.Paths.ConfigDir, "角色记忆.json");
            f.C = new DiagnosticsController(f.Paths, f.Ch, f.Clock, QueryPolicy.ApprovedR10(), null);
            f.C.PortLocks = new PortLockRegistry();
            f.C.SetPort("COM_SIM");
            f.Ch.Deliver("JAYv4 ROLE=BOAT MAC=" + BoatMac +
                         " PEER=24:62:AB:EF:CF:84 PAIR=2 STEER=20 FWID=" + BoatFwid +
                         " VER=20261103-590rpm-r10\n");
            f.C.ConnectAndIdentify();
            f.C.StartRecording();
            return f;
        }

        private static void Feed(Fixture f, string text)
        {
            f.Ch.Deliver(text);
            f.Clock.Advance(10);
            f.C.Pump();
        }

        private static int CompleteGroups(Fixture f)
        {
            int n = 0;
            foreach (FaultGroup g in f.C.Faults.Groups) if (g.Complete) n++;
            return n;
        }

        private static FaultSnapshot LastFault(Fixture f)
        {
            IList<FaultSnapshot> seen = f.C.FaultsSeen;
            return seen.Count == 0 ? null : seen[seen.Count - 1];
        }

        private static bool IsMalformed(FaultSnapshot s)
        {
            return s != null && s.State == FaultAdapterState.Malformed && !s.HasFields;
        }

        // ---------- 1–3 非法小数 ----------

        private static void TestDotDotDot()
        {
            Console.WriteLine();
            Console.WriteLine("1. rpm=... / integral=..");
            Fixture f = Start("dots3");
            Feed(f, "JAY COUNT boot=b1 ms=100 total=70\n" +
                    "JAY FAULT CURRENT id=1 name=OVERSPEED ms=100 rpm=... pwm=100 pulses=72 elapsed=50 integral=..\n" +
                    GoodPrev + "\n");
            FaultSnapshot cur = f.C.FaultsSeen.Count > 0 ? f.C.FaultsSeen[0] : null;
            Check("rpm=... 的行判为解析失败", IsMalformed(cur),
                cur == null ? "无行" : cur.State + " HasFields=" + cur.HasFields);
            Check("失败原因指向 rpm", cur != null && cur.ParseFailureField == "rpm",
                cur == null ? "" : cur.ParseFailureField);
            Check("完整原文保留", cur != null && cur.Raw.Contains("rpm=...") && cur.Raw.Contains("integral=.."),
                cur == null ? "" : cur.Raw);
            Check("同组 PREVIOUS 不能组成完整", CompleteGroups(f) == 0, "完整组 " + CompleteGroups(f));
            // 合法 PREVIOUS 本身会建立“不完整（缺 CURRENT）”组；关键是没有有效 CURRENT 候选。
            bool noValidCurrent = true;
            foreach (FaultGroup grp in f.C.Faults.Groups)
                if (grp.Current != null || grp.CurrentDistinct.Count > 0 || grp.Complete) noValidCurrent = false;
            Check("非法行不进入有效候选", noValidCurrent, "出现有效 CURRENT");
            f.C.StopRecording();
        }

        private static void TestMultipleDots()
        {
            Console.WriteLine();
            Console.WriteLine("2. rpm=1.2.3 / integral=1.2.3");
            Fixture f = Start("dots2");
            Feed(f, "JAY COUNT boot=b1 ms=100 total=70\n" +
                    "JAY FAULT CURRENT id=1 name=OVERSPEED ms=100 rpm=1.2.3 pwm=100 pulses=72 elapsed=50 integral=1.2.3\n" +
                    GoodPrev + "\n");
            FaultSnapshot cur = f.C.FaultsSeen.Count > 0 ? f.C.FaultsSeen[0] : null;
            Check("多个小数点判为解析失败", IsMalformed(cur),
                cur == null ? "无行" : cur.State.ToString());
            Check("不组成完整组", CompleteGroups(f) == 0, "完整组 " + CompleteGroups(f));
            f.C.StopRecording();
        }

        private static void TestEmptyMinusNaNInfinity()
        {
            Console.WriteLine();
            Console.WriteLine("3. 空字段 / 仅负号 / NaN / Infinity");
            string[] badRpm = new string[] { "rpm= ", "rpm=-", "rpm=nan", "rpm=NaN", "rpm=inf", "rpm=Infinity", "rpm=" };
            R10FaultAdapter a = new R10FaultAdapter();
            foreach (string bad in badRpm)
            {
                string line = "JAY FAULT CURRENT id=1 name=X ms=100 " + bad +
                              " pwm=1 pulses=72 elapsed=50 integral=0.10";
                FaultSnapshot s = a.Parse(line, "dev", "b1");
                Check("拒绝 " + bad.Trim(), s.State == FaultAdapterState.Malformed && !s.HasFields,
                    s.State + " HasFields=" + s.HasFields);
            }
            // 空白字段在正则层就可能不匹配；两种路径都必须是失败状态，不能是 Parsed。
            string[] badIntegral = new string[] { "integral=..", "integral=-", "integral=nan", "integral=inf" };
            foreach (string bad in badIntegral)
            {
                string line = "JAY FAULT PREVIOUS id=1 ms=100 rpm=590.00 pwm=1 pulses=72 elapsed=50 " + bad;
                FaultSnapshot s = a.Parse(line, "dev", "b1");
                Check("拒绝 " + bad, s.State != FaultAdapterState.Parsed && !s.HasFields,
                    s.State + " HasFields=" + s.HasFields);
            }
        }

        // ---------- 4–5 合法值 ----------

        private static void TestValidDecimalsAndZero()
        {
            Console.WriteLine();
            Console.WriteLine("4. 正式格式的正常小数与合法零值");
            Fixture f = Start("valid");
            Feed(f, "JAY COUNT boot=b1 ms=100 total=70\n" + GoodCur + "\n" + GoodPrev + "\n");
            FaultSnapshot cur = f.C.FaultsSeen.Count > 0 ? f.C.FaultsSeen[0] : null;
            FaultSnapshot prev = f.C.FaultsSeen.Count > 1 ? f.C.FaultsSeen[1] : null;
            Check("正常两位小数解析成功", cur != null && cur.State == FaultAdapterState.Parsed &&
                cur.HasFields, cur == null ? "无行" : cur.State.ToString());
            Check("rpm 数值正确", cur != null && Math.Abs(cur.RpmValue - 612.40f) < 0.001f,
                cur == null ? "" : cur.RpmValue.ToString(CultureInfo.InvariantCulture));
            Check("integral 数值正确", cur != null && Math.Abs(cur.IntegralValue - 0.10f) < 0.001f,
                cur == null ? "" : cur.IntegralValue.ToString(CultureInfo.InvariantCulture));
            Check("零值合法", prev != null && prev.State == FaultAdapterState.Parsed &&
                prev.IntegralValue == 0f, prev == null ? "无行" : prev.State.ToString());
            Check("两行组成完整组", CompleteGroups(f) == 1, "完整组 " + CompleteGroups(f));

            FaultSnapshot zero = new R10FaultAdapter().Parse(
                "JAY FAULT PREVIOUS id=2 ms=0 rpm=0.00 pwm=0 pulses=0 elapsed=0 integral=0.00", "dev", "b1");
            Check("全零 PREVIOUS 合法", zero.State == FaultAdapterState.Parsed && zero.HasFields,
                zero.State.ToString());
            f.C.StopRecording();
        }

        private static void TestNegativeRules()
        {
            Console.WriteLine();
            Console.WriteLine("5. 正式允许的负值 / 不允许的负值");
            R10FaultAdapter a = new R10FaultAdapter();

            // rpm/integral 是 float → 允许负
            FaultSnapshot neg = a.Parse(
                "JAY FAULT CURRENT id=1 name=X ms=1 rpm=-12.50 pwm=-1 pulses=0 elapsed=0 integral=-0.25",
                "dev", "b1");
            Check("负 rpm 合法", neg.State == FaultAdapterState.Parsed && neg.HasFields &&
                Math.Abs(neg.RpmValue + 12.50f) < 0.001f, neg.State.ToString());
            Check("负 integral 合法", Math.Abs(neg.IntegralValue + 0.25f) < 0.001f,
                neg.IntegralValue.ToString(CultureInfo.InvariantCulture));

            // pwm 是 int32_t → 允许负（-1 表示反转）
            Check("pwm 负值合法", neg.PwmValue == -1, neg.PwmValue.ToString());

            // ms / pulses / elapsed / id 是 uint32_t → 不允许负
            string[] unsignedFields = new string[] { "ms=-1", "pulses=-1", "elapsed=-1", "id=-1" };
            foreach (string bad in unsignedFields)
            {
                string line = "JAY FAULT CURRENT id=1 name=X ms=100 rpm=1.00 pwm=1 pulses=72 elapsed=50 integral=0.10";
                if (bad.StartsWith("id=")) line = line.Replace("id=1", bad);
                else line = line.Replace(bad.Substring(0, bad.IndexOf('=')) + "=" +
                    LineValue(line, bad.Substring(0, bad.IndexOf('='))), bad);
                FaultSnapshot s = a.Parse(line, "dev", "b1");
                Check("拒绝无符号字段负值 " + bad, s.State != FaultAdapterState.Parsed && !s.HasFields,
                    s.State + " HasFields=" + s.HasFields);
            }
        }

        private static string LineValue(string line, string field)
        {
            int i = line.IndexOf(field + "=", StringComparison.Ordinal);
            if (i < 0) return "";
            int start = i + field.Length + 1;
            int end = line.IndexOf(' ', start);
            return end < 0 ? line.Substring(start) : line.Substring(start, end - start);
        }

        private static void TestIntegerBoundaries()
        {
            Console.WriteLine();
            Console.WriteLine("6. 整数类型边界与越界");
            R10FaultAdapter a = new R10FaultAdapter();

            FaultSnapshot maxAll = a.Parse(
                "JAY FAULT CURRENT id=4294967295 name=X ms=4294967295 rpm=1.00 pwm=-2147483648 " +
                "pulses=4294967295 elapsed=4294967295 integral=0.10", "dev", "b1");
            Check("uint32 上界与 int32 下界都合法", maxAll.State == FaultAdapterState.Parsed &&
                maxAll.HasFields, maxAll.State + " " + maxAll.ParseFailureField);

            FaultSnapshot maxPwm = a.Parse(
                "JAY FAULT CURRENT id=1 name=X ms=1 rpm=1.00 pwm=2147483647 pulses=1 elapsed=1 integral=0.10",
                "dev", "b1");
            Check("int32 上界合法", maxPwm.State == FaultAdapterState.Parsed && maxPwm.PwmValue == int.MaxValue,
                maxPwm.State.ToString());

            string[] overflows = new string[] {
                "id=4294967296", "ms=4294967296", "pulses=4294967296", "elapsed=4294967296",
                "pwm=2147483648", "pwm=-2147483649"
            };
            foreach (string bad in overflows)
            {
                string line = "JAY FAULT CURRENT id=1 name=X ms=100 rpm=1.00 pwm=1 pulses=72 elapsed=50 integral=0.10";
                string field = bad.Substring(0, bad.IndexOf('='));
                line = line.Replace(field + "=" + LineValue(line, field), bad);
                FaultSnapshot s = a.Parse(line, "dev", "b1");
                Check("拒绝越界 " + bad, s.State != FaultAdapterState.Parsed && !s.HasFields,
                    s.State + " HasFields=" + s.HasFields);
            }
        }

        private static void TestNonDigitChars()
        {
            Console.WriteLine();
            Console.WriteLine("7. 中文/非数字字符混入字段");
            R10FaultAdapter a = new R10FaultAdapter();
            string[] bads = new string[] {
                "JAY FAULT CURRENT id=1 name=X ms=100 rpm=612.40米 pwm=1 pulses=72 elapsed=50 integral=0.10",
                "JAY FAULT CURRENT id=1 name=X ms=一百 rpm=1.00 pwm=1 pulses=72 elapsed=50 integral=0.10",
                "JAY FAULT CURRENT id=1 name=X ms=100 rpm=1.00 pwm=1 pulses=7２ elapsed=50 integral=0.10",
                "JAY FAULT CURRENT id=1 name=X ms=100 rpm=0x10.00 pwm=1 pulses=72 elapsed=50 integral=0.10",
            };
            for (int i = 0; i < bads.Length; i++)
            {
                FaultSnapshot s = a.Parse(bads[i], "dev", "b1");
                Check("拒绝非数字字符 #" + (i + 1), s.State != FaultAdapterState.Parsed && !s.HasFields,
                    s.State + " HasFields=" + s.HasFields);
                Check("非法行仍保留原文 #" + (i + 1), s.Raw == bads[i], "原文被改");
            }
        }

        // ---------- 8 区域设置 ----------

        private static void TestInvariantCulture()
        {
            Console.WriteLine();
            Console.WriteLine("8. 电脑区域使用逗号小数点时仍按 InvariantCulture 解析");
            CultureInfo backup = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                R10FaultAdapter a = new R10FaultAdapter();
                FaultSnapshot ok = a.Parse(GoodCur, "dev", "b1");
                Check("de-DE 下 612.40 仍解析为 612.40", ok.State == FaultAdapterState.Parsed &&
                    Math.Abs(ok.RpmValue - 612.40f) < 0.001f,
                    ok.State + " value=" + ok.RpmValue.ToString(CultureInfo.InvariantCulture));

                FaultSnapshot comma = a.Parse(
                    "JAY FAULT CURRENT id=1 name=X ms=100 rpm=612,40 pwm=1 pulses=72 elapsed=50 integral=0.10",
                    "dev", "b1");
                Check("逗号小数点按协议判失败", comma.State != FaultAdapterState.Parsed && !comma.HasFields,
                    comma.State.ToString());
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = backup;
            }
        }

        // ---------- 9–12 关联行为 ----------

        private static void TestInvalidCurrentWithValidPrevious()
        {
            Console.WriteLine();
            Console.WriteLine("9. 非法 CURRENT + 合法 PREVIOUS → 不能完整");
            Fixture f = Start("badcur");
            Feed(f, "JAY COUNT boot=b1 ms=100 total=70\n" +
                    "JAY FAULT CURRENT id=1 name=X ms=100 rpm=... pwm=1 pulses=72 elapsed=50 integral=..\n" +
                    GoodPrev + "\n");
            Check("完整组数为 0", CompleteGroups(f) == 0, "完整组 " + CompleteGroups(f));
            IList<FaultGroup> groups = f.C.Faults.Groups;
            bool anyGroup = groups.Count > 0;
            Check("非法 CURRENT 不进入候选", !anyGroup || groups[0].CurrentDistinct.Count == 0,
                anyGroup ? groups[0].CurrentDistinct.Count.ToString() : "无组");
            f.C.StopRecording();
        }

        private static void TestValidCurrentWithInvalidPrevious()
        {
            Console.WriteLine();
            Console.WriteLine("10. 合法 CURRENT + 非法 PREVIOUS → 不能完整");
            Fixture f = Start("badprev");
            Feed(f, "JAY COUNT boot=b1 ms=100 total=70\n" + GoodCur + "\n" +
                    "JAY FAULT PREVIOUS id=1 ms=-1 rpm=595.08 pwm=1 pulses=70 elapsed=50 integral=0.00\n");
            Check("完整组数为 0", CompleteGroups(f) == 0, "完整组 " + CompleteGroups(f));
            FaultGroup g = null;
            IList<FaultGroup> groups = f.C.Faults.Groups;
            if (groups.Count > 0) g = groups[0];
            Check("组停在缺 PREVIOUS", g != null && g.State == FaultGroupState.Incomplete &&
                g.MissingText == "缺 PREVIOUS", g == null ? "无组" : g.DescribeState());
            f.C.StopRecording();
        }

        private static void TestInvalidDoesNotOverwriteExisting()
        {
            Console.WriteLine();
            Console.WriteLine("11. 已有合法 CURRENT，再收到非法 CURRENT → 不补齐、不覆盖、不解除");
            Fixture f = Start("nooverwrite");
            Feed(f, "JAY COUNT boot=b1 ms=100 total=70\n" + GoodCur + "\n");
            FaultGroup g = f.C.Faults.Groups.Count > 0 ? f.C.Faults.Groups[0] : null;
            Check("先进入不完整", g != null && g.State == FaultGroupState.Incomplete, "准备失败");
            string beforeRaw = g == null ? "" : g.Current.Raw;

            Feed(f, "JAY FAULT CURRENT id=1 name=X ms=100 rpm=1.2.3 pwm=1 pulses=72 elapsed=50 integral=..\n");
            Check("非法行不覆盖已有 CURRENT", g != null && g.Current != null && g.Current.Raw == beforeRaw,
                "被覆盖");
            Check("仍为不完整", g != null && g.State == FaultGroupState.Incomplete, g == null ? "" : g.State.ToString());
            Check("非法行本身判失败", IsMalformed(LastFault(f)) && LastFault(f).ParseFailureField != "",
                LastFault(f) == null ? "无行" : LastFault(f).ParseFailureField);

            Feed(f, GoodPrev + "\n");
            Check("后续合法 PREVIOUS 仍能按原规则组成完整", g != null && g.Complete, g == null ? "" : g.State.ToString());
            f.C.StopRecording();
        }

        private static void TestLegalResendAfterInvalid()
        {
            Console.WriteLine();
            Console.WriteLine("12. 非法行后的合法重发 → 按原规则处理，非法证据仍保留");
            Fixture f = Start("resend");
            string bad = "JAY FAULT CURRENT id=1 name=X ms=100 rpm=... pwm=1 pulses=72 elapsed=50 integral=..";
            Feed(f, "JAY COUNT boot=b1 ms=100 total=70\n" + bad + "\n" + GoodPrev + "\n" + GoodCur + "\n");
            Check("重发后组成完整", CompleteGroups(f) == 1, "完整组 " + CompleteGroups(f));
            int badCount = 0;
            foreach (FaultSnapshot s in f.C.FaultsSeen) if (s.Raw == bad) badCount++;
            Check("非法行仍在快照里", badCount == 1, "条数 " + badCount);
            string csv = ReadShared(Path.Combine(f.C.Session.RootDir, "FAULT快照.csv"));
            Check("非法行仍在落盘", csv.Contains("rpm=..."), "缺非法行");
            f.C.StopRecording();
        }

        // ---------- 13 落盘与导出 ----------

        private static void TestInvalidPersistedAndExported()
        {
            Console.WriteLine();
            Console.WriteLine("13. 非法行进入落盘与导出（含 Raw 与失败原因）");
            Fixture f = Start("export");
            string bad = "JAY FAULT CURRENT id=1 name=X ms=100 rpm=... pwm=1 pulses=72 elapsed=50 integral=..";
            Feed(f, "JAY COUNT boot=b1 ms=100 total=70\n" + bad + "\n" + GoodPrev + "\n");
            string dir = f.C.Session.RootDir;
            string csv = ReadShared(Path.Combine(dir, "FAULT快照.csv"));
            Check("FAULT快照.csv 含非法原文", csv.Contains("rpm=..."), "缺原文");
            Check("FAULT快照.csv 含失败状态", csv.Contains("Malformed"), "缺 Malformed");
            Check("FAULT快照.csv 含失败原因", csv.Contains("rpm"), "缺字段名");

            f.C.StopRecording();
            ExportResult r = DiagnosticsExporter.Export(f.C.Session, f.Paths, "test", f.Clock.Now);
            Check("导出成功", r.Ok, r.Error);
            if (!r.Ok) return;
            using (ZipArchive z = ZipFile.OpenRead(r.ZipPath))
            {
                string ez = ReadEntry(z, "FAULT快照.csv");
                Check("导出含非法原文", ez.Contains("rpm=...") && ez.Contains("integral=.."), "缺原文");
                Check("导出含失败原因", ez.Contains("Malformed"), "缺状态");
                string note = ReadEntry(z, "导出说明.txt");
                Check("导出说明提到解析失败", note.Contains("解析失败") || note.Contains("Malformed"),
                    "说明未提及");
            }
        }

        private static void TestConflictRulesUnchanged()
        {
            Console.WriteLine();
            Console.WriteLine("14. 同组有效证据与冲突锁存规则不变");
            Fixture f = Start("conflict");
            string cur2 = GoodCur.Replace("rpm=612.40", "rpm=999.00");
            Feed(f, "JAY COUNT boot=b1 ms=100 total=70\n" + GoodCur + "\n" + cur2 + "\n" + GoodPrev + "\n");
            FaultGroup g = f.C.Faults.Groups.Count > 0 ? f.C.Faults.Groups[0] : null;
            Check("两条不同 CURRENT 仍判冲突", g != null && g.State == FaultGroupState.Conflicting,
                g == null ? "无组" : g.State.ToString());
            Feed(f, "JAY FAULT CURRENT id=1 name=X ms=100 rpm=1.2.3 pwm=1 pulses=72 elapsed=50 integral=..\n");
            Check("非法行不解除冲突", g != null && g.State == FaultGroupState.Conflicting,
                g == null ? "" : g.State.ToString());
            f.C.StopRecording();
        }

        // ---------- Raw 字节未改 ----------

        private static void TestRawBytesUnchanged()
        {
            Console.WriteLine();
            Console.WriteLine("15. 原始接收字节未被改写");
            Fixture f = Start("rawbytes");
            string bad = "JAY FAULT CURRENT id=1 name=X ms=100 rpm=1.2.3 pwm=1 pulses=72 elapsed=50 integral=..";
            Feed(f, "JAY COUNT boot=b1 ms=100 total=70\n" + bad + "\n" + GoodPrev + "\n");
            f.C.StopRecording();
            string rawPath = Path.Combine(f.C.Session.RootDir, "设备分段", f.C.DeviceSegment, "raw.bin");
            byte[] raw = File.ReadAllBytes(rawPath);
            string text = Encoding.UTF8.GetString(raw);
            Check("raw.bin 含非法行原文", text.Contains(bad), "缺非法行");
            Check("raw.bin 含合法行原文", text.Contains(GoodPrev), "缺合法行");
            Check("raw.bin 未被重写（只追加）", raw.Length >= Encoding.UTF8.GetByteCount(bad),
                raw.Length.ToString());
        }

        // ---------- 工具 ----------

        private static string ReadShared(string path)
        {
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (StreamReader r = new StreamReader(fs, Encoding.UTF8))
                return r.ReadToEnd();
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
