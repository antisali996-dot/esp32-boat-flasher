using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace BoatTool
{
    // 诊断采集状态机（第三节）。单向推进，错误状态不会被迟到的成功清除。
    public enum DiagState
    {
        NotConnected = 0,     // 未连接
        Connected = 1,        // 已连接（尚未开始记录）
        Recording = 2,        // 记录中
        Stopped = 3,          // 已停止／导出中
        Exported = 4,         // 已导出
        ConnectFailed = 5,    // 连接失败
        Disconnected = 6,     // 掉线
        WaitingReconnect = 7, // 等待重新连接
        ExportFailed = 8      // 导出失败
    }

    public static class DiagStateText
    {
        public static string Chinese(DiagState s)
        {
            switch (s)
            {
                case DiagState.NotConnected: return "未连接";
                case DiagState.Connected: return "已连接";
                case DiagState.Recording: return "记录中";
                case DiagState.Stopped: return "已停止／导出中";
                case DiagState.Exported: return "已导出";
                case DiagState.ConnectFailed: return "连接失败";
                case DiagState.Disconnected: return "掉线";
                case DiagState.WaitingReconnect: return "等待重新连接";
                case DiagState.ExportFailed: return "导出失败";
                default: return "未知";
            }
        }
    }

    // 一条接收索引项：只描述“工具实际收到了什么”，不做协议解释。
    public class ReceiveIndexEntry
    {
        public long Sequence;          // 单调接收序号，从 1 开始
        public string ReceivedAt;      // 带时区偏移的电脑时间
        public string SessionId = "";
        public string DeviceSegment = "";   // 设备分段目录名
        public string BootSegment = "";     // 启动分段（来自 JAY COUNT boot，未知时为空）
        public string FileName = "";
        public long ByteOffset;
        public int ByteLength;
        public string ChunkSha256 = "";
    }

    // 一次采集错误（串口错误、写盘错误、解析异常）。
    public class CaptureFault
    {
        public string At = "";
        public string Kind = "";
        public string Detail = "";
    }

    // 异常标记：只有三种类型，绝不发送串口命令。
    public enum MarkerKind
    {
        MotorStopped = 1,
        MotorRestarted = 2,
        ServoMicroMove = 3
    }

    public static class MarkerText
    {
        public static string Chinese(MarkerKind k)
        {
            switch (k)
            {
                case MarkerKind.MotorStopped: return "电机停下";
                case MarkerKind.MotorRestarted: return "电机重新启动";
                case MarkerKind.ServoMicroMove: return "舵机微动";
                default: return "未知";
            }
        }

        public static string Code(MarkerKind k)
        {
            switch (k)
            {
                case MarkerKind.MotorStopped: return "motor_stopped";
                case MarkerKind.MotorRestarted: return "motor_restarted";
                case MarkerKind.ServoMicroMove: return "servo_micro_move";
                default: return "unknown";
            }
        }
    }

    public class DiagnosticMarker
    {
        public MarkerKind Kind;
        public string At = "";              // 带时区
        public string SessionId = "";
        public string DeviceSegment = "";
        public long NearSequence;           // 当前最近接收序号（0 表示尚无数据）
    }

    // 带时区的时间戳：格式固定，避免不同区域设置导致导出不一致。
    public static class DiagTime
    {
        public static string Now(DateTime local)
        {
            DateTimeOffset dto = new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Local));
            return dto.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture);
        }
    }
}
