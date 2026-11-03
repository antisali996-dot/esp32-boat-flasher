using System;
using System.Collections.Generic;
using System.Text;

namespace BoatTool
{
    // 配对链路状态。一次绑定会话只会沿这些状态单向推进，错误状态一旦进入不会被后续
    // 迟到的成功字符串清除。
    public enum PairLinkState
    {
        NotStarted = 0,          // 未开始
        BindSent = 1,            // 已发送绑定
        SavedAckAwaitingVerify = 2, // 收到保存回应，等待身份核验
        TimeoutUnknown = 3,      // 超时／结果未知
        SavedButUnconfirmed = 4, // 已保存但未确认生效
        LocalVerified = 5,       // 本端配置已核对
        FailedNeedsPowerCycle = 6 // 失败／需要人工重新上电核验
    }

    public static class PairLinkText
    {
        public static string Chinese(PairLinkState state)
        {
            switch (state)
            {
                case PairLinkState.NotStarted: return "未开始";
                case PairLinkState.BindSent: return "已发送绑定";
                case PairLinkState.SavedAckAwaitingVerify: return "收到保存回应，等待身份核验";
                case PairLinkState.TimeoutUnknown: return "超时／结果未知";
                case PairLinkState.SavedButUnconfirmed: return "已保存但未确认生效";
                case PairLinkState.LocalVerified: return "本端配置已核对";
                case PairLinkState.FailedNeedsPowerCycle: return "失败／需要人工重新上电核验";
                default: return "未知状态";
            }
        }

        // 只有“本端配置已核对”才是成功；其余一律不能显示为完成。
        public static bool IsSuccess(PairLinkState state)
        {
            return state == PairLinkState.LocalVerified;
        }

        // 需要用户断开动力、重新上电后做只读核验的状态。
        public static bool NeedsPowerCycleVerify(PairLinkState state)
        {
            return state == PairLinkState.SavedButUnconfirmed ||
                   state == PairLinkState.FailedNeedsPowerCycle ||
                   state == PairLinkState.TimeoutUnknown;
        }
    }

    // 串口字节流抽象：测试注入模拟字节，真实实现包住 SerialPort。
    public interface IPairChannel
    {
        // 写入原始字节（调用方负责拼出 \n）。
        void Write(string text);

        // 读取最多 count 字节；返回 0 表示当前没有数据。
        int Read(byte[] buffer, int offset, int count);

        // 丢弃接收缓冲。
        void DiscardInput();
    }

    // 字节流 → 行的组装器。半行不会作为完整行返回；超长行被丢弃并报告一次。
    public sealed class PairLinePump
    {
        private readonly StringBuilder current = new StringBuilder();
        private bool overflow;
        public int DroppedOverlong;

        public IEnumerable<string> Feed(byte[] buffer, int count)
        {
            List<string> lines = new List<string>();
            for (int i = 0; i < count; i++)
            {
                char c = (char)buffer[i];
                if (c == '\r') continue;
                if (c != '\n')
                {
                    if (current.Length < 256 && !overflow) current.Append(c);
                    else overflow = true;
                    continue;
                }
                if (overflow)
                {
                    overflow = false;
                    current.Length = 0;
                    DroppedOverlong++;
                    continue;
                }
                string line = current.ToString().Trim();
                current.Length = 0;
                if (line.Length > 0) lines.Add(line);
            }
            return lines;
        }

        public void Reset()
        {
            current.Length = 0;
            overflow = false;
        }
    }

    public class PairOutcome
    {
        public PairLinkState State = PairLinkState.NotStarted;
        public string Message = "";
        public bool AckSeen;          // 只是进展信息，绝不代表成功
        public int BindCommandsSent;
        public int InfoCommandsSent;
        public BoardIdentity FinalIdentity;
        public List<string> Events = new List<string>();

        public bool Ok { get { return PairLinkText.IsSuccess(State); } }

        public void Event(string text)
        {
            Events.Add(text);
        }

        // 已正确的一端：不发送绑定，直接按“已核对”参与双端判定。
        public static PairOutcome Skipped(PairLinkState state, string message)
        {
            PairOutcome outcome = new PairOutcome();
            outcome.State = state;
            outcome.Message = message;
            return outcome;
        }

        public static PairOutcome From(PairWriteResult result)
        {
            PairOutcome outcome = new PairOutcome();
            if (result == null)
            {
                outcome.State = PairLinkState.FailedNeedsPowerCycle;
                outcome.Message = "配对没有返回结果";
                return outcome;
            }
            outcome.State = result.State;
            outcome.Message = string.IsNullOrEmpty(result.Message) ? result.Error : result.Message;
            outcome.AckSeen = result.Saved;
            outcome.BindCommandsSent = result.BindCommandsSent;
            outcome.FinalIdentity = result.After;
            if (result.Events != null) outcome.Events = result.Events;
            return outcome;
        }
    }

    // 一次“绑定 + 核验”会话。
    //
    // 关键规则（与需求一一对应）：
    //   * JAY SAVED REBOOT 只置 AckSeen，不置成功；
    //   * 必须重新发 JAY INFO 并读到完整 JAYv4 行（带 FWID），逐项核对角色、本机 MAC、
    //     PEER、FWID；VERSION/STEER 不替代 FWID；
    //   * 缺字段/半行/旧格式/READ_FAILED 一律不放行；
    //   * SAVED_ACK_NOT_QUEUED / SAVED_ACK_UNCONFIRMED → SavedButUnconfirmed（锁存）；
    //   * BIND_BLOCKED_PENDING_ERROR → 立刻停止，不重发；
    //   * 绑定阶段超时 → TimeoutUnknown，不在本次操作里继续“读回成功”；
    //   * 超时后迟到的回应不会被处理（循环已经结束）。
    public sealed class PairingSession
    {
        private readonly IPairChannel channel;
        private readonly IClock clock;
        private readonly Action<string> log;

        public PairingSession(IPairChannel channel, IClock clock, Action<string> log)
        {
            this.channel = channel;
            this.clock = clock;
            this.log = log;
        }

        private void Log(string text)
        {
            if (log != null) log(text);
        }

        public PairOutcome BindAndVerify(BoardIdentity before, BoardRole role, string targetPeer,
                                         string expectedFwid, int bindTimeoutMs, int verifyTimeoutMs,
                                         int rebootSettleMs)
        {
            PairOutcome outcome = new PairOutcome();
            string peer = MacText.NormalizePeer(targetPeer);
            if (before == null || string.IsNullOrEmpty(before.Mac))
            {
                outcome.State = PairLinkState.FailedNeedsPowerCycle;
                outcome.Message = "写入前没有可用的芯片身份，未发送绑定。";
                return outcome;
            }
            if (peer.Length == 0 || peer == "00:00:00:00:00:00")
            {
                outcome.State = PairLinkState.FailedNeedsPowerCycle;
                outcome.Message = "目标对端地址无效，未发送绑定。";
                return outcome;
            }
            if (peer == before.Mac)
            {
                outcome.State = PairLinkState.FailedNeedsPowerCycle;
                outcome.Message = "目标对端与本机 MAC 相同，未发送绑定。";
                return outcome;
            }

            // 清空接收缓冲只减少误读旧数据的机会；r7 没有请求流水号，
            // 因此不能声称“靠清缓冲就能证明回应属于本次请求”。
            channel.DiscardInput();
            channel.Write("JAY BIND " + peer + "\n");
            outcome.BindCommandsSent = 1;
            outcome.State = PairLinkState.BindSent;
            outcome.Event("发送绑定：JAY BIND " + peer);
            Log("已发送 JAY BIND " + peer + "（仅一次，不自动重发）");

            PairLinePump pump = new PairLinePump();
            bool ackSeen = false;
            string terminalError = null;

            // ---- 阶段一：等保存回应或错误 ----
            DateTime deadline = clock.Now.AddMilliseconds(bindTimeoutMs);
            byte[] buffer = new byte[256];
            while (clock.Now < deadline)
            {
                int read = channel.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                {
                    clock.Sleep(20);
                    continue;
                }
                foreach (string line in pump.Feed(buffer, read))
                {
                    outcome.Event("收到：" + line);
                    if (line.IndexOf("JAY ERROR=SAVED_ACK_NOT_QUEUED", StringComparison.Ordinal) >= 0 ||
                        line.IndexOf("JAY ERROR=SAVED_ACK_UNCONFIRMED", StringComparison.Ordinal) >= 0)
                    {
                        // 保存已写入，但回应没有确认送出：锁存，绝不重启、绝不重绑。
                        outcome.State = PairLinkState.SavedButUnconfirmed;
                        outcome.Message = "板上已保存配对，但保存回应没有确认送出（" + line + "）。" +
                                          "请断开动力，重新上电后执行只读核验。";
                        outcome.AckSeen = ackSeen;
                        Log("进入“已保存但未确认生效”：不再重发绑定，等待人工重新上电核验");
                        return outcome;
                    }
                    if (line.IndexOf("JAY ERROR=BIND_BLOCKED_PENDING_ERROR", StringComparison.Ordinal) >= 0)
                    {
                        outcome.State = PairLinkState.FailedNeedsPowerCycle;
                        outcome.Message = "板上存在尚未处理的配对错误，本次绑定被拒绝。请不要反复重试；" +
                                          "请断开动力，重新上电后执行只读核验，确认状态后再操作。";
                        outcome.AckSeen = ackSeen;
                        Log("收到 BIND_BLOCKED_PENDING_ERROR：停止本轮绑定，不重发");
                        return outcome;
                    }
                    if (line.IndexOf("JAY ERROR=", StringComparison.Ordinal) >= 0)
                    {
                        terminalError = line.Trim();
                        break;
                    }
                    if (line.IndexOf("JAY SAVED REBOOT", StringComparison.Ordinal) >= 0)
                    {
                        // 只是进展信息。
                        ackSeen = true;
                        outcome.AckSeen = true;
                        outcome.State = PairLinkState.SavedAckAwaitingVerify;
                        outcome.Event("保存回应已收到（仅为进展，不代表成功）");
                        Log("收到 JAY SAVED REBOOT：仅作为进展，继续做身份核验");
                        break;
                    }
                }
                if (terminalError != null || ackSeen) break;
            }

            if (terminalError != null)
            {
                outcome.State = PairLinkState.FailedNeedsPowerCycle;
                outcome.Message = "板子拒绝保存配对：" + terminalError +
                                  "。请排除原因后，断开动力重新上电，再执行只读核验。";
                outcome.AckSeen = ackSeen;
                Log("绑定被拒绝：" + terminalError);
                return outcome;
            }

            if (!ackSeen)
            {
                // 绑定阶段超时：本次操作到此为止。即使之后有迟到的 SAVED 行，
                // 也不在本会话内继续判定成功。
                outcome.State = PairLinkState.TimeoutUnknown;
                outcome.Message = "等待保存回应超时，结果未知。本次操作不会判定成功，也不会自动重发绑定。" +
                                  "请断开动力，重新上电后执行只读核验。";
                Log("绑定阶段超时：结果未知，停止本次判定");
                return outcome;
            }

            // ---- 阶段二：重启后重新发 JAY INFO，严格核验 ----
            outcome.State = PairLinkState.SavedAckAwaitingVerify;
            if (rebootSettleMs > 0) clock.Sleep(rebootSettleMs);

            string lastReject = "";
            DateTime verifyDeadline = clock.Now.AddMilliseconds(verifyTimeoutMs);
            DateTime nextInfo = DateTime.MinValue;
            while (clock.Now < verifyDeadline)
            {
                if (clock.Now >= nextInfo)
                {
                    channel.Write("JAY INFO\n");
                    outcome.InfoCommandsSent++;
                    nextInfo = clock.Now.AddMilliseconds(600);
                }
                int read = channel.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                {
                    clock.Sleep(20);
                    continue;
                }
                foreach (string line in pump.Feed(buffer, read))
                {
                    outcome.Event("核验收到：" + line);
                    // 迟到的保存/成功文字不得改变核验结论，这里只记录。
                    if (line.IndexOf("JAY SAVED REBOOT", StringComparison.Ordinal) >= 0)
                    {
                        outcome.Event("忽略迟到的保存回应（不改变核验结论）");
                        continue;
                    }
                    BoardIdentity id;
                    string reject;
                    if (!IdentityParser.TryParseStrict(line, out id, out reject))
                    {
                        lastReject = reject;
                        continue;
                    }
                    string mismatch = Verifier.Mismatch(id, role, before.Mac, peer, expectedFwid);
                    if (mismatch == null)
                    {
                        outcome.State = PairLinkState.LocalVerified;
                        outcome.FinalIdentity = id;
                        outcome.Message = "本端配对配置已核对：角色 " + id.RoleText + "，本机 " + id.Mac +
                                          "，对端 " + id.Peer + "，固件身份与本轮清单一致。";
                        Log("身份核验通过：" + id.Mac + " PEER=" + id.Peer + " FWID=" + id.FirmwareId);
                        return outcome;
                    }
                    lastReject = mismatch;
                    outcome.Event("核验不符：" + mismatch);
                }
            }

            outcome.State = PairLinkState.FailedNeedsPowerCycle;
            outcome.Message = "已收到保存回应，但重启后没有读到符合本次要求的完整身份" +
                              (string.IsNullOrEmpty(lastReject) ? "" : "（最后原因：" + lastReject + "）") +
                              "。请断开动力，重新上电后执行只读核验。";
            Log("保存后核验未通过：" + lastReject);
            return outcome;
        }
    }

    // 只读核验：只发 JAY INFO，完整核对，不写任何配置。
    // 这是解除“已保存但未确认生效／失败需重新上电”状态的唯一途径。
    public sealed class VerifySession
    {
        private readonly IPairChannel channel;
        private readonly IClock clock;
        private readonly Action<string> log;

        public VerifySession(IPairChannel channel, IClock clock, Action<string> log)
        {
            this.channel = channel;
            this.clock = clock;
            this.log = log;
        }

        public PairOutcome Verify(BoardRole role, string expectedMac, string expectedPeer,
                                  string expectedFwid, int timeoutMs)
        {
            PairOutcome outcome = new PairOutcome();
            PairLinePump pump = new PairLinePump();
            byte[] buffer = new byte[256];
            string lastReject = "";
            DateTime deadline = clock.Now.AddMilliseconds(timeoutMs);
            DateTime nextInfo = DateTime.MinValue;

            channel.DiscardInput();
            while (clock.Now < deadline)
            {
                if (clock.Now >= nextInfo)
                {
                    channel.Write("JAY INFO\n");
                    outcome.InfoCommandsSent++;
                    nextInfo = clock.Now.AddMilliseconds(600);
                }
                int read = channel.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                {
                    clock.Sleep(20);
                    continue;
                }
                foreach (string line in pump.Feed(buffer, read))
                {
                    outcome.Event("只读核验：" + line);
                    BoardIdentity id;
                    string reject;
                    if (!IdentityParser.TryParseStrict(line, out id, out reject))
                    {
                        lastReject = reject;
                        continue;
                    }
                    string mismatch = Verifier.Mismatch(id, role, expectedMac, expectedPeer, expectedFwid);
                    if (mismatch == null)
                    {
                        outcome.State = PairLinkState.LocalVerified;
                        outcome.FinalIdentity = id;
                        outcome.Message = "只读核验通过：角色 " + id.RoleText + "，本机 " + id.Mac +
                                          "，对端 " + id.Peer + "，固件身份与本轮清单一致。";
                        return outcome;
                    }
                    lastReject = mismatch;
                    outcome.Event("只读核验不符：" + mismatch);
                }
            }

            outcome.State = PairLinkState.TimeoutUnknown;
            outcome.Message = "只读核验未通过" +
                              (string.IsNullOrEmpty(lastReject) ? "：没有读到完整身份。" : "：" + lastReject) +
                              " 本次没有写入任何配置。";
            return outcome;
        }
    }

    // 严格核验：只接受带 FWID 的 JAYv4+ 行，逐项比对。
    public static class Verifier
    {
        // 返回 null 表示全部符合；否则返回中文原因。
        public static string Mismatch(BoardIdentity id, BoardRole role, string expectedMac,
                                      string expectedPeer, string expectedFwid)
        {
            if (id == null) return "身份为空";
            if (id.IdentityLineVersion < 4) return "身份行不是 JAYv4（" + id.IdentityLineVersion + "）";
            if (!id.HasFirmwareId) return "身份行缺少 FWID";
            if (id.Role != role) return "角色是" + id.RoleText + "，不是" + RoleNames.Chinese(role);
            if (string.IsNullOrEmpty(id.Mac)) return "身份行缺少有效本机 MAC";
            if (!string.Equals(id.Mac, expectedMac, StringComparison.Ordinal))
                return "本机 MAC 是 " + id.Mac + "，写入前核对为 " + expectedMac;
            if (!string.Equals(id.Peer, MacText.NormalizePeer(expectedPeer), StringComparison.Ordinal))
                return "对端是 " + id.PeerText + "，不是本次目标 " + expectedPeer;
            if (!string.Equals(id.FirmwareId, expectedFwid, StringComparison.Ordinal))
                return "固件身份是 " + id.FirmwareId + "，与本轮清单 " + expectedFwid + " 不一致";
            return null;
        }
    }
}
