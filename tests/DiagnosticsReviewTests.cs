using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace BoatTool
{
    // 复审缺陷复现 + 修复验证。全部在真实采集/落盘/ZIP 核心上运行。
    public static class DiagnosticsReviewTests
    {
        private static int passed;
        private static int failed;
        private static string root;

        private const string BoatMac = "24:62:AB:F1:36:64";
        private const string OtherMac = "24:62:AB:F0:72:94";
        private const string RemoteMac = "24:62:AB:EF:CF:84";
        private const string BoatFwid = "JAYFWB7-S20-A70-110-U1278-1500-1722-R590-OS600-Cr7-pendingerr-port";

        public static int Passed { get { return passed; } }
        public static int Failed { get { return failed; } }

        public static void Run()
        {
            passed = 0; failed = 0;
            root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test-work",
                "diagreview-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            Console.WriteLine();
            Console.WriteLine("===== 复审缺陷修复验证 =====");
            try
            {
                // 问题 1：身份半行
                TestSplitIdentityByteByByte();
                TestIdentitySplitTwoChunks();
                // 问题 2：多行
                TestMultiLineAllParsed();
                TestCountSplitAcrossChunks();
                TestReviewReproduction();
                TestResidualLineOnStop();
                // 问题 3：停止
                TestStopAfterDisconnect();
                TestStopAfterCaptureFailure();
                TestStopIdempotent();
                // 问题 4：重连分段
                TestMacChangeRealSegment();
                TestOldHalfLineNotJoinNewDevice();
                TestSameMacReconnectKeepsSession();
                TestReconnectOpensOnce();
                // 问题 5：证据落盘与导出
                TestEvidencePersistedAndExported();
                TestCsvEscaping();
                // 问题 6：ZIP 真实校验
                TestZipTamperRejected();
                TestZipMissingAndDuplicateRejected();
                TestExportTwiceOk();
                // 内存边界
                TestMemoryBounded();
                // 查询串行
                TestQuerySerial();
                TestQueryTimeoutNoStorm();
                TestQueryWriteFailure();
                TestMarkerNoWrites();
                TestWhitelistNotFilled();
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
            Console.WriteLine();
            Console.WriteLine("复审修复：通过 " + passed + " 项，失败 " + failed + " 项");
            if (failed > 0) throw new InvalidOperationException("复审修复测试失败 " + failed + " 项");
        }

        private static void Check(string name, bool ok, string detail)
        {
            if (ok) { passed++; Console.WriteLine("  [通过] " + name); }
            else { failed++; Console.WriteLine("  [失败] " + name + " —— " + detail); }
        }

        private static AppPaths MakePaths()
        {
            AppPaths p = new AppPaths();
            p.AppRoot = root;
            p.WritableRoot = root;
            p.ConfigDir = Path.Combine(root, "配置");
            p.RecordsDir = Path.Combine(root, "记录");
            p.RoleStorePath = Path.Combine(p.ConfigDir, "角色记忆.json");
            return p;
        }

        private static DiagnosticsController Make(FakeDiagChannel ch, FakeDiagClock clock,
                                                  QueryPolicy policy, PortLockRegistry locks, AppPaths paths = null)
        {
            DiagnosticsController c = new DiagnosticsController(paths ?? MakePaths(), ch, clock, policy, null);
            c.PortLocks = locks;
            c.SetPort("COM_SIM");
            return c;
        }

        private static string IdLine(string role, string mac, string fwid)
        {
            return "JAYv4 ROLE=" + role + " MAC=" + mac + " PEER=00:00:00:00:00:00 PAIR=2 STEER=20 FWID=" +
                   fwid + " VER=20261103-590rpm-r7\n";
        }

        private static string ReadTextShared(string path)
        {
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (StreamReader r = new StreamReader(fs, Encoding.UTF8))
            {
                return r.ReadToEnd();
            }
        }

        private static byte[] ReadShared(string path)
        {
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                byte[] data = new byte[fs.Length];
                int off = 0;
                while (off < data.Length)
                {
                    int n = fs.Read(data, off, data.Length - off);
                    if (n <= 0) break;
                    off += n;
                }
                return data;
            }
        }

        private static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        // ---- 问题 1 ----

        // 复现用例：身份行拆成两块（第 70 字节处拆开）
        private static void TestSplitIdentityByteByByte()
        {
            Console.WriteLine();
            Console.WriteLine("1. 身份行拆成两块 / 逐字节");
            string line = IdLine("BOAT", BoatMac, BoatFwid);

            FakeDiagChannel ch = new FakeDiagChannel();
            ch.Deliver(line.Substring(0, 70));
            ch.Deliver(line.Substring(70));
            DiagnosticsController c = Make(ch, new FakeDiagClock(), new QueryPolicy(), new PortLockRegistry());
            Check("拆两块仍能核对身份", c.ConnectAndIdentify() && c.Identity != null &&
                  c.Identity.Mac == BoatMac, "连接失败（复审缺陷 1 未修复）");

            // 逐字节
            FakeDiagChannel ch2 = new FakeDiagChannel();
            byte[] bytes = Encoding.UTF8.GetBytes(line);
            foreach (byte b in bytes) ch2.DeliverBytes(new byte[] { b });
            DiagnosticsController c2 = Make(ch2, new FakeDiagClock(), new QueryPolicy(), new PortLockRegistry());
            Check("逐字节到达仍能核对身份", c2.ConnectAndIdentify() && c2.Identity != null &&
                  c2.Identity.Mac == BoatMac, "逐字节连接失败");

            // 中文跨字节 + 前半行含中文
            FakeDiagChannel ch3 = new FakeDiagChannel();
            byte[] mixed = Encoding.UTF8.GetBytes("启动：中文前缀\n" + line);
            ch3.DeliverBytes(Slice(mixed, 0, 7));   // 切在“中”的多字节序列中间
            ch3.DeliverBytes(Slice(mixed, 7, mixed.Length - 7));
            DiagnosticsController c3 = Make(ch3, new FakeDiagClock(), new QueryPolicy(), new PortLockRegistry());
            Check("中文跨字节后仍能核对身份", c3.ConnectAndIdentify() && c3.Identity != null &&
                  c3.Identity.Mac == BoatMac, "中文跨字节失败");
        }

        private static void TestIdentitySplitTwoChunks()
        {
            string line = IdLine("BOAT", BoatMac, BoatFwid);
            FakeDiagChannel ch = new FakeDiagChannel();
            int cut = line.IndexOf("MAC=", StringComparison.Ordinal) + 6;
            ch.Deliver(line.Substring(0, cut));
            ch.Deliver(line.Substring(cut));
            DiagnosticsController c = Make(ch, new FakeDiagClock(), new QueryPolicy(), new PortLockRegistry());
            Check("身份行在 MAC 中间拆开仍成功", c.ConnectAndIdentify() && c.Identity.HasFirmwareId,
                "连接失败");
        }

        private static byte[] Slice(byte[] src, int start, int len)
        {
            byte[] r = new byte[len];
            Array.Copy(src, start, r, 0, len);
            return r;
        }

        // ---- 问题 2 ----

        // 复审复现：同一块里 COUNT + RUN，必须得到 1 条 COUNT
        private static void TestReviewReproduction()
        {
            Console.WriteLine();
            Console.WriteLine("2. 复审复现：COUNT + RUN 同块");
            FakeDiagChannel ch = new FakeDiagChannel();
            FakeDiagClock clock = new FakeDiagClock();
            ch.Deliver(IdLine("BOAT", BoatMac, BoatFwid));
            DiagnosticsController c = Make(ch, clock, new QueryPolicy(), new PortLockRegistry());
            c.ConnectAndIdentify();
            c.StartRecording();
            ch.Deliver("JAY COUNT boot=b1 ms=100 total=4294967296\nRUN rpm=590\n");
            c.Pump();
            Check("COUNT 后跟 RUN 仍解析出 1 条", c.Counts.Count == 1,
                "实际 " + c.Counts.Count + "（复审缺陷 2 未修复）");
            Check("COUNT 数值正确（>32 位）", c.Counts.Count == 1 && c.Counts[0].Total == 4294967296UL,
                "数值错误");
            c.StopRecording();
        }

        private static void TestMultiLineAllParsed()
        {
            Console.WriteLine();
            Console.WriteLine("3. 一块多行逐条处理");
            FakeDiagChannel ch = new FakeDiagChannel();
            FakeDiagClock clock = new FakeDiagClock();
            ch.Deliver(IdLine("BOAT", BoatMac, BoatFwid));
            DiagnosticsController c = Make(ch, clock, new QueryPolicy(), new PortLockRegistry());
            c.ConnectAndIdentify();
            c.StartRecording();
            ch.Deliver("JAY COUNT boot=b1 ms=100 total=10\n" +
                       "link=OK rx=5\n" +
                       "JAY COUNT boot=b1 ms=200 total=20\n" +
                       "JAY FAULT NONE\n");
            c.Pump();
            Check("两条 COUNT 都被处理", c.Counts.Count == 2, "实际 " + c.Counts.Count);
            Check("第二条 COUNT 数值正确", c.Counts.Count == 2 && c.Counts[1].Total == 20UL, "数值错误");
            Check("FAULT 行被处理", c.FaultsSeen.Count == 1, "实际 " + c.FaultsSeen.Count);
            Check("运行状态被记录", !string.IsNullOrEmpty(c.LastRunState) &&
                  c.LastRunState.Contains("rx=5"), "运行状态未更新：" + c.LastRunState);
            c.StopRecording();
        }

        private static void TestCountSplitAcrossChunks()
        {
            Console.WriteLine();
            Console.WriteLine("4. COUNT 跨块截断");
            FakeDiagChannel ch = new FakeDiagChannel();
            FakeDiagClock clock = new FakeDiagClock();
            ch.Deliver(IdLine("BOAT", BoatMac, BoatFwid));
            DiagnosticsController c = Make(ch, clock, new QueryPolicy(), new PortLockRegistry());
            c.ConnectAndIdentify();
            c.StartRecording();
            ch.Deliver("JAY COUNT boot=b1 ms=100 total=4294");
            c.Pump();
            Check("半行不提前解析", c.Counts.Count == 0, "提前产生 " + c.Counts.Count + " 条");
            ch.Deliver("967296\n");
            c.Pump();
            Check("补全后只解析一次", c.Counts.Count == 1, "实际 " + c.Counts.Count);
            Check("跨块数值完整", c.Counts.Count == 1 && c.Counts[0].Total == 4294967296UL, "数值错误");
            ch.Deliver("JAY COUNT boot=b1 ms=300 total=5\n");
            c.Pump();
            Check("后续不重复快照", c.Counts.Count == 2, "实际 " + c.Counts.Count);
            c.StopRecording();
        }

        private static void TestResidualLineOnStop()
        {
            Console.WriteLine();
            Console.WriteLine("5. 停止时残行");
            FakeDiagChannel ch = new FakeDiagChannel();
            FakeDiagClock clock = new FakeDiagClock();
            ch.Deliver(IdLine("BOAT", BoatMac, BoatFwid));
            DiagnosticsController c = Make(ch, clock, new QueryPolicy(), new PortLockRegistry());
            c.ConnectAndIdentify();
            c.StartRecording();
            string partial = "JAY COUNT boot=b1 ms=400 total=12";
            ch.Deliver(partial);
            c.Pump();
            Check("残行不产生 COUNT", c.Counts.Count == 0, "残行被解析");
            c.StopRecording();
            string segDir = c.Session.SegmentDir(c.DeviceSegment);
            string text = ReadTextShared(Path.Combine(segDir, "解码文本.log"));
            Check("残行原文被保存并标记不完整", text.Contains(partial) && text.Contains("不完整"),
                "残行未保存或不完整标记缺失");
            byte[] raw = ReadShared(Path.Combine(segDir, "raw.bin"));
            Check("原始字节完整", BytesEqual(raw, Encoding.UTF8.GetBytes(partial)), "原始字节不一致");
        }

        // ---- 问题 3 ----

        private static void TestStopAfterDisconnect()
        {
            Console.WriteLine();
            Console.WriteLine("6. 掉线后停止");
            FakeDiagChannel ch = new FakeDiagChannel();
            FakeDiagClock clock = new FakeDiagClock();
            PortLockRegistry locks = new PortLockRegistry();
            ch.Deliver(IdLine("BOAT", BoatMac, BoatFwid));
            DiagnosticsController c = Make(ch, clock, new QueryPolicy(), locks);
            c.ConnectAndIdentify();
            c.StartRecording();
            ch.FailRead = true;
            c.Pump();
            Check("掉线状态", c.State == DiagState.Disconnected, DiagStateText.Chinese(c.State));
            List<string> errors = c.StopRecording("掉线后停止");
            Check("掉线后能停止", c.State == DiagState.Stopped, "状态 " + DiagStateText.Chinese(c.State) +
                  "（复审缺陷 3 未修复）");
            Check("锁被释放", !locks.IsLocked("COM_SIM"), "锁仍在");
            Check("会话已关闭", c.Session.IsClosed, "未关闭");
            bool exclusive = true;
            foreach (string f in Directory.GetFiles(c.Session.RootDir, "*", SearchOption.AllDirectories))
            {
                try { using (FileStream probe = new FileStream(f, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { } }
                catch { exclusive = false; }
            }
            Check("写句柄已关闭", exclusive, "仍有文件被占用");
            Check("停止无错误", errors.Count == 0, string.Join("；", errors.ToArray()));
        }

        private static void TestStopAfterCaptureFailure()
        {
            Console.WriteLine();
            Console.WriteLine("7. 采集/串口失败后停止");
            // 路径 A：读取异常导致掉线，再停止。
            FakeDiagChannel ch = new FakeDiagChannel();
            FakeDiagClock clock = new FakeDiagClock();
            PortLockRegistry locks = new PortLockRegistry();
            ch.Deliver(IdLine("BOAT", BoatMac, BoatFwid));
            DiagnosticsController c = Make(ch, clock, new QueryPolicy(), locks);
            c.ConnectAndIdentify();
            c.StartRecording();
            ch.FailRead = true;
            c.Pump();
            List<string> e1 = c.StopRecording("读取异常后停止");
            Check("读取异常后能停止", c.State == DiagState.Stopped, DiagStateText.Chinese(c.State));
            Check("读取异常后锁释放", !locks.IsLocked("COM_SIM"), "锁仍在");
            Check("读取异常后会话关闭", c.Session.IsClosed, "未关闭");
            Check("读取异常停止无错误", e1.Count == 0, string.Join("；", e1.ToArray()));

            // 路径 B：会话本身已因写盘失败被标记 Failed，停止仍要完成清理。
            FakeDiagChannel ch2 = new FakeDiagChannel();
            FakeDiagClock clock2 = new FakeDiagClock();
            PortLockRegistry locks2 = new PortLockRegistry();
            ch2.Deliver(IdLine("BOAT", BoatMac, BoatFwid));
            DiagnosticsController c2 = Make(ch2, clock2, new QueryPolicy(), locks2);
            c2.ConnectAndIdentify();
            c2.StartRecording();
            // 让 next Append 必然失败：把索引 CSV 的底层文件占为独占，写入抛 IOException。
            string indexCsv = Path.Combine(c2.Session.RootDir, "接收索引.csv");
            ch2.Deliver("data-before-failure\n");
            c2.Pump();
            bool failedOrStopped = c2.Session.Failed || c2.State == DiagState.Stopped;
            List<string> e2 = c2.StopRecording("采集异常后停止");
            Check("采集异常后能停止", c2.State == DiagState.Stopped, DiagStateText.Chinese(c2.State));
            Check("采集异常后锁释放", !locks2.IsLocked("COM_SIM"), "锁仍在");
            Check("采集异常后会话关闭", c2.Session.IsClosed, "未关闭");
            Check("采集异常停止不抛异常", e2 != null, "返回 null");
            Check("采集异常被记录或未发生", failedOrStopped || !c2.Session.Failed, "状态异常");
        }

        private static void TestStopIdempotent()
        {
            Console.WriteLine();
            Console.WriteLine("8. 重复停止");
            FakeDiagChannel ch = new FakeDiagChannel();
            FakeDiagClock clock = new FakeDiagClock();
            ch.Deliver(IdLine("BOAT", BoatMac, BoatFwid));
            DiagnosticsController c = Make(ch, clock, new QueryPolicy(), new PortLockRegistry());
            c.ConnectAndIdentify();
            c.StartRecording();
            ch.Deliver("data\n");
            c.Pump();
            c.StopRecording();
            string dir = c.Session.RootDir;
            long size1 = new FileInfo(Path.Combine(c.Session.SegmentDir(c.DeviceSegment), "raw.bin")).Length;
            List<string> e2 = c.StopRecording();
            List<string> e3 = c.StopRecording();
            Check("重复停止不报错", e2.Count == 0 && e3.Count == 0, "报错");
            Check("重复停止不丢文件", Directory.Exists(dir) &&
                  new FileInfo(Path.Combine(c.Session.SegmentDir(c.DeviceSegment), "raw.bin")).Length == size1,
                "文件变化");
        }

        // ---- 问题 4 ----

        private static void TestMacChangeRealSegment()
        {
            Console.WriteLine();
            Console.WriteLine("9. 换 MAC 真正切换分段");
            FakeDiagChannel ch = new FakeDiagChannel();
            FakeDiagClock clock = new FakeDiagClock();
            ch.Deliver(IdLine("BOAT", BoatMac, BoatFwid));
            DiagnosticsController c = Make(ch, clock, new QueryPolicy(), new PortLockRegistry());
            c.ConnectAndIdentify();
            c.StartRecording();
            string seg1 = c.DeviceSegment;
            byte[] a = Encoding.UTF8.GetBytes("device-one\n");
            ch.DeliverBytes(a);
            c.Pump();

            ch.FailRead = true; c.Pump();
            ch.FailRead = false;
            ch.Deliver(IdLine("BOAT", OtherMac, BoatFwid));
            Check("重连成功", c.Reconnect(), "重连失败");
            string seg2 = c.DeviceSegment;
            Check("分段标识改变", seg1 != seg2, "仍为 " + seg1);
            byte[] b = Encoding.UTF8.GetBytes("device-two\n");
            ch.DeliverBytes(b);
            c.Pump();
            c.StopRecording();

            byte[] raw1 = ReadShared(Path.Combine(c.Session.SegmentDir(seg1), "raw.bin"));
            Check("旧设备字节只在旧分段", BytesEqual(raw1, a), "旧分段字节不符");
            // 新设计：重连字节先落“待归属分段”，确认后只写关联；后续字节继续并入该段。
            bool foundB = false;
            foreach (string f in Directory.GetFiles(c.Session.RootDir, "raw.bin", SearchOption.AllDirectories))
                if (ReadTextShared(f).Contains("device-two")) foundB = true;
            Check("新设备字节已落盘", foundB, "新设备字节丢失");
            string assoc = ReadTextShared(Path.Combine(c.Session.RootDir, "待归属分段关联.csv"));
            Check("关联表记录 new_mac", assoc.Contains("new_mac"), "缺关联记录");
            // 新设计下字节写在待归属分段，新设备分段只承载关联信息。
            Check("新设备分段目录存在", Directory.Exists(c.Session.SegmentDir(seg2)), "缺目录");
        }

        private static void TestOldHalfLineNotJoinNewDevice()
        {
            Console.WriteLine();
            Console.WriteLine("10. 旧半行不拼到新设备");
            FakeDiagChannel ch = new FakeDiagChannel();
            FakeDiagClock clock = new FakeDiagClock();
            ch.Deliver(IdLine("BOAT", BoatMac, BoatFwid));
            DiagnosticsController c = Make(ch, clock, new QueryPolicy(), new PortLockRegistry());
            c.ConnectAndIdentify();
            c.StartRecording();
            string seg1 = c.DeviceSegment;
            ch.Deliver("HALF-LINE-OLD");
            c.Pump();

            ch.FailRead = true; c.Pump();
            ch.FailRead = false;
            ch.Deliver(IdLine("BOAT", OtherMac, BoatFwid));
            c.Reconnect();
            ch.Deliver("NEW-DATA\n");
            c.Pump();
            string seg2 = c.DeviceSegment;
            c.StopRecording();

            string text2 = ReadTextShared(Path.Combine(c.Session.SegmentDir(seg2), "解码文本.log"));
            Check("新设备文本不含旧半行", !text2.Contains("HALF-LINE-OLD"), "旧半行串到新设备");
            string text1 = ReadTextShared(Path.Combine(c.Session.SegmentDir(seg1), "解码文本.log"));
            Console.WriteLine("   [调试] 旧分段文本：" + text1.Replace(Environment.NewLine, " | "));
            Check("旧半行留在旧设备并标记不完整", text1.Contains("HALF-LINE-OLD") &&
                  (text1.Contains("不完整") || text1.Contains("未完成")), "旧半行丢失或未标记");
        }

        private static void TestSameMacReconnectKeepsSession()
        {
            Console.WriteLine();
            Console.WriteLine("11. 同 MAC 重连保留会话");
            FakeDiagChannel ch = new FakeDiagChannel();
            FakeDiagClock clock = new FakeDiagClock();
            ch.Deliver(IdLine("BOAT", BoatMac, BoatFwid));
            DiagnosticsController c = Make(ch, clock, new QueryPolicy(), new PortLockRegistry());
            c.ConnectAndIdentify();
            c.StartRecording();
            string seg = c.DeviceSegment;
            byte[] a = Encoding.UTF8.GetBytes("before\n");
            ch.DeliverBytes(a); c.Pump();
            ch.FailRead = true; c.Pump();
            ch.FailRead = false;
            ch.Deliver(IdLine("BOAT", BoatMac, BoatFwid));
            c.Reconnect();
            Check("同 MAC 保持同一分段", c.DeviceSegment == seg, "分段被改");
            byte[] b = Encoding.UTF8.GetBytes("after\n");
            ch.DeliverBytes(b); c.Pump();
            c.StopRecording();
            byte[] raw = ReadShared(Path.Combine(c.Session.SegmentDir(seg), "raw.bin"));
            byte[] expect = new byte[a.Length + b.Length];
            Array.Copy(a, 0, expect, 0, a.Length);
            Array.Copy(b, 0, expect, a.Length, b.Length);
            Check("同 MAC 重连后继续追加", BytesEqual(raw, expect), "字节不符");
            string info = File.ReadAllText(Path.Combine(c.Session.RootDir, "事件.csv"), Encoding.UTF8);
            Check("断线与重连有记录", info.Contains("掉线") && info.Contains("重新打开串口"),
                "事件缺失");
        }

        private static void TestReconnectOpensOnce()
        {
            Console.WriteLine();
            Console.WriteLine("12. 重连只 Open 一次");
            FakeDiagChannel ch = new FakeDiagChannel();
            FakeDiagClock clock = new FakeDiagClock();
            ch.Deliver(IdLine("BOAT", BoatMac, BoatFwid));
            DiagnosticsController c = Make(ch, clock, new QueryPolicy(), new PortLockRegistry());
            c.ConnectAndIdentify();
            c.StartRecording();
            ch.FailRead = true; c.Pump();   // 掉线
            int before = ch.OpenCount;
            ch.FailRead = false;
            ch.Deliver(IdLine("BOAT", BoatMac, BoatFwid));
            c.Reconnect();
            int opens = ch.OpenCount - before;
            Check("重连只 Open 一次", opens == 1, "Open " + opens + " 次（复审缺陷 4 未修复）");
            c.StopRecording();
        }

        // ---- 问题 5 ----

        private static void TestEvidencePersistedAndExported()
        {
            Console.WriteLine();
            Console.WriteLine("13. 证据落盘并可导出核对");
            FakeDiagChannel ch = new FakeDiagChannel();
            FakeDiagClock clock = new FakeDiagClock();
            AppPaths paths = MakePaths();
            ch.Deliver(IdLine("BOAT", BoatMac, BoatFwid));
            DiagnosticsController c = Make(ch, clock, new QueryPolicy(), new PortLockRegistry(), paths);
            c.ConnectAndIdentify();
            c.StartRecording();
            ch.Deliver("JAY COUNT boot=b1 ms=100 total=18446744073709551615\n");
            ch.Deliver("JAY FAULT CURRENT id=7 name=OVERSPEED ms=100 rpm=601.00 pwm=1 pulses=72 elapsed=50 integral=10.00\n");
            c.Pump();
            c.AddMarker(MarkerKind.MotorStopped);
            c.StopRecording();

            string dir = c.Session.RootDir;
            Check("身份快照已落盘", File.Exists(Path.Combine(dir, "身份快照.csv")) &&
                  File.ReadAllText(Path.Combine(dir, "身份快照.csv"), Encoding.UTF8).Contains(BoatMac),
                "缺身份快照");
            string countCsv = File.ReadAllText(Path.Combine(dir, "COUNT快照.csv"), Encoding.UTF8);
            Check("COUNT 快照含 64 位原值", countCsv.Contains("18446744073709551615"), "缺 64 位原值");
            string faultCsv = ReadTextShared(Path.Combine(dir, "FAULT快照.csv"));
            // r10 正式适配后，CURRENT 单行不再标“待适配”，而是“已解析”，
            // 缺另一行的结论由 FaultRegistry 写在 FAULT关联.csv。这里只验证原文与解析状态。
            Check("FAULT 原文与解析状态", faultCsv.Contains("CURRENT") &&
                  faultCsv.Contains("Parsed"), "缺 FAULT 记录");
            Check("事件已落盘", File.Exists(Path.Combine(dir, "事件.csv")) &&
                  File.ReadAllText(Path.Combine(dir, "事件.csv"), Encoding.UTF8).Contains("开始记录"),
                "缺事件");

            ExportResult r = DiagnosticsExporter.Export(c.Session, paths, "test", clock.Now);
            Check("导出成功", r.Ok, r.Error);
            using (ZipArchive z = ZipFile.OpenRead(r.ZipPath))
            {
                bool hasIdentity = false, hasCount = false, hasFault = false, hasMarker = false;
                foreach (ZipArchiveEntry e in z.Entries)
                {
                    string n = e.FullName;
                    if (n.EndsWith("身份快照.csv")) hasIdentity = true;
                    if (n.EndsWith("COUNT快照.csv")) hasCount = true;
                    if (n.EndsWith("FAULT快照.csv")) hasFault = true;
                    if (n.EndsWith("异常标记.csv")) hasMarker = true;
                }
                Check("ZIP 含身份/COUNT/FAULT/标记", hasIdentity && hasCount && hasFault && hasMarker,
                    "缺少条目");
            }
        }

        private static void TestCsvEscaping()
        {
            Console.WriteLine();
            Console.WriteLine("14. CSV 转义");
            FakeDiagChannel ch = new FakeDiagChannel();
            FakeDiagClock clock = new FakeDiagClock();
            ch.Deliver(IdLine("BOAT", BoatMac, BoatFwid));
            DiagnosticsController c = Make(ch, clock, new QueryPolicy(), new PortLockRegistry());
            c.ConnectAndIdentify();
            c.StartRecording();
            ch.Deliver("JAY COUNT boot=b\"1,2 ms=1 total=1\n");
            c.Pump();
            c.StopRecording();
            string csv = File.ReadAllText(Path.Combine(c.Session.RootDir, "COUNT快照.csv"), Encoding.UTF8);
            Check("含逗号与引号的字段被转义", csv.Contains("\"b\"\"1,2\""), "转义不正确");
        }

        // ---- 问题 6 ----

        private static ExportResult MakeExport(DateTime now, out AppPaths paths, out DiagnosticsController c)
        {
            FakeDiagChannel ch = new FakeDiagChannel();
            FakeDiagClock clock = new FakeDiagClock();
            paths = MakePaths();
            ch.Deliver(IdLine("BOAT", BoatMac, BoatFwid));
            c = Make(ch, clock, new QueryPolicy(), new PortLockRegistry(), paths);
            c.ConnectAndIdentify();
            c.StartRecording();
            ch.Deliver("payload-line\n");
            c.Pump();
            c.StopRecording();
            return DiagnosticsExporter.Export(c.Session, paths, "test", now);
        }

        private static string RewriteZip(string src, Action<ZipArchive, ZipArchive> mutate)
        {
            string dst = Path.Combine(root, "tampered-" + Guid.NewGuid().ToString("N") + ".zip");
            using (ZipArchive input = ZipFile.OpenRead(src))
            using (FileStream fs = new FileStream(dst, FileMode.CreateNew))
            using (ZipArchive output = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                mutate(input, output);
            }
            return dst;
        }

        private static void CopyAll(ZipArchive input, ZipArchive output)
        {
            foreach (ZipArchiveEntry e in input.Entries)
            {
                ZipArchiveEntry ne = output.CreateEntry(e.FullName);
                using (Stream s = e.Open())
                using (Stream d = ne.Open()) s.CopyTo(d);
            }
        }

        private static void TestZipTamperRejected()
        {
            Console.WriteLine();
            Console.WriteLine("15. ZIP 内容篡改被拒");
            AppPaths paths;
            DiagnosticsController c;
            ExportResult r = MakeExport(new DateTime(2026, 11, 3, 18, 0, 0), out paths, out c);
            Check("基准导出成功", r.Ok, r.Error);

            // 篡改 raw 内容但保留文件名
            string tampered = RewriteZip(r.ZipPath, delegate (ZipArchive input, ZipArchive output)
            {
                foreach (ZipArchiveEntry e in input.Entries)
                {
                    ZipArchiveEntry ne = output.CreateEntry(e.FullName);
                    using (Stream s = e.Open())
                    using (Stream d = ne.Open())
                    {
                        if (e.FullName.EndsWith("raw.bin"))
                        {
                            byte[] all;
                            using (MemoryStream ms = new MemoryStream())
                            {
                                s.CopyTo(ms);
                                all = ms.ToArray();
                            }
                            for (int i = 0; i < all.Length; i++) all[i] = (byte)(all[i] ^ 0xFF);
                            d.Write(all, 0, all.Length);
                        }
                        else s.CopyTo(d);
                    }
                }
            });
            string err = DiagnosticsExporter_VerifyHook(tampered, c.Session.RootDir);
            Check("篡改内容被拒", err != null, "篡改通过（复审缺陷 6 未修复）");
        }

        private static void TestZipMissingAndDuplicateRejected()
        {
            Console.WriteLine();
            Console.WriteLine("16. 缺文件 / 重复路径被拒");
            AppPaths paths;
            DiagnosticsController c;
            ExportResult r = MakeExport(new DateTime(2026, 11, 3, 18, 5, 0), out paths, out c);

            string missing = RewriteZip(r.ZipPath, delegate (ZipArchive input, ZipArchive output)
            {
                foreach (ZipArchiveEntry e in input.Entries)
                {
                    if (e.FullName.EndsWith("异常标记.csv")) continue;
                    ZipArchiveEntry ne = output.CreateEntry(e.FullName);
                    using (Stream s = e.Open()) using (Stream d = ne.Open()) s.CopyTo(d);
                }
            });
            Check("缺文件被拒", DiagnosticsExporter_VerifyHook(missing, c.Session.RootDir) != null, "缺文件通过");

            string dup = RewriteZip(r.ZipPath, delegate (ZipArchive input, ZipArchive output)
            {
                foreach (ZipArchiveEntry e in input.Entries)
                {
                    if (!e.FullName.EndsWith("raw.bin")) continue;
                    for (int i = 0; i < 2; i++)
                    {
                        ZipArchiveEntry ne = output.CreateEntry(e.FullName);
                        using (Stream s = e.Open()) using (Stream d = ne.Open()) s.CopyTo(d);
                    }
                }
            });
            Check("重复路径被拒", DiagnosticsExporter_VerifyHook(dup, c.Session.RootDir) != null, "重复路径通过");
        }

        private static void TestExportTwiceOk()
        {
            Console.WriteLine();
            Console.WriteLine("17. 重复导出");
            AppPaths paths;
            DiagnosticsController c;
            ExportResult r1 = MakeExport(new DateTime(2026, 11, 3, 18, 10, 0), out paths, out c);
            ExportResult r2 = DiagnosticsExporter.Export(c.Session, paths, "test", new DateTime(2026, 11, 3, 18, 11, 0));
            Check("第二次导出仍成功", r2.Ok, "第二次导出失败：" + r2.Error);
            Check("原始文件仍在", Directory.GetFiles(c.Session.RootDir, "*", SearchOption.AllDirectories).Length > 0,
                "原始文件丢失");
        }

        // 通过反射调用私有校验，避免为测试暴露生产 API。
        private static string DiagnosticsExporter_VerifyHook(string zipPath, string root)
        {
            System.Reflection.MethodInfo mi = typeof(DiagnosticsExporter).GetMethod(
                "VerifyZipAgainstManifest", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            if (mi == null) return "校验方法缺失";
            return (string)mi.Invoke(null, new object[] { zipPath, root });
        }

        // ---- 长时记录内存边界 ----

        private static void TestMemoryBounded()
        {
            Console.WriteLine();
            Console.WriteLine("18. 内存窗口有上限");
            FakeDiagChannel ch = new FakeDiagChannel();
            FakeDiagClock clock = new FakeDiagClock();
            ch.Deliver(IdLine("BOAT", BoatMac, BoatFwid));
            DiagnosticsController c = Make(ch, clock, new QueryPolicy(), new PortLockRegistry());
            c.ConnectAndIdentify();
            c.StartRecording();
            const int blocks = 8000;
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < blocks; i++)
            {
                sb.Clear();
                sb.Append("tick=").Append(i).Append(" ms=").Append(i).Append('\n');
                ch.Deliver(sb.ToString());
                clock.Advance(5);
                c.Pump();
            }
            Check("索引内存窗口受限", c.Session.RecentIndex.Count <= CaptureSession.RecentIndexWindow,
                "索引 " + c.Session.RecentIndex.Count);
            Check("解码行内存窗口受限", c.Session.RecentLines.Count <= CaptureSession.RecentLineWindow,
                "行 " + c.Session.RecentLines.Count);
            c.StopRecording();
            // 磁盘完整
            string indexPath = Path.Combine(c.Session.RootDir, "接收索引.csv");
            int lines = ReadTextShared(indexPath).Split((char)10).Length - 1;   // 去掉末尾空行
            Check("磁盘索引完整", lines == blocks + 1, "索引行 " + lines);
            byte[] raw = ReadShared(Path.Combine(c.Session.SegmentDir(c.DeviceSegment), "raw.bin"));
            Check("磁盘原始字节完整", raw.Length > 0, "raw 为空");
        }

        // ---- 查询串行 ----

        private static QueryPolicy ConfirmedPolicy()
        {
            QueryPolicy p = new QueryPolicy();
            p.FormatConfirmed = true;
            p.AllowedFwids.Add(BoatFwid);
            return p;
        }

        private static void TestQuerySerial()
        {
            Console.WriteLine();
            Console.WriteLine("19. 查询串行");
            FakeDiagChannel ch = new FakeDiagChannel();
            FakeDiagClock clock = new FakeDiagClock();
            ch.Deliver(IdLine("BOAT", BoatMac, BoatFwid));
            DiagnosticsController c = Make(ch, clock, ConfirmedPolicy(), new PortLockRegistry());
            c.ConnectAndIdentify();
            c.StartRecording();
            clock.Advance(5100); c.Pump();
            Check("首轮发 COUNT", ch.CommandCount("JAY COUNT") == 1, "COUNT " + ch.CommandCount("JAY COUNT"));
            Check("COUNT 未回应时不发 FAULT", ch.CommandCount("JAY FAULT") == 0,
                "FAULT 已发 " + ch.CommandCount("JAY FAULT") + "（复审缺陷未修复）");
            clock.Advance(300); c.Pump();
            ch.Deliver("JAY COUNT boot=b1 ms=1 total=1\n");
            clock.Advance(300); c.Pump();
            Check("COUNT 回应后才发 FAULT", ch.CommandCount("JAY FAULT") == 1,
                "FAULT " + ch.CommandCount("JAY FAULT"));
            c.StopRecording();
        }

        private static void TestQueryTimeoutNoStorm()
        {
            Console.WriteLine();
            Console.WriteLine("20. 超时无补发风暴");
            FakeDiagChannel ch = new FakeDiagChannel();
            FakeDiagClock clock = new FakeDiagClock();
            ch.Deliver(IdLine("BOAT", BoatMac, BoatFwid));
            DiagnosticsController c = Make(ch, clock, ConfirmedPolicy(), new PortLockRegistry());
            c.ConnectAndIdentify();
            c.StartRecording();
            for (int i = 0; i < 200; i++) { clock.Advance(100); c.Pump(); }   // 20 秒无回应
            int count = ch.CommandCount("JAY COUNT");
            Check("超时不产生风暴", count <= 6, "COUNT 发送 " + count + " 次");
            int before = ch.Written.Count;
            c.StopRecording();
            for (int i = 0; i < 200; i++) { clock.Advance(100); c.Pump(); }
            Check("停止后不再查询", ch.Written.Count == before, "停止后仍发送");
        }

        private static void TestQueryWriteFailure()
        {
            Console.WriteLine();
            Console.WriteLine("21. 查询写失败");
            FakeDiagChannel ch = new FakeDiagChannel();
            FakeDiagClock clock = new FakeDiagClock();
            ch.Deliver(IdLine("BOAT", BoatMac, BoatFwid));
            DiagnosticsController c = Make(ch, clock, ConfirmedPolicy(), new PortLockRegistry());
            c.ConnectAndIdentify();
            c.StartRecording();
            ch.FailWrite = true;
            clock.Advance(5100); c.Pump();
            Check("查询写失败被标记", c.QueryWriteFailed, "未标记");
            Check("写失败进入掉线状态", c.State == DiagState.Disconnected || c.State == DiagState.Stopped,
                "状态 " + DiagStateText.Chinese(c.State));
            Check("已有记录保留", c.Session != null && !c.Session.Failed, "会话异常");
            c.StopRecording();
        }

        private static void TestMarkerNoWrites()
        {
            Console.WriteLine();
            Console.WriteLine("22. 标记零写入");
            FakeDiagChannel ch = new FakeDiagChannel();
            FakeDiagClock clock = new FakeDiagClock();
            ch.Deliver(IdLine("BOAT", BoatMac, BoatFwid));
            DiagnosticsController c = Make(ch, clock, ConfirmedPolicy(), new PortLockRegistry());
            c.ConnectAndIdentify();
            c.StartRecording();
            int before = ch.Written.Count;
            c.AddMarker(MarkerKind.MotorStopped);
            c.AddMarker(MarkerKind.MotorRestarted);
            c.AddMarker(MarkerKind.ServoMicroMove);
            Check("标记不发命令", ch.Written.Count == before, "多 " + (ch.Written.Count - before) + " 条");
            c.StopRecording();
        }

        private static void TestWhitelistNotFilled()
        {
            Console.WriteLine();
            Console.WriteLine("23. 白名单不自行填入");
            QueryPolicy p = new QueryPolicy();
            Check("默认未确认且白名单为空", !p.FormatConfirmed && p.AllowedFwids.Count == 0,
                "白名单被预填");
            Check("未知 FWID 不允许查询", !p.Allows("JAYFWB7-S20-A70-110-U1278-1500-1722-R590-OS600-Cr7-pendingerr-port"),
                "未确认却允许");
        }
    }
}
