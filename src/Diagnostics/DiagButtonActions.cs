using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace BoatTool
{
    // 诊断页按钮的真实分派入口。GUI 与测试调用同一份代码，不存在“仅测试用”的第二套逻辑。
    public enum DiagConnectOutcome
    {
        Connected = 0,          // 普通连接（此前没有活动会话）
        Reconnected = 1,        // 复用原会话重连成功
        RefusedRecording = 2,   // 正在记录，拒绝重复连接
        NoPort = 3,
        Failed = 4
    }

    public enum DiagStartOutcome
    {
        Started = 0,
        RefusedRecording = 1,
        RefusedUnclosedSession = 2,   // 旧会话未关闭：核心层拒绝新建
        RefusedNotConnected = 3
    }

    public static class DiagButtonActions
    {
        public static bool HasUnclosedSession(DiagnosticsController d)
        {
            return d != null && d.Session != null && !d.Session.IsClosed;
        }

        // “连接并核对版本”按规则 A–E 分派。
        public static DiagConnectOutcome ConnectOrReconnect(DiagnosticsController d, string port)
        {
            if (d == null) return DiagConnectOutcome.Failed;
            if (string.IsNullOrEmpty(port)) return DiagConnectOutcome.NoPort;

            // D：记录中禁止重复连接。
            if (d.State == DiagState.Recording) return DiagConnectOutcome.RefusedRecording;

            bool unclosed = HasUnclosedSession(d);

            // B/C：掉线、等待重连、或仍有未关闭会话（含重连识别失败）→ 只能重连，保留原会话。
            // 直接用“重连到目标串口”的核心事务：不在 GUI 先 SetPort，
            // 以免改动活动会话端口后锁没迁移。
            if (d.State == DiagState.Disconnected || d.State == DiagState.WaitingReconnect || unclosed)
                return d.Reconnect(port) ? DiagConnectOutcome.Reconnected : DiagConnectOutcome.Failed;

            // A：没有活动会话 → 普通连接。
            d.SetPort(port);
            return d.ConnectAndIdentify() ? DiagConnectOutcome.Connected : DiagConnectOutcome.Failed;
        }

        // “开始记录”按规则 B/D/E 分派：重连成功后不需要再点开始；旧会话未关时核心拒绝。
        public static DiagStartOutcome StartNewOrResume(DiagnosticsController d)
        {
            if (d == null) return DiagStartOutcome.RefusedNotConnected;
            if (d.State == DiagState.Recording) return DiagStartOutcome.RefusedRecording;
            if (HasUnclosedSession(d)) return DiagStartOutcome.RefusedUnclosedSession;
            if (d.State != DiagState.Connected) return DiagStartOutcome.RefusedNotConnected;
            return d.StartRecording() ? DiagStartOutcome.Started : DiagStartOutcome.RefusedNotConnected;
        }
    }
}
