using System;
using System.Collections.Generic;
using System.Text;

namespace BoatTool
{
    // 模拟串口字节流：按顺序把预设字节喂给状态机，并记录写入的命令。
    // 这里注入的是真实字节流，不是伪造的 WritePair 返回值。
    public sealed class ScriptedPairChannel : IPairChannel
    {
        private readonly Queue<byte[]> chunks = new Queue<byte[]>();
        private byte[] current = new byte[0];
        private int pos;
        public readonly List<string> Written = new List<string>();
        public int DiscardCount;

        // 立即送达一批字节（会在下一个 Read 返回）。
        public void Deliver(string text)
        {
            chunks.Enqueue(Encoding.ASCII.GetBytes(text));
        }

        // 延后送达：只有在本通道被读取 attempt 次之后才投递。
        public void DeliverAfter(string text, int reads)
        {
            delayed.Add(new KeyValuePair<int, byte[]>(readAttempts + Math.Max(1, reads), Encoding.ASCII.GetBytes(text)));
        }

        private readonly List<KeyValuePair<int, byte[]>> delayed = new List<KeyValuePair<int, byte[]>>();
        private int readAttempts;

        public void Write(string text)
        {
            Written.Add(text.TrimEnd('\r', '\n'));
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            readAttempts++;
            for (int i = delayed.Count - 1; i >= 0; i--)
            {
                if (delayed[i].Key <= readAttempts)
                {
                    chunks.Enqueue(delayed[i].Value);
                    delayed.RemoveAt(i);
                }
            }
            while (pos >= current.Length)
            {
                if (chunks.Count == 0) return 0;
                current = chunks.Dequeue();
                pos = 0;
            }
            int available = Math.Min(count, current.Length - pos);
            Array.Copy(current, pos, buffer, offset, available);
            pos += available;
            return available;
        }

        public void DiscardInput()
        {
            DiscardCount++;
            // 真实串口只丢掉“尚未读到”的数据；已排队的模拟字节保留，
            // 以便测试验证“清缓冲不能证明回应归属”。
        }

        public int BindCommands()
        {
            int n = 0;
            foreach (string w in Written) if (w.StartsWith("JAY BIND ", StringComparison.Ordinal)) n++;
            return n;
        }

        public int InfoCommands()
        {
            int n = 0;
            foreach (string w in Written) if (w == "JAY INFO") n++;
            return n;
        }
    }

    // 固定步进时钟：每次 Now 增加固定毫秒，Sleep 也推进时间，保证超时可控可复现。
    public sealed class StepClock : IClock
    {
        private DateTime now = new DateTime(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);
        private readonly int stepMs;
        public StepClock(int stepMs) { this.stepMs = stepMs; }
        public void Sleep(int milliseconds)
        {
            // 只推进一个步长，避免测试里被大步长直接耗尽整个超时窗口。
            now = now.AddMilliseconds(Math.Min(milliseconds, stepMs));
        }
        public DateTime Now { get { now = now.AddMilliseconds(stepMs); return now; } }
    }
}
