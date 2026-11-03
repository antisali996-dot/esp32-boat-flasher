using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace BoatTool
{
    // 极简 JSON 读写：只支持对象/数组/字符串/数字/布尔/null，避免额外程序集依赖。
    public static class Json
    {
        public static object Parse(string text)
        {
            int index = 0;
            object value = ParseValue(text, ref index);
            SkipWhitespace(text, ref index);
            if (index != text.Length) throw new FormatException("JSON 末尾存在多余内容");
            return value;
        }

        public static string Serialize(object value)
        {
            StringBuilder builder = new StringBuilder();
            WriteValue(builder, value, 0);
            return builder.ToString();
        }

        public static Dictionary<string, object> AsObject(object value)
        {
            Dictionary<string, object> dict = value as Dictionary<string, object>;
            if (dict == null) throw new FormatException("期望 JSON 对象");
            return dict;
        }

        public static List<object> AsArray(object value)
        {
            List<object> list = value as List<object>;
            if (list == null) throw new FormatException("期望 JSON 数组");
            return list;
        }

        public static string GetString(Dictionary<string, object> obj, string key, string fallback)
        {
            object value;
            if (obj == null || !obj.TryGetValue(key, out value) || value == null) return fallback;
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        public static int GetInt(Dictionary<string, object> obj, string key, int fallback)
        {
            object value;
            if (obj == null || !obj.TryGetValue(key, out value) || value == null) return fallback;
            if (value is double) return (int)(double)value;
            int parsed;
            if (int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out parsed)) return parsed;
            return fallback;
        }

        private static object ParseValue(string text, ref int index)
        {
            SkipWhitespace(text, ref index);
            if (index >= text.Length) throw new FormatException("JSON 意外结束");
            char c = text[index];
            if (c == '{') return ParseObject(text, ref index);
            if (c == '[') return ParseArray(text, ref index);
            if (c == '"') return ParseString(text, ref index);
            if (c == 't') { Expect(text, ref index, "true"); return true; }
            if (c == 'f') { Expect(text, ref index, "false"); return false; }
            if (c == 'n') { Expect(text, ref index, "null"); return null; }
            return ParseNumber(text, ref index);
        }

        private static Dictionary<string, object> ParseObject(string text, ref int index)
        {
            Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
            index++;
            SkipWhitespace(text, ref index);
            if (index < text.Length && text[index] == '}') { index++; return result; }
            while (true)
            {
                SkipWhitespace(text, ref index);
                if (index >= text.Length || text[index] != '"') throw new FormatException("JSON 对象缺少键");
                string key = ParseString(text, ref index);
                SkipWhitespace(text, ref index);
                if (index >= text.Length || text[index] != ':') throw new FormatException("JSON 对象缺少冒号");
                index++;
                result[key] = ParseValue(text, ref index);
                SkipWhitespace(text, ref index);
                if (index >= text.Length) throw new FormatException("JSON 对象意外结束");
                if (text[index] == ',') { index++; continue; }
                if (text[index] == '}') { index++; return result; }
                throw new FormatException("JSON 对象分隔符错误");
            }
        }

        private static List<object> ParseArray(string text, ref int index)
        {
            List<object> result = new List<object>();
            index++;
            SkipWhitespace(text, ref index);
            if (index < text.Length && text[index] == ']') { index++; return result; }
            while (true)
            {
                result.Add(ParseValue(text, ref index));
                SkipWhitespace(text, ref index);
                if (index >= text.Length) throw new FormatException("JSON 数组意外结束");
                if (text[index] == ',') { index++; continue; }
                if (text[index] == ']') { index++; return result; }
                throw new FormatException("JSON 数组分隔符错误");
            }
        }

        private static string ParseString(string text, ref int index)
        {
            index++;
            StringBuilder builder = new StringBuilder();
            while (index < text.Length)
            {
                char c = text[index++];
                if (c == '"') return builder.ToString();
                if (c != '\\') { builder.Append(c); continue; }
                if (index >= text.Length) break;
                char escape = text[index++];
                switch (escape)
                {
                    case '"': builder.Append('"'); break;
                    case '\\': builder.Append('\\'); break;
                    case '/': builder.Append('/'); break;
                    case 'b': builder.Append('\b'); break;
                    case 'f': builder.Append('\f'); break;
                    case 'n': builder.Append('\n'); break;
                    case 'r': builder.Append('\r'); break;
                    case 't': builder.Append('\t'); break;
                    case 'u':
                        if (index + 4 > text.Length) throw new FormatException("JSON Unicode 转义不完整");
                        builder.Append((char)int.Parse(text.Substring(index, 4), NumberStyles.HexNumber,
                            CultureInfo.InvariantCulture));
                        index += 4;
                        break;
                    default: throw new FormatException("JSON 非法转义");
                }
            }
            throw new FormatException("JSON 字符串未结束");
        }

        private static double ParseNumber(string text, ref int index)
        {
            int start = index;
            while (index < text.Length && "+-0123456789.eE".IndexOf(text[index]) >= 0) index++;
            if (start == index) throw new FormatException("JSON 数值非法");
            double value;
            if (!double.TryParse(text.Substring(start, index - start), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out value))
                throw new FormatException("JSON 数值非法");
            return value;
        }

        private static void Expect(string text, ref int index, string literal)
        {
            if (index + literal.Length > text.Length ||
                string.CompareOrdinal(text, index, literal, 0, literal.Length) != 0)
                throw new FormatException("JSON 字面量非法");
            index += literal.Length;
        }

        private static void SkipWhitespace(string text, ref int index)
        {
            while (index < text.Length && char.IsWhiteSpace(text[index])) index++;
        }

        private static void WriteValue(StringBuilder builder, object value, int depth)
        {
            if (value == null) { builder.Append("null"); return; }
            string text = value as string;
            if (text != null) { WriteString(builder, text); return; }
            if (value is bool) { builder.Append(((bool)value) ? "true" : "false"); return; }
            if (value is int || value is long) { builder.Append(Convert.ToString(value, CultureInfo.InvariantCulture)); return; }
            if (value is double || value is float)
            {
                builder.Append(((double)value).ToString("R", CultureInfo.InvariantCulture));
                return;
            }
            Dictionary<string, object> obj = value as Dictionary<string, object>;
            if (obj != null)
            {
                builder.Append('{');
                bool first = true;
                foreach (KeyValuePair<string, object> pair in obj)
                {
                    if (!first) builder.Append(',');
                    first = false;
                    WriteString(builder, pair.Key);
                    builder.Append(':');
                    WriteValue(builder, pair.Value, depth + 1);
                }
                builder.Append('}');
                return;
            }
            System.Collections.IEnumerable sequence = value as System.Collections.IEnumerable;
            if (sequence != null)
            {
                builder.Append('[');
                bool first = true;
                foreach (object item in sequence)
                {
                    if (!first) builder.Append(',');
                    first = false;
                    WriteValue(builder, item, depth + 1);
                }
                builder.Append(']');
                return;
            }
            WriteString(builder, Convert.ToString(value, CultureInfo.InvariantCulture));
        }

        private static void WriteString(StringBuilder builder, string text)
        {
            builder.Append('"');
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                switch (c)
                {
                    case '"': builder.Append("\\\""); break;
                    case '\\': builder.Append("\\\\"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    default:
                        if (c < 0x20) builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else builder.Append(c);
                        break;
                }
            }
            builder.Append('"');
        }
    }
}
