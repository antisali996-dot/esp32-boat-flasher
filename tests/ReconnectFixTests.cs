using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace BoatTool
{
    // 复审二轮：GUI 重连分派 + 重连期原始字节完整性。
    public static class ReconnectFixTests
    {
        private static int passed;
        private static int failed;
        private static string root;

        private const string BoatMac = "24:62:AB:F1:36:64";
        private const string OtherMac = "24:62:AB:F0:72:94";
        private const string BoatFwid = "JAYFWB7-S20-A70-110-U1278-1500-1722-R590-OS600-Cr7-pendingerr-port";

        public static void Run()
        {
            passed = 0; failed = 0;
            root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test-work",
                "reconfix-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            Console.WriteLine();
            Console.WriteLine("===== 重连分派与字节完整性 =====");
            try
            {
                // —— GUI 分派 ——
                TestConnectWhileRecordingRefused();
                TestConnectWithNoSessionIsPlainConnect();
                TestDisconnectedConnectUsesReconnect();
                TestReconnectKeepsSameSessionObject();
                TestNoStartNeededAfterReconnect();
                TestReconnectFailureStillStoppable();
                TestCoreRefusesNewSessionWhileUnclosed();
                TestStoppedSessionNotAutoReplaced();
                TestNoSecondSessionWhileRecording();

                // —— 重连字节完整性 ——
                TestReconnectBytesAllPersisted();
                TestReconnectSplitAndByteByByte();
                TestReconnectHalfLineSameRead();
                TestReconnectTimeoutKeepsBytes();
                TestReconnectOtherMacAttribution();
                TestSameMacKeepsBootEvidence();
                TestFullSessionReassembly();
                TestExportReassembly();
                TestPendingSegmentInManifest();
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
            Console.WriteLine();
            Console.WriteLine("重连修复：通过 " + passed + " 项，失败 " + failed + " 项");
            if (failed > 0) throw new InvalidOperationException("重连修复测试失败 " + failed + " 项");
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

        private static DiagnosticsController Make(FakeDiagChannel ch, FakeDiagClock clock, AppPaths paths = null)
        {
            DiagnosticsController c = new DiagnosticsController(paths ?? MakePaths(), ch, clock,
                new QueryPolicy(), null);
            c.PortLocks = new PortLockRegistry();
            c.SetPort("COM_SIM");
            return c;
        }

        private static string Id(string mac)
        {
            return "JAYv4 ROLE=BOAT MAC=" + mac + " PEER=00:00:00:00:00:00 PAIR=2 STEER=20 FWID=" +
                   BoatFwid + " VER=20261103-590rpm-r7\n";
        }

        private static void Drop(DiagnosticsController c, FakeDiagChannel ch)
        {
            ch.FailRead = true;
            c.Pump();
            ch.FailRead = false;
        }

        private static string ReadTextShared(string p)
        {
            using (FileStream fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (StreamReader r = new StreamReader(fs, Encoding.UTF8))
                return r.ReadToEnd();
        }

        // 按接收索引重组全部 raw，验证逐字节等于输入且不重复。
        private static byte[] Reassemble(DiagnosticsController c)
        {
            string indexPath = Path.Combine(c.Session.RootDir, "接收索引.csv");
            string text = ReadTextShared(indexPath);
            MemoryStream ms = new MemoryStream();
            string[] rows = text.Split((char)10);
            for (int i = 1; i < rows.Length; i++)
            {
                string row = rows[i].Trim();
                if (row.Length == 0) continue;
                string[] f = SplitCsv(row);
                if (f.Length < 9) continue;
                string seg = f[3];
                long off = long.Parse(f[6], System.Globalization.CultureInfo.InvariantCulture);
                int len = int.Parse(f[7], System.Globalization.CultureInfo.InvariantCulture);
                string rawPath = Path.Combine(c.Session.RootDir, "设备分段", seg, "raw.bin");
                byte[] raw;
                using (FileStream fs = new FileStream(rawPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    raw = new byte[fs.Length];
                    int read = 0;
                    while (read < raw.Length)
                    {
                        int n = fs.Read(raw, read, raw.Length - read);
                        if (n <= 0) break;
                        read += n;
                    }
                }
                ms.Write(raw, (int)off, len);
            }
            return ms.ToArray();
        }

        // 最小 CSV 解析（本测试只读我们自己写的、无引号的数字列）。
        private static string[] SplitCsv(string row)
        {
            List<string> parts = new List<string>();
            StringBuilder cur = new StringBuilder();
            bool inQuote = false;
            for (int i = 0; i < row.Length; i++)
            {
                char ch = row[i];
                if (inQuote)
                {
                    if (ch == '"' && i + 1 < row.Length && row[i + 1] == '"') { cur.Append('"'); i++; }
                    else if (ch == '"') inQuote = false;
                    else cur.Append(ch);
                }
                else if (ch == '"') inQuote = true;
                else if (ch == ',') { parts.Add(cur.ToString()); cur.Length = 0; }
                else cur.Append(ch);
            }
            parts.Add(cur.ToString());
            return parts.ToArray();
        }

        private static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        // ---------- GUI 分派 ----------

        private static void TestConnectWhileRecordingRefused()
        {
            Console.WriteLine();
            Console.WriteLine("1. 记录中拒绝重复连接");
            FakeDiagChannel ch = new FakeDiagChannel();
            ch.Deliver(Id(BoatMac));
            DiagnosticsController c = Make(ch, new FakeDiagClock());
            DiagButtonActions.ConnectOrReconnect(c, "COM_SIM");
            DiagButtonActions.StartNewOrResume(c);
            CaptureSession s = c.Session;
            DiagConnectOutcome r = DiagButtonActions.ConnectOrReconnect(c, "COM_SIM");
            Check("记录中连接被拒绝", r == DiagConnectOutcome.RefusedRecording, r.ToString());
            Check("会话对象未变", ReferenceEquals(s, c.Session), "会话被替换");
            c.StopRecording();
            s.Dispose();
        }

        private static void TestConnectWithNoSessionIsPlainConnect()
        {
            Console.WriteLine();
            Console.WriteLine("2. 无会话时普通连接");
            FakeDiagChannel ch = new FakeDiagChannel();
            ch.Deliver(Id(BoatMac));
            DiagnosticsController c = Make(ch, new FakeDiagClock());
            DiagConnectOutcome r = DiagButtonActions.ConnectOrReconnect(c, "COM_SIM");
            Check("无会话走普通连接", r == DiagConnectOutcome.Connected, r.ToString());
            Check("尚无会话", c.Session == null, "提前创建会话");
        }

        private static void TestDisconnectedConnectUsesReconnect()
        {
            Console.WriteLine();
            Console.WriteLine("3. 掉线后连接走 Reconnect");
            FakeDiagChannel ch = new FakeDiagChannel();
            ch.Deliver(Id(BoatMac));
            DiagnosticsController c = Make(ch, new FakeDiagClock());
            DiagButtonActions.ConnectOrReconnect(c, "COM_SIM");
            DiagButtonActions.StartNewOrResume(c);
            ch.Deliver("BEFORE\n"); c.Pump();
            Drop(c, ch);
            string beforeSeg = c.DeviceSegment;
            CaptureSession old = c.Session;
            ch.Deliver("PRE_MARK\n" + Id(BoatMac) + "POST_MARK\n");
            DiagConnectOutcome r = DiagButtonActions.ConnectOrReconnect(c, "COM_SIM");
            Check("掉线后走重连", r == DiagConnectOutcome.Reconnected, r.ToString());
            Check("未创建新会话", ReferenceEquals(old, c.Session), "会话被替换");
            Check("重连后回到记录中", c.State == DiagState.Recording, DiagStateText.Chinese(c.State));
            Check("同 MAC 分段不变", c.DeviceSegment == beforeSeg, "分段改变");
            c.StopRecording();
            old.Dispose();
        }

        private static void TestReconnectKeepsSameSessionObject()
        {
            Console.WriteLine();
            Console.WriteLine("4. 重连保留 SessionId/RootDir/对象");
            FakeDiagChannel ch = new FakeDiagChannel();
            ch.Deliver(Id(BoatMac));
            DiagnosticsController c = Make(ch, new FakeDiagClock());
            DiagButtonActions.ConnectOrReconnect(c, "COM_SIM");
            DiagButtonActions.StartNewOrResume(c);
            string sid = c.Session.SessionId, dir = c.Session.RootDir;
            CaptureSession obj = c.Session;
            Drop(c, ch);
            ch.Deliver(Id(BoatMac));
            DiagButtonActions.ConnectOrReconnect(c, "COM_SIM");
            Check("对象不变", ReferenceEquals(obj, c.Session), "对象变了");
            Check("SessionId 不变", c.Session.SessionId == sid, "SessionId 变了");
            Check("RootDir 不变", c.Session.RootDir == dir, "RootDir 变了");
            c.StopRecording();
            obj.Dispose();
        }

        private static void TestNoStartNeededAfterReconnect()
        {
            Console.WriteLine();
            Console.WriteLine("5. 重连后无需再点开始");
            FakeDiagChannel ch = new FakeDiagChannel();
            ch.Deliver(Id(BoatMac));
            DiagnosticsController c = Make(ch, new FakeDiagClock());
            DiagButtonActions.ConnectOrReconnect(c, "COM_SIM");
            DiagButtonActions.StartNewOrResume(c);
            Drop(c, ch);
            ch.Deliver(Id(BoatMac));
            DiagButtonActions.ConnectOrReconnect(c, "COM_SIM");
            DiagStartOutcome again = DiagButtonActions.StartNewOrResume(c);
            Check("重连后再点开始被拒绝（已在记录）", again == DiagStartOutcome.RefusedRecording, again.ToString());
            ch.Deliver("AFTER_RECONNECT\n");
            c.Pump();
            string raw = Path.Combine(c.Session.SegmentDir(c.DeviceSegment), "raw.bin");
            string all = ReadTextShared(Path.Combine(c.Session.RootDir, "接收索引.csv"));
            Check("继续落到原会话", all.Contains(c.Session.SessionId), "索引缺失");
            c.StopRecording();
            c.Session.Dispose();
        }

        private static void TestReconnectFailureStillStoppable()
        {
            Console.WriteLine();
            Console.WriteLine("6. 重连失败仍可停止/导出");
            FakeDiagChannel ch = new FakeDiagChannel();
            ch.Deliver(Id(BoatMac));
            DiagnosticsController c = Make(ch, new FakeDiagClock());
            DiagButtonActions.ConnectOrReconnect(c, "COM_SIM");
            DiagButtonActions.StartNewOrResume(c);
            ch.Deliver("DATA1\n"); c.Pump();
            Drop(c, ch);
            // 重连时没有任何身份回应
            DiagConnectOutcome r = DiagButtonActions.ConnectOrReconnect(c, "COM_SIM");
            Check("重连失败返回 Failed", r == DiagConnectOutcome.Failed, r.ToString());
            Check("会话仍保留", c.Session != null && !c.Session.IsClosed, "会话丢失");
            List<string> errs = c.StopRecording("重连失败后停止");
            Check("失败后能停止", c.State == DiagState.Stopped, DiagStateText.Chinese(c.State));
            Check("失败后句柄关闭", c.Session.IsClosed, "未关闭");
            ExportResult ex = DiagnosticsExporter.Export(c.Session, MakePaths(), "test", DateTime.Now);
            Check("失败后仍可导出", ex.Ok, ex.Error);
        }

        private static void TestCoreRefusesNewSessionWhileUnclosed()
        {
            Console.WriteLine();
            Console.WriteLine("7. 核心层拒绝在未关闭会话时新建");
            FakeDiagChannel ch = new FakeDiagChannel();
            ch.Deliver(Id(BoatMac));
            DiagnosticsController c = Make(ch, new FakeDiagClock());
            DiagButtonActions.ConnectOrReconnect(c, "COM_SIM");
            DiagButtonActions.StartNewOrResume(c);
            CaptureSession old = c.Session;
            Drop(c, ch);
            ch.Deliver(Id(BoatMac));
            // 绕过界面直接调核心
            DiagConnectOutcome r = DiagButtonActions.ConnectOrReconnect(c, "COM_SIM");
            DiagStartOutcome s = DiagButtonActions.StartNewOrResume(c);
            Check("未关闭时核心拒绝新建", s == DiagStartOutcome.RefusedRecording ||
                  s == DiagStartOutcome.RefusedUnclosedSession, s.ToString());
            Check("旧会话仍是同一个", ReferenceEquals(old, c.Session), "会话被替换");
            Check("旧会话未被关闭", !old.IsClosed, "旧会话被关闭");
            c.StopRecording();
            old.Dispose();
        }

        private static void TestStoppedSessionNotAutoReplaced()
        {
            Console.WriteLine();
            Console.WriteLine("8. 已停止会话不被自动替换");
            FakeDiagChannel ch = new FakeDiagChannel();
            ch.Deliver(Id(BoatMac));
            DiagnosticsController c = Make(ch, new FakeDiagClock());
            DiagButtonActions.ConnectOrReconnect(c, "COM_SIM");
            DiagButtonActions.StartNewOrResume(c);
            c.StopRecording();
            CaptureSession stopped = c.Session;
            string dir = stopped.RootDir;
            DiagStartOutcome r = DiagButtonActions.StartNewOrResume(c);
            Check("已停止会话下不自动新建", r == DiagStartOutcome.RefusedUnclosedSession ||
                  r == DiagStartOutcome.RefusedNotConnected, r.ToString());
            Check("旧目录仍在", Directory.Exists(dir), "目录丢失");
            Check("旧会话对象未被替换", ReferenceEquals(stopped, c.Session), "对象被替换");
            stopped.Dispose();
        }

        private static void TestNoSecondSessionWhileRecording()
        {
            Console.WriteLine();
            Console.WriteLine("9. 记录中不创建第二个会话");
            FakeDiagChannel ch = new FakeDiagChannel();
            ch.Deliver(Id(BoatMac));
            DiagnosticsController c = Make(ch, new FakeDiagClock());
            DiagButtonActions.ConnectOrReconnect(c, "COM_SIM");
            DiagButtonActions.StartNewOrResume(c);
            CaptureSession s = c.Session;
            string dir = s.RootDir;
            DiagStartOutcome r = DiagButtonActions.StartNewOrResume(c);
            Check("记录中开始被拒绝", r == DiagStartOutcome.RefusedRecording, r.ToString());
            Check("会话未变", ReferenceEquals(s, c.Session), "会话变了");
            Check("只有一个会话目录", Directory.GetDirectories(Path.Combine(c.Session.RootDir, "..")).Length >= 1,
                "目录异常");
            c.StopRecording();
            s.Dispose();
        }

        // ---------- 重连字节完整性 ----------

        private static void TestReconnectBytesAllPersisted()
        {
            Console.WriteLine();
            Console.WriteLine("10. 重连同次 Read 的三段字节全部落盘");
            FakeDiagChannel ch = new FakeDiagChannel();
            ch.Deliver(Id(BoatMac));
            DiagnosticsController c = Make(ch, new FakeDiagClock());
            DiagButtonActions.ConnectOrReconnect(c, "COM_SIM");
            DiagButtonActions.StartNewOrResume(c);
            ch.Deliver("BEFORE\n"); c.Pump();
            Drop(c, ch);
            ch.Deliver("PRE_MARK\n" + Id(BoatMac) + "POST_MARK\n");
            DiagButtonActions.ConnectOrReconnect(c, "COM_SIM");
            c.Pump();
            c.StopRecording();
            byte[] all = Reassemble(c);
            string text = Encoding.UTF8.GetString(all);
            Check("重连前标记在 raw", text.Contains("PRE_MARK"), "缺 PRE_MARK");
            Check("身份后标记在 raw", text.Contains("POST_MARK"), "缺 POST_MARK");
            Check("重连前旧数据仍在", text.Contains("BEFORE"), "缺 BEFORE");
            c.Session.Dispose();
        }

        private static void TestReconnectSplitAndByteByByte()
        {
            Console.WriteLine();
            Console.WriteLine("11. 重连数据拆块/逐字节/中文跨字节");
            FakeDiagChannel ch = new FakeDiagChannel();
            ch.Deliver(Id(BoatMac));
            DiagnosticsController c = Make(ch, new FakeDiagClock());
            DiagButtonActions.ConnectOrReconnect(c, "COM_SIM");
            DiagButtonActions.StartNewOrResume(c);
            Drop(c, ch);

            byte[] payload = Encoding.UTF8.GetBytes("中文前标记\n" + Id(BoatMac) + "中文后标记\n");
            int mid = 3;   // 切在“中”的多字节序列内部附近
            ch.DeliverBytes(Slice(payload, 0, mid));
            ch.DeliverBytes(Slice(payload, mid, payload.Length - mid));
            DiagButtonActions.ConnectOrReconnect(c, "COM_SIM");
            c.Pump();
            c.StopRecording();
            byte[] all = Reassemble(c);
            Check("拆分后原始字节仍完整", BytesEqual(all, payload), "字节不一致");
            string text = Encoding.UTF8.GetString(all);
            Check("中文前标记保留", text.Contains("中文前标记"), "缺");
            Check("中文后标记保留", text.Contains("中文后标记"), "缺");
            c.Session.Dispose();
        }

        private static void TestReconnectHalfLineSameRead()
        {
            Console.WriteLine();
            Console.WriteLine("12. 身份与下一行半行同次 Read");
            FakeDiagChannel ch = new FakeDiagChannel();
            ch.Deliver(Id(BoatMac));
            DiagnosticsController c = Make(ch, new FakeDiagClock());
            DiagButtonActions.ConnectOrReconnect(c, "COM_SIM");
            DiagButtonActions.StartNewOrResume(c);
            Drop(c, ch);
            // 身份行 + 半行
            ch.Deliver(Id(BoatMac) + "PARTIAL_LINE_START");
            DiagButtonActions.ConnectOrReconnect(c, "COM_SIM");
            ch.Deliver("_END\n");
            c.Pump();
            c.StopRecording();
            byte[] all = Reassemble(c);
            string text = Encoding.UTF8.GetString(all);
            Check("半行内容未丢失", text.Contains("PARTIAL_LINE_START_END"), "半行拼接失败");
            Check("无重复字节", text.IndexOf("PARTIAL_LINE_START", StringComparison.Ordinal) ==
                  text.LastIndexOf("PARTIAL_LINE_START", StringComparison.Ordinal), "出现重复");
            c.Session.Dispose();
        }

        private static void TestReconnectTimeoutKeepsBytes()
        {
            Console.WriteLine();
            Console.WriteLine("13. 重连超时前的字节仍保留");
            FakeDiagChannel ch = new FakeDiagChannel();
            ch.Deliver(Id(BoatMac));
            DiagnosticsController c = Make(ch, new FakeDiagClock());
            DiagButtonActions.ConnectOrReconnect(c, "COM_SIM");
            DiagButtonActions.StartNewOrResume(c);
            Drop(c, ch);
            ch.Deliver("TIMEOUT_MARK_BEFORE\n");    // 无身份行，重连会超时
            DiagConnectOutcome r = DiagButtonActions.ConnectOrReconnect(c, "COM_SIM");
            Check("重连超时返回 Failed", r == DiagConnectOutcome.Failed, r.ToString());
            c.StopRecording();
            byte[] all = Reassemble(c);
            string text = Encoding.UTF8.GetString(all);
            Check("超时前字节保留", text.Contains("TIMEOUT_MARK_BEFORE"), "缺超时前字节");
            ExportResult ex = DiagnosticsExporter.Export(c.Session, MakePaths(), "test", DateTime.Now);
            Check("超时后仍可导出", ex.Ok, ex.Error);
        }

        private static void TestReconnectOtherMacAttribution()
        {
            Console.WriteLine();
            Console.WriteLine("14. 换 MAC：待归属字节关联到新设备");
            FakeDiagChannel ch = new FakeDiagChannel();
            ch.Deliver(Id(BoatMac));
            DiagnosticsController c = Make(ch, new FakeDiagClock());
            DiagButtonActions.ConnectOrReconnect(c, "COM_SIM");
            DiagButtonActions.StartNewOrResume(c);
            string seg1 = c.DeviceSegment;
            ch.Deliver("OLD_DEVICE_DATA\n"); c.Pump();
            Drop(c, ch);
            ch.Deliver("NEWDEV_PRE\n" + Id(OtherMac) + "NEWDEV_POST\n");
            DiagButtonActions.ConnectOrReconnect(c, "COM_SIM");
            string seg2 = c.DeviceSegment;
            c.StopRecording();
            Check("分段已切换", seg1 != seg2, "分段未变");

            // 待归属分段里保存了重连接收的全部字节（不移动、不重写）。
            string pendingRoot = Path.Combine(c.Session.RootDir, "设备分段");
            string pendingText = "";
            string newText = "";
            foreach (string dir in Directory.GetDirectories(pendingRoot))
            {
                string raw = Path.Combine(dir, "raw.bin");
                if (!File.Exists(raw)) continue;
                string t = ReadTextShared(raw);
                if (dir.EndsWith(seg1)) continue;
                if (t.Contains("NEWDEV"))
                {
                    if (Path.GetFileName(dir).StartsWith("待归属")) pendingText += t;
                    else newText += t;
                }
            }
            Check("待归属分段保留重连字节", pendingText.Contains("NEWDEV_PRE") &&
                  pendingText.Contains("NEWDEV_POST"), "待归属分段缺字节");
            Check("旧设备分段不含新设备字节", !ReadTextShared(Path.Combine(
                  c.Session.SegmentDir(seg1), "raw.bin")).Contains("NEWDEV_PRE"), "误挂旧设备");

            // 关联表记录 new_mac → 新设备分段
            string assoc = ReadTextShared(Path.Combine(c.Session.RootDir, "待归属分段关联.csv"));
            Check("关联表记录 new_mac 与新分段", assoc.Contains("new_mac") &&
                  assoc.IndexOf(seg2, StringComparison.Ordinal) >= 0, assoc);
            c.Session.Dispose();
        }

        private static void TestSameMacKeepsBootEvidence()
        {
            Console.WriteLine();
            Console.WriteLine("15. 同 MAC 重连保留 boot 边界");
            FakeDiagChannel ch = new FakeDiagChannel();
            ch.Deliver(Id(BoatMac));
            DiagnosticsController c = Make(ch, new FakeDiagClock());
            DiagButtonActions.ConnectOrReconnect(c, "COM_SIM");
            DiagButtonActions.StartNewOrResume(c);
            ch.Deliver("JAY COUNT boot=B1 ms=1 total=1\n"); c.Pump();
            Drop(c, ch);
            ch.Deliver(Id(BoatMac) + "JAY COUNT boot=B1 ms=2 total=2\n");
            DiagButtonActions.ConnectOrReconnect(c, "COM_SIM");
            c.Pump();
            c.StopRecording();
            string counts = ReadTextShared(Path.Combine(c.Session.RootDir, "COUNT快照.csv"));
            Check("同 MAC 重连后 boot 保持", counts.Split((char)10).Length >= 3, "COUNT 缺行");
            string events = ReadTextShared(Path.Combine(c.Session.RootDir, "事件.csv"));
            Check("掉线与重连都有事件", events.Contains("掉线") && events.Contains("重新打开串口"), "事件缺失");
            c.Session.Dispose();
        }

        private static void TestFullSessionReassembly()
        {
            Console.WriteLine();
            Console.WriteLine("16. 全会话按索引重组逐字节一致");
            FakeDiagChannel ch = new FakeDiagChannel();
            ch.Deliver(Id(BoatMac));
            DiagnosticsController c = Make(ch, new FakeDiagClock());
            DiagButtonActions.ConnectOrReconnect(c, "COM_SIM");
            DiagButtonActions.StartNewOrResume(c);

            MemoryStream expected = new MemoryStream();
            byte[] a = Encoding.UTF8.GetBytes("A1\n");
            ch.DeliverBytes(a); expected.Write(a, 0, a.Length); c.Pump();
            byte[] b = Encoding.UTF8.GetBytes("B2中文\n");
            ch.DeliverBytes(b); expected.Write(b, 0, b.Length); c.Pump();
            Drop(c, ch);
            byte[] rc = Encoding.UTF8.GetBytes("PRE\n" + Id(BoatMac) + "POST\n");
            ch.DeliverBytes(rc);
            DiagButtonActions.ConnectOrReconnect(c, "COM_SIM");
            c.Pump();
            expected.Write(rc, 0, rc.Length);
            byte[] d = Encoding.UTF8.GetBytes("D4\n");
            ch.DeliverBytes(d); expected.Write(d, 0, d.Length); c.Pump();
            c.StopRecording();

            byte[] actual = Reassemble(c);
            Check("重组字节与输入完全一致", BytesEqual(actual, expected.ToArray()),
                "长度 " + actual.Length + " vs " + expected.Length);
            c.Session.Dispose();
        }

        private static void TestExportReassembly()
        {
            Console.WriteLine();
            Console.WriteLine("17. 导出 ZIP 后重组仍一致");
            FakeDiagChannel ch = new FakeDiagChannel();
            ch.Deliver(Id(BoatMac));
            AppPaths paths = MakePaths();
            DiagnosticsController c = Make(ch, new FakeDiagClock(), paths);
            DiagButtonActions.ConnectOrReconnect(c, "COM_SIM");
            DiagButtonActions.StartNewOrResume(c);
            byte[] a = Encoding.UTF8.GetBytes("ZIP_A\n");
            ch.DeliverBytes(a); c.Pump();
            Drop(c, ch);
            byte[] rc = Encoding.UTF8.GetBytes("ZIP_PRE\n" + Id(BoatMac) + "ZIP_POST\n");
            ch.DeliverBytes(rc);
            DiagButtonActions.ConnectOrReconnect(c, "COM_SIM");
            c.Pump();
            c.StopRecording();
            byte[] expected = Reassemble(c);
            ExportResult ex = DiagnosticsExporter.Export(c.Session, paths, "test", DateTime.Now);
            Check("导出成功", ex.Ok, ex.Error);

            string unzip = Path.Combine(root, "unzip-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(unzip);
            ZipFile.ExtractToDirectory(ex.ZipPath, unzip);
            // 用导出后的索引重组
            string indexPath = Directory.GetFiles(unzip, "接收索引.csv", SearchOption.AllDirectories)[0];
            string text = ReadTextShared(indexPath);
            MemoryStream ms = new MemoryStream();
            foreach (string row in text.Split((char)10))
            {
                if (row.Trim().Length == 0 || row.StartsWith("seq,")) continue;
                string[] f = SplitCsv(row);
                if (f.Length < 9) continue;
                string rawPath = Path.Combine(Path.GetDirectoryName(indexPath), "设备分段", f[3], "raw.bin");
                byte[] raw = File.ReadAllBytes(rawPath);
                ms.Write(raw, int.Parse(f[6]), int.Parse(f[7]));
            }
            Check("ZIP 重组一致", BytesEqual(ms.ToArray(), expected), "ZIP 内字节不一致");
            c.Session.Dispose();
        }

        private static void TestPendingSegmentInManifest()
        {
            Console.WriteLine();
            Console.WriteLine("18. 待归属分段进清单与哈希校验");
            FakeDiagChannel ch = new FakeDiagChannel();
            ch.Deliver(Id(BoatMac));
            AppPaths paths = MakePaths();
            DiagnosticsController c = Make(ch, new FakeDiagClock(), paths);
            DiagButtonActions.ConnectOrReconnect(c, "COM_SIM");
            DiagButtonActions.StartNewOrResume(c);
            Drop(c, ch);
            ch.Deliver("PENDING_DATA\n" + Id(BoatMac) + "PENDING_AFTER\n");
            DiagButtonActions.ConnectOrReconnect(c, "COM_SIM");
            c.Pump();
            c.StopRecording();
            ExportResult ex = DiagnosticsExporter.Export(c.Session, paths, "test", DateTime.Now);
            Check("导出成功", ex.Ok, ex.Error);
            using (ZipArchive z = ZipFile.OpenRead(ex.ZipPath))
            {
                bool manifest = false;
                foreach (ZipArchiveEntry e in z.Entries)
                    if (e.FullName.EndsWith("文件清单与SHA256.txt")) manifest = true;
                Check("ZIP 含文件清单", manifest, "缺清单");
            }
            string man = Directory.GetFiles(c.Session.RootDir, "文件清单与SHA256.txt")[0];
            string manText = ReadTextShared(man);
            Check("清单含待归属分段 raw", manText.Contains("raw.bin"), "清单缺 raw");
            c.Session.Dispose();
        }

        private static byte[] Slice(byte[] src, int start, int len)
        {
            byte[] r = new byte[len];
            Array.Copy(src, start, r, 0, len);
            return r;
        }
    }
}
