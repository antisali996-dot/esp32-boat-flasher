using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace BoatTool
{
    // 三轮复审：重连边界（数据归属/半行生命周期）+ 串口锁迁移。
    public static class BoundaryFixTests
    {
        private static int passed;
        private static int failed;
        private static string root;

        private const string MacA = "24:62:AB:F0:72:94";
        private const string MacB = "24:62:AB:F1:36:64";
        private const string Fwid = "JAYFWB7-S20-A70-110-U1278-1500-1722-R590-OS600-Cr7-pendingerr-port";

        public static void Run()
        {
            passed = 0; failed = 0;
            root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test-work",
                "boundary-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            Console.WriteLine();
            Console.WriteLine("===== 重连边界与锁迁移 =====");
            try
            {
                TestNewCountAttributedToNewDevice();
                TestNewCountNotAttributedToOld();
                TestNewTailCompletesTo123();
                TestOldHalfLineNotJoined();
                TestSameMacKeepsAllBytes();
                TestIdentityFailureKeepsBytes();
                TestSnapshotConsistency();
                TestReassembleEqualsInput();

                TestLockMigratesPort();
                TestStopReleasesAllOwnedLocks();
                TestTargetLockedByOtherRejected();
                TestOpenFailureRollsBack();
                TestSamePortReconnectNoLeak();
                TestCoreStillRejectsActivePort();
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
            Console.WriteLine();
            Console.WriteLine("边界修复：通过 " + passed + " 项，失败 " + failed + " 项");
            if (failed > 0) throw new InvalidOperationException("边界修复测试失败 " + failed + " 项");
        }

        private static void Check(string name, bool ok, string detail)
        {
            if (ok) { passed++; Console.WriteLine("  [通过] " + name); }
            else { failed++; Console.WriteLine("  [失败] " + name + " —— " + detail); }
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

        private static DiagnosticsController Make(FakeDiagChannel ch, PortLockRegistry locks, string port = "COM8")
        {
            DiagnosticsController c = new DiagnosticsController(MakePaths(), ch, new FakeDiagClock(),
                new QueryPolicy(), null);
            c.PortLocks = locks;
            c.SetPort(port);
            return c;
        }

        private static string Id(string mac)
        {
            return "JAYv4 ROLE=BOAT MAC=" + mac + " PEER=24:62:AB:EF:CF:84 PAIR=2 STEER=20 FWID=" +
                   Fwid + " VER=20261103-590rpm-r7\n";
        }

        private static void Drop(DiagnosticsController c, FakeDiagChannel ch)
        {
            ch.FailRead = true; c.Pump(); ch.FailRead = false;
        }

        private static string ReadText(string p)
        {
            using (FileStream fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (StreamReader r = new StreamReader(fs, Encoding.UTF8))
                return r.ReadToEnd();
        }

        // ---- 数据归属与半行生命周期 ----

        private static void TestNewCountAttributedToNewDevice()
        {
            Console.WriteLine();
            Console.WriteLine("1. 新设备同批 COUNT 归属新设备");
            FakeDiagChannel ch = new FakeDiagChannel();
            DiagnosticsController c = Make(ch, new PortLockRegistry());
            ch.Deliver(Id(MacA)); c.ConnectAndIdentify(); c.StartRecording();
            Drop(c, ch);
            ch.Deliver(Id(MacB) + "JAY COUNT boot=new ms=1 total=55\n");
            c.Reconnect("COM8");
            Check("COUNT 归属新设备", c.Counts.Count >= 1 && c.Counts[0].DeviceSegment == c.DeviceSegment,
                "实际段 " + (c.Counts.Count > 0 ? c.Counts[0].DeviceSegment : "无"));
            c.StopRecording();
        }

        private static void TestNewCountNotAttributedToOld()
        {
            Console.WriteLine();
            Console.WriteLine("2. 新设备 COUNT 不归属旧设备");
            FakeDiagChannel ch = new FakeDiagChannel();
            DiagnosticsController c = Make(ch, new PortLockRegistry());
            ch.Deliver(Id(MacA)); c.ConnectAndIdentify(); c.StartRecording();
            string oldSeg = c.DeviceSegment;
            Drop(c, ch);
            ch.Deliver(Id(MacB) + "JAY COUNT boot=new ms=1 total=55\n");
            c.Reconnect("COM8");
            Check("不归属旧设备", c.Counts.Count >= 1 && c.Counts[0].DeviceSegment != oldSeg,
                "仍归旧设备");
            c.StopRecording();
        }

        private static void TestNewTailCompletesTo123()
        {
            Console.WriteLine();
            Console.WriteLine("3. 新半行补齐得 123（恰好一条）");
            FakeDiagChannel ch = new FakeDiagChannel();
            DiagnosticsController c = Make(ch, new PortLockRegistry());
            ch.Deliver(Id(MacA)); c.ConnectAndIdentify(); c.StartRecording();
            Drop(c, ch);
            ch.Deliver(Id(MacB) + "JAY COUNT boot=new ms=2 total=12");
            c.Reconnect("COM8");
            int before = c.Counts.Count;
            ch.Deliver("3\n"); c.Pump();
            Check("补齐前无 12 快照", before == 0, "提前产生 " + before + " 条");
            Check("补齐后恰好一条 total=123", c.Counts.Count == 1 && c.Counts[0].Total == 123UL,
                "实际 " + c.Counts.Count + " 条，" +
                (c.Counts.Count > 0 ? c.Counts[0].Total.ToString() : ""));
            c.StopRecording();
        }

        private static void TestOldHalfLineNotJoined()
        {
            Console.WriteLine();
            Console.WriteLine("4. 旧残行不与新设备拼接");
            FakeDiagChannel ch = new FakeDiagChannel();
            DiagnosticsController c = Make(ch, new PortLockRegistry());
            ch.Deliver(Id(MacA)); c.ConnectAndIdentify(); c.StartRecording();
            string oldSeg = c.DeviceSegment;
            ch.Deliver("OLD_HALF"); c.Pump();
            Drop(c, ch);
            ch.Deliver(Id(MacB) + "NEW_LINE\n");
            c.Reconnect("COM8");
            ch.Deliver("MORE_NEW\n"); c.Pump();
            c.StopRecording();
            string oldText = ReadText(Path.Combine(c.Session.SegmentDir(oldSeg), "解码文本.log"));
            string newText = ReadText(Path.Combine(c.Session.SegmentDir(c.DeviceSegment), "解码文本.log"));
            Check("旧残行留在旧分段并标不完整", oldText.Contains("OLD_HALF") &&
                  (oldText.Contains("不完整") || oldText.Contains("未完成")), "旧残行丢失");
            Check("旧残行不与新设备拼接", !newText.Contains("OLD_HALF"), "拼接了");
            Check("新设备行独立", newText.Contains("NEW_LINE") || newText.Contains("MORE_NEW"), "新行丢失");
        }

        private static void TestSameMacKeepsAllBytes()
        {
            Console.WriteLine();
            Console.WriteLine("5. 同 MAC 重连保留全部原始字节");
            FakeDiagChannel ch = new FakeDiagChannel();
            DiagnosticsController c = Make(ch, new PortLockRegistry());
            ch.Deliver(Id(MacA)); c.ConnectAndIdentify(); c.StartRecording();
            byte[] pre = Encoding.UTF8.GetBytes("PRE_BYTES\n");
            ch.DeliverBytes(pre); c.Pump();
            Drop(c, ch);
            byte[] rc = Encoding.UTF8.GetBytes("RC_PRE\n" + Id(MacA) + "RC_POST\n");
            ch.DeliverBytes(rc);
            c.Reconnect("COM8");
            c.Pump();
            c.StopRecording();
            string all = AllRaw(c);
            Check("同 MAC 重连全部字节保留", all.Contains("PRE_BYTES") && all.Contains("RC_PRE") &&
                  all.Contains("RC_POST"), "字节缺失");
        }

        private static void TestIdentityFailureKeepsBytes()
        {
            Console.WriteLine();
            Console.WriteLine("6. 身份失败保留字节");
            FakeDiagChannel ch = new FakeDiagChannel();
            DiagnosticsController c = Make(ch, new PortLockRegistry());
            ch.Deliver(Id(MacA)); c.ConnectAndIdentify(); c.StartRecording();
            Drop(c, ch);
            ch.Deliver("NO_IDENTITY_BYTES\n");
            c.Reconnect("COM8");
            c.StopRecording();
            Check("身份失败仍保留字节", AllRaw(c).Contains("NO_IDENTITY_BYTES"), "字节丢失");
        }

        private static void TestSnapshotConsistency()
        {
            Console.WriteLine();
            Console.WriteLine("7. 快照设备/序号一致");
            FakeDiagChannel ch = new FakeDiagChannel();
            DiagnosticsController c = Make(ch, new PortLockRegistry());
            ch.Deliver(Id(MacA)); c.ConnectAndIdentify(); c.StartRecording();
            Drop(c, ch);
            ch.Deliver(Id(MacB) + "JAY COUNT boot=new ms=5 total=7\n");
            c.Reconnect("COM8");
            c.StopRecording();
            string csv = ReadText(Path.Combine(c.Session.RootDir, "COUNT快照.csv"));
            Check("COUNT 快照含新设备段", csv.Contains(c.DeviceSegment), "快照段不符");
            string assoc = ReadText(Path.Combine(c.Session.RootDir, "待归属分段关联.csv"));
            Check("关联表含 new_mac", assoc.Contains("new_mac"), "缺关联");
        }

        private static void TestReassembleEqualsInput()
        {
            Console.WriteLine();
            Console.WriteLine("8. 按索引重组逐字节一致");
            FakeDiagChannel ch = new FakeDiagChannel();
            DiagnosticsController c = Make(ch, new PortLockRegistry());
            ch.Deliver(Id(MacA)); c.ConnectAndIdentify(); c.StartRecording();
            MemoryStream exp = new MemoryStream();
            byte[] a = Encoding.UTF8.GetBytes("A\n"); ch.DeliverBytes(a); exp.Write(a, 0, a.Length); c.Pump();
            Drop(c, ch);
            byte[] b = Encoding.UTF8.GetBytes("B\n" + Id(MacB) + "C\n");
            ch.DeliverBytes(b); exp.Write(b, 0, b.Length);
            c.Reconnect("COM8");
            byte[] d = Encoding.UTF8.GetBytes("D\n"); ch.DeliverBytes(d); exp.Write(d, 0, d.Length); c.Pump();
            c.StopRecording();
            byte[] act = Reassemble(c);
            Check("重组与输入一致", BytesEqual(act, exp.ToArray()),
                act.Length + " vs " + exp.Length);
        }

        private static string AllRaw(DiagnosticsController c)
        {
            StringBuilder sb = new StringBuilder();
            foreach (string f in Directory.GetFiles(c.Session.RootDir, "raw.bin", SearchOption.AllDirectories))
                sb.Append(ReadText(f));
            return sb.ToString();
        }

        private static byte[] Reassemble(DiagnosticsController c)
        {
            string text = ReadText(Path.Combine(c.Session.RootDir, "接收索引.csv"));
            MemoryStream ms = new MemoryStream();
            foreach (string row in text.Split((char)10))
            {
                if (row.Trim().Length == 0 || row.StartsWith("seq,")) continue;
                string[] f = row.Split(',');
                if (f.Length < 9) continue;
                string raw = Path.Combine(c.Session.RootDir, "设备分段", f[3], "raw.bin");
                byte[] all = File.ReadAllBytes(raw);
                int off = int.Parse(f[6]), len = int.Parse(f[7]);
                ms.Write(all, off, len);
            }
            return ms.ToArray();
        }

        private static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        // ---- 串口锁迁移 ----

        private static void TestLockMigratesPort()
        {
            Console.WriteLine();
            Console.WriteLine("9. COM8→COM9 锁迁移");
            FakeDiagChannel ch = new FakeDiagChannel();
            PortLockRegistry locks = new PortLockRegistry();
            DiagnosticsController c = Make(ch, locks);
            ch.Deliver(Id(MacA)); c.ConnectAndIdentify(); c.StartRecording();
            Drop(c, ch);
            ch.Deliver(Id(MacB));
            DiagConnectOutcome r = DiagButtonActions.ConnectOrReconnect(c, "COM9");
            Check("重连成功", r == DiagConnectOutcome.Reconnected, r.ToString());
            Check("新串口被锁", locks.IsLocked("COM9"), "COM9 未锁");
            Check("旧串口释放", !locks.IsLocked("COM8"), "COM8 仍锁");
            c.StopRecording();
        }

        private static void TestStopReleasesAllOwnedLocks()
        {
            Console.WriteLine();
            Console.WriteLine("10. 停止后无遗留锁");
            FakeDiagChannel ch = new FakeDiagChannel();
            PortLockRegistry locks = new PortLockRegistry();
            DiagnosticsController c = Make(ch, locks);
            ch.Deliver(Id(MacA)); c.ConnectAndIdentify(); c.StartRecording();
            Drop(c, ch);
            ch.Deliver(Id(MacB));
            DiagButtonActions.ConnectOrReconnect(c, "COM9");
            c.StopRecording();
            Check("停止后 COM8 无本会话锁", !locks.IsLocked("COM8"), "COM8 遗留");
            Check("停止后 COM9 无本会话锁", !locks.IsLocked("COM9"), "COM9 遗留");
        }

        private static void TestTargetLockedByOtherRejected()
        {
            Console.WriteLine();
            Console.WriteLine("11. 目标串口被他人占用");
            FakeDiagChannel ch = new FakeDiagChannel();
            PortLockRegistry locks = new PortLockRegistry();
            DiagnosticsController c = Make(ch, locks);
            ch.Deliver(Id(MacA)); c.ConnectAndIdentify(); c.StartRecording();
            Drop(c, ch);
            locks.Lock("COM9", "other-session");
            int writes = ch.Written.Count;
            int opens = ch.OpenCount;
            DiagConnectOutcome r = DiagButtonActions.ConnectOrReconnect(c, "COM9");
            Check("被占用时拒绝", r == DiagConnectOutcome.Failed, r.ToString());
            Check("零 Open", ch.OpenCount == opens, "Open 了");
            Check("零写入", ch.Written.Count == writes, "写入了");
            Check("他人锁未变", locks.IsLocked("COM9") &&
                  locks.IsOwnedBy("COM9", "other-session"), "他人锁被改");
            c.StopRecording();
        }

        private static void TestOpenFailureRollsBack()
        {
            Console.WriteLine();
            Console.WriteLine("12. 目标打开失败回滚");
            FakeDiagChannel ch = new FakeDiagChannel();
            PortLockRegistry locks = new PortLockRegistry();
            DiagnosticsController c = Make(ch, locks);
            ch.Deliver(Id(MacA)); c.ConnectAndIdentify(); c.StartRecording();
            string seg = c.DeviceSegment;
            Drop(c, ch);
            ch.FailOpen = true;
            DiagConnectOutcome r = DiagButtonActions.ConnectOrReconnect(c, "COM9");
            Check("打开失败返回 Failed", r == DiagConnectOutcome.Failed, r.ToString());
            Check("临时锁已回滚", !locks.IsLocked("COM9"), "COM9 锁残留");
            Check("旧会话保留", c.Session != null && !c.Session.IsClosed, "会话丢失");
            Check("旧串口仍锁", locks.IsLocked("COM8"), "COM8 锁丢失");
            ch.FailOpen = false;
            List<string> errs = c.StopRecording();
            Check("仍可停止", c.State == DiagState.Stopped, DiagStateText.Chinese(c.State));
            ExportResult ex = DiagnosticsExporter.Export(c.Session, MakePaths(), "t", DateTime.Now);
            Check("仍可导出", ex.Ok, ex.Error);
        }

        private static void TestSamePortReconnectNoLeak()
        {
            Console.WriteLine();
            Console.WriteLine("13. 同串口重连不泄漏");
            FakeDiagChannel ch = new FakeDiagChannel();
            PortLockRegistry locks = new PortLockRegistry();
            DiagnosticsController c = Make(ch, locks);
            ch.Deliver(Id(MacA)); c.ConnectAndIdentify(); c.StartRecording();
            Drop(c, ch);
            ch.Deliver(Id(MacA));
            DiagButtonActions.ConnectOrReconnect(c, "COM8");
            Check("同串口仍锁定", locks.IsLocked("COM8"), "锁丢失");
            int n = 0;
            for (int i = 0; i < 3; i++) { c.StopRecording(); n++; }
            Check("重复停止不泄漏", !locks.IsLocked("COM8"), "遗留锁");
        }

        private static void TestCoreStillRejectsActivePort()
        {
            Console.WriteLine();
            Console.WriteLine("14. 烧录/配对/扫描仍拒绝活动采集串口");
            FakeDiagChannel ch = new FakeDiagChannel();
            PortLockRegistry locks = new PortLockRegistry();
            DiagnosticsController c = Make(ch, locks);
            ch.Deliver(Id(MacA)); c.ConnectAndIdentify(); c.StartRecording();
            Drop(c, ch);
            ch.Deliver(Id(MacB));
            DiagButtonActions.ConnectOrReconnect(c, "COM9");
            Check("活动串口为 COM9", locks.IsLocked("COM9") && !locks.IsLocked("COM8"), "迁移失败");

            SimulatedTransport transport = new SimulatedTransport();
            SimBoard board = new SimBoard { Mac = MacB, Role = BoardRole.Boat, Fwid = Fwid, SteerDeg = 20 };
            transport.Boards.Add(board);
            transport.Plug("COM9", MacB);
            Workflow wf = new Workflow(MakePaths(), null, new RoleStore(Path.Combine(root,
                "st-" + Guid.NewGuid().ToString("N") + ".json")), transport, new SimClock(), new SimUi());
            wf.PortLocks = locks;
            PortScan scan = new PortScan();
            scan.Port = new SerialPortInfo { Name = "COM9" };
            scan.Identity = new BoardIdentity { Responded = true, Role = BoardRole.Boat, Mac = MacB,
                FirmwareId = Fwid, IdentityLineVersion = 4, SteeringAngle = 20, PairProtocol = 2 };
            OperationResult flash = wf.FlashCurrent(scan, BoardRole.Boat, false, false);
            Check("烧录被拒绝", !flash.Ok && transport.WriteFirmwareCount == 0, flash.Error);
            OperationResult pair = wf.PairTwoBoards(scan, scan);
            Check("配对被拒绝", !pair.Ok && transport.WritePairCount == 0, pair.Error);
            int probes = transport.ProbeCount;
            wf.Scan();
            Check("扫描不探测活动口", transport.ProbeCount == probes, "仍探测");
            c.StopRecording();
        }
    }
}
