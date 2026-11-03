using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace BoatTool
{
    public class ExportResult
    {
        public bool Ok;
        public string ZipPath = "";
        public string Error = "";
        public string Sha256 = "";
        public long Bytes;
        public int FileCount;
    }

    // 导出：先停稳会话 → 生成稳定清单 → 临时 ZIP → 逐条目读全量内容核对路径/大小/SHA256 → 才算完成。
    // 失败一律保留原始会话目录，允许重试。
    public static class DiagnosticsExporter
    {
        public static ExportResult Export(CaptureSession session, AppPaths paths, string toolVersion,
                                          DateTime exportLocal)
        {
            ExportResult result = new ExportResult();
            if (session == null)
            {
                result.Error = "没有可导出的会话";
                return result;
            }

            // 已停止会话再次导出不得因重复 Flush 已关闭句柄报假错误。
            if (!session.IsClosed)
            {
                try { session.Close(exportLocal, "导出前停止"); }
                catch (Exception ex)
                {
                    // 关闭失败也要尝试继续导出已有文件；结果会在清单里体现。
                    session.RecordEvent("导出前关闭出现异常：" + ex.Message, exportLocal);
                }
            }

            string temp = Path.Combine(Path.GetDirectoryName(session.RootDir),
                Path.GetFileName(session.RootDir) + "-打包中-" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".zip");
            try
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
            catch (Exception ex)
            {
                result.Error = "无法创建临时 ZIP：" + ex.Message;
                return result;
            }

            try
            {
                WriteMetadata(session, toolVersion, exportLocal);
                // 稳定清单：相对路径、字节数、SHA256
                string manifestError;
                Dictionary<string, string> manifest = BuildManifest(session.RootDir, out manifestError);
                if (manifestError != null)
                {
                    result.Error = "生成文件清单失败：" + manifestError;
                    return result;
                }

                using (FileStream fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (ZipArchive zip = new ZipArchive(fs, ZipArchiveMode.Create))
                {
                    HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (KeyValuePair<string, string> kv in manifest)
                    {
                        if (!seen.Add(kv.Key))
                        {
                            result.Error = "导出中止：清单里有重复路径 " + kv.Key;
                            SafeDelete(temp);
                            return result;
                        }
                        AddFile(zip, session.RootDir, kv.Key);
                    }
                }
            }
            catch (Exception ex)
            {
                result.Error = "生成 ZIP 失败：" + ex.Message + "（原始记录保留在 " + session.RootDir + "）";
                SafeDelete(temp);
                return result;
            }

            // 真实校验：逐条目读取全部内容、比对路径集合/大小/SHA256。
            string verifyError = VerifyZipAgainstManifest(temp, session.RootDir);
            if (verifyError != null)
            {
                result.Error = "ZIP 校验失败：" + verifyError + "（原始记录保留在 " + session.RootDir + "）";
                SafeDelete(temp);
                return result;
            }

            result.Ok = true;
            result.ZipPath = temp;
            result.Bytes = new FileInfo(temp).Length;
            result.Sha256 = Sha256File(temp);
            result.FileCount = CountEntries(temp);
            return result;
        }

        // 生成稳定清单（相对路径 → sha256），并同时写出 文件清单与SHA256.txt。
        private static Dictionary<string, string> BuildManifest(string root, out string error)
        {
            error = null;
            Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            List<string> files;
            try
            {
                files = new List<string>(Directory.GetFiles(root, "*", SearchOption.AllDirectories));
                files.Sort(StringComparer.Ordinal);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return map;
            }

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("路径\t字节\tSHA256");
            foreach (string f in files)
            {
                string rel = f.Substring(root.Length + 1).Replace('\\', '/');
                if (rel == "文件清单与SHA256.txt") continue;
                if (rel.Contains("..")) { error = "非法路径：" + rel; return map; }
                string h;
                long size;
                try
                {
                    size = new FileInfo(f).Length;
                    h = Sha256File(f);
                }
                catch (Exception ex)
                {
                    error = "读取 " + rel + " 失败：" + ex.Message;
                    return map;
                }
                if (map.ContainsKey(rel)) { error = "重复路径：" + rel; return map; }
                map[rel] = h;
                sb.AppendLine(rel + "\t" + size.ToString(CultureInfo.InvariantCulture) + "\t" + h);
            }
            try
            {
                File.WriteAllText(Path.Combine(root, "文件清单与SHA256.txt"), sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                error = "写文件清单失败：" + ex.Message;
                return map;
            }
            // 清单本身也进包（内容固定，写入后再计算一次）
            map["文件清单与SHA256.txt"] = Sha256File(Path.Combine(root, "文件清单与SHA256.txt"));
            return map;
        }

        private static void AddFile(ZipArchive zip, string root, string rel)
        {
            string src = Path.Combine(root, rel.Replace('/', '\\'));
            ZipArchiveEntry entry = zip.CreateEntry(rel, CompressionLevel.Optimal);
            using (Stream dst = entry.Open())
            using (FileStream input = File.OpenRead(src))
            {
                input.CopyTo(dst);
            }
        }

        // 逐条目读全量内容并核对；不允许 Open 后立即关闭。
        private static string VerifyZipAgainstManifest(string zipPath, string root)
        {
            string mErr;
            Dictionary<string, string> expected = BuildManifest(root, out mErr);
            if (mErr != null) return "重建清单失败：" + mErr;

            try
            {
                using (FileStream fs = File.OpenRead(zipPath))
                using (ZipArchive zip = new ZipArchive(fs, ZipArchiveMode.Read))
                {
                    HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    HashSet<string> checkedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (ZipArchiveEntry e in zip.Entries)
                    {
                        string name = e.FullName.Replace('\\', '/');
                        if (name.EndsWith("/", StringComparison.Ordinal)) continue;
                        if (name.Contains("..")) return "越界路径：" + name;
                        if (!seen.Add(name)) return "重复路径：" + name;
                        string expectedHash;
                        if (!expected.TryGetValue(name, out expectedHash)) return "清单中没有该文件：" + name;

                        string actual;
                        long length = 0;
                        using (Stream s = e.Open())
                        {
                            using (SHA256 sha = SHA256.Create())
                            {
                                byte[] buffer = new byte[81920];
                                int n;
                                while ((n = s.Read(buffer, 0, buffer.Length)) > 0)
                                {
                                    sha.TransformBlock(buffer, 0, n, null, 0);
                                    length += n;
                                }
                                sha.TransformFinalBlock(new byte[0], 0, 0);
                                actual = Hex(sha.Hash);
                            }
                        }
                        if (actual != expectedHash) return "哈希不一致：" + name;
                        checkedNames.Add(name);
                    }
                    foreach (string name in expected.Keys)
                    {
                        if (!checkedNames.Contains(name)) return "ZIP 缺少文件：" + name;
                    }
                }
                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        private static string Hex(byte[] data)
        {
            StringBuilder sb = new StringBuilder(data.Length * 2);
            for (int i = 0; i < data.Length; i++) sb.Append(data[i].ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        private static void WriteMetadata(CaptureSession session, string toolVersion, DateTime exportLocal)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("ESP32 现场诊断导出");
            sb.AppendLine("格式版本：2");
            sb.AppendLine("会话：" + session.SessionId);
            sb.AppendLine("设备分段数：" + session.SegmentCount.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("记录开始（带时区）：" + session.StartedAt);
            sb.AppendLine("记录停止（带时区）：" + (string.IsNullOrEmpty(session.StoppedAt) ? "未停止" : session.StoppedAt));
            sb.AppendLine("停止原因：" + (string.IsNullOrEmpty(session.StopReason) ? "未知" : session.StopReason));
            sb.AppendLine("导出时间（带时区）：" + DiagTime.Now(exportLocal));
            sb.AppendLine("时区：" + TimeZoneInfo.Local.Id);
            sb.AppendLine("工具版本：" + toolVersion);
            sb.AppendLine("采集失败：" + (session.Failed ? session.FailureReason : "无"));
            sb.AppendLine("最近接收序号：" + session.LastSequence.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine();
            sb.AppendLine("说明：本导出只包含工具实际接收到的数据；不能据此证明设备从未丢日志。");
            sb.AppendLine("说明：FAULT 行按 r10 正式格式解析；“完整”只表示 CURRENT 与 PREVIOUS 两行配套，");
            sb.AppendLine("      不代表故障根因成立、动作被验证或问题已解决。");
            sb.AppendLine("说明：boot 未确认或设备分段未确认的故障行，一律不判为完整。");
            AppendMalformedSummary(session, sb);
            sb.AppendLine("说明：本导出不包含根因结论，也不代表问题已解决。");
            AppendFaultSummary(session, sb);
            File.WriteAllText(Path.Combine(session.RootDir, "导出说明.txt"), sb.ToString(), new UTF8Encoding(false));
        }

        private static int CountEntries(string zipPath)
        {
            using (FileStream fs = File.OpenRead(zipPath))
            using (ZipArchive zip = new ZipArchive(fs, ZipArchiveMode.Read))
            {
                return zip.Entries.Count;
            }
        }

        // 解析失败的 FAULT 行：原文与失败原因都在 FAULT快照.csv 里。
        // 这里给出条数与涉及字段，明确它们不参与关联、也不影响已有组。
        private static void AppendMalformedSummary(CaptureSession session, StringBuilder sb)
        {
            string file = Path.Combine(session.RootDir, "FAULT快照.csv");
            if (!File.Exists(file)) return;
            int malformed = 0;
            Dictionary<string, int> fields = new Dictionary<string, int>(StringComparer.Ordinal);
            List<string[]> rows = ReadCsvRows(file);
            bool first = true;
            foreach (string[] f in rows)
            {
                if (first) { first = false; continue; }
                // 列顺序：at, device, boot, near_seq, kind, id, adapter_state, note, raw
                if (f.Length < 8) continue;
                if (f[6] != "Malformed") continue;
                malformed++;
                string note = f[7];
                string field = "未指明";
                int idx = note.IndexOf("字段 ", StringComparison.Ordinal);
                if (idx >= 0)
                {
                    int end = note.IndexOf(' ', idx + 3);
                    field = end > idx ? note.Substring(idx + 3, end - idx - 3) : note.Substring(idx + 3);
                }
                int n;
                fields.TryGetValue(field, out n);
                fields[field] = n + 1;
            }
            if (malformed == 0) return;
            sb.AppendLine();
            sb.AppendLine("解析失败行数：" + malformed.ToString(CultureInfo.InvariantCulture) +
                          "（原文与失败原因见 FAULT快照.csv；这些行不作为有效证据，未参与关联）");
            foreach (KeyValuePair<string, int> kv in fields)
                sb.AppendLine("  字段 " + kv.Key + "：" + kv.Value.ToString(CultureInfo.InvariantCulture) + " 行");
        }

        // 故障关联汇总：只从磁盘上的 FAULT关联.csv / FAULT关联状态变化.csv 读取，
        // 不依赖内存近期窗口，因此窗口淘汰不会让历史组“消失”或错误地变成缺行。
        private static void AppendFaultSummary(CaptureSession session, StringBuilder sb)
        {
            sb.AppendLine();
            sb.AppendLine("—— 故障关联（从磁盘记录重建，不依赖内存窗口）——");
            string groupFile = Path.Combine(session.RootDir, "FAULT关联.csv");
            if (!File.Exists(groupFile))
            {
                sb.AppendLine("本次会话没有故障关联记录。");
                return;
            }

            // 按 (device|boot|id) 取“最后一次写入”的状态，并用状态等级保证冲突/完整不会被后面的
            // 低等级行覆盖（与运行期锁存规则一致）。
            Dictionary<string, string> lastState = new Dictionary<string, string>(StringComparer.Ordinal);
            Dictionary<string, int> rank = new Dictionary<string, int>(StringComparer.Ordinal);
            List<string[]> rows = ReadCsvRows(groupFile);
            bool first = true;
            foreach (string[] f in rows)
            {
                if (first) { first = false; continue; }        // 跳过表头
                if (f.Length < 7) continue;
                if (string.IsNullOrEmpty(f[5])) continue;      // 无 id 的行不构成关联组
                if (f[6] == "capacity_limit") continue;        // 容量提示不是组
                string key = f[1] + "||" + f[3] + "||" + f[5];
                string state = f[6];
                int r = FaultStateRank(state);
                int prev;
                if (!rank.TryGetValue(key, out prev) || r >= prev)
                {
                    rank[key] = r;
                    lastState[key] = state;
                }
            }

            Dictionary<string, int> counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, string> kv in lastState)
            {
                int n;
                counts.TryGetValue(kv.Value, out n);
                counts[kv.Value] = n + 1;
            }
            sb.AppendLine("关联组数：" + lastState.Count.ToString(CultureInfo.InvariantCulture));
            foreach (KeyValuePair<string, int> kv in counts)
                sb.AppendLine("  " + kv.Key + "：" + kv.Value.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("说明：状态等级 complete < conflicting 的锁存规则与运行期一致；");
            sb.AppendLine("      boot_unconfirmed / device_unconfirmed 永远不会被判为 complete。");
            sb.AppendLine("明细见 FAULT关联.csv（含 CURRENT/PREVIOUS 接收序号与两行原文）与 FAULT关联状态变化.csv。");
        }

        private static int FaultStateRank(string state)
        {
            switch (state)
            {
                case "conflicting": return 4;
                case "complete": return 3;
                case "incomplete": return 2;
                case "boot_unconfirmed": return 1;
                case "device_unconfirmed": return 0;
                default: return -1;
            }
        }

        // 最小 CSV 读取（RFC4180，支持引号内逗号与双引号转义），只用于导出汇总。
        private static List<string[]> ReadCsvRows(string path)
        {
            List<string[]> rows = new List<string[]>();
            string text;
            try { text = File.ReadAllText(path, Encoding.UTF8); }
            catch { return rows; }
            List<string> fields = new List<string>();
            StringBuilder cur = new StringBuilder();
            bool inQuotes = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (inQuotes)
                {
                    if (c == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"') { cur.Append('"'); i++; }
                        else inQuotes = false;
                    }
                    else cur.Append(c);
                }
                else if (c == '"') inQuotes = true;
                else if (c == ',') { fields.Add(cur.ToString()); cur.Length = 0; }
                else if (c == '\n')
                {
                    fields.Add(cur.ToString()); cur.Length = 0;
                    rows.Add(fields.ToArray()); fields.Clear();
                }
                else if (c != '\r') cur.Append(c);
            }
            if (cur.Length > 0 || fields.Count > 0)
            {
                fields.Add(cur.ToString());
                rows.Add(fields.ToArray());
            }
            return rows;
        }

        private static void SafeDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        public static string Sha256File(string path)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream fs = File.OpenRead(path))
            {
                return Hex(sha.ComputeHash(fs));
            }
        }
    }
}
