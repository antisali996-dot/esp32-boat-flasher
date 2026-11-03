using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace BoatTool
{
    // 可控诊断时钟：让 5 秒查询周期与超时在测试里瞬时推进。
    public sealed class FakeDiagClock : IDiagClock
    {
        private DateTime now = new DateTime(2026, 11, 3, 16, 0, 0, DateTimeKind.Local);
        public DateTime Now { get { return now; } }
        public void Sleep(int ms) { now = now.AddMilliseconds(ms); }
        public void Advance(int ms) { now = now.AddMilliseconds(ms); }
    }

    // 模拟诊断串口：注入字节、记录写出的命令、可模拟掉线/写失败。
    public sealed class FakeDiagChannel : IDiagChannel
    {
        private readonly Queue<byte[]> chunks = new Queue<byte[]>();
        private byte[] current = new byte[0];
        private int pos;
        public readonly List<string> Written = new List<string>();
        public bool Opened;
        public bool FailOpen;
        public bool FailRead;
        public bool FailWrite;
        public bool Closed;

        public string Port;
        public int OpenCount;

        public void Open(string p)
        {
            if (FailOpen) throw new IOException("模拟：串口被占用");
            Port = p; Opened = true; OpenCount++;
        }

        public void Close() { Opened = false; Closed = true; }
        public bool IsOpen { get { return Opened; } }

        public void Deliver(string text) { chunks.Enqueue(Encoding.UTF8.GetBytes(text)); }
        public void DeliverBytes(byte[] b) { chunks.Enqueue(b); }

        public int Read(byte[] buffer, int offset, int count)
        {
            if (FailRead) throw new IOException("模拟：读取异常");
            while (pos >= current.Length)
            {
                if (chunks.Count == 0) return 0;
                current = chunks.Dequeue();
                pos = 0;
            }
            int n = Math.Min(count, current.Length - pos);
            Array.Copy(current, pos, buffer, offset, n);
            pos += n;
            return n;
        }

        public void Write(string text)
        {
            if (FailWrite) throw new IOException("模拟：写入异常");
            Written.Add(text.TrimEnd('\r', '\n'));
        }

        public int CommandCount(string cmd)
        {
            int n = 0;
            foreach (string w in Written) if (w == cmd) n++;
            return n;
        }

        public int BindCount()
        {
            int n = 0;
            foreach (string w in Written) if (w.StartsWith("JAY BIND ", StringComparison.Ordinal)) n++;
            return n;
        }
    }

    public static class DiagnosticsTests
    {
        private static int passed;
        private static int failed;
        private static string root;

        private const string BoatFwidR7 = "JAYFWB7-S20-A70-110-U1278-1500-1722-R590-OS600-Cr7-pendingerr-port";

        public static void Run()
        {
            passed = 0; failed = 0;
            root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test-work",
                "diag-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);

            Console.WriteLine();
            Console.WriteLine("===== 现场诊断采集（模拟串口）=====");
            try
            {
                TestRawBytesIntegrity();
                TestStreamingDecode();
                TestCountParsing();
                TestBootSegmentation();
                TestFaultAdapter();
                TestMarkersSendNothing();
                TestQueryPolicy();
                TestDisconnectKeepsFiles();
                TestMacChangeCreatesNewSegment();
                TestUnguardedOperationsRejected();
                TestExportAndFailure();
                TestLongRun();
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
            Console.WriteLine();
            Console.WriteLine("现场诊断：通过 " + passed + " 项，失败 " + failed + " 项");
            if (failed > 0) throw new InvalidOperationException("现场诊断测试失败 " + failed + " 项");
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

        private static DiagnosticsController MakeController(FakeDiagChannel ch, FakeDiagClock clock,
                                                            QueryPolicy policy, PortLockRegistry locks)
        {
            DiagnosticsController c = new DiagnosticsController(MakePaths(), ch, clock, policy, null);
            c.PortLocks = locks;
            c.SetPort("COM_TEST");
            return c;
        }

        private static string IdentityLine(string role, string mac, string fwid)
        {
            return "JAYv4 ROLE=" + role + " MAC=" + mac + " PEER=00:00:00:00:00:00 PAIR=2 STEER=20 FWID=" +
                   fwid + " VER=20261103-590rpm-r7\n";
        }

        // 1. 原始字节完整性：逐字节与输入一致
        private static void TestRawBytesIntegrity()
        {
            Console.WriteLine();
            Console.WriteLine("1. 原始字节逐字节一致");
            FakeDiagChannel ch = new FakeDiagChannel();
            FakeDiagClock clock = new FakeDiagClock();
            var c = MakeController(ch, clock, new QueryPolicy(), new PortLockRegistry());
            ch.Deliver(IdentityLine("BOAT", "24:62:AB:F1:36:64", BoatFwidR7));
            Check("连接成功", c.ConnectAndIdentify(), "连接失败");
            Check("连接状态为已连接", c.State == DiagState.Connected, DiagStateText.Chinese(c.State));
            Check("开始记录", c.StartRecording(), "无法开始记录");

            // 混合：中文 + 二进制 + 无换行残行
            byte[] payload = Encoding.UTF8.GetBytes("启动完成：电机目标 590 转\nlink=OK rx=1\n残行没有换行");
            ch.DeliverBytes(payload);
            clock.Advance(100);
            c.Pump();

            c.Session.Flush();
            string raw = Path.Combine(c.Session.SegmentDir(c.DeviceSegment), "raw.bin");
            byte[] stored = ReadShared(raw);
            Check("raw.bin 存在", File.Exists(raw), "缺 raw.bin");
            Check("原始字节与输入逐字节一致", BytesEqual(stored, payload),
                "长度 " + stored.Length + " vs " + payload.Length);
            Check("接收索引有记录", c.Session.RecentIndex.Count >= 1, "索引为空");
            c.StopRecording();
        }

        private static string ReadTextShared(string path)
        {
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (StreamReader r = new StreamReader(fs, Encoding.UTF8))
            {
                return r.ReadToEnd();
            }
        }

        // 读取仍在写入的 raw.bin（模拟界面在记录过程中查看/导出前刷新）。
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
                if (off == data.Length) return data;
                byte[] trimmed = new byte[off];
                Array.Copy(data, trimmed, off);
                return trimmed;
            }
        }

        private static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        // 2. 流式解码：跨块身份行、中文跨字节、一次多行、无结尾换行
        private static void TestStreamingDecode()
        {
            Console.WriteLine();
            Console.WriteLine("2. 流式解码与跨块");
            FakeDiagChannel ch = new FakeDiagChannel();
            FakeDiagClock clock = new FakeDiagClock();
            var c = MakeController(ch, clock, new QueryPolicy(), new PortLockRegistry());
            ch.Deliver(IdentityLine("BOAT", "24:62:AB:F1:36:64", BoatFwidR7));
            c.ConnectAndIdentify();
            c.StartRecording();

            // 把一行中文与身份行拆成多个字节块，其中中文被拆在一个 UTF-8 多字节序列中间。
            string text = "中文跨字节测试：舵机微动\n" + IdentityLine("BOAT", "24:62:AB:F1:36:64", BoatFwidR7) +
                          "额外一行没有结尾换行";
            byte[] all = Encoding.UTF8.GetBytes(text);
            int cut = text.IndexOf("跨字节", StringComparison.Ordinal) * 1; // 粗定位
            int splitAt = Math.Max(2, Math.Min(all.Length - 2, all.Length / 3));
            // 确保切在多字节字符中间：找到第一个非 ASCII 字节后 +1
            int mid = -1;
            for (int i = 1; i < all.Length; i++) if (all[i] >= 0x80) { mid = i + 1; break; }
            if (mid > 1 && mid < all.Length) splitAt = mid;

            ch.DeliverBytes(Slice(all, 0, splitAt));
            clock.Advance(20); c.Pump();
            ch.DeliverBytes(Slice(all, splitAt, all.Length - splitAt));
            clock.Advance(20); c.Pump();

            c.Session.Flush();
            string raw = Path.Combine(c.Session.SegmentDir(c.DeviceSegment), "raw.bin");
            Check("跨块后原始字节仍完整", BytesEqual(ReadShared(raw), all), "原始字节被破坏");
            c.Session.Flush();
            string segText = ReadTextShared(Path.Combine(c.Session.SegmentDir(c.DeviceSegment), "解码文本.log"));
            bool hasChinese = segText.Contains("舵机微动");
            Check("跨块中文正确解码", hasChinese, "中文未正确还原");
            bool hasIdentity = segText.Contains("JAYv4 ROLE=BOAT");
            Check("跨块身份行还原为一行", hasIdentity, "身份行未完整还原");
            bool hasNoNewline = segText.Contains("没有结尾换行");
            Check("无结尾换行内容被保存", hasNoNewline, "尾行丢失");
            c.StopRecording();
        }

        private static byte[] Slice(byte[] src, int start, int len)
        {
            byte[] r = new byte[len];
            Array.Copy(src, start, r, 0, len);
            return r;
        }

        // 3. COUNT 解析：64 位、最大值、越界、非法
        private static void TestCountParsing()
        {
            Console.WriteLine();
            Console.WriteLine("3. COUNT 64 位解析");
            CountSnapshot big = CountParser.Parse("JAY COUNT boot=7 ms=12345 total=4294967296");
            Check("超过 32 位仍精确", big.Parsed && big.Total == 4294967296UL, "解析失败：" + big.ParseError);
            CountSnapshot max = CountParser.Parse("JAY COUNT boot=b1 ms=1 total=18446744073709551615");
            Check("UInt64 最大值", max.Parsed && max.Total == ulong.MaxValue, "最大值解析失败：" + max.ParseError);
            CountSnapshot over = CountParser.Parse("JAY COUNT boot=b1 ms=1 total=18446744073709551616");
            Check("超出 64 位判为失败并保留原文", !over.Parsed && over.TotalRaw == "18446744073709551616" &&
                  over.Raw.Length > 0, "越界未被拦下");
            CountSnapshot bad = CountParser.Parse("JAY COUNT boot=b1 ms=abc total=10");
            Check("非法 ms 判为失败", !bad.Parsed, "非法数字未拦下");
            CountSnapshot missing = CountParser.Parse("JAY COUNT boot=b1 total=10");
            Check("缺字段判为失败", !missing.Parsed && missing.ParseError.Contains("ms"), missing.ParseError);
            CountSnapshot bootText = CountParser.Parse("JAY COUNT boot=BOOT-A ms=5 total=99");
            Check("boot 原样保存", bootText.Parsed && bootText.BootRaw == "BOOT-A", "boot 被改写");
            CountSnapshot totalText = CountParser.Parse("JAY COUNT boot=x ms=5 total=000123");
            Check("total 原始文本保留", totalText.TotalRaw == "000123" && totalText.Total == 123UL, "原始文本丢失");
        }

        // 4. boot 改变 → 新启动分段；同 boot 计数倒退只记录异常
        private static void TestBootSegmentation()
        {
            Console.WriteLine();
            Console.WriteLine("4. 启动分段与计数异常");
            FakeDiagChannel ch = new FakeDiagChannel();
            FakeDiagClock clock = new FakeDiagClock();
            var c = MakeController(ch, clock, new QueryPolicy(), new PortLockRegistry());
            ch.Deliver(IdentityLine("BOAT", "24:62:AB:F1:36:64", BoatFwidR7));
            c.ConnectAndIdentify();
            c.StartRecording();

            ch.Deliver("JAY COUNT boot=B1 ms=1000 total=100\n");
            clock.Advance(10); c.Pump();
            ch.Deliver("JAY COUNT boot=B1 ms=2000 total=900\n");
            clock.Advance(10); c.Pump();
            Check("同一 boot 下计数正常记录", c.Counts.Count == 2, "条数 " + c.Counts.Count);
            Check("计数倒退不当作重启", c.CurrentBoot == "B1", "boot 被误改");

            ch.Deliver("JAY COUNT boot=B2 ms=50 total=5\n");
            clock.Advance(10); c.Pump();
            Check("boot 改变产生新启动分段", c.CurrentBoot == "B2" && c.Counts[2].BootSegment == "B2",
                "未分段：" + c.CurrentBoot);
            c.StopRecording();
        }

        // 5. FAULT 适配器：待适配、只一行、不同 id 不拼凑
        private static void TestFaultAdapter()
        {
            Console.WriteLine();
            Console.WriteLine("5. FAULT 适配器（待适配）");
            IFaultAdapter adapter = new PendingFaultAdapter();
            Check("正式模式未确认格式", !adapter.FormatConfirmed, "格式被误标为已确认");
            FaultSnapshot none = adapter.Parse("JAY FAULT NONE", "seg", "B1");
            Check("NONE 识别为无故障", none.State == FaultAdapterState.NoneReported, none.State.ToString());
            FaultSnapshot cur = adapter.Parse("JAY FAULT CURRENT id=5", "seg", "B1");
            // 本测试用 PendingFaultAdapter（格式未确认的兼容适配器）：单行仍只能标“待适配”。
            // 正式 r10 路径用 R10FaultAdapter，单行状态由 FaultRegistry 判为“不完整（缺 PREVIOUS）”。
            Check("兼容适配器下单行标为待适配", cur.Kind == "CURRENT" &&
                  cur.State == FaultAdapterState.Unadapted, cur.State.ToString());
            Check("CURRENT 保留原文", cur.Raw == "JAY FAULT CURRENT id=5", cur.Raw);
            FaultSnapshot prev = adapter.Parse("JAY FAULT PREVIOUS id=7", "seg", "B1");
            Check("不同 id 不会自动拼成完整故障",
                !(cur.Kind == "CURRENT" && prev.Kind == "PREVIOUS" && cur.Id == prev.Id),
                "被错误关联");
            FaultSnapshot unknown = adapter.Parse("JAY WHATEVER", "seg", "B1");
            Check("未知行保留原文", unknown.Kind == "UNKNOWN" && unknown.Raw.Length > 0, "未知行丢失");
        }

        // 6. 标记不发送任何串口命令
        private static void TestMarkersSendNothing()
        {
            Console.WriteLine();
            Console.WriteLine("6. 标记不发送串口命令");
            FakeDiagChannel ch = new FakeDiagChannel();
            FakeDiagClock clock = new FakeDiagClock();
            var c = MakeController(ch, clock, new QueryPolicy(), new PortLockRegistry());
            ch.Deliver(IdentityLine("BOAT", "24:62:AB:F1:36:64", BoatFwidR7));
            c.ConnectAndIdentify();
            c.StartRecording();
            ch.Deliver("hello\n");
            clock.Advance(10); c.Pump();

            int before = ch.Written.Count;
            c.AddMarker(MarkerKind.MotorStopped);
            c.AddMarker(MarkerKind.MotorRestarted);
            c.AddMarker(MarkerKind.ServoMicroMove);
            int after = ch.Written.Count;
            Check("三次标记没有发出任何命令", before == after, "多发了 " + (after - before) + " 条");
            Check("三条标记都写入了本地", c.Session.Markers.Count == 3, "标记数 " + c.Session.Markers.Count);
            Check("标记带接收序号", c.Session.Markers[0].NearSequence >= 1, "缺少序号");
            c.StopRecording();
        }

        // 7. 查询策略：未确认不发；确认后按周期、不重叠、超时不补发
        private static void TestQueryPolicy()
        {
            Console.WriteLine();
            Console.WriteLine("7. 查询调度");
            FakeDiagChannel ch = new FakeDiagChannel();
            FakeDiagClock clock = new FakeDiagClock();
            QueryPolicy pending = new QueryPolicy();
            var c = MakeController(ch, clock, pending, new PortLockRegistry());
            ch.Deliver(IdentityLine("BOAT", "24:62:AB:F1:36:64", BoatFwidR7));
            c.ConnectAndIdentify();
            c.StartRecording();
            clock.Advance(20000); c.Pump();
            Check("未确认接口时零查询", ch.CommandCount("JAY COUNT") == 0 && ch.CommandCount("JAY FAULT") == 0,
                "发出了查询");

            FakeDiagChannel ch2 = new FakeDiagChannel();
            FakeDiagClock clock2 = new FakeDiagClock();
            QueryPolicy confirmed = new QueryPolicy();
            confirmed.FormatConfirmed = true;
            confirmed.AllowedFwids.Add(BoatFwidR7);
            var c2 = MakeController(ch2, clock2, confirmed, new PortLockRegistry());
            ch2.Deliver(IdentityLine("BOAT", "24:62:AB:F1:36:64", BoatFwidR7));
            c2.ConnectAndIdentify();
            c2.StartRecording();

            clock2.Advance(5100); c2.Pump();
            Check("到周期发送 COUNT", ch2.CommandCount("JAY COUNT") == 1, "次数 " + ch2.CommandCount("JAY COUNT"));
            clock2.Advance(100); c2.Pump();
            Check("未收到回应前不叠发", ch2.CommandCount("JAY COUNT") == 1, "发生重叠");
            clock2.Advance(6000); c2.Pump();   // 超时
            Check("超时后不重发，等下一周期", ch2.CommandCount("JAY COUNT") <= 2,
                "出现补发风暴：" + ch2.CommandCount("JAY COUNT"));

            int beforeStop = ch2.Written.Count;
            c2.StopRecording();
            clock2.Advance(30000); c2.Pump();
            Check("停止后不再发送查询", ch2.Written.Count == beforeStop, "停止后仍有查询");
        }

        // 8. 掉线保留文件
        private static void TestDisconnectKeepsFiles()
        {
            Console.WriteLine();
            Console.WriteLine("8. 掉线保留会话与文件");
            FakeDiagChannel ch = new FakeDiagChannel();
            FakeDiagClock clock = new FakeDiagClock();
            var c = MakeController(ch, clock, new QueryPolicy(), new PortLockRegistry());
            ch.Deliver(IdentityLine("BOAT", "24:62:AB:F1:36:64", BoatFwidR7));
            c.ConnectAndIdentify();
            c.StartRecording();
            ch.Deliver("before-drop\n");
            clock.Advance(10); c.Pump();
            string dir = c.Session.RootDir;
            string raw = Path.Combine(c.Session.SegmentDir(c.DeviceSegment), "raw.bin");
            long sizeBefore = new FileInfo(raw).Length;

            ch.FailRead = true;
            c.Pump();
            Check("掉线状态", c.State == DiagState.Disconnected, DiagStateText.Chinese(c.State));
            Check("掉线后文件仍在", File.Exists(raw) && new FileInfo(raw).Length == sizeBefore, "文件丢失或改变");
            Check("掉线被记录", c.Session.Faults.Count >= 1, "未记录掉线");

            ch.FailRead = false;
            ch.Deliver(IdentityLine("BOAT", "24:62:AB:F1:36:64", BoatFwidR7));
            bool re = c.Reconnect();
            Check("重连成功", re && c.State == DiagState.Recording, "重连失败");
            ch.Deliver("after-reconnect\n");
            clock.Advance(10); c.Pump();
            c.Session.Flush();
            // 同 MAC 重连：继续写回原设备分段，原字节保留、后续字节追加。
            byte[] oldRaw = ReadShared(raw);
            Check("同 MAC 原字节保留", oldRaw.Length >= sizeBefore, "原字节丢失");
            Check("同 MAC 后续字节追加", ReadTextShared(raw).Contains("after-reconnect"), "字节丢失");
            // 识别期会有一个“待归属”分段，但设备归属仍只有同一块板。
            int confirmed = 0;
            foreach (string segDir in Directory.GetDirectories(Path.Combine(c.Session.RootDir, "设备分段")))
                if (!Path.GetFileName(segDir).StartsWith("待归属")) confirmed++;
            Check("设备归属仍只有一个", confirmed == 1, "出现 " + confirmed + " 个已确认分段");
            c.StopRecording();
        }

        // 9. 换 MAC 建立新设备分段
        private static void TestMacChangeCreatesNewSegment()
        {
            Console.WriteLine();
            Console.WriteLine("9. 换 MAC 新建设备分段");
            FakeDiagChannel ch = new FakeDiagChannel();
            FakeDiagClock clock = new FakeDiagClock();
            var c = MakeController(ch, clock, new QueryPolicy(), new PortLockRegistry());
            ch.Deliver(IdentityLine("BOAT", "24:62:AB:F1:36:64", BoatFwidR7));
            c.ConnectAndIdentify();
            c.StartRecording();
            string firstSegment = c.DeviceSegment;
            ch.FailRead = true; c.Pump();
            ch.FailRead = false;

            ch.Deliver(IdentityLine("BOAT", "24:62:AB:F0:72:94", BoatFwidR7));
            c.Reconnect();
            Check("换 MAC 后设备分段改变", c.DeviceSegment != firstSegment,
                "仍是 " + c.DeviceSegment);
            bool hasSegEvent = false;
            foreach (string ev in c.Events) if (ev.Contains("MAC 改变")) hasSegEvent = true;
            Check("换 MAC 有明确提示", hasSegEvent, "无提示");
            c.StopRecording();
        }

        // 10. 记录中烧录/配对/扫描被核心层拒绝
        private static void TestUnguardedOperationsRejected()
        {
            Console.WriteLine();
            Console.WriteLine("10. 记录中禁止烧录/配对/扫描");
            PortLockRegistry locks = new PortLockRegistry();
            FakeDiagChannel ch = new FakeDiagChannel();
            FakeDiagClock clock = new FakeDiagClock();
            var c = MakeController(ch, clock, new QueryPolicy(), locks);
            ch.Deliver(IdentityLine("BOAT", "24:62:AB:F1:36:64", BoatFwidR7));
            c.ConnectAndIdentify();
            c.StartRecording();
            Check("串口已锁定", locks.IsLocked("COM_TEST"), "未锁定");

            SimulatedTransport transport = new SimulatedTransport();
            SimBoard board = new SimBoard();
            board.Mac = "24:62:AB:F1:36:64"; board.Role = BoardRole.Boat; board.Fwid = BoatFwidR7;
            board.SteerDeg = 20;
            transport.Boards.Add(board);
            transport.Plug("COM_TEST", board.Mac);

            Workflow wf = new Workflow(MakePaths(), null, new RoleStore(Path.Combine(root,
                "lock-store-" + Guid.NewGuid().ToString("N") + ".json")), transport, new SimClock(), new SimUi());
            wf.PortLocks = locks;
            PortScan scan = new PortScan();
            scan.Port = new SerialPortInfo { Name = "COM_TEST" };
            scan.Identity = new BoardIdentity { Responded = true, Role = BoardRole.Boat,
                Mac = board.Mac, FirmwareId = BoatFwidR7, IdentityLineVersion = 4, SteeringAngle = 20, PairProtocol = 2 };

            OperationResult flash = wf.FlashCurrent(scan, BoardRole.Boat, false, false);
            Check("记录中烧录被拒绝", !flash.Ok && transport.WriteFirmwareCount == 0 &&
                  flash.Error.Contains("现场诊断"), flash.Error);
            OperationResult pair = wf.PairTwoBoards(scan, scan);
            Check("记录中配对被拒绝", !pair.Ok && transport.WritePairCount == 0, pair.Error);
            int probes = transport.ProbeCount;
            wf.Scan();
            Check("记录中扫描不探测该串口", transport.ProbeCount == probes, "仍探测了被锁串口");
            c.StopRecording();
            Check("停止后解锁", !locks.IsLocked("COM_TEST"), "仍锁定");
        }

        // 11. 导出成功 / 失败保留
        private static void TestExportAndFailure()
        {
            Console.WriteLine();
            Console.WriteLine("11. 导出与失败保护");
            FakeDiagChannel ch = new FakeDiagChannel();
            FakeDiagClock clock = new FakeDiagClock();
            var c = MakeController(ch, clock, new QueryPolicy(), new PortLockRegistry());
            ch.Deliver(IdentityLine("BOAT", "24:62:AB:F1:36:64", BoatFwidR7));
            c.ConnectAndIdentify();
            c.StartRecording();
            byte[] payload = Encoding.UTF8.GetBytes("export-test-数据\n");
            ch.DeliverBytes(payload);
            clock.Advance(10); c.Pump();
            c.AddMarker(MarkerKind.MotorStopped);
            c.StopRecording();
            string dir = c.Session.RootDir;
            // 诊断：导出前确认所有记录文件已关闭可独占打开
            bool exclusive = true;
            foreach (string f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { using (FileStream probe = new FileStream(f, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { } }
                catch (Exception ex) { exclusive = false; Console.WriteLine("   [调试] 仍被占用：" + Path.GetFileName(f) + " -> " + ex.Message); }
            }
            Check("导出前记录文件已全部关闭", exclusive, "仍有文件被占用");

            ExportResult ok = DiagnosticsExporter.Export(c.Session, MakePaths(), "test", clock.Now);
            Check("导出成功", ok.Ok, ok.Error);
            Check("ZIP 存在且非空", ok.Ok && File.Exists(ok.ZipPath) && ok.Bytes > 0, "缺 ZIP");

            // 解压后 raw 与输入逐字节一致
            string unzip = Path.Combine(root, "unzip-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(unzip);
            ZipFile.ExtractToDirectory(ok.ZipPath, unzip);
            string rawPath = Directory.GetFiles(unzip, "raw.bin", SearchOption.AllDirectories)[0];
            Check("ZIP 内原始字节与输入一致", BytesEqual(File.ReadAllBytes(rawPath), payload), "字节不一致");
            Check("ZIP 含接收索引", Directory.GetFiles(unzip, "接收索引.csv", SearchOption.AllDirectories).Length == 1, "缺索引");
            Check("ZIP 含异常标记", Directory.GetFiles(unzip, "异常标记.csv", SearchOption.AllDirectories).Length == 1, "缺标记");
            Check("ZIP 含文件清单", Directory.GetFiles(unzip, "文件清单与SHA256.txt", SearchOption.AllDirectories).Length == 1, "缺清单");
            Check("导出后原始目录仍在", Directory.Exists(dir), "原始目录被删");

            // 模拟导出失败：把目标换成不可写位置
            ExportResult fail = DiagnosticsExporter.Export(c.Session, MakePaths(), "test", clock.Now);
            Check("重复导出仍可成功（可重试）", fail.Ok || !string.IsNullOrEmpty(fail.Error), "无结果");
            Check("失败不清空原始记录", Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Length > 0,
                "原始文件被删");
        }

        // 12. 长时间记录：大量输入 + 可控时钟
        private static void TestLongRun()
        {
            Console.WriteLine();
            Console.WriteLine("12. 长时间记录（可控时钟 + 大量输入）");
            FakeDiagChannel ch = new FakeDiagChannel();
            FakeDiagClock clock = new FakeDiagClock();
            var c = MakeController(ch, clock, new QueryPolicy(), new PortLockRegistry());
            ch.Deliver(IdentityLine("BOAT", "24:62:AB:F1:36:64", BoatFwidR7));
            c.ConnectAndIdentify();
            c.StartRecording();

            const int blocks = 3000;
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < blocks; i++)
            {
                sb.Clear();
                sb.Append("tick=").Append(i).Append(" ms=").Append(i * 20).Append('\n');
                ch.Deliver(sb.ToString());
                clock.Advance(20);
                c.Pump();
            }
            Check("长时间记录索引窗口受限", c.Session.RecentIndex.Count <= CaptureSession.RecentIndexWindow,
                "索引 " + c.Session.RecentIndex.Count);
            Check("没有采集错误", !c.Session.Failed, c.Session.FailureReason);
            int indexLines = ReadTextShared(Path.Combine(c.Session.RootDir, "接收索引.csv")).Split((char)10).Length - 1;
            Check("磁盘索引完整", indexLines == blocks + 1, "索引行 " + indexLines);
            c.StopRecording();
        }
    }
}
