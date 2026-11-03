using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace BoatTool
{
    public class KnownBoard
    {
        public string Mac = "";
        public BoardRole Role = BoardRole.Unknown;
        public string FirstSeen = "";
        public string LastSeen = "";
        public string LastPort = "";
        public string Note = "";
    }

    // 角色记忆只按芯片 MAC 归属。串口号用于连接选择，绝不作为身份。
    public class RoleStore
    {
        private readonly string path;
        private readonly Dictionary<string, KnownBoard> boards = new Dictionary<string, KnownBoard>(StringComparer.Ordinal);
        public string LoadError = "";
        public bool Dirty;

        public RoleStore(string path)
        {
            this.path = path;
            Load();
        }

        public IEnumerable<KnownBoard> All
        {
            get { return boards.Values; }
        }

        public KnownBoard Find(string mac)
        {
            string key = MacText.Normalize(mac);
            if (key.Length == 0) return null;
            KnownBoard board;
            return boards.TryGetValue(key, out board) ? board : null;
        }

        public BoardRole RoleOf(string mac)
        {
            KnownBoard board = Find(mac);
            return board == null ? BoardRole.Unknown : board.Role;
        }

        public void Remember(string mac, BoardRole role, string port, string note)
        {
            string key = MacText.Normalize(mac);
            if (key.Length == 0) throw new ArgumentException("芯片 MAC 无效，不能登记角色");
            if (role == BoardRole.Unknown) throw new ArgumentException("必须指定小船或遥控器");
            KnownBoard board = Find(key);
            string now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            if (board == null)
            {
                board = new KnownBoard();
                board.Mac = key;
                board.FirstSeen = now;
                boards[key] = board;
            }
            board.Role = role;
            board.LastSeen = now;
            board.LastPort = port ?? "";
            if (!string.IsNullOrEmpty(note)) board.Note = note;
            Dirty = true;
            Save();
        }

        public void Touch(string mac, string port)
        {
            KnownBoard board = Find(mac);
            if (board == null) return;
            board.LastSeen = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            if (!string.IsNullOrEmpty(port)) board.LastPort = port;
            Dirty = true;
        }

        public void Forget(string mac)
        {
            string key = MacText.Normalize(mac);
            if (key.Length == 0) return;
            if (boards.Remove(key)) { Dirty = true; Save(); }
        }

        // 已知角色的板子互相配对时必须不同芯片；同一 MAC 不可能既是小船又是遥控器。
        public bool IsRoleConflict(string mac, BoardRole role)
        {
            BoardRole known = RoleOf(mac);
            return known != BoardRole.Unknown && known != role;
        }

        public void Save()
        {
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                List<object> items = new List<object>();
                foreach (KnownBoard board in boards.Values)
                {
                    Dictionary<string, object> item = new Dictionary<string, object>();
                    item["mac"] = board.Mac;
                    item["role"] = RoleNames.Code(board.Role);
                    item["first_seen"] = board.FirstSeen;
                    item["last_seen"] = board.LastSeen;
                    item["last_port"] = board.LastPort;
                    item["note"] = board.Note;
                    items.Add(item);
                }
                Dictionary<string, object> root = new Dictionary<string, object>();
                root["schema"] = 1;
                root["note"] = "角色按芯片 MAC 记录；串口号仅表示上次连接，不作为身份";
                root["boards"] = items;
                string temp = path + ".tmp";
                File.WriteAllText(temp, Json.Serialize(root), new UTF8Encoding(false));
                if (File.Exists(path)) File.Delete(path);
                File.Move(temp, path);
                Dirty = false;
            }
            catch (Exception ex)
            {
                LoadError = "角色记忆保存失败：" + ex.Message;
            }
        }

        private void Load()
        {
            boards.Clear();
            LoadError = "";
            if (!File.Exists(path)) return;
            try
            {
                Dictionary<string, object> root = Json.AsObject(Json.Parse(File.ReadAllText(path)));
                object listValue;
                if (!root.TryGetValue("boards", out listValue)) return;
                foreach (object entry in Json.AsArray(listValue))
                {
                    Dictionary<string, object> obj = Json.AsObject(entry);
                    KnownBoard board = new KnownBoard();
                    board.Mac = MacText.Normalize(Json.GetString(obj, "mac", ""));
                    board.Role = RoleNames.FromCode(Json.GetString(obj, "role", ""));
                    if (board.Mac.Length == 0 || board.Role == BoardRole.Unknown) continue;
                    board.FirstSeen = Json.GetString(obj, "first_seen", "");
                    board.LastSeen = Json.GetString(obj, "last_seen", "");
                    board.LastPort = Json.GetString(obj, "last_port", "");
                    board.Note = Json.GetString(obj, "note", "");
                    boards[board.Mac] = board;
                }
            }
            catch (Exception ex)
            {
                // 记忆文件损坏时不能猜角色，直接清空并要求重新选择。
                boards.Clear();
                LoadError = "角色记忆文件损坏，已忽略（不会按串口号猜角色）：" + ex.Message;
            }
        }
    }
}
