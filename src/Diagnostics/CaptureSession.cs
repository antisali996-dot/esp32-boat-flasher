using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace BoatTool
{
    // 一个设备分段：独立目录、独立 raw.bin、独立解码文本。
    public sealed class CaptureSegment
    {
        public string Id = "";
        public string Role = "";
        public string Mac = "";
        public string Boot = "";
        public string OpenedAt = "";
        public Stream Raw;
        public StreamWriter Text;
        public long Offset;
        public long ChunkCount;
    }

    // 采集会话：所有磁盘写入的唯一入口。界面不直接写日志文件。
    //
    // 关键约束（对应复审问题 1/2/3/5/长时记录）：
    //   * 原始字节先落盘，再解码/解析；解析失败不影响原始文件。
    //   * 流式行组装：半行保留，不丢、不提前解析；新产生的完整行按顺序向上交付。
    //   * 内存只保留固定上限的近期窗口；完整证据在磁盘。
    //   * 写盘失败立即报告并停止“假装正常记录”，保留已写入部分与缺口。
    public sealed class CaptureSession : IDisposable
    {
        public const int RecentLineWindow = 500;
        public const int RecentIndexWindow = 2000;

        public readonly string SessionId;
        public readonly string RootDir;
        public readonly string StartedAt;
        public string StoppedAt = "";
        public string StopReason = "";

        private readonly object gate = new object();
        private readonly List<ReceiveIndexEntry> recentIndex = new List<ReceiveIndexEntry>();
        private readonly List<string> recentLines = new List<string>();
        private readonly List<DiagnosticMarker> markers = new List<DiagnosticMarker>();
        private readonly List<CaptureFault> faults = new List<CaptureFault>();
        private readonly Dictionary<string, CaptureSegment> segments =
            new Dictionary<string, CaptureSegment>(StringComparer.Ordinal);
        // 每个设备分段有独立的解码上下文：新设备的半行不会与旧设备拼接，
        // 也不会在切换设备时被清掉（保留给后续 Pump 补齐）。
        private readonly Dictionary<string, StreamingLineAssembler> assemblers =
            new Dictionary<string, StreamingLineAssembler>(StringComparer.Ordinal);
        private readonly StreamingLineAssembler globalAssembler = new StreamingLineAssembler();
        private string activeSegmentId = "";

        private StreamingLineAssembler AssemblerFor(string segmentId)
        {
            if (string.IsNullOrEmpty(segmentId)) return globalAssembler;
            StreamingLineAssembler a;
            if (!assemblers.TryGetValue(segmentId, out a))
            {
                a = new StreamingLineAssembler();
                assemblers[segmentId] = a;
            }
            return a;
        }

        // 当前活动的解码分段（由控制器在重连/关联后设置）。
        public void SetActiveSegment(string segmentId)
        {
            lock (gate) { activeSegmentId = segmentId ?? ""; }
        }

        private StreamWriter indexWriter, markerWriter, faultWriter, eventWriter;
        private StreamWriter identityWriter, countWriter, faultSnapshotWriter, queryWriter;
        private StreamWriter faultGroupWriter, faultGroupStateWriter;
        private StreamWriter pendingWriter;
        private string pendingSegmentId = "";
        private bool failed;
        private string failureReason = "";
        private long nextSequence = 1;
        private bool closed;
        private bool disposed;

        public CaptureSession(string workRoot, string sessionId, string role, string mac, DateTime startedLocal)
        {
            SessionId = sessionId;
            RootDir = Path.Combine(workRoot, "诊断记录", sessionId);
            StartedAt = DiagTime.Now(startedLocal);
            Directory.CreateDirectory(RootDir);
            Directory.CreateDirectory(Path.Combine(RootDir, "设备分段"));
            try
            {
                indexWriter = NewWriter(Path.Combine(RootDir, "接收索引.csv"));
                WriteCsv(indexWriter, new string[] { "seq", "received_at", "session", "device_segment",
                    "boot_segment", "file", "offset", "length", "sha256" });
                markerWriter = NewWriter(Path.Combine(RootDir, "异常标记.csv"));
                WriteCsv(markerWriter, new string[] { "kind", "at", "session", "device_segment", "near_seq" });
                faultWriter = NewWriter(Path.Combine(RootDir, "采集错误.csv"));
                WriteCsv(faultWriter, new string[] { "at", "kind", "detail" });
                eventWriter = NewWriter(Path.Combine(RootDir, "事件.csv"));
                WriteCsv(eventWriter, new string[] { "at", "event" });
                identityWriter = NewWriter(Path.Combine(RootDir, "身份快照.csv"));
                WriteCsv(identityWriter, new string[] { "at", "device_segment", "role", "mac", "peer",
                    "pair_protocol", "steer", "fwid", "ver", "raw" });
                countWriter = NewWriter(Path.Combine(RootDir, "COUNT快照.csv"));
                WriteCsv(countWriter, new string[] { "at", "device_segment", "boot_segment", "near_seq",
                    "parsed", "boot_raw", "ms_raw", "ms", "total_raw", "total_u64", "raw", "parse_error" });
                faultSnapshotWriter = NewWriter(Path.Combine(RootDir, "FAULT快照.csv"));
                WriteCsv(faultSnapshotWriter, new string[] { "at", "device_segment", "boot_segment", "near_seq",
                    "kind", "id", "adapter_state", "note", "raw" });
                queryWriter = NewWriter(Path.Combine(RootDir, "查询记录.csv"));
                WriteCsv(queryWriter, new string[] { "at", "command", "result" });
                // 故障关联（正式）：每次状态推进都追加一行，历史不依赖内存窗口。
                faultGroupWriter = NewWriter(Path.Combine(RootDir, "FAULT关联.csv"));
                WriteCsv(faultGroupWriter, new string[] {
                    "at", "device_segment", "device_confirmed", "boot_segment", "boot_confirmed", "id",
                    "group_state", "missing", "conflict_reason",
                    "current_seq", "previous_seq", "current_raw", "previous_raw",
                    "current_count", "previous_count", "current_distinct", "previous_distinct",
                    "dropped_groups_notice" });
                faultGroupStateWriter = NewWriter(Path.Combine(RootDir, "FAULT关联状态变化.csv"));
                WriteCsv(faultGroupStateWriter, new string[] { "at", "device_segment", "boot_segment", "id",
                    "from_state", "to_state", "reason" });
                pendingWriter = NewWriter(Path.Combine(RootDir, "待归属分段关联.csv"));
                WriteCsv(pendingWriter, new string[] { "at", "pending_segment", "resolution",
                    "device_segment", "role", "mac" });
                WriteSessionInfo(false);
            }
            catch (Exception ex)
            {
                failed = true;
                failureReason = "初始化记录文件失败：" + ex.Message;
            }
        }

        private void WriteSessionInfo(bool stopped)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("格式版本：2");
            sb.AppendLine("会话：" + SessionId);
            sb.AppendLine("开始（带时区）：" + StartedAt);
            if (stopped)
            {
                sb.AppendLine("停止（带时区）：" + StoppedAt);
                sb.AppendLine("停止原因：" + StopReason);
            }
            sb.AppendLine("时区：" + TimeZoneInfo.Local.Id);
            sb.AppendLine("接收块数：" + (nextSequence - 1).ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("设备分段数：" + segments.Count.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("采集失败：" + (failed ? failureReason : "无"));
            sb.AppendLine("说明：完整证据以本目录下的 CSV 与 raw 文件为准；工具只保存实际接收到的数据。");
            sb.AppendLine("说明：板上 ms、boot 与电脑接收时间分别保存，不混成一个时间轴。");
            File.WriteAllText(Path.Combine(RootDir, "会话信息.txt"), sb.ToString(), new UTF8Encoding(false));
        }

        public bool Failed { get { return failed; } }
        public string FailureReason { get { return failureReason; } }
        public long LastSequence { get { return nextSequence - 1; } }
        public int SegmentCount { get { lock (gate) { return segments.Count; } } }
        public bool IsClosed { get { lock (gate) { return closed; } } }
        public IList<string> OverflowNotices
        {
            get
            {
                lock (gate)
                {
                    List<string> all = new List<string>(globalAssembler.OverflowNotices);
                    foreach (KeyValuePair<string, StreamingLineAssembler> kv in assemblers)
                        all.AddRange(kv.Value.OverflowNotices);
                    return all;
                }
            }
        }
        public IList<ReceiveIndexEntry> RecentIndex { get { lock (gate) { return recentIndex.ToArray(); } } }
        public IList<string> RecentLines { get { lock (gate) { return recentLines.ToArray(); } } }
        public IList<DiagnosticMarker> Markers { get { lock (gate) { return markers.ToArray(); } } }
        public IList<CaptureFault> Faults { get { lock (gate) { return faults.ToArray(); } } }

        public string SegmentDir(string segmentId)
        {
            return Path.Combine(RootDir, "设备分段", segmentId);
        }

        private static StreamWriter NewWriter(string path)
        {
            return new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite),
                new UTF8Encoding(false));
        }

        // RFC4180：逗号、双引号、换行都安全转义。
        private static void WriteCsv(StreamWriter w, string[] fields)
        {
            if (w == null) return;
            for (int i = 0; i < fields.Length; i++)
            {
                if (i > 0) w.Write(',');
                string f = fields[i] ?? "";
                bool quote = f.IndexOf(',') >= 0 || f.IndexOf('"') >= 0 ||
                             f.IndexOf('\n') >= 0 || f.IndexOf('\r') >= 0;
                if (quote) w.Write('"');
                w.Write(f.Replace("\"", "\"\""));
                if (quote) w.Write('"');
            }
            w.WriteLine();
            w.Flush();
        }

        private CaptureSegment GetSegment(string id, string role, string mac)
        {
            CaptureSegment seg;
            if (segments.TryGetValue(id, out seg)) return seg;
            string dir = Path.Combine(RootDir, "设备分段", id);
            Directory.CreateDirectory(dir);
            seg = new CaptureSegment();
            seg.Id = id;
            seg.Role = role ?? "";
            seg.Mac = mac ?? "";
            seg.OpenedAt = DiagTime.Now(DateTime.Now);
            seg.Raw = new FileStream(Path.Combine(dir, "raw.bin"), FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
            StreamWriter tw = new StreamWriter(new FileStream(Path.Combine(dir, "解码文本.log"), FileMode.Create,
                FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false));
            tw.AutoFlush = true;
            seg.Text = tw;
            segments[id] = seg;
            return seg;
        }

        // 接收一块原始字节：整块写入指定分段的 raw，再把新产生的完整行交给调用者解析。
        // 返回本次新产生的完整行（按顺序）；解析由控制器完成，保证“落盘优先”。
        public List<string> AppendAndCollectLines(byte[] buffer, int count, DateTime receivedLocal,
                                                  string segmentId, string role, string mac, string bootBefore)
        {
            List<string> lines = new List<string>();
            if (count <= 0) return lines;
            lock (gate)
            {
                if (disposed || closed || failed) return lines;
                try
                {
                    long seq = nextSequence++;
                    byte[] copy = new byte[count];
                    Buffer.BlockCopy(buffer, 0, copy, 0, count);

                    CaptureSegment seg = GetSegment(segmentId, role, mac);
                    seg.Raw.Write(copy, 0, count);
                    seg.Raw.Flush();
                    string sha = Sha256Hex(copy);

                    ReceiveIndexEntry entry = new ReceiveIndexEntry();
                    entry.Sequence = seq;
                    entry.ReceivedAt = DiagTime.Now(receivedLocal);
                    entry.SessionId = SessionId;
                    entry.DeviceSegment = segmentId;
                    entry.BootSegment = bootBefore ?? "";
                    entry.FileName = "设备分段/" + segmentId + "/raw.bin";
                    entry.ByteOffset = seg.Offset;
                    entry.ByteLength = count;
                    entry.ChunkSha256 = sha;
                    seg.Offset += count;
                    seg.ChunkCount++;

                    recentIndex.Add(entry);
                    if (recentIndex.Count > RecentIndexWindow) recentIndex.RemoveAt(0);

                    WriteCsv(indexWriter, new string[] {
                        seq.ToString(CultureInfo.InvariantCulture), entry.ReceivedAt, SessionId,
                        segmentId, entry.BootSegment, entry.FileName,
                        entry.ByteOffset.ToString(CultureInfo.InvariantCulture),
                        count.ToString(CultureInfo.InvariantCulture), sha });

                    StreamingLineAssembler asm = AssemblerFor(segmentId);
                    List<string> produced = asm.Feed(copy, count);
                    pendingTailText = asm.PendingText;
                    foreach (string line in produced)
                    {
                        recentLines.Add(line);
                        if (recentLines.Count > RecentLineWindow) recentLines.RemoveAt(0);
                        seg.Text.WriteLine("[" + seq.ToString(CultureInfo.InvariantCulture) + "] " + line);
                        lines.Add(line);
                    }
                    if (produced.Count > 0) seg.Text.Flush();
                    // 残行在采集期间也写入解码文本，明确标记“未完成”，不参与解析。
                    if (asm.HasPending)
                    {
                        try
                        {
                            seg.Text.WriteLine("[残行-未完成] " + asm.PendingText);
                            seg.Text.Flush();
                        }
                        catch { }
                    }
                    return lines;
                }
                catch (Exception ex)
                {
                    failed = true;
                    failureReason = "写入记录文件失败：" + ex.Message + "（已保存到第 " + (nextSequence - 1) +
                                    " 块；之后的数据未保存）";
                    CapFault("io", failureReason, DateTime.Now);
                    return lines;
                }
            }
        }

        // 当前尚未成行的尾部内容（用于“无结尾换行的内容”在采集期间也可见）。
        private string pendingTailText = "";
        public string PendingTailText { get { lock (gate) { return pendingTailText; } } }

        // ===== 待归属接收分段（重连识别期间） =====
        //
        // 重连时还不知道刚插上的是哪块板。先把每次 Read 返回的字节原样写进一个
        // 唯一命名、带“待归属”前缀的分段；身份确认后只写关联元数据，
        // 不移动/重写 raw，也不重新分配接收时间。
        public string BeginPendingSegment(DateTime local)
        {
            lock (gate)
            {
                string id = "待归属-" + (nextSequence).ToString(CultureInfo.InvariantCulture) + "-" +
                            local.ToString("HHmmss", CultureInfo.InvariantCulture) + "-" +
                            Guid.NewGuid().ToString("N").Substring(0, 4);
                pendingSegmentId = id;
                GetSegment(id, "", "");
                try
                {
                    WriteCsv(pendingWriter, new string[] { DiagTime.Now(local), id, "opened", "", "", "" });
                }
                catch { }
                return id;
            }
        }

        public string PendingSegmentId { get { lock (gate) { return pendingSegmentId; } } }

        // 身份确认后的关联：只记录元数据（同 MAC / 新 MAC / 未确认）。
        public void AssociatePendingSegment(string pendingId, string resolution, string deviceSegment,
                                            string role, string mac, DateTime local)
        {
            lock (gate)
            {
                if (string.IsNullOrEmpty(pendingId)) return;
                // 确保被关联的目标设备分段真实存在（目录 + raw + 解码文本），
                // 后续字节才能继续写入该设备；待归属分段本身保持不动。
                if (!string.IsNullOrEmpty(deviceSegment) && !segments.ContainsKey(deviceSegment))
                    GetSegment(deviceSegment, role, mac);
                CaptureSegment seg;
                if (segments.TryGetValue(pendingId, out seg))
                {
                    seg.Role = role ?? "";
                    seg.Mac = mac ?? "";
                }
                try
                {
                    WriteCsv(pendingWriter, new string[] {
                        DiagTime.Now(local), pendingId, resolution, deviceSegment ?? "", role ?? "", mac ?? "" });
                }
                catch { }
            }
        }

        // 待归属分段当前已落盘的字节数（用于“确认身份后仍有后续字节”）。
        public long PendingBytes(string pendingId)
        {
            lock (gate)
            {
                CaptureSegment seg;
                if (pendingId != null && segments.TryGetValue(pendingId, out seg)) return seg.Offset;
                return 0;
            }
        }

        public void SetSegmentBoot(string segmentId, string boot)
        {
            lock (gate)
            {
                CaptureSegment seg;
                if (segments.TryGetValue(segmentId, out seg)) seg.Boot = boot ?? "";
            }
        }

        // 取出某分段尚未成行的尾部（用于切换设备时移交半行）。
        public string TakeSegmentTail(string segmentId)
        {
            lock (gate) { return AssemblerFor(segmentId).TakePending(); }
        }

        // 把半行原文写入目标分段的解码上下文（不再写 raw，raw 已在来源分段）。
        public void SeedSegmentTail(string segmentId, string tail)
        {
            if (string.IsNullOrEmpty(tail)) return;
            lock (gate) { AssemblerFor(segmentId).Seed(tail); }
        }

        // 结束“旧设备”的解码上下文：只清旧分段，其他分段（含待归属）保持不动。
        public void ClearDecoderState(string segmentId)
        {
            lock (gate)
            {
                StreamingLineAssembler a;
                if (!string.IsNullOrEmpty(segmentId) && assemblers.TryGetValue(segmentId, out a)) a.Reset();
                else globalAssembler.Reset();
            }
        }

        private static string Sha256Hex(byte[] data)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] h = sha.ComputeHash(data);
                StringBuilder sb = new StringBuilder(h.Length * 2);
                for (int i = 0; i < h.Length; i++) sb.Append(h[i].ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        public void AddMarker(MarkerKind kind, DateTime local, string segmentId)
        {
            lock (gate)
            {
                if (closed) return;
                DiagnosticMarker m = new DiagnosticMarker();
                m.Kind = kind;
                m.At = DiagTime.Now(local);
                m.SessionId = SessionId;
                m.DeviceSegment = segmentId;
                m.NearSequence = LastSequence;
                markers.Add(m);
                if (markers.Count > RecentIndexWindow) markers.RemoveAt(0);
                try
                {
                    WriteCsv(markerWriter, new string[] { MarkerText.Code(kind), m.At, SessionId, segmentId,
                        m.NearSequence.ToString(CultureInfo.InvariantCulture) });
                }
                catch (Exception ex) { CapFault("io", "写标记失败：" + ex.Message, local); }
            }
        }

        public void RecordIdentity(BoardIdentity id, string segmentId, DateTime local)
        {
            if (id == null) return;
            lock (gate)
            {
                try
                {
                    WriteCsv(identityWriter, new string[] {
                        DiagTime.Now(local), segmentId, id.RoleText, id.MacText, id.PeerText,
                        id.PairProtocol.ToString(CultureInfo.InvariantCulture),
                        id.SteeringAngle.ToString(CultureInfo.InvariantCulture),
                        id.HasFirmwareId ? id.FirmwareId : "", id.FirmwareVersion ?? "", id.RawLine ?? "" });
                }
                catch (Exception ex) { CapFault("io", "写身份快照失败：" + ex.Message, local); }
            }
        }

        public void RecordCount(CountSnapshot c)
        {
            if (c == null) return;
            lock (gate)
            {
                try
                {
                    WriteCsv(countWriter, new string[] {
                        c.ReceivedAt, c.DeviceSegment, c.BootSegment,
                        c.NearSequence.ToString(CultureInfo.InvariantCulture),
                        c.Parsed ? "1" : "0", c.BootRaw, c.MsRaw,
                        c.Ms.ToString(CultureInfo.InvariantCulture),
                        c.TotalRaw,
                        c.HasTotal ? c.Total.ToString(CultureInfo.InvariantCulture) : "",
                        c.Raw, c.ParseError });
                }
                catch (Exception ex) { CapFault("io", "写 COUNT 快照失败：" + ex.Message, DateTime.Now); }
            }
        }

        public void RecordFaultSnapshot(FaultSnapshot f)
        {
            if (f == null) return;
            lock (gate)
            {
                try
                {
                    WriteCsv(faultSnapshotWriter, new string[] {
                        f.ReceivedAt, f.DeviceSegment, f.BootSegment,
                        f.NearSequence.ToString(CultureInfo.InvariantCulture),
                        f.Kind, f.Id, f.State.ToString(), f.Note, f.Raw });
                }
                catch (Exception ex) { CapFault("io", "写 FAULT 快照失败：" + ex.Message, DateTime.Now); }
            }
        }

        // 故障关联：状态推进时立即落盘（追加行，不覆盖历史）。
        // 只在“该组状态发生变化”或“首次入组”时调用，避免每个周期重复写同一组。
        public void RecordFaultGroup(FaultGroup g, FaultGroupState fromState, string reason, DateTime local)
        {
            if (g == null) return;
            lock (gate)
            {
                try
                {
                    WriteCsv(faultGroupWriter, new string[] {
                        DiagTime.Now(local),
                        g.DeviceSegment, FaultDevice.IsConfirmed(g.DeviceSegment) ? "1" : "0",
                        g.BootSegment, FaultBoot.IsConfirmed(g.BootSegment) ? "1" : "0",
                        g.Id,
                        FaultText.Code(g.State), g.MissingText, g.ConflictReason,
                        SeqText(g.Current), SeqText(g.Previous),
                        JoinRaw(g.Current, g.CurrentDistinct, g.CurrentOverflow),
                        JoinRaw(g.Previous, g.PreviousDistinct, g.PreviousOverflow),
                        g.CurrentLines.ToString(CultureInfo.InvariantCulture),
                        g.PreviousLines.ToString(CultureInfo.InvariantCulture),
                        g.CurrentDistinct.Count.ToString(CultureInfo.InvariantCulture) +
                            (g.CurrentOverflow > 0 ? "+" + g.CurrentOverflow + "超出" : ""),
                        g.PreviousDistinct.Count.ToString(CultureInfo.InvariantCulture) +
                            (g.PreviousOverflow > 0 ? "+" + g.PreviousOverflow + "超出" : ""),
                        "" });
                    g.SnapshotWritten = true;
                }
                catch (Exception ex) { CapFault("io", "写 FAULT 关联失败：" + ex.Message, local); }
            }
        }

        // 该组该类型的全部不同内容原文（冲突时全部保留，可追溯），
        // 超出上限的只写计数，原文仍在 FAULT快照.csv。
        private static string JoinRaw(FaultSnapshot first, List<FaultSnapshot> distinct, int overflow)
        {
            StringBuilder sb = new StringBuilder();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            if (first != null && seen.Add(first.Raw)) sb.Append(first.Raw);
            foreach (FaultSnapshot f in distinct)
            {
                if (!seen.Add(f.Raw)) continue;
                if (sb.Length > 0) sb.Append(" || ");
                sb.Append(f.Raw);
            }
            if (overflow > 0)
            {
                if (sb.Length > 0) sb.Append(" || ");
                sb.Append("[另有 ").Append(overflow).Append(" 条不同内容未列出，原文见 FAULT快照.csv]");
            }
            return sb.ToString();
        }

        // 关联状态的迁移轨迹（从哪个状态到什么状态、原因、时间）。
        public void RecordFaultGroupTransition(FaultGroup g, FaultGroupState fromState,
                                               FaultGroupState toState, string reason, DateTime local)
        {
            if (g == null) return;
            lock (gate)
            {
                try
                {
                    WriteCsv(faultGroupStateWriter, new string[] {
                        DiagTime.Now(local), g.DeviceSegment, g.BootSegment, g.Id,
                        FaultText.Code(fromState), FaultText.Code(toState), reason ?? "" });
                }
                catch (Exception ex) { CapFault("io", "写 FAULT 关联状态变化失败：" + ex.Message, local); }
            }
        }

        private static string SeqText(FaultSnapshot f)
        {
            return f == null ? "" : f.NearSequence.ToString(CultureInfo.InvariantCulture);
        }

        // 登记表达到容量上限时明确记录，不丢弃原始证据、不伪称状态完整。
        public void RecordFaultCapacityNotice(int max, int dropped, DateTime local)
        {
            lock (gate)
            {
                try
                {
                    WriteCsv(faultGroupStateWriter, new string[] {
                        DiagTime.Now(local), "", "", "", "", "capacity_limit",
                        "关联登记表达到上限 " + max + " 组；此后新键未纳入内存关联（已累计 " + dropped +
                        " 组）。原始 FAULT 行仍全部在 FAULT快照.csv 与各类 raw 中，历史不会因此消失。" });
                }
                catch (Exception ex) { CapFault("io", "写容量限制说明失败：" + ex.Message, local); }
            }
        }

        // 停止/导出时把当前全部组的状态完整写一遍（保证最后一态一定在磁盘上，
        // 且不依赖内存窗口：导出时从这里恢复历史判定）。
        public void RecordFaultGroupFinal(FaultGroup g, DateTime local)
        {
            if (g == null) return;
            // 运行期已在每次状态变化时写入；这里只补写“内存里最后一态与已落盘不同”的组，
            // 避免同一组在停止时重复追加两行（导出按状态等级去重，不依赖重复次数）。
            if (g.LastLoggedState == g.State && g.SnapshotWritten) return;
            RecordFaultGroup(g, g.State, "停止时最终状态", local);
            g.SnapshotWritten = true;
        }

        public void RecordQuery(string command, string result, DateTime local)
        {
            lock (gate)
            {
                try { WriteCsv(queryWriter, new string[] { DiagTime.Now(local), command, result }); }
                catch (Exception ex) { CapFault("io", "写查询记录失败：" + ex.Message, local); }
            }
        }

        public void RecordEvent(string text, DateTime local)
        {
            lock (gate)
            {
                try { WriteCsv(eventWriter, new string[] { DiagTime.Now(local), text ?? "" }); }
                catch (Exception ex) { CapFault("io", "写事件失败：" + ex.Message, local); }
            }
        }

        public void RecordFault(string kind, string detail, DateTime local)
        {
            CapFault(kind, detail, local);
        }

        private void CapFault(string kind, string detail, DateTime local)
        {
            CaptureFault f = new CaptureFault { At = DiagTime.Now(local), Kind = kind ?? "", Detail = detail ?? "" };
            faults.Add(f);
            if (faults.Count > RecentIndexWindow) faults.RemoveAt(0);
            try { WriteCsv(faultWriter, new string[] { f.At, f.Kind, f.Detail }); } catch { }
        }

        // 停止时保存残行：保留原文并明确标记不完整，不补造字段。
        public string FinalizePendingLine(string segmentId, DateTime local)
        {
            lock (gate)
            {
                if (closed) return null;
                string pending = AssemblerFor(segmentId).TakePending();
                if (pending == null) return null;
                CaptureSegment seg;
                if (segments.TryGetValue(segmentId, out seg))
                {
                    try
                    {
                        seg.Text.WriteLine("[残行-不完整] " + pending);
                        seg.Text.Flush();
                    }
                    catch { }
                }
                try { WriteCsv(eventWriter, new string[] { DiagTime.Now(local), "停止时存在残行（未完成一行，未参与解析）：" + pending }); }
                catch { }
                return pending;
            }
        }

        // 关闭：尽力执行每一步，某步失败也继续后续清理。
        public List<string> Close(DateTime local, string reason)
        {
            List<string> errors = new List<string>();
            lock (gate)
            {
                if (closed) return errors;
                closed = true;
                StoppedAt = DiagTime.Now(local);
                StopReason = reason ?? "";
                foreach (KeyValuePair<string, CaptureSegment> kv in segments)
                {
                    try { if (kv.Value.Raw != null) { kv.Value.Raw.Flush(); kv.Value.Raw.Dispose(); kv.Value.Raw = null; } }
                    catch (Exception ex) { errors.Add("关闭 " + kv.Key + " raw 失败：" + ex.Message); }
                    try { if (kv.Value.Text != null) { kv.Value.Text.Flush(); kv.Value.Text.Dispose(); kv.Value.Text = null; } }
                    catch (Exception ex) { errors.Add("关闭 " + kv.Key + " 文本失败：" + ex.Message); }
                }
                StreamWriter[] writers = new StreamWriter[] {
                    indexWriter, markerWriter, faultWriter, eventWriter,
                    identityWriter, countWriter, faultSnapshotWriter, queryWriter, pendingWriter,
                    faultGroupWriter, faultGroupStateWriter };
                string[] names = new string[] { "接收索引", "异常标记", "采集错误", "事件",
                    "身份快照", "COUNT快照", "FAULT快照", "查询记录", "待归属分段关联",
                    "FAULT关联", "FAULT关联状态变化" };
                for (int i = 0; i < writers.Length; i++)
                {
                    try { if (writers[i] != null) { writers[i].Flush(); writers[i].Dispose(); } }
                    catch (Exception ex) { errors.Add("关闭 " + names[i] + " 失败：" + ex.Message); }
                }
                indexWriter = null; markerWriter = null; faultWriter = null; eventWriter = null;
                identityWriter = null; countWriter = null; faultSnapshotWriter = null; queryWriter = null;
                pendingWriter = null; faultGroupWriter = null; faultGroupStateWriter = null;
                try { WriteSessionInfo(true); }
                catch (Exception ex) { errors.Add("写会话信息失败：" + ex.Message); }
            }
            return errors;
        }

        public void Flush()
        {
            lock (gate)
            {
                if (closed) return;
                try
                {
                    foreach (KeyValuePair<string, CaptureSegment> kv in segments)
                    {
                        if (kv.Value.Raw != null) kv.Value.Raw.Flush();
                        if (kv.Value.Text != null) kv.Value.Text.Flush();
                    }
                }
                catch (Exception ex)
                {
                    failed = true;
                    failureReason = "刷新记录文件失败：" + ex.Message;
                }
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            try { Close(DateTime.Now, "释放会话"); } catch { }
        }
    }
}
