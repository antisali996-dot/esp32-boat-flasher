using System;
using System.Collections.Generic;
using System.Text;

namespace BoatTool
{
    // 流式 UTF-8 行组装器。
    //
    // 供“连接识别”和“记录采集”共用：
    //   * 跨块保留未完成的半行（不丢字节、不提前解析）；
    //   * 一块多行时按顺序逐条产出；
    //   * 中文可跨字节块（使用带状态的 Decoder）；
    //   * 超长行不静默截断：整行原文保留，另附“解析限制”标记。
    public sealed class StreamingLineAssembler
    {
        public const int MaxLineChars = 4096;      // 解析可接受的最大行长
        public const int MaxBufferedChars = 262144; // 超过此长度仍未换行：记录截断标记，只保留尾部

        private readonly StringBuilder partial = new StringBuilder();
        private readonly Decoder decoder;
        private readonly List<string> overflowNotices = new List<string>();
        private bool markedOverflow;

        public StreamingLineAssembler()
        {
            decoder = new UTF8Encoding(false, false).GetDecoder();
        }

        // 已缓冲但尚未遇到换行的字符数（用于“停止时残行”判断）。
        public int PendingChars { get { return partial.Length; } }
        public bool HasPending { get { return partial.Length > 0; } }

        // 是否有过超长行（解析限制）。
        public bool HadOverflow { get { return markedOverflow; } }
        public IList<string> OverflowNotices { get { return overflowNotices; } }

        // 输入一块原始字节，返回本次新产生的完整行（按接收顺序）。
        public List<string> Feed(byte[] data, int count)
        {
            List<string> lines = new List<string>();
            if (count <= 0) return lines;

            char[] chars = new char[Math.Max(1, count)];
            int produced = decoder.GetChars(data, 0, count, chars, 0, false);
            for (int i = 0; i < produced; i++)
            {
                char c = chars[i];
                if (c == '\r') continue;
                if (c == '\n')
                {
                    lines.Add(FinishLine());
                    continue;
                }
                partial.Append(c);
                if (partial.Length > MaxBufferedChars)
                {
                    // 极端情况：单行超过缓冲上限。保留尾部原文并记录解析限制，绝不丢弃整行证据。
                    overflowNotices.Add("单行超过 " + MaxBufferedChars + " 字符，仅保留尾部；原始字节仍完整保存在 raw 文件中。");
                    markedOverflow = true;
                    partial.Remove(0, partial.Length - MaxLineChars);
                }
            }
            return lines;
        }

        private string FinishLine()
        {
            string line = partial.ToString();
            partial.Length = 0;
            if (line.Length > MaxLineChars && !markedOverflow)
            {
                overflowNotices.Add("单行长度 " + line.Length + " 超过解析上限 " + MaxLineChars +
                                    "，按解析限制处理；原始字节仍完整保存在 raw 文件中。");
                markedOverflow = true;
                return line;   // 整行原文仍然返回，解析方自行决定是否处理
            }
            return line;
        }

        // 把一段已有的半行原文放入缓冲（用于设备切换时移交），不视为完整行。
        public void Seed(string text)
        {
            if (!string.IsNullOrEmpty(text)) partial.Append(text);
        }

        // 当前残行原文（不取出，仅查看）。
        public string PendingText { get { return partial.ToString(); } }

        // 停止时取出残行（不带换行）。返回 null 表示没有残行。
        public string TakePending()
        {
            if (partial.Length == 0) return null;
            string s = partial.ToString();
            partial.Length = 0;
            return s;
        }

        // 切换设备分段时清空解码状态，避免旧设备半行拼到新设备。
        public void Reset()
        {
            partial.Length = 0;
            decoder.Reset();
        }
    }
}
