using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Text;
using System.Text.RegularExpressions;
using System.Diagnostics;
using System.Threading;

namespace BoatTool
{
    // 真实硬件实现：串口读身份 + 独立 esptool.exe 读写。
    public class HardwareTransport : IBoardTransport
    {
        private readonly AppPaths paths;
        private readonly IClock clock;

        public HardwareTransport(AppPaths paths, IClock clock)
        {
            this.paths = paths;
            this.clock = clock;
        }

        public IList<SerialPortInfo> ListSerialPorts()
        {
            List<SerialPortInfo> result = new List<SerialPortInfo>();
            string[] names = SerialPort.GetPortNames();
            Array.Sort(names, ComparePortNames);
            Dictionary<string, string> descriptions = QueryDescriptions();
            foreach (string name in names)
            {
                SerialPortInfo info = new SerialPortInfo();
                info.Name = name.ToUpperInvariant();
                string desc;
                info.Description = descriptions.TryGetValue(info.Name, out desc) ? desc : "";
                result.Add(info);
            }
            return result;
        }

        private static int ComparePortNames(string a, string b)
        {
            int na, nb;
            bool oka = int.TryParse(a.Substring(3), out na);
            bool okb = int.TryParse(b.Substring(3), out nb);
            if (oka && okb) return na.CompareTo(nb);
            return string.CompareOrdinal(a, b);
        }

        // 通过 WMI 读取驱动描述，避免把 CH340 误当成板子身份；失败时留空。
        private static Dictionary<string, string> QueryDescriptions()
        {
            Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (System.Management.ManagementObjectSearcher searcher =
                    new System.Management.ManagementObjectSearcher(
                        "SELECT Name, Caption FROM Win32_PnPEntity WHERE Caption LIKE '%(COM%'"))
                {
                    foreach (System.Management.ManagementObject item in searcher.Get())
                    {
                        string caption = Convert.ToString(item["Caption"]);
                        Match m = Regex.Match(caption ?? "", @"\((COM\d+)\)");
                        if (!m.Success) continue;
                        map[m.Groups[1].Value.ToUpperInvariant()] = caption;
                    }
                }
            }
            catch
            {
                // WMI 不可用时不影响串口枚举。
            }
            return map;
        }

        public ProbeResult Probe(string port, int timeoutMs)
        {
            if (string.IsNullOrEmpty(port)) return ProbeResult.Fail("没有选择串口");
            SerialPort serial = null;
            try
            {
                // DTR/RTS 保持释放，避免识别操作把板子拉进下载模式。
                serial = new SerialPort(port, 115200, Parity.None, 8, StopBits.One);
                serial.DtrEnable = false;
                serial.RtsEnable = false;
                serial.ReadTimeout = 250;
                serial.WriteTimeout = 500;
                serial.NewLine = "\n";
                serial.Open();
                clock.Sleep(400);
                serial.DiscardInBuffer();
                serial.WriteLine("JAY INFO");

                List<string> lines = new List<string>();
                DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                int retries = 0;
                while (DateTime.UtcNow < deadline)
                {
                    try
                    {
                        string line = serial.ReadLine();
                        if (!string.IsNullOrEmpty(line))
                        {
                            lines.Add(line.Trim());
                            BoardIdentity parsed = IdentityParser.FromLine(line);
                            if (parsed != null) return new ProbeResult { Ok = true, Identity = parsed };
                        }
                    }
                    catch (TimeoutException)
                    {
                        retries++;
                        // 板子可能刚上电或在跑主循环；每约 1.5 秒补发一次查询。
                        if (retries % 6 == 0 && DateTime.UtcNow < deadline)
                        {
                            try { serial.WriteLine("JAY INFO"); }
                            catch { }
                        }
                    }
                }
                BoardIdentity any = IdentityParser.FromLines(lines);
                if (any != null) return new ProbeResult { Ok = true, Identity = any };
                return ProbeResult.Fail("没有收到板子身份回应（可能是旧固件、串口被占用或选了别的设备）");
            }
            catch (UnauthorizedAccessException)
            {
                return ProbeResult.Fail("串口被其他程序占用，请关闭串口监视器等程序后重试");
            }
            catch (IOException ex)
            {
                return ProbeResult.Fail("串口打开失败：" + ex.Message);
            }
            catch (Exception ex)
            {
                return ProbeResult.Fail("读取身份失败：" + ex.Message);
            }
            finally
            {
                if (serial != null)
                {
                    try { if (serial.IsOpen) serial.Close(); }
                    catch { }
                    serial.Dispose();
                }
            }
        }

        public ChipInfo ReadChip(string port, string esptoolPath, out string error)
        {
            error = "";
            string output;
            int code = RunEsptool(esptoolPath,
                new string[] { "--chip", "esp32", "--port", port, "--baud", "115200", "flash_id" },
                null, out output);
            if (code != 0)
            {
                error = "读取芯片信息失败（esptool 退出码 " + code + "）";
                return null;
            }
            Match mac = Regex.Match(output, @"(?m)^MAC:\s*([0-9a-fA-F:]{17})");
            if (!mac.Success) { error = "工具输出中没有芯片 MAC"; return null; }
            string normalized = MacText.Normalize(mac.Groups[1].Value);
            if (normalized.Length == 0) { error = "读到的芯片 MAC 非法：" + mac.Groups[1].Value; return null; }

            ChipInfo info = new ChipInfo();
            info.Mac = normalized;
            Match chip = Regex.Match(output, @"Chip is\s+([A-Za-z0-9\-]+)");
            info.ChipType = chip.Success ? chip.Groups[1].Value : "";
            Match size = Regex.Match(output, @"Detected flash size:\s*(4|8|16)MB");
            if (size.Success) info.FlashBytes = long.Parse(size.Groups[1].Value, CultureInfo.InvariantCulture) * 1024 * 1024;
            info.RawLog = output;
            return info;
        }

        public BackflashResult ReadFlash(string port, string esptoolPath, string destPath, long bytes, Logger log)
        {
            BackflashResult result = new BackflashResult();
            if (bytes <= 0) { result.Error = "未识别到可备份的 Flash 容量"; return result; }
            string output;
            int code = RunEsptool(esptoolPath, new string[]
            {
                "--chip", "esp32", "--port", port, "--baud", "460800", "read_flash",
                "0x0", "0x" + bytes.ToString("X", CultureInfo.InvariantCulture), destPath
            }, log, out output);
            if (code != 0)
            {
                result.Error = "读取 Flash 失败（esptool 退出码 " + code + "）";
                return result;
            }
            if (!File.Exists(destPath)) { result.Error = "备份文件未生成"; return result; }
            long length = new FileInfo(destPath).Length;
            if (length != bytes)
            {
                result.Error = "备份大小不正确：期望 " + bytes + " 字节，实际 " + length + " 字节";
                return result;
            }
            result.Ok = true;
            result.Path = destPath;
            result.Bytes = length;
            result.Sha256 = Hashing.Sha256File(destPath);
            return result;
        }

        public FlashResult WriteFirmware(string port, string esptoolPath, FirmwareSpec spec, bool verify,
                                         string logDir, Logger log)
        {
            FlashResult result = new FlashResult();
            List<string> args = new List<string>
            {
                "--chip", "esp32", "--port", port, "--baud", "460800", "write_flash",
                "-z", "--flash_mode", "dio", "--flash_freq", "40m", "--flash_size", "keep"
            };
            if (verify) args.Add("--verify");
            foreach (KeyValuePair<long, string> image in spec.Images())
            {
                args.Add("0x" + image.Key.ToString("X", CultureInfo.InvariantCulture));
                args.Add(image.Value);
            }
            if (!verify)
            {
                // 界面永远传 true；这条分支只为防止将来误把关掉校验。
                result.Error = "写入校验被关闭，本工具不允许无校验写入";
                return result;
            }
            string output;
            int code = RunEsptool(esptoolPath, args.ToArray(), log, out output);
            result.LogPath = logDir;
            // esptool 在部分错误下仍可能返回 0，必须同时看到校验成功字样。
            bool hashOk = output.IndexOf("Hash of data verified", StringComparison.OrdinalIgnoreCase) >= 0;
            if (code != 0)
            {
                result.Error = "写入失败（esptool 退出码 " + code + "），日志已保存";
                return result;
            }
            if (!hashOk)
            {
                result.Error = "工具没有报告写入校验成功（--verify），不能确认烧录完成";
                return result;
            }
            result.Ok = true;
            result.Verified = true;
            return result;
        }

        public PairWriteResult WritePair(string port, string peerMac, BoardIdentity expected, Logger log)
        {
            PairWriteResult result = new PairWriteResult();
            string peer = MacText.NormalizePeer(peerMac);
            if (peer.Length == 0 || peer == "00:00:00:00:00:00")
            {
                result.Error = "配对地址无效";
                return result;
            }

            SerialPort serial = null;
            try
            {
                serial = new SerialPort(port, 115200, Parity.None, 8, StopBits.One);
                serial.DtrEnable = false;
                serial.RtsEnable = false;
                serial.ReadTimeout = 250;
                serial.WriteTimeout = 500;
                serial.NewLine = "\n";
                serial.Open();
                clock.Sleep(400);

                // 写入前再核对一次身份，防止用户中途换插。
                BoardIdentity before = QueryIdentity(serial, 5000);
                if (before == null)
                {
                    result.Error = "写入配对前没有读到板子身份，已停止";
                    return result;
                }
                if (!SameIdentity(before, expected))
                {
                    result.Error = "写入配对前核对失败：当前板子与选定时不一致（" + Describe(before) +
                                   "，选定时为 " + Describe(expected) + "），已停止";
                    return result;
                }
                if (before.Mac == peer)
                {
                    result.Error = "对端地址与本机相同，已停止";
                    return result;
                }

                serial.DiscardInBuffer();
                serial.WriteLine("JAY BIND " + peer);
                bool ack = false;
                bool rebooted = false;
                DateTime deadline = DateTime.UtcNow.AddMilliseconds(6000);
                while (DateTime.UtcNow < deadline)
                {
                    try
                    {
                        string line = serial.ReadLine();
                        if (string.IsNullOrEmpty(line)) continue;
                        if (line.IndexOf("JAY SAVED REBOOT", StringComparison.Ordinal) >= 0)
                        {
                            ack = true;
                            rebooted = true;
                            break;
                        }
                        if (line.IndexOf("JAY ERROR=", StringComparison.Ordinal) >= 0)
                        {
                            result.Error = "板子拒绝保存配对：" + line.Trim();
                            return result;
                        }
                    }
                    catch (TimeoutException) { }
                }
                if (!ack)
                {
                    result.Error = "没有收到配对保存回应，不能认为配对成功（板上程序可能过旧）";
                    return result;
                }
                result.Saved = true;

                // 等待重启完成，再读回核验。
                clock.Sleep(1200);
                BoardIdentity after = null;
                DateTime readUntil = DateTime.UtcNow.AddMilliseconds(8000);
                while (DateTime.UtcNow < readUntil)
                {
                    after = QueryIdentity(serial, 1500);
                    if (after != null) break;
                    clock.Sleep(300);
                }
                result.After = after;
                if (after == null)
                {
                    result.Error = "已保存并重启，但没有读到重启后的身份，未完成读回核验";
                    return result;
                }
                if (after.Mac != before.Mac)
                {
                    result.Error = "重启后读到的芯片 MAC 变化（" + before.Mac + " → " + after.Mac + "），未通过核验";
                    return result;
                }
                if (after.Role != before.Role)
                {
                    result.Error = "重启后角色变化（" + before.RoleText + " → " + after.RoleText + "），未通过核验";
                    return result;
                }
                if (after.Peer != peer)
                {
                    result.Error = "重启后读回的配对地址是 " + after.PeerText + "，不是 " + peer + "，未通过核验";
                    return result;
                }
                result.Ok = true;
                result.Rebooted = rebooted;
                result.ReadBackVerified = true;
                return result;
            }
            catch (UnauthorizedAccessException)
            {
                result.Error = "串口被其他程序占用，请关闭后重试";
                return result;
            }
            catch (Exception ex)
            {
                result.Error = "配对写入异常：" + ex.Message;
                return result;
            }
            finally
            {
                if (serial != null)
                {
                    try { if (serial.IsOpen) serial.Close(); }
                    catch { }
                    serial.Dispose();
                }
            }
        }

        private BoardIdentity QueryIdentity(SerialPort serial, int timeoutMs)
        {
            try { serial.DiscardInBuffer(); }
            catch { }
            try { serial.WriteLine("JAY INFO"); }
            catch { return null; }
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    string line = serial.ReadLine();
                    BoardIdentity id = IdentityParser.FromLine(line);
                    if (id != null) return id;
                }
                catch (TimeoutException) { }
            }
            return null;
        }

        // 身份核对以芯片 MAC 为准；串口名只表示本次连接，不参与身份判断。
        public static bool SameIdentity(BoardIdentity actual, BoardIdentity expected)
        {
            if (actual == null || expected == null) return false;
            if (string.IsNullOrEmpty(actual.Mac) || string.IsNullOrEmpty(expected.Mac)) return false;
            if (actual.Mac != expected.Mac) return false;
            if (expected.Role != BoardRole.Unknown && actual.Role != expected.Role) return false;
            return true;
        }

        public static string Describe(BoardIdentity id)
        {
            if (id == null) return "未知";
            return "MAC " + id.MacText + " / " + id.RoleText;
        }

        private int RunEsptool(string esptoolPath, string[] args, Logger log, out string output)
        {
            List<string> full = new List<string>();
            for (int i = 0; i < args.Length; i++) full.Add(args[i]);
            ProcessStartInfo start = new ProcessStartInfo();
            start.FileName = string.IsNullOrEmpty(esptoolPath) ? paths.EsptoolPath : esptoolPath;
            start.Arguments = JoinArguments(full);
            start.UseShellExecute = false;
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            start.RedirectStandardInput = true;
            start.CreateNoWindow = true;
            // esptool 输出是 ASCII；用默认编码解码，避免控制台代码页差异造成乱码或丢行。
            start.StandardOutputEncoding = Encoding.Default;
            start.StandardErrorEncoding = Encoding.Default;

            StringBuilder all = new StringBuilder();
            using (Process process = new Process())
            {
                process.StartInfo = start;
                DataReceivedEventHandler handler = delegate(object sender, DataReceivedEventArgs e)
                {
                    if (e.Data == null) return;
                    all.AppendLine(e.Data);
                    if (log != null) log.Raw(e.Data);
                };
                process.OutputDataReceived += handler;
                process.ErrorDataReceived += handler;
                try
                {
                    process.Start();
                }
                catch (Exception ex)
                {
                    output = "";
                    if (log != null) log.Info("无法启动烧录工具：" + ex.Message);
                    return -1;
                }
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                try { process.StandardInput.Close(); }
                catch { }
                // 读整片 8MB Flash 在低速下可能接近 10 分钟，统一给足等待时间。
                if (!process.WaitForExit(600000))
                {
                    try { process.Kill(); }
                    catch { }
                    try { process.WaitForExit(5000); } catch { }
                    output = all.ToString();
                    return -1;
                }
                process.WaitForExit();
                output = all.ToString();
                return process.ExitCode;
            }
        }

        public static string JoinArguments(IEnumerable<string> args)
        {
            StringBuilder builder = new StringBuilder();
            foreach (string arg in args)
            {
                if (builder.Length > 0) builder.Append(' ');
                if (arg.Length == 0 || arg.IndexOf(' ') >= 0 || arg.IndexOf('"') >= 0)
                {
                    builder.Append('"').Append(arg.Replace("\"", "\\\"")).Append('"');
                }
                else builder.Append(arg);
            }
            return builder.ToString();
        }
    }
}
