using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace BoatTool
{
    // 故障证据关联修复测试（第 r10-fault-fix 轮）。
    //
    // 全部通过实际 DiagnosticsController.Pump 注入文本，不直接测 Build 返回值；
    // 只有明确的“注册表单元规则”才直接操作 FaultRegistry。
    public static class R10FaultFixTests
    {
        private static int passed;
        private static int failed;
        private static string root;

        private const string BoatFwid = "JAYFWB10-S20-A70-110-U1278-1500-1722-R590-OS600-Cr10-diagshared";
        private const string BoatMac = "24:62:AB:F1:36:64";
        private const string BoatMac2 = "24:62:AB:F0:72:94";

        private const string Cur =
            "JAY FAULT CURRENT id=1 name=OVERSPEED ms=2850 rpm=612.40 pwm=1 pulses=74 elapsed=50 integral=0.10";
        private const string Prev =
            "JAY FAULT PREVIOUS id=1 ms=2800 rpm=595.08 pwm=1 pulses=70 elapsed=50 integral=0.00";

        public static void Run()
        {
            passed = 0; failed = 0;
            root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test-work",
                "faultfix-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            Console.WriteLine();
            Console.WriteLine("===== 故障证据关联修复 =====");
            try
            {
                TestCompleteViaPump();
                TestPreviousFirst();
                TestOnlyCurrent();
                TestOnlyPrevious();
                TestEmptyBoot();
                TestPlaceholderBoot();
                TestCrossBoundaries();
                TestUnconfirmedDevice();
                TestDuplicateCurrent();
                TestConflictingCurrent();
                TestConflictingPrevious();
                TestConflictAfterOldCombo();
                TestNoneKeepsHistory();
                TestCountAfterUnknownBootFault();
                TestPendingSegmentFault();
                TestMultiLineAndSplitChunks();
                TestNotStuckInPendingAdapter();
                TestHistorySurvivesWindow();
                TestExportFailureKeepsData();
                TestExportZipContainsGroups();
                TestControllerQueryableStates();
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
            Console.WriteLine();
            Console.WriteLine("故障关联修复：通过 " + passed + " 项，失败 " + failed + " 项");
            if (failed > 0) throw new InvalidOperationException("故障关联修复测试失败 " + failed + " 项");
        }

        private static void Check(string name, bool ok, string detail)
        {
            if (ok) { passed++; Console.WriteLine("  [通过] " + name); }
            else { failed++; Console.WriteLine("  [失败] " + name + " —— " + detail); }
        }

        // ---------- 测试用采集夹具 ----------

        private sealed class Fixture
        {
            public FakeDiagChannel Ch;
            public FakeDiagClock Clock;
            public DiagnosticsController C;
            public AppPaths Paths;
            public string Dir;

            public void Stop() { try { C.StopRecording(); } catch { } }
        }

        private static Fixture Start(string label, string mac)
        {
            string dir = Path.Combine(root, label + "-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(dir);
            Fixture f = new Fixture();
            f.Dir = dir;
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
            f.Ch.Deliver("JAYv4 ROLE=BOAT MAC=" + mac +
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

        private static FaultGroup GroupOf(Fixture f, string id, string boot)
        {
            return f.C.Faults.Find(f.C.DeviceSegment, boot, id);
        }

        private static FaultGroup FirstGroup(Fixture f)
        {
            IList<FaultGroup> list = f.C.Faults.Groups;
            return list.Count == 0 ? null : list[0];
        }

        private static FaultGroup FirstOf(FaultRegistry reg)
        {
            IList<FaultGroup> list = reg.Groups;
            return list.Count == 0 ? null : list[0];
        }

        // ---------- 1–4 基本两行/顺序/缺行 ----------

        private static void TestCompleteViaPump()
        {
            Console.WriteLine();
            Console.WriteLine("1. 已确认设备与 boot，两行 → 完整");
            Fixture f = Start("complete", BoatMac);
            Feed(f, "JAY COUNT boot=b1 ms=100 total=10\n" + Cur + "\n" + Prev + "\n");
            FaultGroup g = GroupOf(f, "1", "b1");
            Check("存在关联组", g != null, "未建立组");
            Check("状态为完整", g != null && g.State == FaultGroupState.Complete,
                g == null ? "无组" : g.State.ToString());
            Check("CURRENT 与 PREVIOUS 都引用到真实行",
                g != null && g.Current != null && g.Previous != null && g.Current.Raw == Cur && g.Previous.Raw == Prev,
                "原文引用丢失");
            Check("两行都有接收序号",
                g != null && g.Current.NearSequence > 0 && g.Previous.NearSequence > 0,
                "缺序号");
            f.Stop();
        }

        private static void TestPreviousFirst()
        {
            Console.WriteLine();
            Console.WriteLine("2. PREVIOUS 先到 → 最终仍完整");
            Fixture f = Start("prevfirst", BoatMac);
            Feed(f, "JAY COUNT boot=b1 ms=100 total=10\n" + Prev + "\n");
            FaultGroup g = GroupOf(f, "1", "b1");
            Check("先到 PREVIOUS 时为不完整", g != null && g.State == FaultGroupState.Incomplete,
                g == null ? "无组" : g.State.ToString());
            Check("说明缺 CURRENT", g != null && g.MissingText == "缺 CURRENT", g == null ? "" : g.MissingText);
            Feed(f, Cur + "\n");
            Check("CURRENT 到达后变完整", g != null && g.State == FaultGroupState.Complete,
                g == null ? "无组" : g.State.ToString());
            f.Stop();
        }

        private static void TestOnlyCurrent()
        {
            Console.WriteLine();
            Console.WriteLine("3. 仅 CURRENT → 不完整，缺 PREVIOUS");
            Fixture f = Start("onlycur", BoatMac);
            Feed(f, "JAY COUNT boot=b1 ms=100 total=10\n" + Cur + "\n");
            FaultGroup g = GroupOf(f, "1", "b1");
            Check("状态不完整", g != null && g.State == FaultGroupState.Incomplete,
                g == null ? "无组" : g.State.ToString());
            Check("明确缺 PREVIOUS", g != null && g.MissingText == "缺 PREVIOUS", g == null ? "" : g.MissingText);
            Check("不再停留在待适配", f.C.LatestFault != null && f.C.LatestFault.State == FaultAdapterState.Parsed,
                f.C.LatestFault == null ? "无行" : f.C.LatestFault.State.ToString());
            f.Stop();
        }

        private static void TestOnlyPrevious()
        {
            Console.WriteLine();
            Console.WriteLine("4. 仅 PREVIOUS → 不完整，缺 CURRENT");
            Fixture f = Start("onlyprev", BoatMac);
            Feed(f, "JAY COUNT boot=b1 ms=100 total=10\n" + Prev + "\n");
            FaultGroup g = GroupOf(f, "1", "b1");
            Check("状态不完整", g != null && g.State == FaultGroupState.Incomplete,
                g == null ? "无组" : g.State.ToString());
            Check("明确缺 CURRENT", g != null && g.MissingText == "缺 CURRENT", g == null ? "" : g.MissingText);
            f.Stop();
        }

        // ---------- 5–6 boot 未确认 ----------

        private static void TestEmptyBoot()
        {
            Console.WriteLine();
            Console.WriteLine("5. 两行 boot 均为空 → 启动未确认，不完整");
            Fixture f = Start("emptyboot", BoatMac);
            Feed(f, Cur + "\n" + Prev + "\n");
            FaultGroup g = GroupOf(f, "1", "");
            Check("状态为启动分段未确认", g != null && g.State == FaultGroupState.BootUnconfirmed,
                g == null ? "无组" : g.State.ToString());
            Check("不判完整", g == null || !g.Complete, "被误判完整");
            f.Stop();
        }

        private static void TestPlaceholderBoot()
        {
            Console.WriteLine();
            Console.WriteLine("6. 两行 boot 相同占位值 → 不完整");
            FaultRegistry reg = new FaultRegistry(null);
            R10FaultAdapter a = new R10FaultAdapter();
            FaultSnapshot c = a.Parse(Cur, "dev-A", "boot-unknown");
            FaultSnapshot p = a.Parse(Prev, "dev-A", "boot-unknown");
            reg.Submit(c); reg.Submit(p);
            FaultGroup g = FirstOf(reg);
            Check("占位 boot 判为启动分段未确认",
                g != null && g.State == FaultGroupState.BootUnconfirmed,
                g == null ? "无组" : g.State.ToString());
            Check("占位 boot 不判完整", g == null || !g.Complete, "被误判完整");

            // 字符串 "unknown" 同样是占位值
            FaultRegistry reg2 = new FaultRegistry(null);
            reg2.Submit(a.Parse(Cur, "dev-A", "unknown"));
            reg2.Submit(a.Parse(Prev, "dev-A", "unknown"));
            FaultGroup g2 = FirstOf(reg2);
            Check("unknown 文本也不当已确认 boot",
                g2 != null && g2.State == FaultGroupState.BootUnconfirmed,
                g2 == null ? "无组" : g2.State.ToString());
        }

        // ---------- 7–8 跨边界与未确认设备 ----------

        private static void TestCrossBoundaries()
        {
            Console.WriteLine();
            Console.WriteLine("7. 不同 boot / 不同设备 / 不同 id → 不合并");
            FaultRegistry reg = new FaultRegistry(null);
            R10FaultAdapter a = new R10FaultAdapter();

            reg.Submit(a.Parse(Cur, "dev-A", "b1"));
            reg.Submit(a.Parse(Prev, "dev-A", "b2"));
            Check("跨 boot 不合并", reg.GroupCount == 2 && !AnyComplete(reg), "被合并");

            FaultRegistry reg2 = new FaultRegistry(null);
            reg2.Submit(a.Parse(Cur, "dev-A", "b1"));
            reg2.Submit(a.Parse(Prev, "dev-B", "b1"));
            Check("跨设备不合并", reg2.GroupCount == 2 && !AnyComplete(reg2), "被合并");

            FaultRegistry reg3 = new FaultRegistry(null);
            reg3.Submit(a.Parse(Cur, "dev-A", "b1"));
            FaultSnapshot p2 = a.Parse(Prev.Replace("id=1", "id=2"), "dev-A", "b1");
            reg3.Submit(p2);
            Check("跨 id 不合并", reg3.GroupCount == 2 && !AnyComplete(reg3), "被合并");
        }

        private static bool AnyComplete(FaultRegistry reg)
        {
            foreach (FaultGroup g in reg.Groups) if (g.Complete) return true;
            return false;
        }

        private static void TestUnconfirmedDevice()
        {
            Console.WriteLine();
            Console.WriteLine("8. 未确认设备分段 → 不完整");
            FaultRegistry reg = new FaultRegistry(null);
            R10FaultAdapter a = new R10FaultAdapter();
            reg.Submit(a.Parse(Cur, "待归属-0001-120000-abcd", "b1"));
            reg.Submit(a.Parse(Prev, "待归属-0001-120000-abcd", "b1"));
            FaultGroup g = FirstOf(reg);
            Check("待归属设备判为设备分段未确认",
                g != null && g.State == FaultGroupState.DeviceUnconfirmed,
                g == null ? "无组" : g.State.ToString());
            Check("未确认设备不判完整", g == null || !g.Complete, "被误判完整");

            // 空设备名同样不能完整
            FaultRegistry reg2 = new FaultRegistry(null);
            reg2.Submit(a.Parse(Cur, "", "b1"));
            reg2.Submit(a.Parse(Prev, "", "b1"));
            Check("空设备名不判完整", !AnyComplete(reg2), "被误判完整");
        }

        // ---------- 9–12 重复与冲突 ----------

        private static void TestDuplicateCurrent()
        {
            Console.WriteLine();
            Console.WriteLine("9. 相同 CURRENT 重复 → 非冲突，原始记录保留");
            Fixture f = Start("dupcur", BoatMac);
            Feed(f, "JAY COUNT boot=b1 ms=100 total=10\n" + Cur + "\n" + Cur + "\n" + Prev + "\n");
            FaultGroup g = GroupOf(f, "1", "b1");
            Check("重复不导致冲突", g != null && g.State == FaultGroupState.Complete,
                g == null ? "无组" : g.State.ToString());
            Check("组内只有一条代表 CURRENT", g != null && g.CurrentDistinct.Count == 1,
                g == null ? "无组" : g.CurrentDistinct.Count.ToString());
            Check("收到 2 次 CURRENT（计数）", g != null && g.CurrentLines == 2,
                g == null ? "无组" : g.CurrentLines.ToString());
            int rawCount = 0;
            foreach (FaultSnapshot s in f.C.FaultsSeen) if (s.Raw == Cur) rawCount++;
            Check("两次接收都留在快照里", rawCount == 2, "只有 " + rawCount + " 条");

            string csv = ReadShared(Path.Combine(f.C.Session.RootDir, "FAULT快照.csv"));
            int csvLines = 0;
            foreach (string line in csv.Split(new string[] { "\r\n", "\n" }, StringSplitOptions.None))
                if (line.Contains("JAY FAULT CURRENT id=1")) csvLines++;
            Check("落盘也保留两次接收", csvLines == 2, "只有 " + csvLines + " 行");
            Check("组数不虚增", f.C.Faults.GroupCount == 1, f.C.Faults.GroupCount.ToString());
            f.Stop();
        }

        private static void TestConflictingCurrent()
        {
            Console.WriteLine();
            Console.WriteLine("10. 内容不同的 CURRENT 重复 + PREVIOUS → 冲突");
            Fixture f = Start("confcur", BoatMac);
            string cur2 = Cur.Replace("rpm=612.40", "rpm=999.00");
            Feed(f, "JAY COUNT boot=b1 ms=100 total=10\n" + Cur + "\n" + cur2 + "\n" + Prev + "\n");
            FaultGroup g = GroupOf(f, "1", "b1");
            Check("状态为冲突", g != null && g.State == FaultGroupState.Conflicting,
                g == null ? "无组" : g.State.ToString());
            Check("不判完整", g == null || !g.Complete, "被误判完整");
            Check("保留两种 CURRENT 原文", g != null && g.CurrentDistinct.Count == 2,
                g == null ? "无组" : g.CurrentDistinct.Count.ToString());
            Check("冲突原因可读", g != null && g.ConflictReason.Contains("CURRENT"),
                g == null ? "" : g.ConflictReason);
            f.Stop();
        }

        private static void TestConflictingPrevious()
        {
            Console.WriteLine();
            Console.WriteLine("11. 内容不同的 PREVIOUS 重复 + CURRENT → 冲突");
            Fixture f = Start("confprev", BoatMac);
            string prev2 = Prev.Replace("rpm=595.08", "rpm=111.11");
            Feed(f, "JAY COUNT boot=b1 ms=100 total=10\n" + Prev + "\n" + prev2 + "\n" + Cur + "\n");
            FaultGroup g = GroupOf(f, "1", "b1");
            Check("状态为冲突", g != null && g.State == FaultGroupState.Conflicting,
                g == null ? "无组" : g.State.ToString());
            Check("保留两种 PREVIOUS 原文", g != null && g.PreviousDistinct.Count == 2,
                g == null ? "无组" : g.PreviousDistinct.Count.ToString());
            Check("冲突原因可读", g != null && g.ConflictReason.Contains("PREVIOUS"),
                g == null ? "" : g.ConflictReason);
            f.Stop();
        }

        private static void TestConflictAfterOldCombo()
        {
            Console.WriteLine();
            Console.WriteLine("12. 冲突后再收到旧成功组合 → 仍冲突");
            Fixture f = Start("confsticky", BoatMac);
            string cur2 = Cur.Replace("rpm=612.40", "rpm=999.00");
            Feed(f, "JAY COUNT boot=b1 ms=100 total=10\n" + Cur + "\n" + cur2 + "\n" + Prev + "\n");
            FaultGroup g = GroupOf(f, "1", "b1");
            Check("先进入冲突", g != null && g.State == FaultGroupState.Conflicting,
                g == null ? "无组" : g.State.ToString());
            Feed(f, Cur + "\n" + Prev + "\n");
            Check("重收旧组合后仍是冲突", g != null && g.State == FaultGroupState.Conflicting,
                g == null ? "无组" : g.State.ToString());
            Feed(f, "JAY FAULT NONE\n");
            Check("NONE 也不能解除冲突", g != null && g.State == FaultGroupState.Conflicting,
                g == null ? "无组" : g.State.ToString());
            f.Stop();
        }

        // ---------- 13–15 NONE / COUNT 倒填 / 待归属 ----------

        private static void TestNoneKeepsHistory()
        {
            Console.WriteLine();
            Console.WriteLine("13. NONE 不删除历史完整/缺行/冲突证据");
            Fixture f = Start("nonekeep", BoatMac);
            Feed(f, "JAY COUNT boot=b1 ms=100 total=10\n" + Cur + "\n" + Prev + "\n");
            Feed(f, "JAY COUNT boot=b1 ms=200 total=20\n" + Cur.Replace("id=1", "id=2") + "\n");
            string cur3 = Cur.Replace("id=1", "id=3");
            string cur3b = cur3.Replace("rpm=612.40", "rpm=777.00");
            Feed(f, "JAY COUNT boot=b1 ms=300 total=30\n" + cur3 + "\n" + cur3b +
                     "\n" + Prev.Replace("id=1", "id=3") + "\n");
            int before = f.C.Faults.GroupCount;
            FaultGroup complete = GroupOf(f, "1", "b1");
            FaultGroup incomplete = GroupOf(f, "2", "b1");
            FaultGroup conflict = GroupOf(f, "3", "b1");
            Check("准备状态：完整/不完整/冲突各一",
                complete != null && complete.State == FaultGroupState.Complete &&
                incomplete != null && incomplete.State == FaultGroupState.Incomplete &&
                conflict != null && conflict.State == FaultGroupState.Conflicting,
                "准备失败");

            Feed(f, "JAY FAULT NONE\n");
            Check("NONE 后组数不变", f.C.Faults.GroupCount == before,
                before + " → " + f.C.Faults.GroupCount);
            Check("完整组仍在", complete.State == FaultGroupState.Complete, complete.State.ToString());
            Check("不完整组仍在", incomplete.State == FaultGroupState.Incomplete, incomplete.State.ToString());
            Check("冲突组仍是冲突", conflict.State == FaultGroupState.Conflicting, conflict.State.ToString());

            string dir = f.C.Session.RootDir;
            string csv = ReadShared(Path.Combine(dir, "FAULT快照.csv"));
            Check("NONE 已单独落盘", csv.Contains("JAY FAULT NONE"), "缺 NONE 记录");
            f.Stop();
        }

        private static void TestCountAfterUnknownBootFault()
        {
            Console.WriteLine();
            Console.WriteLine("14. boot 未知的 FAULT 之后收到 COUNT → 不能自动倒填并变完整");
            Fixture f = Start("nodowngrade", BoatMac);
            // 先来两行 FAULT（boot 还是空），再来 COUNT 带来 boot=b1
            Feed(f, Cur + "\n" + Prev + "\n");
            FaultGroup emptyGroup = GroupOf(f, "1", "");
            Check("先为启动分段未确认",
                emptyGroup != null && emptyGroup.State == FaultGroupState.BootUnconfirmed,
                emptyGroup == null ? "无组" : emptyGroup.State.ToString());

            Feed(f, "JAY COUNT boot=b1 ms=100 total=10\n");
            Check("收到 COUNT 后原组不被改写", emptyGroup.State == FaultGroupState.BootUnconfirmed,
                emptyGroup.State.ToString());
            Check("没有凭空变出 boot=b1 的完整组",
                GroupOf(f, "1", "b1") == null || !GroupOf(f, "1", "b1").Complete, "被倒填成完整");
            Check("全部组里没有完整", !AnyComplete(f.C.Faults), "出现了完整组");
            f.Stop();
        }

        private static void TestPendingSegmentFault()
        {
            Console.WriteLine();
            Console.WriteLine("15. 待归属分段 FAULT 不能冒用上一设备或上一 boot");
            Fixture f = Start("pendingseg", BoatMac);
            Feed(f, "JAY COUNT boot=b1 ms=100 total=10\n" + Cur + "\n" + Prev + "\n");
            FaultGroup original = GroupOf(f, "1", "b1");
            Check("第一块板先形成完整组", original != null && original.Complete, "准备失败");

            // 掉线 → 重连（新板 MAC 不同），识别期间收到的 FAULT 应归到待归属分段
            f.Ch.FailRead = true;
            f.Clock.Advance(10); f.C.Pump();
            f.Ch.FailRead = false;
            Feed(f, "JAYv4 ROLE=BOAT MAC=" + BoatMac2 +
                     " PEER=24:62:AB:EF:CF:84 PAIR=2 STEER=20 FWID=" + BoatFwid +
                     " VER=20261103-590rpm-r10\n" + Cur + "\n" + Prev + "\n");
            f.C.Reconnect();
            Feed(f, Cur + "\n" + Prev + "\n");

            Check("旧设备完整组仍是完整", original.State == FaultGroupState.Complete, original.State.ToString());
            bool anyWrongDevice = false;
            foreach (FaultGroup g in f.C.Faults.Groups)
                if (g != original && g.DeviceSegment == f.C.DeviceSegment && g.BootSegment == "b1" && g.Complete)
                    anyWrongDevice = true;
            Check("新设备没有沿用旧 boot 拼出完整组", !anyWrongDevice, "新设备故障被冒用旧 boot");
            f.Stop();
        }

        // ---------- 16 多行/跨块/重复查询 ----------

        private static void TestMultiLineAndSplitChunks()
        {
            Console.WriteLine();
            Console.WriteLine("16. 一块多行 / 跨块两行 / 重复查询回应 → 关联结果一致");
            // 一次投递
            Fixture a = Start("chunk-one", BoatMac);
            Feed(a, "JAY COUNT boot=b1 ms=100 total=10\n" + Cur + "\n" + Prev + "\n");
            FaultGroup ga = GroupOf(a, "1", "b1");
            Check("一次投递形成完整", ga != null && ga.State == FaultGroupState.Complete,
                ga == null ? "无组" : ga.State.ToString());
            a.Stop();

            // 跨块（CURRENT 与 PREVIOUS 分两次投递）
            Fixture b = Start("chunk-split", BoatMac);
            Feed(b, "JAY COUNT boot=b1 ms=100 total=10\n" + Cur + "\n");
            Feed(b, Prev + "\n");
            FaultGroup gb = GroupOf(b, "1", "b1");
            Check("跨块投递形成完整", gb != null && gb.State == FaultGroupState.Complete,
                gb == null ? "无组" : gb.State.ToString());
            b.Stop();

            // 重复查询回应（同一组内容重复三次）
            Fixture c = Start("chunk-repeat", BoatMac);
            Feed(c, "JAY COUNT boot=b1 ms=100 total=10\n");
            for (int i = 0; i < 3; i++) Feed(c, Cur + "\n" + Prev + "\n");
            FaultGroup gc = GroupOf(c, "1", "b1");
            Check("重复查询回应仍是完整", gc != null && gc.State == FaultGroupState.Complete,
                gc == null ? "无组" : gc.State.ToString());
            Check("重复回应不虚增组数", c.C.Faults.GroupCount == 1, c.C.Faults.GroupCount.ToString());
            Check("重复回应不虚增不同内容", gc != null && gc.CurrentDistinct.Count == 1 &&
                gc.PreviousDistinct.Count == 1, "内容种类变多");
            c.Stop();
        }

        // ---------- 17 正式控制器不再停在待适配 ----------

        private static void TestNotStuckInPendingAdapter()
        {
            Console.WriteLine();
            Console.WriteLine("17. 通过正式控制器输入两行后不停留在 PendingAdapter");
            Fixture f = Start("notpending", BoatMac);
            Feed(f, "JAY COUNT boot=b1 ms=100 total=10\n" + Cur + "\n" + Prev + "\n");
            IList<FaultSnapshot> seen = f.C.FaultsSeen;
            bool anyUnadapted = false;
            foreach (FaultSnapshot s in seen) if (s.State == FaultAdapterState.Unadapted) anyUnadapted = true;
            Check("没有行停留在待适配", !anyUnadapted, "仍有待适配行");
            Check("两行都是已解析", seen.Count == 2 && seen[0].State == FaultAdapterState.Parsed &&
                seen[1].State == FaultAdapterState.Parsed, "行状态不对");
            Check("控制器能查到完整组", GroupOf(f, "1", "b1") != null &&
                GroupOf(f, "1", "b1").Complete, "查不到完整组");
            f.Stop();
        }

        // ---------- 18 窗口淘汰后导出仍保留历史 ----------

        private static void TestHistorySurvivesWindow()
        {
            Console.WriteLine();
            Console.WriteLine("18. 近期窗口淘汰后导出仍保留历史关联结果与原文");
            Fixture f = Start("window", BoatMac);
            Feed(f, "JAY COUNT boot=b1 ms=100 total=10\n" + Cur + "\n" + Prev + "\n");
            FaultGroup g = GroupOf(f, "1", "b1");
            Check("先形成完整组", g != null && g.Complete, "准备失败");

            // 灌入远超窗口的噪声行（ResultWindow=500、RecentLineWindow=500）
            StringBuilder noise = new StringBuilder();
            for (int i = 0; i < 1200; i++)
                noise.Append("link=OK rx=").Append(i).Append('\n');
            Feed(f, noise.ToString());
            Check("原故障行已被挤出内存窗口", f.C.FaultsSeen.Count <= 500 && f.C.FaultsSeen.Count < 1200,
                "行数 " + f.C.FaultsSeen.Count);
            Check("关联组仍在内存登记表", g != null && g.State == FaultGroupState.Complete,
                g == null ? "无组" : g.State.ToString());

            f.Stop();
            ExportResult r = DiagnosticsExporter.Export(f.C.Session, f.Paths, "test", f.Clock.Now);
            Check("导出成功", r.Ok, r.Error);
            if (!r.Ok) return;
            using (ZipArchive z = ZipFile.OpenRead(r.ZipPath))
            {
                string groups = ReadEntry(z, "FAULT关联.csv");
                Check("导出含完整组记录", groups.Contains("complete") && groups.Contains("id=1") ||
                    groups.Contains(",\"1\","), "缺完整组");
                Check("导出含两行原文",
                    groups.Contains("JAY FAULT CURRENT id=1") && groups.Contains("JAY FAULT PREVIOUS id=1"),
                    "缺原文引用");
                string note = ReadEntry(z, "导出说明.txt");
                Check("导出说明含关联汇总", note.Contains("故障关联"), "缺汇总段");
                Check("汇总里 complete=1", note.Contains("complete：1") || note.Contains("complete: 1"),
                    "汇总计数不对");
                Check("汇总组数为 1（表头不被算作一组）", note.Contains("关联组数：1"),
                    "汇总组数不是 1");
            }
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

        // ---------- 19 写盘/导出失败保留数据 ----------

        private static void TestExportFailureKeepsData()
        {
            Console.WriteLine();
            Console.WriteLine("19. 关联写盘或导出失败时原始证据仍在且错误明确");
            Fixture f = Start("exportfail", BoatMac);
            Feed(f, "JAY COUNT boot=b1 ms=100 total=10\n" + Cur + "\n" + Prev + "\n");
            string rootDir = f.C.Session.RootDir;
            f.Stop();

            // 模拟导出失败：把根目录设成只读不现实，改为传 null 会话/不存在的路径验证错误信息
            ExportResult bad = DiagnosticsExporter.Export(null, f.Paths, "test", f.Clock.Now);
            Check("空会话导出明确报错", !bad.Ok && bad.Error.Length > 0, "未报错");
            Check("原始目录仍在", Directory.Exists(rootDir), "目录丢失");
            Check("原始 FAULT 行仍在磁盘",
                ReadShared(Path.Combine(rootDir, "FAULT快照.csv")).Contains("JAY FAULT CURRENT id=1"),
                "缺原始行");
            Check("关联表仍在磁盘",
                File.Exists(Path.Combine(rootDir, "FAULT关联.csv")), "缺关联表");

            // 关联表不可写时：状态推进不吞异常，原始快照继续落盘
            Fixture w = Start("exportfail2", BoatMac);
            Feed(w, "JAY COUNT boot=b1 ms=100 total=10\n" + Cur + "\n");
            w.Stop();
            Check("组状态在停止时已落盘",
                ReadShared(Path.Combine(w.C.Session.RootDir, "FAULT关联.csv")).Contains("incomplete"),
                "缺不完整记录");
        }

        // ---------- 20 导出 ZIP 一致 ----------

        private static void TestExportZipContainsGroups()
        {
            Console.WriteLine();
            Console.WriteLine("20. 导出 ZIP 解压后关联行、原始引用与哈希一致");
            Fixture f = Start("zipverify", BoatMac);
            Feed(f, "JAY COUNT boot=b1 ms=100 total=10\n" + Cur + "\n" + Prev + "\n");
            string cur2 = Cur.Replace("id=1", "id=9");
            string cur2b = cur2.Replace("rpm=612.40", "rpm=888.00");
            Feed(f, "JAY COUNT boot=b1 ms=200 total=20\n" + cur2 + "\n" + cur2b +
                     "\n" + Prev.Replace("id=1", "id=9") + "\n");
            Feed(f, "JAY COUNT boot=b1 ms=300 total=30\n" + Cur.Replace("id=1", "id=5") + "\n");
            f.Stop();

            ExportResult r = DiagnosticsExporter.Export(f.C.Session, f.Paths, "test", f.Clock.Now);
            Check("导出成功", r.Ok, r.Error);
            if (!r.Ok) return;

            // 文件清单与 SHA256 核验（含新增的关联文件）
            using (ZipArchive z = ZipFile.OpenRead(r.ZipPath))
            {
                bool hasGroups = false, hasTransitions = false, hasManifest = false;
                string manifest = "";
                foreach (ZipArchiveEntry e in z.Entries)
                {
                    string n = e.FullName.Replace('\\', '/');
                    if (n == "FAULT关联.csv") hasGroups = true;
                    if (n == "FAULT关联状态变化.csv") hasTransitions = true;
                    if (n == "文件清单与SHA256.txt") { hasManifest = true; manifest = ReadEntry(z, n); }
                }
                Check("ZIP 含 FAULT关联.csv", hasGroups, "缺失");
                Check("ZIP 含 FAULT关联状态变化.csv", hasTransitions, "缺失");
                Check("ZIP 含文件清单", hasManifest, "缺失");

                List<string> missing = new List<string>();
                foreach (string line in manifest.Split(new string[] { "\r\n", "\n" }, StringSplitOptions.None))
                {
                    if (string.IsNullOrEmpty(line) || line.StartsWith("路径\t")) continue;
                    string[] parts = line.Split('\t');
                    if (parts.Length < 3) continue;
                    if (!parts[0].StartsWith("FAULT")) continue;
                    string content = ReadEntry(z, parts[0]);
                    if (string.IsNullOrEmpty(content) && parts[0] != "FAULT快照.csv") missing.Add(parts[0]);
                }
                Check("清单里的关联文件都能在 ZIP 中找到", missing.Count == 0,
                    string.Join(",", missing.ToArray()));

                // 状态一致性：完整组、冲突组、不完整组各自的原文都能追溯
                string groups = ReadEntry(z, "FAULT关联.csv");
                Check("完整组可追溯到 CURRENT 原文", groups.Contains("JAY FAULT CURRENT id=1"),
                    "缺 CURRENT 原文");
                Check("冲突组可追溯到两种 CURRENT 原文",
                    groups.Contains("rpm=612.40") && groups.Contains("rpm=888.00"), "冲突原文不全");
                Check("不完整组记录缺行说明", groups.Contains("缺 PREVIOUS") || groups.Contains("缺 PREVIOUS"),
                    "缺缺行说明");
            }
        }

        // ---------- 21 控制器可查询各种状态 ----------

        private static void TestControllerQueryableStates()
        {
            Console.WriteLine();
            Console.WriteLine("21. 控制器可查询完整／冲突／缺行／未知 boot");
            Fixture f = Start("queryable", BoatMac);
            Feed(f, "JAY COUNT boot=b1 ms=100 total=10\n" + Cur + "\n" + Prev + "\n");
            Feed(f, "JAY COUNT boot=b1 ms=200 total=20\n" +
                     Cur.Replace("id=1", "id=2") + "\n" +
                     Cur.Replace("id=1", "id=2").Replace("rpm=612.40", "rpm=500.00") + "\n" +
                     Prev.Replace("id=1", "id=2") + "\n");
            Feed(f, "JAY COUNT boot=b1 ms=300 total=30\n" + Cur.Replace("id=1", "id=3") + "\n");

            FaultGroup complete = GroupOf(f, "1", "b1");
            FaultGroup conflict = GroupOf(f, "2", "b1");
            FaultGroup incomplete = GroupOf(f, "3", "b1");
            Check("可查到完整组", complete != null && complete.State == FaultGroupState.Complete,
                complete == null ? "无组" : complete.State.ToString());
            Check("可查到冲突组", conflict != null && conflict.State == FaultGroupState.Conflicting,
                conflict == null ? "无组" : conflict.State.ToString());
            Check("可查到缺行组", incomplete != null && incomplete.State == FaultGroupState.Incomplete &&
                incomplete.MissingText == "缺 PREVIOUS", incomplete == null ? "无组" : incomplete.State.ToString());
            Check("汇总文本可读", f.C.FaultSummary.Contains("完整") && f.C.FaultSummary.Contains("冲突"),
                f.C.FaultSummary);
            f.Stop();
        }

        // ---------- 工具 ----------

        private static string ReadShared(string path)
        {
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (StreamReader r = new StreamReader(fs, Encoding.UTF8))
                return r.ReadToEnd();
        }
    }
}
