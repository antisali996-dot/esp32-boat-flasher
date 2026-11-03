using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace BoatTool
{
    // r10 正式 COUNT / FAULT 适配器（按获批快照 boat/diagnostic_commands.h、
    // boat/fault_evidence.h 的实际格式实现）。
    //
    // 能力按角色区分：获批快照里只有小船实现 COUNT/FAULT（遥控器 servicePairing
    // 不传 diagHooks，会回 JAY ERROR=UNSUPPORTED_COMMAND）。
    //
    // 本类只负责“一行怎么解析”。是否完整、是否冲突，由 FaultRegistry 统一判定，
    // 保证控制器、测试、导出使用同一套关联实现。

    // r10 正式 COUNT / FAULT 适配器（按获批快照 boat/diagnostic_commands.h、
    // boat/fault_evidence.h 的实际格式实现）。
    //
    // 能力按角色区分：获批快照里只有小船实现 COUNT/FAULT（遥控器 servicePairing
    // 不传 diagHooks，会回 JAY ERROR=UNSUPPORTED_COMMAND）。
    //
    // 本类只负责“一行怎么解析、字段是否合法”。是否完整、是否冲突，由 FaultRegistry
    // 统一判定，保证控制器、测试、导出使用同一套关联实现。
    public sealed class R10FaultAdapter : IFaultAdapter
    {
        public bool FormatConfirmed { get { return true; } }

        // 字段类型依据（只读已批准 r10 快照，不按字段名猜）：
        //
        //   boat/fault_evidence.h
        //     struct MeasureRecord {
        //       uint32_t ms; float rpm; int32_t pwm; uint32_t pulses;
        //       uint32_t elapsed; float integral;
        //     };
        //     struct FaultEvidence { uint32_t id; ... };
        //
        //   boat/fault_evidence.h → formatCurrent / formatPrevious
        //     "JAY FAULT CURRENT id=%lu name=%s ms=%lu rpm=%.2f pwm=%ld "
        //     "pulses=%lu elapsed=%lu integral=%.2f"
        //
        // 因此：id/ms/pulses/elapsed 为 uint32（无符号整数），pwm 为 int32（有符号整数），
        // rpm/integral 为 float 且按 %.2f 固定两位小数输出。
        //
        // 正则只做“形状”匹配；数值合法性由 Validate 用 InvariantCulture 逐字段判定，
        // 不靠异常后补零、截断文本或任何猜测。
        private static readonly Regex CurrentPattern = Build(true);
        private static readonly Regex PreviousPattern = Build(false);

        private static Regex Build(bool current)
        {
            string head = current
                ? @"^JAY FAULT CURRENT id=(?<id>\d{1,10}) name=(?<name>\S+) "
                : @"^JAY FAULT PREVIOUS id=(?<id>\d{1,10}) ";
            string body = @"ms=(?<ms>\d{1,10}) rpm=(?<rpm>-?\d{1,20}\.\d{2}) " +
                          @"pwm=(?<pwm>-?\d{1,10}) pulses=(?<pulses>\d{1,10}) " +
                          @"elapsed=(?<elapsed>\d{1,10}) integral=(?<integral>-?\d{1,20}\.\d{2})$";
            return new Regex(head + body, RegexOptions.Compiled);
        }

        public FaultSnapshot Parse(string line, string deviceSegment, string bootSegment)
        {
            FaultSnapshot s = new FaultSnapshot();
            s.Raw = line ?? "";
            s.DeviceSegment = deviceSegment ?? "";
            s.BootSegment = bootSegment ?? "";
            string text = s.Raw.Trim();

            if (text.Equals("JAY FAULT NONE", StringComparison.Ordinal))
            {
                s.Kind = "NONE";
                s.State = FaultAdapterState.NoneReported;
                s.Note = "板上报告无故障；不删除此前已记录故障。";
                return s;
            }
            if (text.StartsWith("JAY ERROR=UNSUPPORTED_COMMAND", StringComparison.Ordinal))
            {
                s.Kind = "UNSUPPORTED";
                s.State = FaultAdapterState.Unsupported;
                s.Note = "该角色不支持 FAULT 查询（本连接已停用）。";
                return s;
            }

            // 先判形：前缀都不对才是“待适配”。前缀对但字段非法是“解析失败” ——
            // 两者含义不同，不能混成一个状态。
            bool looksCurrent = text.StartsWith("JAY FAULT CURRENT", StringComparison.Ordinal);
            bool looksPrevious = text.StartsWith("JAY FAULT PREVIOUS", StringComparison.Ordinal);
            if (!looksCurrent && !looksPrevious)
            {
                s.Kind = "UNKNOWN";
                s.State = FaultAdapterState.Unadapted;
                s.Note = "未识别的故障行，已保留原文。";
                return s;
            }

            s.Kind = looksCurrent ? "CURRENT" : "PREVIOUS";
            Match m = looksCurrent ? CurrentPattern.Match(text) : PreviousPattern.Match(text);
            if (!m.Success)
            {
                s.State = FaultAdapterState.Malformed;
                s.HasFields = false;
                LocateBadField(text, s);
                s.Note = "解析失败：" + DescribeFailure(s) + "。原文已保留，不作为有效故障证据。";
                return s;
            }

            string field, reason;
            if (!Validate(m, s, out field, out reason))
            {
                s.State = FaultAdapterState.Malformed;
                s.HasFields = false;
                s.ParseFailureField = field;
                s.ParseFailureReason = reason;
                s.Note = "解析失败：" + DescribeFailure(s) + "。原文已保留，不作为有效故障证据。";
                return s;
            }

            s.State = FaultAdapterState.Parsed;
            s.Note = "已解析 " + s.Kind + " 行。";
            return s;
        }

        private static string DescribeFailure(FaultSnapshot s)
        {
            if (string.IsNullOrEmpty(s.ParseFailureField)) return "字段格式不符合 r10 协议";
            return "字段 " + s.ParseFailureField + " " + s.ParseFailureReason;
        }

        // 形状不符时定位第一个可疑字段，仅用于说明，不用于推测数值。
        private static void LocateBadField(string text, FaultSnapshot s)
        {
            string[] fields = new string[] { "id", "ms", "rpm", "pwm", "pulses", "elapsed", "integral" };
            for (int i = 0; i < fields.Length; i++)
            {
                int idx = text.IndexOf(" " + fields[i] + "=", StringComparison.Ordinal);
                if (idx < 0) continue;
                int start = idx + 1 + fields[i].Length + 1;
                int end = text.IndexOf(' ', start);
                string value = end < 0 ? text.Substring(start) : text.Substring(start, end - start);
                bool ok;
                if (fields[i] == "rpm" || fields[i] == "integral") ok = IsFixed2Shape(value);
                else if (fields[i] == "pwm") ok = IsInt32Shape(value);
                else ok = IsUInt32Shape(value);
                if (!ok)
                {
                    s.ParseFailureField = fields[i];
                    s.ParseFailureReason = "不是合法数值（" + value + "）";
                    return;
                }
            }
            s.ParseFailureField = "";
            s.ParseFailureReason = "字段数量、顺序或末尾有多余字符，不符合 r10 协议";
        }

        private static bool Validate(Match m, FaultSnapshot s, out string field, out string reason)
        {
            field = ""; reason = "";
            uint u;
            int i32;
            float f;

            if (!TryUInt32(m.Groups["id"].Value, out u)) { field = "id"; reason = "不是合法 uint32"; return false; }
            s.IdValue = u;
            if (!TryUInt32(m.Groups["ms"].Value, out u)) { field = "ms"; reason = "不是合法 uint32"; return false; }
            s.MsValue = u;
            if (!TryFixed2(m.Groups["rpm"].Value, out f)) { field = "rpm"; reason = "不是合法有限 float"; return false; }
            s.RpmValue = f;
            if (!TryInt32(m.Groups["pwm"].Value, out i32)) { field = "pwm"; reason = "不是合法 int32"; return false; }
            s.PwmValue = i32;
            if (!TryUInt32(m.Groups["pulses"].Value, out u)) { field = "pulses"; reason = "不是合法 uint32"; return false; }
            s.PulsesValue = u;
            if (!TryUInt32(m.Groups["elapsed"].Value, out u)) { field = "elapsed"; reason = "不是合法 uint32"; return false; }
            s.ElapsedValue = u;
            if (!TryFixed2(m.Groups["integral"].Value, out f)) { field = "integral"; reason = "不是合法有限 float"; return false; }
            s.IntegralValue = f;

            // 全部通过后才写入文本字段并置 HasFields：顺序保证失败时一定没有 HasFields。
            s.Id = m.Groups["id"].Value;
            s.Name = m.Groups["name"].Value;
            s.Ms = m.Groups["ms"].Value;
            s.Rpm = m.Groups["rpm"].Value;
            s.Pwm = m.Groups["pwm"].Value;
            s.Pulses = m.Groups["pulses"].Value;
            s.Elapsed = m.Groups["elapsed"].Value;
            s.Integral = m.Groups["integral"].Value;
            s.HasFields = true;
            return true;
        }

        // ---------- 字段级校验（全部 InvariantCulture） ----------

        private static bool IsUInt32Shape(string v)
        {
            if (string.IsNullOrEmpty(v)) return false;
            for (int i = 0; i < v.Length; i++) if (v[i] < '0' || v[i] > '9') return false;
            return true;
        }

        private static bool IsInt32Shape(string v)
        {
            if (string.IsNullOrEmpty(v)) return false;
            int start = v[0] == '-' ? 1 : 0;
            if (start >= v.Length) return false;
            for (int i = start; i < v.Length; i++) if (v[i] < '0' || v[i] > '9') return false;
            return true;
        }

        private static bool IsFixed2Shape(string v)
        {
            if (string.IsNullOrEmpty(v)) return false;
            int i = v[0] == '-' ? 1 : 0;
            int digits = 0;
            while (i < v.Length && v[i] >= '0' && v[i] <= '9') { i++; digits++; }
            if (digits == 0) return false;                  // 只有负号或空值
            if (i >= v.Length || v[i] != '.') return false;  // 必须是定点小数
            i++;
            int frac = 0;
            while (i < v.Length && v[i] >= '0' && v[i] <= '9') { i++; frac++; }
            if (frac != 2) return false;                    // r10 固定两位小数
            return i == v.Length;                           // 不允许任何多余字符
        }

        private static bool TryUInt32(string v, out uint value)
        {
            value = 0;
            if (!IsUInt32Shape(v)) return false;
            return uint.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out value);
        }

        private static bool TryInt32(string v, out int value)
        {
            value = 0;
            if (!IsInt32Shape(v)) return false;
            return int.TryParse(v, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
        }

        // rpm / integral 是 float，设备用 %.2f 输出。多个小数点、只有小数点、
        // 只有负号、空值、NaN、Infinity 都在形状阶段就被拒绝。
        private static bool TryFixed2(string v, out float value)
        {
            value = 0f;
            if (!IsFixed2Shape(v)) return false;
            if (!float.TryParse(v, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture, out value)) return false;
            if (float.IsNaN(value) || float.IsInfinity(value)) { value = 0f; return false; }
            return true;
        }
    }

    // boot 文本是否为“可用作关联键的已确认启动分段”。
    public static class FaultBoot
    {
        // 内部占位值：不能当作真实 boot 参与配对。
        private static readonly string[] Placeholders = new string[] {
            "boot-unknown", "unknown", "?", "-"
        };

        public static bool IsConfirmed(string boot)
        {
            if (string.IsNullOrEmpty(boot)) return false;
            string t = boot.Trim();
            if (t.Length == 0) return false;
            for (int i = 0; i < Placeholders.Length; i++)
                if (string.Equals(t, Placeholders[i], StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }
    }

    // 设备分段是否为“已确认属于某块具体板子”。
    public static class FaultDevice
    {
        public static bool IsConfirmed(string segment)
        {
            if (string.IsNullOrEmpty(segment)) return false;
            if (segment == "未识别") return false;
            if (segment.StartsWith("待归属", StringComparison.Ordinal)) return false;
            return true;
        }
    }

    public static class FaultGroupKey
    {
        // 关联键 = 设备分段 + 启动分段 + id（全部用原始文本，不做归一化猜测）。
        public static string Make(string device, string boot, string id)
        {
            return (device ?? "") + "|" + (boot ?? "") + "|" + (id ?? "");
        }
    }

    // 一个故障关联组：CURRENT + PREVIOUS 属于同一设备、同一已确认 boot、同一 id。
    public sealed class FaultGroup
    {
        public string DeviceSegment = "";
        public string BootSegment = "";
        public string Id = "";

        // 每种类型保存“内容互不相同”的代表行（原文），上限 MaxDistinctPerType；
        // 超出部分只计数（原文都在 FAULT快照.csv 与关联状态日志里）。
        public const int MaxDistinctPerType = 8;
        public readonly List<FaultSnapshot> CurrentDistinct = new List<FaultSnapshot>();
        public readonly List<FaultSnapshot> PreviousDistinct = new List<FaultSnapshot>();
        public int CurrentLines;
        public int PreviousLines;
        public int CurrentOverflow;
        public int PreviousOverflow;

        public FaultSnapshot Current;      // 首个不同内容的代表
        public FaultSnapshot Previous;
        public bool Conflicted;
        public string FirstSeenAt = "";
        public string LastChangeAt = "";
        public FaultGroupState LastLoggedState = (FaultGroupState)(-1);
        public bool SnapshotWritten;

        public FaultGroupState State
        {
            get
            {
                if (Conflicted) return FaultGroupState.Conflicting;
                if (!FaultDevice.IsConfirmed(DeviceSegment)) return FaultGroupState.DeviceUnconfirmed;
                if (!FaultBoot.IsConfirmed(BootSegment)) return FaultGroupState.BootUnconfirmed;
                if (string.IsNullOrEmpty(Id)) return FaultGroupState.Incomplete;
                if (Current != null && Previous != null) return FaultGroupState.Complete;
                return FaultGroupState.Incomplete;
            }
        }

        public bool Complete { get { return State == FaultGroupState.Complete; } }

        public string MissingText
        {
            get
            {
                if (Current != null && Previous != null) return "无缺失";
                if (Current == null && Previous == null) return "缺 CURRENT 与 PREVIOUS";
                if (Current == null) return "缺 CURRENT";
                return "缺 PREVIOUS";
            }
        }

        public string ConflictReason
        {
            get
            {
                if (!Conflicted) return "";
                List<string> parts = new List<string>();
                if (CurrentLines > 1 && CurrentDistinct.Count > 1)
                    parts.Add("CURRENT 出现 " + CurrentDistinct.Count + " 种不同内容");
                if (PreviousLines > 1 && PreviousDistinct.Count > 1)
                    parts.Add("PREVIOUS 出现 " + PreviousDistinct.Count + " 种不同内容");
                if (parts.Count == 0) parts.Add("同键同类型出现不同内容");
                return string.Join("；", parts.ToArray());
            }
        }

        public string Key { get { return FaultGroupKey.Make(DeviceSegment, BootSegment, Id); } }

        public string DescribeState()
        {
            FaultGroupState s = State;
            string text = FaultText.Chinese(s);
            if (s == FaultGroupState.Incomplete) text += "（" + MissingText + "）";
            if (s == FaultGroupState.Conflicting) text += "（" + ConflictReason + "）";
            return text;
        }
    }

    // 正式故障关联登记表：控制器、测试、导出共用这一套规则。
    //
    // 约束：
    //   * 每组每类型的候选行有固定上限，不随记录时长无限增长；完整历史在磁盘。
    //   * 冲突一旦锁存，后续旧行/另一类型行/NONE 都不会把它恢复成完整。
    //   * 重复的同内容行：原文照常保存，组内去重，不新增完整组数、不算冲突。
    //   * 超出 MaxGroups 的新键不进入内存，明确计数并记录；原文仍已落盘。
    public sealed class FaultRegistry
    {
        public const int MaxGroups = 2000;

        private readonly Dictionary<string, FaultGroup> groups =
            new Dictionary<string, FaultGroup>(StringComparer.Ordinal);
        private readonly List<FaultGroup> order = new List<FaultGroup>();
        private int droppedGroups;

        // 状态变化回调：立即落盘 + 更新界面。参数＝组、新状态、说明。
        private readonly Action<FaultGroup, FaultGroupState, string> onStateChange;

        public FaultRegistry(Action<FaultGroup, FaultGroupState, string> onStateChange)
        {
            this.onStateChange = onStateChange;
        }

        public int GroupCount { get { return order.Count; } }
        public int DroppedGroups { get { return droppedGroups; } }
        public bool AtCapacity { get { return order.Count >= MaxGroups; } }
        public IList<FaultGroup> Groups { get { return order.ToArray(); } }

        public FaultGroup Find(string device, string boot, string id)
        {
            FaultGroup g;
            return groups.TryGetValue(FaultGroupKey.Make(device, boot, id), out g) ? g : null;
        }

        // 提交一条已解析的 CURRENT/PREVIOUS 行，返回它所属的组（可能为 null：无 id、非两类型、或超容量）。
        public FaultGroup Submit(FaultSnapshot f)
        {
            if (f == null) return null;
            if (f.Kind != "CURRENT" && f.Kind != "PREVIOUS") return null;
            if (string.IsNullOrEmpty(f.Id)) return null;   // 无 id 不能关联

            string key = FaultGroupKey.Make(f.DeviceSegment, f.BootSegment, f.Id);
            FaultGroup g;
            if (!groups.TryGetValue(key, out g))
            {
                if (order.Count >= MaxGroups)
                {
                    droppedGroups++;
                    f.Note = f.Note + "（关联登记表已达上限 " + MaxGroups + "，该行未纳入内存关联；原文仍已落盘）";
                    return null;
                }
                g = new FaultGroup();
                g.DeviceSegment = f.DeviceSegment ?? "";
                g.BootSegment = f.BootSegment ?? "";
                g.Id = f.Id;
                g.FirstSeenAt = f.ReceivedAt;
                g.LastChangeAt = f.ReceivedAt;
                groups[key] = g;
                order.Add(g);
            }

            FaultGroupState before = g.State;
            bool wasConflicted = g.Conflicted;

            List<FaultSnapshot> distinct = f.Kind == "CURRENT" ? g.CurrentDistinct : g.PreviousDistinct;
            if (f.Kind == "CURRENT") g.CurrentLines++; else g.PreviousLines++;

            bool sameContent = false;
            foreach (FaultSnapshot prev in distinct)
            {
                if (string.Equals(prev.FieldSignature, f.FieldSignature, StringComparison.Ordinal))
                {
                    sameContent = true;
                    break;
                }
            }

            if (!sameContent)
            {
                if (distinct.Count >= FaultGroup.MaxDistinctPerType)
                {
                    if (f.Kind == "CURRENT") g.CurrentOverflow++; else g.PreviousOverflow++;
                }
                else
                {
                    distinct.Add(f);
                }
                if (distinct.Count > 1 || (f.Kind == "CURRENT" ? g.CurrentOverflow : g.PreviousOverflow) > 0)
                {
                    // 同键同类型出现不同内容 → 冲突锁存。
                    // 不静默选择第一条/最后一条/数值更大的那条作为完整证据。
                    g.Conflicted = true;
                    g.LastChangeAt = f.ReceivedAt;
                }
                else
                {
                    if (f.Kind == "CURRENT") g.Current = f; else g.Previous = f;
                    g.LastChangeAt = f.ReceivedAt;
                }
            }

            f.Group = g;

            FaultGroupState after = g.State;
            f.Note = "关联状态：" + g.DescribeState() + "（设备 " + DescribeDevice(g) + "，boot " +
                     (FaultBoot.IsConfirmed(g.BootSegment) ? g.BootSegment : "未确认") + "）";

            if (after != before || g.Conflicted != wasConflicted)
            {
                string reason = g.Conflicted
                    ? "状态变为冲突：" + g.ConflictReason
                    : "状态：" + g.DescribeState();
                if (onStateChange != null) onStateChange(g, after, reason);
            }
            return g;
        }

        private static string DescribeDevice(FaultGroup g)
        {
            return FaultDevice.IsConfirmed(g.DeviceSegment) ? g.DeviceSegment : "未确认";
        }

        // JAY FAULT NONE：只记录，不删除、覆盖或解除历史故障和冲突。
        public void NoteNoneReported(FaultSnapshot none, Action<string> log)
        {
            if (log != null)
                log("板上报告无故障（JAY FAULT NONE）：已单独记录，不删除此前故障与冲突证据。");
        }

        // 从已落盘的关联状态恢复历史（导出/重开用），保证近期窗口淘汰不影响历史判定。
        public void RestoreFrom(FaultGroup g)
        {
            if (g == null || string.IsNullOrEmpty(g.Id)) return;
            string key = g.Key;
            FaultGroup existing;
            if (groups.TryGetValue(key, out existing))
            {
                if (GroupStateRank(g.State) > GroupStateRank(existing.State))
                {
                    existing.Conflicted = g.Conflicted;
                    if (existing.Current == null) existing.Current = g.Current;
                    if (existing.Previous == null) existing.Previous = g.Previous;
                    if (existing.CurrentDistinct.Count == 0) existing.CurrentDistinct.AddRange(g.CurrentDistinct);
                    if (existing.PreviousDistinct.Count == 0) existing.PreviousDistinct.AddRange(g.PreviousDistinct);
                    existing.CurrentLines = Math.Max(existing.CurrentLines, g.CurrentLines);
                    existing.PreviousLines = Math.Max(existing.PreviousLines, g.PreviousLines);
                }
                return;
            }
            if (order.Count >= MaxGroups) { droppedGroups++; return; }
            groups[key] = g;
            order.Add(g);
        }

        private static int GroupStateRank(FaultGroupState s)
        {
            switch (s)
            {
                case FaultGroupState.Conflicting: return 4;
                case FaultGroupState.Complete: return 3;
                case FaultGroupState.Incomplete: return 2;
                case FaultGroupState.BootUnconfirmed: return 1;
                case FaultGroupState.DeviceUnconfirmed: return 0;
                default: return -1;
            }
        }

        public Dictionary<FaultGroupState, int> CountByState()
        {
            Dictionary<FaultGroupState, int> map = new Dictionary<FaultGroupState, int>();
            foreach (FaultGroup g in order)
            {
                FaultGroupState s = g.State;
                int n;
                map.TryGetValue(s, out n);
                map[s] = n + 1;
            }
            return map;
        }

        public string SummaryText()
        {
            Dictionary<FaultGroupState, int> map = CountByState();
            List<string> parts = new List<string>();
            FaultGroupState[] states = new FaultGroupState[] {
                FaultGroupState.Complete, FaultGroupState.Incomplete, FaultGroupState.Conflicting,
                FaultGroupState.BootUnconfirmed, FaultGroupState.DeviceUnconfirmed };
            foreach (FaultGroupState s in states)
            {
                int n;
                if (map.TryGetValue(s, out n) && n > 0)
                    parts.Add(FaultText.Chinese(s) + " " + n);
            }
            if (parts.Count == 0) return "暂无故障关联组";
            return string.Join("，", parts.ToArray()) + "（共 " + order.Count + " 组）";
        }
    }

    // 兼容层：保留旧的静态配对 API（测试与复审脚本使用）。
    // 内部完全委托给 FaultRegistry，不存在第二套关联规则。
    public static class FaultPairing
    {
        public sealed class Pair
        {
            public string DeviceSegment = "";
            public string BootSegment = "";
            public string Id = "";
            public FaultGroup Group;

            public FaultSnapshot Current { get { return Group == null ? null : Group.Current; } }
            public FaultSnapshot Previous { get { return Group == null ? null : Group.Previous; } }
            public bool Complete { get { return Group != null && Group.Complete; } }

            public string State
            {
                get
                {
                    if (Group == null) return "empty";
                    switch (Group.State)
                    {
                        case FaultGroupState.Complete: return "complete";
                        case FaultGroupState.Conflicting: return "conflicting";
                        case FaultGroupState.BootUnconfirmed: return "boot_unconfirmed";
                        case FaultGroupState.DeviceUnconfirmed: return "device_unconfirmed";
                        default: return "incomplete";
                    }
                }
            }
        }

        public static string Key(FaultSnapshot f)
        {
            return FaultGroupKey.Make(f.DeviceSegment, f.BootSegment, f.Id);
        }

        public static Dictionary<string, Pair> Build(IEnumerable<FaultSnapshot> snapshots)
        {
            FaultRegistry reg = new FaultRegistry(null);
            Dictionary<string, Pair> map = new Dictionary<string, Pair>(StringComparer.Ordinal);
            if (snapshots == null) return map;
            foreach (FaultSnapshot f in snapshots)
            {
                FaultGroup g = reg.Submit(f);
                if (g == null) continue;
                if (map.ContainsKey(g.Key)) continue;
                Pair p = new Pair();
                p.DeviceSegment = g.DeviceSegment;
                p.BootSegment = g.BootSegment;
                p.Id = g.Id;
                p.Group = g;
                map[g.Key] = p;
            }
            return map;
        }
    }
}
