using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace BoatTool
{
    public class SimBoard
    {
        public string Mac = "";
        public BoardRole Role = BoardRole.Unknown;
        public string Peer = "00:00:00:00:00:00";
        public string Fwid = "";
        public string Version = "";
        public int SteerDeg = -1;
        public bool Legacy;
        public int PairProtocol = 2;
        public bool SaveFails;
        public bool SaveNoAck;
        public bool ReadBackPeerWrong;
        public bool WriteFails;

        public SimBoard Clone()
        {
            return (SimBoard)MemberwiseClone();
        }
    }

    public class SimulatedTransport : IBoardTransport
    {
        public readonly List<SimBoard> Boards = new List<SimBoard>();
        public readonly Dictionary<string, string> PortMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> PortOrder = new List<string>();
        public int ProbeCount;
        public int ReadChipCount;
        public int ReadFlashCount;
        public int WriteFirmwareCount;
        public int WritePairCount;
        public int FailNextWrite;
        public int FailNextReadFlash;
        public bool ReportVerification = true;
        public List<string> Events = new List<string>();
        public string LastBackupPath = "";
        // 某些端口上的板子完全不回应身份查询（旧程序/空白板）。
        public HashSet<string> SilentPorts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // 模拟写入后板子的实际状态（用于验证读回判定）。
        public Action<string, FirmwareSpec, SimBoard> OnWrite;

        public void AddPort(string name)
        {
            if (!PortMap.ContainsKey(name)) { PortMap[name] = ""; PortOrder.Add(name); }
        }

        public void Plug(string port, string mac)
        {
            AddPort(port);
            PortMap[port] = mac;
        }

        public void Unplug(string port)
        {
            if (PortMap.ContainsKey(port)) PortMap[port] = "";
        }

        public SimBoard Board(string mac)
        {
            foreach (SimBoard b in Boards) if (b.Mac == mac) return b;
            return null;
        }

        public IList<SerialPortInfo> ListSerialPorts()
        {
            List<SerialPortInfo> list = new List<SerialPortInfo>();
            foreach (string port in PortOrder)
            {
                SerialPortInfo info = new SerialPortInfo();
                info.Name = port;
                info.Description = "模拟串口 " + port;
                list.Add(info);
            }
            return list;
        }

        private SimBoard BoardOnPort(string port)
        {
            string mac;
            if (!PortMap.TryGetValue(port, out mac) || string.IsNullOrEmpty(mac)) return null;
            return Board(mac);
        }

        public ProbeResult Probe(string port, int timeoutMs)
        {
            ProbeCount++;
            SimBoard board = BoardOnPort(port);
            if (board == null) return ProbeResult.Fail("模拟：该串口没有板子");
            if (SilentPorts.Contains(port)) return ProbeResult.Fail("模拟：板子没有回应身份查询");
            BoardIdentity id = new BoardIdentity();
            id.Responded = true;
            id.Role = board.Role;
            id.Mac = board.Mac;
            id.Peer = board.Peer;
            id.PairProtocol = board.PairProtocol;
            id.SteeringAngle = board.SteerDeg;
            if (!board.Legacy) { id.FirmwareId = board.Fwid; id.FirmwareVersion = board.Version; }
            id.RawLine = "SIM " + board.Mac;
            Events.Add("probe:" + port + ":" + board.Mac);
            return new ProbeResult { Ok = true, Identity = id };
        }

        public ChipInfo ReadChip(string port, out string error)
        {
            ReadChipCount++;
            SimBoard board = BoardOnPort(port);
            if (board == null) { error = "模拟：该串口没有板子"; return null; }
            error = "";
            ChipInfo info = new ChipInfo();
            info.Mac = board.Mac;
            info.ChipType = "ESP32-D0WDQ6";
            info.FlashBytes = 4L * 1024 * 1024;
            info.RawLog = "MAC: " + board.Mac.ToLowerInvariant();
            Events.Add("chip:" + port + ":" + board.Mac);
            return info;
        }

        public BackflashResult ReadFlash(string port, string destPath, long bytes, Logger log)
        {
            ReadFlashCount++;
            SimBoard board = BoardOnPort(port);
            BackflashResult result = new BackflashResult();
            if (board == null) { result.Error = "模拟：该串口没有板子"; return result; }
            if (FailNextReadFlash > 0)
            {
                FailNextReadFlash--;
                result.Error = "模拟：读取 Flash 失败";
                return result;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destPath));
            byte[] data = new byte[bytes];
            for (int i = 0; i < data.Length; i++) data[i] = (byte)(i % 251);
            File.WriteAllBytes(destPath, data);
            LastBackupPath = destPath;
            result.Ok = true;
            result.Path = destPath;
            result.Bytes = bytes;
            result.Sha256 = Hashing.Sha256File(destPath);
            Events.Add("readflash:" + port + ":" + board.Mac);
            return result;
        }

        public FlashResult WriteFirmware(string port, FirmwareSpec spec, bool verify, string logDir, Logger log)
        {
            WriteFirmwareCount++;
            SimBoard board = BoardOnPort(port);
            FlashResult result = new FlashResult();
            if (board == null) { result.Error = "模拟：该串口没有板子"; return result; }
            if (FailNextWrite > 0)
            {
                FailNextWrite--;
                result.Error = "模拟：写入失败";
                Events.Add("writefail:" + port);
                return result;
            }
            board.Role = spec.Role;
            board.Fwid = spec.Fwid;
            board.Version = spec.Version;
            board.SteerDeg = spec.SteerDeg;
            board.Legacy = false;
            if (OnWrite != null) OnWrite(port, spec, board);
            if (!ReportVerification)
            {
                result.Error = "工具没有报告写入校验成功，不能确认烧录完成";
                return result;
            }
            result.Ok = true;
            result.Verified = true;
            Events.Add("write:" + port + ":" + board.Mac + ":" + spec.Fwid);
            return result;
        }

        public PairWriteResult WritePair(string port, string peerMac, BoardIdentity expected, Logger log)
        {
            WritePairCount++;
            SimBoard board = BoardOnPort(port);
            PairWriteResult result = new PairWriteResult();
            if (board == null) { result.Error = "模拟：该串口没有板子"; return result; }
            if (board.Mac != expected.Mac)
            {
                result.Error = "写入配对前核对失败：当前板子与选定时不一致";
                return result;
            }
            if (board.SaveFails)
            {
                result.Error = "板子拒绝保存配对：JAY ERROR=SAVE_FAILED";
                Events.Add("pairsavefail:" + board.Mac);
                return result;
            }
            if (board.SaveNoAck)
            {
                result.Error = "没有收到配对保存回应，不能认为配对成功";
                Events.Add("pairnoack:" + board.Mac);
                return result;
            }
            board.Peer = board.ReadBackPeerWrong ? "AA:BB:CC:DD:EE:FF" : peerMac;
            result.Saved = true;
            result.Rebooted = true;
            result.ReadBackVerified = !board.ReadBackPeerWrong;
            if (board.ReadBackPeerWrong)
            {
                result.Error = "重启后读回的配对地址不是本次写入值，未通过核验";
                Events.Add("pairreadbackbad:" + board.Mac);
                return result;
            }
            result.After = new BoardIdentity
            {
                Responded = true,
                Role = board.Role,
                Mac = board.Mac,
                Peer = board.Peer,
                SteeringAngle = board.SteerDeg,
                FirmwareId = board.Fwid,
                FirmwareVersion = board.Version
            };
            result.Ok = true;
            Events.Add("pair:" + board.Mac + "->" + peerMac);
            return result;
        }
    }

    public class SimClock : IClock
    {
        public int Slept;
        public void Sleep(int milliseconds) { Slept += milliseconds; }
        public DateTime Now { get { return new DateTime(2026, 11, 3, 12, 0, 0); } }
    }

    public class SimUi : IUserInteraction
    {
        public int SafetyPrompts;
        public bool SafetyAnswer = true;
        public bool FlashAnswer = true;
        public bool BackupAnswer = true;
        public bool PairAnswer = true;
        public FlashPlan LastFlashPlan;
        public BackupPlan LastBackupPlan;
        public PairPlan LastPairPlan;
        public Action<FlashPlan> OnConfirmFlash;
        public Action<PairPlan> OnConfirmPair;
        public List<string> Messages = new List<string>();

        public bool ConfirmSafetyNotice(string detail) { SafetyPrompts++; return SafetyAnswer; }

        public bool ConfirmFlash(FlashPlan plan)
        {
            LastFlashPlan = plan;
            if (OnConfirmFlash != null) OnConfirmFlash(plan);
            return FlashAnswer;
        }

        public bool ConfirmBackup(BackupPlan plan) { LastBackupPlan = plan; return BackupAnswer; }
        public bool ConfirmPair(PairPlan plan)
        {
            LastPairPlan = plan;
            if (OnConfirmPair != null) OnConfirmPair(plan);
            return PairAnswer;
        }
        public void Report(string text) { Messages.Add(text); }
    }
}
