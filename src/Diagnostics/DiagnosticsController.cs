using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace BoatTool
{
    public interface IDiagChannel
    {
        void Open(string port);
        void Close();
        bool IsOpen { get; }
        int Read(byte[] buffer, int offset, int count);
        void Write(string text);
    }

    public interface IDiagClock
    {
        DateTime Now { get; }
        void Sleep(int milliseconds);
    }

    public sealed class SystemDiagClock : IDiagClock
    {
        public DateTime Now { get { return DateTime.Now; } }
        public void Sleep(int ms) { System.Threading.Thread.Sleep(ms); }
    }

    // 查询调度：只在接口白名单确认后才发 COUNT / FAULT。
    public class QueryPolicy
    {
        public bool FormatConfirmed;
        // 按角色分别批准：获批 r10 快照里只有小船实现 COUNT/FAULT，
        // 遥控器 servicePairing 不传 diagHooks，会回 UNSUPPORTED_COMMAND。
        public HashSet<string> BoatFwids = new HashSet<string>(StringComparer.Ordinal);
        public HashSet<string> RemoteFwids = new HashSet<string>(StringComparer.Ordinal);
        public int PeriodMs = 5000;

        // 兼容旧测试用法：写入 BoatFwids。
        public HashSet<string> AllowedFwids { get { return BoatFwids; } }

        public bool Allows(string fwid, BoardRole role)
        {
            if (!FormatConfirmed) return false;
            if (string.IsNullOrEmpty(fwid)) return false;
            if (role == BoardRole.Boat) return BoatFwids.Contains(fwid);
            if (role == BoardRole.Remote) return RemoteFwids.Contains(fwid);
            return false;
        }

        public bool Allows(string fwid)
        {
            return Allows(fwid, BoardRole.Boat);
        }

        // 获批套装的能力表：逐条按 FWID + 角色登记，不用“前缀是 JAYFWB11 就全放行”的宽泛规则。
        //
        //   r10 小船 JAYFWB10-...-OS600-Cr10-diagshared  COUNT ✓ FAULT ✓
        //   r11 小船 JAYFWB11-...-OSOFF-Cr11-osoff       COUNT ✓ FAULT ✓
        //   遥控器（r10 / r11 均未实现 diagHooks）        无（零自动查询）
        public static QueryPolicy ApprovedR11()
        {
            QueryPolicy p = new QueryPolicy();
            p.FormatConfirmed = true;
            p.BoatFwids.Add("JAYFWB10-S20-A70-110-U1278-1500-1722-R590-OS600-Cr10-diagshared");
            p.BoatFwids.Add("JAYFWB11-S20-A70-110-U1278-1500-1722-R590-OSOFF-Cr11-osoff");
            // RemoteFwids 保持为空：两端遥控器固件都没有实现 COUNT/FAULT。
            return p;
        }

        // 兼容旧名：仍返回同一张表（r10 + r11），避免出现两套不一致的批准列表。
        public static QueryPolicy ApprovedR10()
        {
            return ApprovedR11();
        }
    }

    // 采集控制器：状态机 + 落盘 + 分段 + 串行查询 + 标记。
    public class DiagnosticsController
    {
        // 结果窗口（固定上限，完整证据在磁盘）
        public const int ResultWindow = 500;

        private readonly AppPaths paths;
        private readonly IDiagChannel channel;
        private readonly IDiagClock clock;
        private readonly QueryPolicy policy;
        private readonly Action<string> log;
        private IPortLockRegistry locks;

        private CaptureSession session;
        private DiagState state = DiagState.NotConnected;
        private string port = "";
        private BoardIdentity identity;
        private string deviceSegment = "未识别";
        private string currentBoot = "";
        private DateTime recordingStartLocal;
        private DateTime lastRoundLocal = DateTime.MinValue;

        // 串行查询状态：同一时刻最多一条在途。
        private bool queryInFlight;
        private string inFlightCommand = "";
        private DateTime inFlightSentAt = DateTime.MinValue;
        private bool faultRoundPending;   // 本轮 COUNT 已结束，待发 FAULT
        private readonly int queryTimeoutMs = 2500;
        private bool queryWriteFailed;

        private readonly List<CountSnapshot> counts = new List<CountSnapshot>();
        private readonly List<FaultSnapshot> faultsSeen = new List<FaultSnapshot>();
        // 正式故障关联：控制器、测试、导出共用这一个登记表（不另写一套规则）。
        private readonly FaultRegistry faultRegistry;
        private readonly IFaultAdapter faultAdapter = new R10FaultAdapter();
        private int lastReportedDroppedGroups;
        private readonly List<string> events = new List<string>();
        private readonly List<string> sentQueries = new List<string>();
        private readonly List<string> sentQueryTimes = new List<string>();

        public DiagnosticsController(AppPaths paths, IDiagChannel channel, IDiagClock clock,
                                     QueryPolicy policy, Action<string> log)
        {
            this.paths = paths;
            this.channel = channel;
            this.clock = clock;
            this.policy = policy ?? new QueryPolicy();
            this.log = log;
            this.faultRegistry = new FaultRegistry(OnFaultGroupChanged);
        }

        // 关联状态变化：立即落盘 + 立即更新界面/日志（同一套规则，不重写）。
        private void OnFaultGroupChanged(FaultGroup g, FaultGroupState state, string reason)
        {
            g.LastLoggedState = state;
            if (session != null) session.RecordFaultGroup(g, state, reason, clock.Now);
            Log("故障关联[" + g.DeviceSegment + "/" + (FaultBoot.IsConfirmed(g.BootSegment) ? g.BootSegment : "boot未确认") +
                "/id=" + g.Id + "] " + reason);
            if (FaultGroupChanged != null) FaultGroupChanged(g, state);
        }

        // 界面订阅（只读展示，不改关联结果）。
        public event Action<FaultGroup, FaultGroupState> FaultGroupChanged;

        public IPortLockRegistry PortLocks { get { return locks; } set { locks = value; } }

        public DiagState State { get { return state; } }
        public string Port { get { return port; } }
        public BoardIdentity Identity { get { return identity; } }
        public CaptureSession Session { get { return session; } }
        public string DeviceSegment { get { return deviceSegment; } }
        public string CurrentBoot { get { return currentBoot; } }
        public string LastResetReason { get; private set; }
        public string LastRunState { get; private set; }
        public bool QueryWriteFailed { get { return queryWriteFailed; } }

        // 结果窗口（只读副本；固定上限）
        public IList<CountSnapshot> Counts { get { return counts.ToArray(); } }
        public IList<FaultSnapshot> FaultsSeen { get { return faultsSeen.ToArray(); } }
        public IList<string> Events { get { return events.ToArray(); } }
        public IList<string> SentQueries { get { return sentQueries.ToArray(); } }
        public IList<string> SentQueryTimes { get { return sentQueryTimes.ToArray(); } }

        public int CountSnapshotCount { get { return counts.Count; } }

        // 正式故障关联（同一份数据给界面与导出用）。
        public FaultRegistry Faults { get { return faultRegistry; } }
        public string FaultSummary { get { return faultRegistry.SummaryText(); } }
        public FaultSnapshot LatestFault
        {
            get { return faultsSeen.Count == 0 ? null : faultsSeen[faultsSeen.Count - 1]; }
        }

        private void Log(string text)
        {
            events.Add(text);
            if (events.Count > ResultWindow) events.RemoveAt(0);
            if (session != null) session.RecordEvent(text, clock.Now);
            if (log != null) log(text);
        }

        public void SetPort(string portName)
        {
            port = portName ?? "";
        }

        // 连接并核对版本：打开串口一次，只发 JAY INFO。
        // 使用持续缓冲 + 流式组装：半行保留，多行逐条处理，不丢内容。
        public bool ConnectAndIdentify()
        {
            if (string.IsNullOrEmpty(port))
            {
                Log("没有选择串口。");
                return false;
            }
            if (state == DiagState.Recording)
            {
                Log("记录中，忽略重复连接请求。");
                return true;
            }
            // 核心层保护：已有未关闭会话时，普通连接一律转为“重连”，
            // 不重置识别上下文、不替换会话（防止调用方绕过界面造成旧会话悬空）。
            if (session != null && !session.IsClosed &&
                (state == DiagState.Disconnected || state == DiagState.WaitingReconnect))
            {
                Log("检测到未关闭会话，转为重连原会话。");
                return Reconnect();
            }
            try
            {
                channel.Open(port);
                Log("已打开串口（DTR=false, RTS=false）：" + port);
            }
            catch (Exception ex)
            {
                state = DiagState.ConnectFailed;
                Log("打开串口失败：" + ex.Message);
                return false;
            }
            return IdentifyOnce("连接");
        }

        // 只查询身份；调用者负责已经 Open。
        private string preservedSegmentForSameMac = "";
        private string preservedMacForSameMac = "";
        // 重连识别后仍在使用“待归属”分段承接半行；确认该段活动归属后清空。
        private string activePendingSegment = "";
        // 待归属分段是否仍承接后续字节（一旦该段被关闭/停止则为 false）。
        private bool pendingSegmentOpen = false;

        // 识别身份（重连路径的完整分段生命周期）。
        //
        // 顺序严格按复审要求：
        //   1) 固定旧分段 ID（调用方传入）；
        //   2) 旧分段残行写回旧分段并标记不完整；
        //   3) 结束旧解码上下文；
        //   4) 新建待归属分段 + 新解码上下文；
        //   5) 每次 Read 先落盘（原始时间/序号/偏移/哈希）；
        //   6) 身份未确认前不用旧 deviceSegment/MAC/boot 解析新行；
        //   7) 确认后建立关联；
        //   8) 用新设备上下文回放本批已落盘行（不重复写 raw）；
        //   9) 新半行留在新上下文，交给后续 Pump；
        //  12) 失败保留待归属 raw 与索引，不猜归属。
        private bool IdentifyOnce(string phase)
        {
            identity = null;
            bool persist = session != null && !session.IsClosed;

            // 1–3) 旧设备边界：先把旧残行写回旧分段，再清掉旧解码上下文。
            string oldSegment = deviceSegment;
            if (persist)
            {
                try { session.FinalizePendingLine(oldSegment, clock.Now); } catch { }
                session.ClearDecoderState(oldSegment);
            }

            // 4) 新的待归属分段（独立 raw 与独立解码上下文）。
            string pendingId = persist ? session.BeginPendingSegment(clock.Now) : null;
            StreamingLineAssembler asm = new StreamingLineAssembler();
            List<string> bufferedLines = new List<string>();
            BoardIdentity found = null;
            bool foundSameMac = false;
            try
            {
                channel.Write("JAY INFO\n");
                Log(phase + "：已发送一次 JAY INFO（只读）");
                DateTime deadline = clock.Now.AddSeconds(4);
                byte[] buf = new byte[512];
                while (clock.Now < deadline)
                {
                    int n;
                    try { n = channel.Read(buf, 0, buf.Length); }
                    catch (Exception ex) { Log(phase + "：读取异常：" + ex.Message); break; }
                    if (n <= 0) { clock.Sleep(20); continue; }

                    // 5) 先落盘（原始字节 + 索引）。全程归属待归属分段，不写旧分段。
                    List<string> lines;
                    if (persist)
                    {
                        // 用空 boot：身份未确认前不继承旧 boot。
                        lines = session.AppendAndCollectLines(buf, n, clock.Now, pendingId, "", "", "");
                        if (session.Failed) { Log(phase + "：落盘失败，停止识别。"); break; }
                    }
                    else lines = asm.Feed(buf, n);

                    foreach (string line in lines)
                    {
                        bufferedLines.Add(line);
                        if (found == null)
                        {
                            BoardIdentity id = IdentityParser.FromLine(line);
                            if (id != null)
                            {
                                found = id;
                                foundSameMac = !string.IsNullOrEmpty(preservedSegmentForSameMac) &&
                                    string.Equals(id.Mac, preservedMacForSameMac, StringComparison.Ordinal);
                            }
                        }
                    }
                    if (found != null) break;
                }

                if (found == null)
                {
                    state = DiagState.ConnectFailed;
                    if (pendingId != null)
                        session.AssociatePendingSegment(pendingId, "unconfirmed", "", "", "", clock.Now);
                    Log(phase + "：未读到完整身份行，连接未完成（不伪造成功）。");
                    return false;
                }

                // 7) 建立关联（只写元数据，不动 raw）。
                string target = foundSameMac ? preservedSegmentForSameMac : SegmentName(found);
                if (pendingId != null)
                    session.AssociatePendingSegment(pendingId, foundSameMac ? "same_mac" : "new_mac",
                        target, found.RoleText, found.Mac, clock.Now);

                // 8) 切换设备上下文：新设备不能继承旧 boot/计数/故障关联。
                identity = found;
                deviceSegment = target;
                currentBoot = "";
                if (session != null) session.RecordIdentity(found, deviceSegment, clock.Now);

                // 9) 用新设备上下文回放本批已落盘行（身份行之后的部分才进入业务解析）。
                bool afterIdentity = false;
                foreach (string line in bufferedLines)
                {
                    if (!afterIdentity)
                    {
                        if (IdentityParser.FromLine(line) != null) { afterIdentity = true; }
                        continue;
                    }
                    // 业务行以新设备上下文解析；boot 变化按正常逻辑处理。
                    HandleLineInNewContext(line);
                }

                // 10–11) 身份之后尚未完成的新半行必须保留，交给后续 Pump 补齐。
                //   把待归属分段里尚未成行的尾部原样移交给已确认设备分段，
                //   不丢、不重复写 raw（raw 已在待归属分段）。
                if (pendingId != null)
                {
                    string tail = session.TakeSegmentTail(pendingId);
                    if (!string.IsNullOrEmpty(tail)) session.SeedSegmentTail(deviceSegment, tail);
                    session.SetActiveSegment(deviceSegment);
                }
                activePendingSegment = "";
                pendingSegmentOpen = false;
                state = DiagState.Connected;
                Log("身份：" + found.MacText + " / " + found.RoleText + " / FWID=" +
                    (found.HasFirmwareId ? found.FirmwareId : "无"));
                return true;
            }
            catch (Exception ex)
            {
                state = DiagState.ConnectFailed;
                if (pendingId != null && session != null && !session.IsClosed)
                {
                    try { session.AssociatePendingSegment(pendingId, "unconfirmed", "", "", "", clock.Now); }
                    catch { }
                }
                Log(phase + "：读取身份异常：" + ex.Message);
                return false;
            }
        }

        // 新设备上下文中的业务行处理（与常规 HandleLine 相同，但明确以当前 deviceSegment 为准）。
        private void HandleLineInNewContext(string line)
        {
            HandleLine(line);
        }

        private string SegmentName(BoardIdentity id)
        {
            string mac = string.IsNullOrEmpty(id.Mac) ? "MAC未知" : id.Mac.Replace(":", "");
            // 唯一分段标识：角色 + MAC + 打开时刻 + 随机后缀，避免仅按秒命名冲突。
            return id.RoleText + "-" + mac + "-" +
                   clock.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + "-" +
                   Guid.NewGuid().ToString("N").Substring(0, 4);
        }

        public bool StartRecording()
        {
            // 核心层保护：旧会话未关闭时绝不新建会话覆盖旧记录（不能只靠按钮禁用）。
            if (session != null && !session.IsClosed)
            {
                Log("上一会话尚未关闭（" + session.SessionId + "），已拒绝新建会话；" +
                    "请先停止并导出，或继续原会话。");
                return false;
            }
            if (state != DiagState.Connected)
            {
                Log("当前状态为“" + DiagStateText.Chinese(state) + "”，不能开始记录。");
                return false;
            }
            string sid = "诊断-" + clock.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" +
                         Guid.NewGuid().ToString("N").Substring(0, 6);
            recordingStartLocal = clock.Now;
            session = new CaptureSession(paths.WritableRoot, sid,
                identity == null ? "" : identity.RoleText,
                identity == null ? "" : identity.MacText, recordingStartLocal);
            if (session.Failed)
            {
                Log("开始记录失败：" + session.FailureReason);
                session = null;
                return false;
            }
            state = DiagState.Recording;
            countDisabled = false;
            faultDisabled = false;
            queryWriteFailed = false;
            if (locks != null) locks.Lock(port, "diagnostics:" + sid);
            lastRoundLocal = clock.Now;
            queryInFlight = false;
            inFlightCommand = "";
            faultRoundPending = false;
            if (identity != null) session.RecordIdentity(identity, deviceSegment, clock.Now);
            Log("开始记录，目录：" + session.RootDir);
            if (!policy.Allows(SafeFwid(), CurrentRole()))
                Log("该角色/固件的接口能力未确认：本次不自动发送 JAY COUNT / JAY FAULT，只被动记录。");
            return true;
        }

        // FAULT 行归属哪一段：重连后仍处于“待归属”承接期时，如实标为待归属，
        // 不冒用上一设备的 deviceSegment（否则新板故障会被归到旧板）。
        private string FaultDeviceSegment()
        {
            if (!string.IsNullOrEmpty(activePendingSegment) && pendingSegmentOpen)
                return activePendingSegment;
            return deviceSegment;
        }

        private string SafeFwid()
        {
            return identity != null && identity.HasFirmwareId ? identity.FirmwareId : "";
        }

        private BoardRole CurrentRole()
        {
            return identity == null ? BoardRole.Unknown : identity.Role;
        }

        // 收到 UNSUPPORTED_COMMAND 后，本连接停用相应查询（不再每 5 秒重发）。
        private bool countDisabled;
        private bool faultDisabled;

        public bool CountQueryDisabled { get { return countDisabled; } }
        public bool FaultQueryDisabled { get { return faultDisabled; } }

        // 采集一步：读串口 → 落盘 → 逐条解析本次新产生的完整行。
        public void Pump()
        {
            if (state != DiagState.Recording || session == null) return;
            byte[] buf = new byte[1024];
            // 一步内尽量读完当前可读数据（有上限，避免长时间占用 UI 线程）。
            for (int round = 0; round < 32; round++)
            {
                int n;
                try
                {
                    n = channel.Read(buf, 0, buf.Length);
                }
                catch (Exception ex)
                {
                    HandleDisconnect("串口读取异常：" + ex.Message);
                    return;
                }
                if (n <= 0) break;
                string bootBefore = currentBoot;
                // 重连后仍有待归属半行时，字节继续写入该分段；其行解析归属到已确认设备。
                string writeSegment = !string.IsNullOrEmpty(activePendingSegment) &&
                                      pendingSegmentOpen ? activePendingSegment : deviceSegment;
                List<string> lines = session.AppendAndCollectLines(buf, n, clock.Now,
                    writeSegment, identity == null ? "" : identity.RoleText,
                    identity == null ? "" : identity.MacText, bootBefore);
                if (session.Failed)
                {
                    Log("采集异常，已停止记录：" + session.FailureReason);
                    state = DiagState.Stopped;
                    if (locks != null) locks.Unlock(port);
                    return;
                }
                foreach (string line in lines) HandleLine(line);
            }
            PumpQueries();
        }

        // 单条完整行解析。纯被动识别，不发送任何命令。
        private void HandleLine(string line)
        {
            if (string.IsNullOrEmpty(line)) return;

            if (line.StartsWith("JAY COUNT", StringComparison.Ordinal))
            {
                CountSnapshot c = CountParser.Parse(line);
                c.ReceivedAt = DiagTime.Now(clock.Now);
                c.NearSequence = session == null ? 0 : session.LastSequence;
                c.DeviceSegment = deviceSegment;
                if (c.Parsed)
                {
                    if (!string.Equals(c.BootRaw, currentBoot, StringComparison.Ordinal))
                    {
                        if (currentBoot.Length > 0)
                            Log("检测到启动分段变化：" + currentBoot + " → " + c.BootRaw);
                        currentBoot = c.BootRaw;
                        if (session != null) session.SetSegmentBoot(deviceSegment, currentBoot);
                    }
                    c.BootSegment = currentBoot;
                }
                else
                {
                    c.BootSegment = currentBoot;
                    Log("COUNT 解析失败，已保留原文：" + c.ParseError);
                }
                counts.Add(c);
                if (counts.Count > ResultWindow) counts.RemoveAt(0);
                if (session != null) session.RecordCount(c);
                if (inFlightCommand == "JAY COUNT") { queryInFlight = false; inFlightCommand = ""; faultRoundPending = true; }
                return;
            }

            if (line.IndexOf("JAY ERROR=UNSUPPORTED_COMMAND", StringComparison.Ordinal) >= 0)
            {
                // 该角色不支持该查询：记录并停用本连接对应查询。
                if (inFlightCommand == "JAY COUNT" || line.IndexOf("COUNT", StringComparison.Ordinal) >= 0)
                    countDisabled = true;
                if (inFlightCommand == "JAY FAULT" || line.IndexOf("FAULT", StringComparison.Ordinal) >= 0)
                    faultDisabled = true;
                if (inFlightCommand == "JAY COUNT" || inFlightCommand == "JAY FAULT") faultRoundPending = false;
                queryInFlight = false;
                inFlightCommand = "";
                Log("板上报告不支持该只读查询（" + line.Trim() + "），本连接已停用对应查询。");
                if (session != null) session.RecordEvent("UNSUPPORTED_COMMAND：" + line.Trim(), clock.Now);
                return;
            }

            if (line.StartsWith("JAY FAULT", StringComparison.Ordinal) ||
                line.IndexOf("JAY FAULT", StringComparison.Ordinal) >= 0)
            {
                // 正式采集路径：原始字节 → 完整行 → 适配器解析 → 带分段/序号/时间
                // → 更新正式关联状态 → 落盘 → 界面。
                // 设备和 boot 都用“当前实际上下文”，待归属分段不冒充已确认设备。
                string faultDevice = FaultDeviceSegment();
                string faultBoot = currentBoot;
                FaultSnapshot f = faultAdapter.Parse(line, faultDevice, faultBoot);
                f.ReceivedAt = DiagTime.Now(clock.Now);
                f.NearSequence = session == null ? 0 : session.LastSequence;

                faultsSeen.Add(f);
                if (faultsSeen.Count > ResultWindow) faultsSeen.RemoveAt(0);

                if (f.Kind == "NONE")
                {
                    faultRegistry.NoteNoneReported(f, Log);
                    if (session != null) session.RecordFaultSnapshot(f);
                }
                else if (f.Kind == "CURRENT" || f.Kind == "PREVIOUS")
                {
                    // 门控：只有通过全部字段校验的 Parsed 行才允许进入关联登记表。
                    // 解析失败的行保留原文、落盘导出，但不补齐、不覆盖、不解除任何组。
                    FaultGroup g = null;
                    bool eligible = f.State == FaultAdapterState.Parsed && f.HasFields;
                    if (eligible) g = faultRegistry.Submit(f);
                    if (session != null) session.RecordFaultSnapshot(f);
                    if (!eligible)
                    {
                        Log("故障行解析失败（" + (string.IsNullOrEmpty(f.ParseFailureField)
                            ? "字段格式不符合协议" : "字段 " + f.ParseFailureField) +
                            "）：原文已保留，本条不作为有效故障证据，不参与关联。");
                    }
                    if (faultRegistry.DroppedGroups > lastReportedDroppedGroups)
                    {
                        lastReportedDroppedGroups = faultRegistry.DroppedGroups;
                        if (session != null)
                            session.RecordFaultCapacityNotice(FaultRegistry.MaxGroups,
                                faultRegistry.DroppedGroups, clock.Now);
                        Log("故障关联登记表已达上限 " + FaultRegistry.MaxGroups +
                            " 组：新键不再进入内存关联（已 " + faultRegistry.DroppedGroups +
                            " 组），原始行仍全部落盘。");
                    }
                }
                else
                {
                    if (session != null) session.RecordFaultSnapshot(f);
                    if (f.Kind == "UNKNOWN")
                        Log("未识别的 FAULT 行，已保留原文（不纳入关联）：" + line.Trim());
                }

                if (inFlightCommand == "JAY FAULT") { queryInFlight = false; inFlightCommand = ""; }
                return;
            }

            // 复位原因只按板子实际输出更新；没有输出就保持“未知”。
            if (line.IndexOf("BOOT RESET=", StringComparison.Ordinal) >= 0 ||
                line.IndexOf("reset_reason=", StringComparison.Ordinal) >= 0 ||
                line.StartsWith("RESET REASON", StringComparison.Ordinal))
            {
                LastResetReason = line.Trim();
                if (session != null) session.RecordEvent("复位原因：" + LastResetReason, clock.Now);
                return;
            }

            // 最近一次可识别运行状态：只认明确的运行行。
            if (line.StartsWith("link=", StringComparison.Ordinal))
            {
                LastRunState = line.Trim();
                return;
            }

            if (line.StartsWith("JAYv", StringComparison.Ordinal) || line.StartsWith("JAY ROLE=", StringComparison.Ordinal))
            {
                BoardIdentity id = IdentityParser.FromLine(line);
                if (id != null)
                {
                    identity = id;
                    if (session != null) session.RecordIdentity(id, deviceSegment, clock.Now);
                }
            }
        }

        // 串行查询：同一时刻最多一条在途；COUNT 结束或超时后才发 FAULT；
        // 每 PeriodMs 计划一轮，不补发过期轮次。
        private void PumpQueries()
        {
            if (state != DiagState.Recording) return;
            if (!policy.Allows(SafeFwid(), CurrentRole())) return;
            DateTime now = clock.Now;

            if (queryInFlight && (now - inFlightSentAt).TotalMilliseconds > queryTimeoutMs)
            {
                Log(inFlightCommand + " 查询超时，本次不重发，等待下一周期。");
                if (inFlightCommand == "JAY COUNT") faultRoundPending = true;
                queryInFlight = false;
                inFlightCommand = "";
            }

            if (queryInFlight) return;

            // 同一轮内的第二条：COUNT 已结束（收到回应或超时）才发 FAULT。
            if (faultRoundPending)
            {
                faultRoundPending = false;
                if (!faultDisabled) SendQuery("JAY FAULT", now);
                return;
            }

            if ((now - lastRoundLocal).TotalMilliseconds < policy.PeriodMs) return;
            lastRoundLocal = now;
            if (!countDisabled) SendQuery("JAY COUNT", now);
        }

        private void SendQuery(string command, DateTime now)
        {
            try
            {
                channel.Write(command + "\n");
            }
            catch (Exception ex)
            {
                queryWriteFailed = true;
                Log("查询写入失败：" + command + " —— " + ex.Message + "。已保留已有记录，后续查询停止。");
                if (session != null) session.RecordQuery(command, "write_failed:" + ex.Message, now);
                HandleDisconnect("查询写入失败：" + ex.Message);
                return;
            }
            queryInFlight = true;
            inFlightCommand = command;
            inFlightSentAt = now;
            sentQueries.Add(command);
            if (sentQueries.Count > ResultWindow) sentQueries.RemoveAt(0);
            sentQueryTimes.Add(DiagTime.Now(now));
            if (sentQueryTimes.Count > ResultWindow) sentQueryTimes.RemoveAt(0);
            if (session != null) session.RecordQuery(command, "sent", now);
        }

        public bool AddMarker(MarkerKind kind)
        {
            if (state != DiagState.Recording || session == null)
            {
                Log("未在记录中，标记未保存。");
                return false;
            }
            session.AddMarker(kind, clock.Now, deviceSegment);
            Log("已记录标记：" + MarkerText.Chinese(kind));
            return true;
        }

        private void HandleDisconnect(string reason)
        {
            if (session != null)
            {
                session.RecordFault("serial", reason, clock.Now);
                session.Flush();
            }
            Log("掉线：" + reason + "。已保留当前会话与全部已写入文件。");
            state = DiagState.Disconnected;
            queryInFlight = false;
            inFlightCommand = "";
            faultRoundPending = false;
            // 串口锁保留到用户停止记录。
            try { channel.Close(); } catch { }
        }

        // 仍有会话的状态：都允许停止。
        private static bool HoldsSession(DiagState s)
        {
            return s == DiagState.Recording || s == DiagState.Disconnected ||
                   s == DiagState.WaitingReconnect || s == DiagState.ConnectFailed;
        }

        // 停止：取消查询、刷新并关闭、释放本会话的锁；幂等；不删除会话。
        public List<string> StopRecording(string reason = "用户停止")
        {
            List<string> errors = new List<string>();
            if (session == null)
            {
                state = DiagState.Stopped;
                return errors;
            }
            if (session.IsClosed)
            {
                // 已停止过：不重复覆盖，只确保本会话的锁全部释放。
                try { ReleaseOwnedLocks(); } catch { }
                state = DiagState.Stopped;
                return errors;
            }

            state = DiagState.Stopped;
            queryInFlight = false;
            inFlightCommand = "";
            faultRoundPending = false;

            try { session.FinalizePendingLine(deviceSegment, clock.Now); }
            catch (Exception ex) { errors.Add("保存残行失败：" + ex.Message); }

            // 停止时把每个关联组的最终状态补写一遍：磁盘上的历史完整，
            // 导出不依赖内存窗口，也不会把曾经完整的组写回缺行。
            try
            {
                foreach (FaultGroup g in faultRegistry.Groups) session.RecordFaultGroupFinal(g, clock.Now);
                if (faultRegistry.DroppedGroups > 0)
                    session.RecordFaultCapacityNotice(FaultRegistry.MaxGroups,
                        faultRegistry.DroppedGroups, clock.Now);
            }
            catch (Exception ex) { errors.Add("写故障关联最终状态失败：" + ex.Message); }

            try
            {
                List<string> closeErrors = session.Close(clock.Now, reason);
                if (closeErrors != null) errors.AddRange(closeErrors);
            }
            catch (Exception ex) { errors.Add("关闭会话失败：" + ex.Message); }

            // 按 owner 释放本会话实际持有的全部锁（含迁移前后两个串口）。
            try { ReleaseOwnedLocks(); }
            catch (Exception ex) { errors.Add("释放串口锁失败：" + ex.Message); }
            activePendingSegment = "";
            pendingSegmentOpen = false;

            try { channel.Close(); } catch { }
            Log("已停止记录（" + reason + "），停止查询与采集。");
            return errors;
        }

        // 重连：只 Open 一次，然后查询身份；MAC 变化 → 新分段并清空旧状态。
        // 重连到指定串口（GUI 与核心共用这一个入口）。
        //
        // 事务顺序：
        //   1) 记录 owner / 旧活动串口 / 本会话已持有的锁集合；
        //   2) 目标 == 当前：只有锁属于本会话才继续；
        //   3) 目标 != 当前：先 TryAcquire；失败立即拒绝（不 Open、不发命令）；
        //   4) 连接 + 身份 + 分段全部成功后，切换活动串口并释放旧串口的本会话锁；
        //   5) 失败：关闭本次通道、回滚临时锁、保留原会话与证据。
        public bool Reconnect(string targetPort)
        {
            string newPort = string.IsNullOrEmpty(targetPort) ? port : targetPort;

            // 目标串口与当前不同：必须先拿到锁，再 Open。
            if (!string.Equals(newPort, port, StringComparison.OrdinalIgnoreCase))
            {
                if (locks != null && !locks.TryAcquire(newPort, OwnerKey()))
                {
                    Log("串口 " + newPort + " 正在被其他会话占用，重连已拒绝（未打开、未发送任何命令）。");
                    state = DiagState.WaitingReconnect;
                    return false;
                }
                acquiredTargetPort = newPort;
            }
            else if (locks != null && !locks.IsOwnedBy(newPort, OwnerKey()) && locks.IsLocked(newPort))
            {
                Log("串口 " + newPort + " 被其他会话占用，重连已拒绝。");
                return false;
            }

            bool ok = ReconnectCore(newPort);
            if (!ok)
            {
                // 5) 失败回滚：关闭本次通道；释放临时获得的目标锁（仅当目标 != 旧串口）。
                try { channel.Close(); } catch { }
                if (acquiredTargetPort != null && locks != null)
                {
                    locks.ReleaseIfOwned(acquiredTargetPort, OwnerKey());
                    acquiredTargetPort = null;
                }
                return false;
            }

            // 4) 成功：切换活动串口，释放旧串口上属于本会话的锁。
            if (!string.Equals(newPort, port, StringComparison.OrdinalIgnoreCase))
            {
                if (locks != null) locks.ReleaseIfOwned(port, OwnerKey());
                Log("活动串口已从 " + port + " 切换到 " + newPort + "；旧串口锁已释放。");
                session.RecordEvent("活动串口迁移：" + port + " → " + newPort, clock.Now);
                port = newPort;
            }
            acquiredTargetPort = null;
            return true;
        }

        // 旧签名：重连到当前串口。
        public bool Reconnect()
        {
            return Reconnect(port);
        }

        private string acquiredTargetPort;

        // 本会话的锁归属标识。
        private string OwnerKey()
        {
            return session == null ? "diagnostics" : "diagnostics:" + session.SessionId;
        }

        // 实际重连流程（不含锁迁移）。
        private bool ReconnectCore(string newPort)
        {
            if (state != DiagState.Disconnected && state != DiagState.WaitingReconnect) return false;
            state = DiagState.WaitingReconnect;
            BoardIdentity before = identity;
            preservedSegmentForSameMac = deviceSegment;
            preservedMacForSameMac = before == null ? "" : before.Mac;

            try { channel.Close(); } catch { }
            try
            {
                channel.Open(newPort);
                Log("重新打开串口：" + newPort);
            }
            catch (Exception ex)
            {
                Log("重新连接失败（打开串口）：" + ex.Message);
                return false;
            }

            if (!IdentifyOnce("重连"))
            {
                try { channel.Close(); } catch { }
                return false;
            }

            bool macChanged = before != null && identity != null && before.Mac != identity.Mac;
            if (macChanged)
            {
                currentBoot = "";
                Log("警告：重新连接后芯片 MAC 改变（" + before.Mac + " → " + identity.Mac +
                    "）。不会续接为同一块板，已建立新的设备分段：" + deviceSegment);
                session.RecordEvent("设备分段切换：" + preservedSegmentForSameMac + " → " + deviceSegment, clock.Now);
                session.RecordIdentity(identity, deviceSegment, clock.Now);
            }
            else
            {
                Log("已重新连接并重新读取身份（同一块板），继续记录同一会话。");
                session.RecordIdentity(identity, deviceSegment, clock.Now);
            }
            state = DiagState.Recording;
            countDisabled = false;
            faultDisabled = false;
            pendingSegmentOpen = true;
            preservedSegmentForSameMac = "";
            preservedMacForSameMac = "";
            return true;
        }

        // 释放本会话实际持有的全部锁（不能只看当前 port）。
        private void ReleaseOwnedLocks()
        {
            if (locks == null) return;
            string owner = OwnerKey();
            IList<string> held = locks.LockedPorts;
            foreach (string p in held)
            {
                try { locks.ReleaseIfOwned(p, owner); } catch { }
            }
        }
    }
}
