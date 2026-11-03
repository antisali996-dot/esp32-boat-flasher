using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace BoatTool
{
    // r10 正式接口适配测试（基于获批快照的实际格式）。
    public static class R10InterfaceTests
    {
        private static int passed;
        private static int failed;
        private static string root;

        private const string BoatFwid10 = "JAYFWB10-S20-A70-110-U1278-1500-1722-R590-OS600-Cr10-diagshared";
        private const string RemoteFwid10 = "JAYFWR10-S20-A70-110-U1278-1500-1722-D1750-2150";
        private const string Mac = "24:62:AB:F0:72:94";

        public static void Run()
        {
            passed = 0; failed = 0;
            root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test-work",
                "r10-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            Console.WriteLine();
            Console.WriteLine("===== r10 正式接口适配 =====");
            try
            {
                TestCountFormat();
                TestFaultNoneAndTwoLines();
                TestFaultPairingRules();
                TestWhitelistByRole();
                TestUnsupportedDisablesQuery();
                TestRemotePassiveOnly();
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
            Console.WriteLine();
            Console.WriteLine("r10 接口：通过 " + passed + " 项，失败 " + failed + " 项");
            if (failed > 0) throw new InvalidOperationException("r10 接口测试失败 " + failed + " 项");
        }

        private static void Check(string name, bool ok, string detail)
        {
            if (ok) { passed++; Console.WriteLine("  [通过] " + name); }
            else { failed++; Console.WriteLine("  [失败] " + name + " —— " + detail); }
        }

        private static void TestCountFormat()
        {
            Console.WriteLine();
            Console.WriteLine("1. r10 COUNT 格式");
            CountSnapshot c = CountParser.Parse("JAY COUNT boot=0a1a2a3a ms=123456 total=8589934591");
            Check("解析成功", c.Parsed, c.ParseError);
            Check("UInt64 精确", c.Total == 8589934591UL, c.Total.ToString());
            Check("保留原始十进制", c.TotalRaw == "8589934591", c.TotalRaw);
            Check("boot 原样", c.BootRaw == "0a1a2a3a", c.BootRaw);
            Check("板上 ms 独立", c.Ms == 123456, c.Ms.ToString());

            CountSnapshot max = CountParser.Parse("JAY COUNT boot=b ms=1 total=18446744073709551615");
            Check("UInt64 最大值", max.Parsed && max.Total == ulong.MaxValue, max.ParseError);
        }

        private static void TestFaultNoneAndTwoLines()
        {
            Console.WriteLine();
            Console.WriteLine("2. r10 FAULT NONE 与两行");
            IFaultAdapter a = new R10FaultAdapter();
            Check("格式已确认", a.FormatConfirmed, "未确认");

            FaultSnapshot none = a.Parse("JAY FAULT NONE", "seg", "boot1");
            Check("NONE 识别", none.State == FaultAdapterState.NoneReported, none.State.ToString());

            string cur = "JAY FAULT CURRENT id=1 name=OVERSPEED ms=2850 rpm=612.40 pwm=1 pulses=74 elapsed=50 integral=0.10";
            FaultSnapshot c = a.Parse(cur, "seg", "boot1");
            Check("CURRENT 解析 id", c.Kind == "CURRENT" && c.Id == "1", c.Kind + "/" + c.Id);
            Check("CURRENT 保留 name", c.Name == "OVERSPEED", c.Name);
            Check("CURRENT 原文保留", c.Raw == cur, "原文被改");

            string prev = "JAY FAULT PREVIOUS id=1 ms=2800 rpm=595.08 pwm=1 pulses=70 elapsed=50 integral=0.00";
            FaultSnapshot p = a.Parse(prev, "seg", "boot1");
            Check("PREVIOUS 解析 id", p.Kind == "PREVIOUS" && p.Id == "1", p.Kind + "/" + p.Id);

            // 全零 PREVIOUS 保留原值，不猜根因
            FaultSnapshot zero = a.Parse(
                "JAY FAULT PREVIOUS id=2 ms=0 rpm=0.00 pwm=0 pulses=0 elapsed=0 integral=0.00", "seg", "boot1");
            Check("全零 PREVIOUS 保留原文", zero.Raw.Contains("rpm=0.00") && zero.Id == "2", zero.Raw);
        }

        private static void TestFaultPairingRules()
        {
            Console.WriteLine();
            Console.WriteLine("3. FAULT 配对规则");
            List<FaultSnapshot> list = new List<FaultSnapshot>();
            list.Add(new FaultSnapshot { Kind = "CURRENT", Id = "1", DeviceSegment = "d1", BootSegment = "b1",
                Name = "OVERSPEED" });
            list.Add(new FaultSnapshot { Kind = "PREVIOUS", Id = "1", DeviceSegment = "d1", BootSegment = "b1" });
            Dictionary<string, FaultPairing.Pair> pairs = FaultPairing.Build(list);
            Check("同 id 同 boot 配成完整", pairs.Count == 1 && IsComplete(pairs), "未配对");

            // 只有一行 → 不完整
            List<FaultSnapshot> one = new List<FaultSnapshot>();
            one.Add(new FaultSnapshot { Kind = "CURRENT", Id = "1", DeviceSegment = "d1", BootSegment = "b1" });
            Dictionary<string, FaultPairing.Pair> p1 = FaultPairing.Build(one);
            Check("只有一行标不完整", p1.Count == 1 && !IsComplete(p1), "被当成完整");

            // 不同 id 不拼
            List<FaultSnapshot> diff = new List<FaultSnapshot>();
            diff.Add(new FaultSnapshot { Kind = "CURRENT", Id = "1", DeviceSegment = "d1", BootSegment = "b1" });
            diff.Add(new FaultSnapshot { Kind = "PREVIOUS", Id = "2", DeviceSegment = "d1", BootSegment = "b1" });
            Dictionary<string, FaultPairing.Pair> pd = FaultPairing.Build(diff);
            Check("不同 id 不拼成完整", pd.Count == 2 && !IsComplete(pd), "错误拼接");

            // 同 id 不同 boot 不拼
            List<FaultSnapshot> diffBoot = new List<FaultSnapshot>();
            diffBoot.Add(new FaultSnapshot { Kind = "CURRENT", Id = "1", DeviceSegment = "d1", BootSegment = "b1" });
            diffBoot.Add(new FaultSnapshot { Kind = "PREVIOUS", Id = "1", DeviceSegment = "d1", BootSegment = "b2" });
            Dictionary<string, FaultPairing.Pair> pb = FaultPairing.Build(diffBoot);
            Check("跨启动不拼成完整", pb.Count == 2 && !IsComplete(pb), "跨 boot 被拼");

            // 同 id 不同设备不拼
            List<FaultSnapshot> diffDev = new List<FaultSnapshot>();
            diffDev.Add(new FaultSnapshot { Kind = "CURRENT", Id = "1", DeviceSegment = "d1", BootSegment = "b1" });
            diffDev.Add(new FaultSnapshot { Kind = "PREVIOUS", Id = "1", DeviceSegment = "d2", BootSegment = "b1" });
            Dictionary<string, FaultPairing.Pair> pdd = FaultPairing.Build(diffDev);
            Check("跨设备不拼成完整", pdd.Count == 2 && !IsComplete(pdd), "跨设备被拼");

            // 无 boot 证据：不再合成 "boot-unknown" 键去拼完整，
            // 而是如实留空并让该组状态固定为“启动分段未确认”。
            FaultSnapshot nb = new FaultSnapshot { Kind = "CURRENT", DeviceSegment = "d1", BootSegment = "", Id = "3" };
            Check("无 boot 证据不合成占位键", FaultPairing.Key(nb) == "d1||3", FaultPairing.Key(nb));
            FaultRegistry reg = new FaultRegistry(null);
            R10FaultAdapter ra = new R10FaultAdapter();
            reg.Submit(ra.Parse("JAY FAULT CURRENT id=3 name=X ms=1 rpm=1.00 pwm=1 pulses=1 elapsed=1 integral=1.00", "d1", ""));
            reg.Submit(ra.Parse("JAY FAULT PREVIOUS id=3 ms=1 rpm=1.00 pwm=1 pulses=1 elapsed=1 integral=1.00", "d1", ""));
            FaultGroup ng = reg.Groups.Count == 0 ? null : reg.Groups[0];
            Check("无 boot 的两行判启动分段未确认",
                ng != null && ng.State == FaultGroupState.BootUnconfirmed,
                ng == null ? "无组" : ng.State.ToString());
        }

        private static bool IsComplete(Dictionary<string, FaultPairing.Pair> pairs)
        {
            foreach (KeyValuePair<string, FaultPairing.Pair> kv in pairs)
                if (kv.Value.Complete) return true;
            return false;
        }

        private static void TestWhitelistByRole()
        {
            Console.WriteLine();
            Console.WriteLine("4. 白名单按角色");
            QueryPolicy p = QueryPolicy.ApprovedR10();
            Check("小船 r10 允许", p.Allows(BoatFwid10, BoardRole.Boat), "小船被拒");
            Check("遥控器 r10 不允许 COUNT/FAULT", !p.Allows(RemoteFwid10, BoardRole.Remote), "遥控器被允许");
            Check("旧 r9 固件不允许", !p.Allows("JAYFWB9-S20-A70-110-U1278-1500-1722-R590-OS600-Cr9-window-enum",
                BoardRole.Boat), "旧固件被允许");
            Check("未知 FWID 不允许", !p.Allows("JAYFWB99-unknown", BoardRole.Boat), "未知被允许");
            Check("两端头文件相同不自动启用遥控器", p.RemoteFwids.Count == 0, "遥控器白名单非空");
        }

        private static void TestUnsupportedDisablesQuery()
        {
            Console.WriteLine();
            Console.WriteLine("5. UNSUPPORTED_COMMAND 停用查询");
            FakeDiagChannel ch = new FakeDiagChannel();
            FakeDiagClock clock = new FakeDiagClock();
            AppPaths paths = MakePaths();
            DiagnosticsController c = new DiagnosticsController(paths, ch, clock, QueryPolicy.ApprovedR10(), null);
            c.PortLocks = new PortLockRegistry();
            c.SetPort("COM_SIM");
            ch.Deliver("JAYv4 ROLE=BOAT MAC=" + Mac + " PEER=00:00:00:00:00:00 PAIR=2 STEER=20 FWID=" +
                       BoatFwid10 + " VER=20261103-590rpm-r10\n");
            c.ConnectAndIdentify();
            c.StartRecording();

            clock.Advance(5100); c.Pump();
            Check("先发出 COUNT", ch.CommandCount("JAY COUNT") == 1, "未发");
            ch.Deliver("JAY ERROR=UNSUPPORTED_COMMAND\n");
            clock.Advance(100); c.Pump();
            Check("收到 UNSUPPORTED 后停用", c.CountQueryDisabled, "未停用");
            int before = ch.CommandCount("JAY COUNT");
            for (int i = 0; i < 50; i++) { clock.Advance(1000); c.Pump(); }
            Check("不再重复发送 COUNT", ch.CommandCount("JAY COUNT") == before,
                "又发了 " + (ch.CommandCount("JAY COUNT") - before) + " 次");
            c.StopRecording();
        }

        private static void TestRemotePassiveOnly()
        {
            Console.WriteLine();
            Console.WriteLine("6. 遥控器只被动记录");
            FakeDiagChannel ch = new FakeDiagChannel();
            FakeDiagClock clock = new FakeDiagClock();
            DiagnosticsController c = new DiagnosticsController(MakePaths(), ch, clock,
                QueryPolicy.ApprovedR10(), null);
            c.PortLocks = new PortLockRegistry();
            c.SetPort("COM_SIM");
            ch.Deliver("JAYv4 ROLE=REMOTE MAC=24:62:AB:EF:CF:84 PEER=00:00:00:00:00:00 PAIR=2 STEER=20 FWID=" +
                       RemoteFwid10 + " VER=20261103-590rpm-r10\n");
            c.ConnectAndIdentify();
            c.StartRecording();
            for (int i = 0; i < 60; i++) { clock.Advance(1000); c.Pump(); }
            Check("遥控器零自动查询", ch.CommandCount("JAY COUNT") == 0 && ch.CommandCount("JAY FAULT") == 0,
                "发了 " + ch.CommandCount("JAY COUNT") + "/" + ch.CommandCount("JAY FAULT"));
            c.StopRecording();
        }

        private static AppPaths MakePaths()
        {
            AppPaths p = new AppPaths();
            p.AppRoot = root; p.WritableRoot = root;
            p.ConfigDir = Path.Combine(root, "配置");
            p.RecordsDir = Path.Combine(root, "记录");
            p.RoleStorePath = Path.Combine(p.ConfigDir, "角色记忆.json");
            return p;
        }
    }
}
