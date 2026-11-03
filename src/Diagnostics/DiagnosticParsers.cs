using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace BoatTool
{
    // JAY COUNT boot=<启动标识> ms=<采样毫秒> total=<64位累计计数>
    public class CountSnapshot
    {
        public bool Parsed;
        public string Raw = "";
        public string BootRaw = "";          // 原样保存，不假定为整数
        public string MsRaw = "";
        public string TotalRaw = "";         // 原始十进制文本
        public long Ms = -1;
        public ulong Total;                  // 64 位无符号，不经过 double/int
        public bool HasTotal;
        public string ParseError = "";
        public string ReceivedAt = "";
        public long NearSequence;
        public string DeviceSegment = "";
        public string BootSegment = "";
    }

    public static class CountParser
    {
        private static readonly Regex LinePattern = new Regex(
            @"^\s*JAY\s+COUNT\b(?<rest>.*)$", RegexOptions.Compiled);

        // 只解析给定格式；任何缺字段/非法数字都保留原文并标记解析失败。
        public static CountSnapshot Parse(string line)
        {
            CountSnapshot snap = new CountSnapshot();
            snap.Raw = line ?? "";
            if (string.IsNullOrEmpty(line)) { snap.ParseError = "空行"; return snap; }
            Match m = LinePattern.Match(line);
            if (!m.Success) { snap.ParseError = "不是 JAY COUNT 行"; return snap; }

            Dictionary<string, string> fields = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string token in m.Groups["rest"].Value.Split(new char[] { ' ', '\t' },
                         StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = token.IndexOf('=');
                if (eq <= 0) { snap.ParseError = "字段缺少 '='：" + token; return snap; }
                fields[token.Substring(0, eq)] = token.Substring(eq + 1);
            }

            string boot, ms, total;
            if (!fields.TryGetValue("boot", out boot)) { snap.ParseError = "缺少 boot 字段"; return snap; }
            if (!fields.TryGetValue("ms", out ms)) { snap.ParseError = "缺少 ms 字段"; return snap; }
            if (!fields.TryGetValue("total", out total)) { snap.ParseError = "缺少 total 字段"; return snap; }

            // boot 原样保存；不假定它是整数，也不假定终身唯一。
            snap.BootRaw = boot;
            snap.MsRaw = ms;
            snap.TotalRaw = total;

            long msValue;
            if (!long.TryParse(ms, NumberStyles.Integer, CultureInfo.InvariantCulture, out msValue))
            {
                snap.ParseError = "ms 不是合法整数：" + ms;
                return snap;
            }
            snap.Ms = msValue;

            // 必须用 UInt64 解析：超过 long 范围但不超过 ulong 的值也要正确。
            ulong totalValue;
            if (!ulong.TryParse(total, NumberStyles.None, CultureInfo.InvariantCulture, out totalValue))
            {
                snap.ParseError = "total 不是合法 64 位无符号整数：" + total;
                return snap;
            }
            snap.Total = totalValue;
            snap.HasTotal = true;
            snap.Parsed = true;
            return snap;
        }
    }

    // 单条 FAULT 行的解析状态。区分“格式不认识”和“格式认识但证据不齐”，
    // 不再用一个 PendingAdapter 同时表示所有情况。
    public enum FaultAdapterState
    {
        Unadapted = 0,      // 待适配：未知格式或接口未确认
        Parsed = 1,         // 正式格式，全部字段通过合法性校验
        NoneReported = 2,   // JAY FAULT NONE
        Unsupported = 3,    // 该角色不支持 FAULT 查询
        Malformed = 4       // 格式对得上但某个必需数字字段非法（整行不作有效证据）
    }

    // 一个故障关联组的状态（由 FaultRegistry 维护）。
    public enum FaultGroupState
    {
        Incomplete = 0,          // 不完整：缺 CURRENT 或 PREVIOUS
        Complete = 1,            // 完整：两行齐全且关联条件全部满足
        Conflicting = 2,         // 冲突：同一关联键出现内容不同的同类型行
        BootUnconfirmed = 3,     // 启动分段未确认：boot 缺失或为内部占位值
        DeviceUnconfirmed = 4,   // 设备分段未确认（待归属分段，尚未关联到具体设备）
        Unspecified = 5          // 解析失败的原始行（不参与关联，单独归类）
    }

    public static class FaultText
    {
        public static string Chinese(FaultGroupState s)
        {
            switch (s)
            {
                case FaultGroupState.Complete: return "完整";
                case FaultGroupState.Conflicting: return "冲突";
                case FaultGroupState.BootUnconfirmed: return "启动分段未确认";
                case FaultGroupState.DeviceUnconfirmed: return "设备分段未确认";
                case FaultGroupState.Incomplete: return "不完整";
                case FaultGroupState.Unspecified: return "解析失败（未计入）";
                default: return "未知";
            }
        }

        public static string Chinese(FaultAdapterState s)
        {
            switch (s)
            {
                case FaultAdapterState.Parsed: return "已解析";
                case FaultAdapterState.NoneReported: return "无故障报告";
                case FaultAdapterState.Unsupported: return "接口不支持";
                case FaultAdapterState.Unadapted: return "待适配";
                case FaultAdapterState.Malformed: return "解析失败";
                default: return "未知";
            }
        }

        public static string Code(FaultGroupState s)
        {
            switch (s)
            {
                case FaultGroupState.Complete: return "complete";
                case FaultGroupState.Conflicting: return "conflicting";
                case FaultGroupState.BootUnconfirmed: return "boot_unconfirmed";
                case FaultGroupState.DeviceUnconfirmed: return "device_unconfirmed";
                case FaultGroupState.Incomplete: return "incomplete";
                case FaultGroupState.Unspecified: return "unspecified";
                default: return "unknown";
            }
        }
    }

    public class FaultSnapshot
    {
        public string Raw = "";
        public string ReceivedAt = "";
        public long NearSequence;
        public string DeviceSegment = "";
        public string BootSegment = "";
        public string Id = "";
        public string Name = "";             // r10 CURRENT 行的 name= 字段（原样保留）
        public string Kind = "";             // CURRENT / PREVIOUS / NONE / UNKNOWN / UNSUPPORTED
        public FaultAdapterState State = FaultAdapterState.Unadapted;
        public string Note = "";

        // 正式解析出的字段（冲突比较只比较这些，不比较原文排版）。
        // 只有全部字段通过合法性校验时 HasFields 才为 true。
        public bool HasFields;
        public string Ms = "";
        public string Rpm = "";
        public string Pwm = "";
        public string Pulses = "";
        public string Elapsed = "";
        public string Integral = "";

        // 校验通过后的数值（按 r10 快照的实际 C 类型保存）。
        public uint IdValue;
        public uint MsValue;
        public float RpmValue;
        public int PwmValue;
        public uint PulsesValue;
        public uint ElapsedValue;
        public float IntegralValue;

        // 解析失败详情：只对 Malformed 有效。
        public string ParseFailureField = "";
        public string ParseFailureReason = "";

        // 该行最终归属的关联组（由 FaultRegistry 回填；用于导出可追溯）。
        public FaultGroup Group;

        // 用于“内容是否相同”的比较串：只含正式字段，不含接收时间/序号。
        // 冲突比较只用“校验通过的数值”，不用原始排版：
        // 同一数值的不同文本排版（若协议允许）不会被误判为冲突；
        // 协议不允许的排版在解析阶段就已判失败，不会走到这里。
        public string FieldSignature
        {
            get
            {
                if (!HasFields) return "raw:" + (Raw ?? "");
                // rpm/integral 是 float，用 float 的往返表示（R）比较，避免 0.10 与 0.1 之类
                // 因格式化差异产生假冲突；整数字段直接用数值。
                return Kind + "|id=" + IdValue.ToString(CultureInfo.InvariantCulture) +
                       "|name=" + Name +
                       "|ms=" + MsValue.ToString(CultureInfo.InvariantCulture) +
                       "|rpm=" + RpmValue.ToString("R", CultureInfo.InvariantCulture) +
                       "|pwm=" + PwmValue.ToString(CultureInfo.InvariantCulture) +
                       "|pulses=" + PulsesValue.ToString(CultureInfo.InvariantCulture) +
                       "|elapsed=" + ElapsedValue.ToString(CultureInfo.InvariantCulture) +
                       "|integral=" + IntegralValue.ToString("R", CultureInfo.InvariantCulture);
            }
        }
    }

    public interface IFaultAdapter
    {
        // 是否已经拿到正式格式（正式模式为 false → 只保留原文）。
        bool FormatConfirmed { get; }
        FaultSnapshot Parse(string line, string deviceSegment, string bootSegment);
    }

    // 兼容旧用法：未确认格式时使用的适配器，只保留原文。
    // 新代码不再用它做单行状态的唯一来源（需要区分“缺行”时由 FaultRegistry 判定）。
    public sealed class PendingFaultAdapter : IFaultAdapter
    {
        public bool FormatConfirmed { get { return false; } }

        public FaultSnapshot Parse(string line, string deviceSegment, string bootSegment)
        {
            FaultSnapshot snap = new FaultSnapshot();
            snap.Raw = line ?? "";
            snap.DeviceSegment = deviceSegment ?? "";
            snap.BootSegment = bootSegment ?? "";
            string text = (line ?? "").Trim();
            if (text.Equals("JAY FAULT NONE", StringComparison.Ordinal))
            {
                snap.Kind = "NONE";
                snap.State = FaultAdapterState.NoneReported;
                snap.Note = "板上报告无故障；此状态不删除此前已记录的故障。";
                return snap;
            }
            if (text.StartsWith("JAY FAULT CURRENT", StringComparison.Ordinal))
            {
                snap.Kind = "CURRENT";
            }
            else if (text.StartsWith("JAY FAULT PREVIOUS", StringComparison.Ordinal))
            {
                snap.Kind = "PREVIOUS";
            }
            else
            {
                snap.Kind = "UNKNOWN";
                snap.State = FaultAdapterState.Unadapted;
                snap.Note = "未识别的故障行，已保留原文。";
                return snap;
            }
            // 只在能明确识别 id=... 时提取，用于后续关联键（设备分段 + 启动分段 + id）。
            Match idm = Regex.Match(text, @"(?:^|\s)id=([A-Za-z0-9_.:-]+)");
            if (idm.Success) snap.Id = idm.Groups[1].Value;
            snap.State = FaultAdapterState.Unadapted;
            snap.Note = "故障接口待适配：正式字段格式尚未交付，已保留原文。";
            return snap;
        }
    }
}
