using System;
using System.Collections.Generic;

namespace BoatTool
{
    public class SerialPortInfo
    {
        public string Name = "";
        public string Description = "";
        public string HardwareId = "";
    }

    public class ProbeResult
    {
        public bool Ok;
        public BoardIdentity Identity;
        public string Error = "";
        public bool ResumedBoard;

        public static ProbeResult Fail(string message)
        {
            ProbeResult r = new ProbeResult();
            r.Ok = false;
            r.Error = message;
            return r;
        }
    }

    public class ChipInfo
    {
        public string Mac = "";
        public string ChipType = "";
        public long FlashBytes = -1;
        public string RawLog = "";
    }

    public class BackflashResult
    {
        public bool Ok;
        public string Path = "";
        public long Bytes = -1;
        public string Sha256 = "";
        public string Error = "";
    }

    public class FlashResult
    {
        public bool Ok;
        public bool Verified;
        public string Error = "";
        public string LogPath = "";
    }

    public class PairWriteResult
    {
        public bool Ok;
        public bool Saved;
        public bool Rebooted;
        public bool ReadBackVerified;
        public string Error = "";
        public BoardIdentity After;
    }

    // 硬件接口：真实实现走串口+esptool；测试用模拟实现，绝不触碰真实设备。
    public interface IBoardTransport
    {
        IList<SerialPortInfo> ListSerialPorts();

        // 读取 USB 身份；识别操作可能复位板子。
        ProbeResult Probe(string port, int timeoutMs);

        // 读芯片 MAC 与 Flash 容量；会复位并占用串口。
        ChipInfo ReadChip(string port, out string error);

        BackflashResult ReadFlash(string port, string destPath, long bytes, Logger log);

        // verify 始终为 true（保留写入校验）；保留参数以便测试可验证"未校验即失败"的分支。
        FlashResult WriteFirmware(string port, FirmwareSpec spec, bool verify, string logDir, Logger log);

        PairWriteResult WritePair(string port, string peerMac, BoardIdentity expected, Logger log);
    }

    public interface IClock
    {
        void Sleep(int milliseconds);
        DateTime Now { get; }
    }

    public class SystemClock : IClock
    {
        private readonly Action<int> sleeper;

        public SystemClock() : this(null) { }

        public SystemClock(Action<int> sleeper)
        {
            this.sleeper = sleeper;
        }

        public void Sleep(int milliseconds)
        {
            if (sleeper != null) { sleeper(milliseconds); return; }
            System.Threading.Thread.Sleep(milliseconds);
        }

        public DateTime Now { get { return DateTime.Now; } }
    }
}
