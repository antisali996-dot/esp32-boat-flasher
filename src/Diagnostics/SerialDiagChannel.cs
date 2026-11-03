using System;
using System.IO;
using System.IO.Ports;

namespace BoatTool
{
    // 真实诊断串口：打开前设置 DTR=false、RTS=false。
    //
    // 注意：软件保持 DTR、RTS 为 false，但不能保证所有 USB 串口硬件在打开连接时都绝不复位。
    // 这是硬件/驱动行为，工具无法控制。
    public sealed class SerialDiagChannel : IDiagChannel
    {
        private SerialPort serial;
        private readonly int baud;
        private readonly Action<string> log;

        public SerialDiagChannel(int baud, Action<string> log)
        {
            this.baud = baud;
            this.log = log;
        }

        public bool IsOpen { get { return serial != null && serial.IsOpen; } }

        public void Open(string port)
        {
            Close();
            SerialPort sp = new SerialPort(port, baud, Parity.None, 8, StopBits.One);
            // 先禁用硬件流控相关的握手，再显式拉低 DTR/RTS。
            sp.Handshake = Handshake.None;
            sp.DtrEnable = false;
            sp.RtsEnable = false;
            sp.ReadTimeout = 100;
            sp.WriteTimeout = 500;
            sp.NewLine = "\n";
            sp.Open();
            // 打开后再次确认（部分驱动会在 Open 时改写状态）。
            sp.DtrEnable = false;
            sp.RtsEnable = false;
            serial = sp;
        }

        public void Close()
        {
            if (serial == null) return;
            try { if (serial.IsOpen) serial.Close(); } catch { }
            try { serial.Dispose(); } catch { }
            serial = null;
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            if (serial == null || !serial.IsOpen) throw new IOException("串口未打开");
            try
            {
                return serial.Read(buffer, offset, count);
            }
            catch (TimeoutException)
            {
                return 0;
            }
        }

        public void Write(string text)
        {
            if (serial == null || !serial.IsOpen) throw new IOException("串口未打开");
            serial.Write(text);
        }
    }
}
