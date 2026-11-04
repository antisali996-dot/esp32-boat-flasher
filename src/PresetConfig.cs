using System;
using System.Collections.Generic;
using System.IO;

namespace BoatTool
{
    // 分类包的交付配置（preset.json）。只描述“允许操作哪两块板、各自对端是谁”，
    // 不篡改固件，也不写芯片出厂 MAC。是否应用由用户运行时确认。
    public class PresetConfig
    {
        public string Id = "";
        public string Kind = "general";     // general / category
        public string Title = "";
        public string BoatMac = "";
        public string RemoteMac = "";
        public string BoatFwid = "";
        public string RemoteFwid = "";
        public string SuiteId = "";
        public List<string> Problems = new List<string>();

        public bool IsCategory { get { return string.Equals(Kind, "category", StringComparison.OrdinalIgnoreCase); } }
        public bool Usable { get { return Problems.Count == 0; } }

        public string BoatMacOrAny { get { return string.IsNullOrEmpty(BoatMac) ? "任意" : BoatMac; } }
        public string RemoteMacOrAny { get { return string.IsNullOrEmpty(RemoteMac) ? "任意" : RemoteMac; } }

        public static PresetConfig Load(string path)
        {
            PresetConfig preset = new PresetConfig();
            if (!File.Exists(path))
            {
                preset.Problems.Add("缺少 preset.json");
                return preset;
            }
            try
            {
                Dictionary<string, object> root = Json.AsObject(Json.Parse(File.ReadAllText(path)));
                preset.Id = Json.GetString(root, "id", "");
                preset.Kind = Json.GetString(root, "kind", "general");
                preset.Title = Json.GetString(root, "title", "");
                preset.SuiteId = Json.GetString(root, "suite_id", "");
                preset.BoatFwid = Json.GetString(root, "boat_fwid", "");
                preset.RemoteFwid = Json.GetString(root, "remote_fwid", "");
                string boat = Json.GetString(root, "boat_mac", "");
                string remote = Json.GetString(root, "remote_mac", "");
                if (!string.IsNullOrEmpty(boat))
                {
                    preset.BoatMac = MacText.Normalize(boat);
                    if (preset.BoatMac.Length == 0) preset.Problems.Add("preset 里的小船 MAC 无效：" + boat);
                }
                if (!string.IsNullOrEmpty(remote))
                {
                    preset.RemoteMac = MacText.Normalize(remote);
                    if (preset.RemoteMac.Length == 0) preset.Problems.Add("preset 里的遥控器 MAC 无效：" + remote);
                }
                if (preset.BoatMac.Length > 0 && preset.BoatMac == preset.RemoteMac)
                    preset.Problems.Add("preset 里小船与遥控器的 MAC 相同");
            }
            catch (Exception ex)
            {
                preset.Problems.Add("preset.json 无法解析：" + ex.Message);
            }
            return preset;
        }

        // 分类包必须核对实际芯片：地址不符禁止写入或绑定。
        public bool AllowsBoat(string mac, out string reason)
        {
            reason = "";
            if (!IsCategory || string.IsNullOrEmpty(BoatMac)) return true;
            if (MacText.Normalize(mac) != BoatMac)
            {
                reason = "这块板的芯片 MAC 是 " + mac + "，本分类包只允许小船 " + BoatMac;
                return false;
            }
            return true;
        }

        public bool AllowsRemote(string mac, out string reason)
        {
            reason = "";
            if (!IsCategory || string.IsNullOrEmpty(RemoteMac)) return true;
            if (MacText.Normalize(mac) != RemoteMac)
            {
                reason = "这块板的芯片 MAC 是 " + mac + "，本分类包只允许遥控器 " + RemoteMac;
                return false;
            }
            return true;
        }

        // 分类预设只强制角色与芯片 MAC；suite_id / boat_fwid / remote_fwid 只是
        // 打包时的推荐信息，不永久锁死可选版本。版本核对以当前所选套装的清单为准。
        //
        //   RecommendedSuiteExists(library)  推荐套装是否还在（不在只提示，不禁用工具）
        //   ApplySelection(...)              命中推荐且用户未选时给出推荐项；否则尊重用户选择
        //
        // 烧录身份核对一律使用所选套装的清单 FWID（见 Workflow），preset 的旧 FWID 只用于提示。
        public bool RecommendedSuiteExists(FirmwareLibrary library)
        {
            if (string.IsNullOrEmpty(SuiteId)) return true;
            return library != null && library.Find(SuiteId) != null;
        }

        public string RecommendText(FirmwareLibrary library)
        {
            if (string.IsNullOrEmpty(SuiteId)) return "";
            if (RecommendedSuiteExists(library)) return "本包推荐套装：" + SuiteId + "（可改选其它有效版本）";
            return "本包推荐套装 " + SuiteId + " 不在当前固件库里，请自行选择有效版本。";
        }

        // 兼容保留：仅在“套装就是推荐的那一套”时核对 FWID 一致性（提示打包错误用）。
        // 不再作为通用前置校验，也不会因此禁用工具。
        public void CrossCheck(FirmwareSuite suite)
        {
            if (suite == null) { Problems.Add("固件库没有可用套装"); return; }
            bool matchesRecommendation = string.IsNullOrEmpty(SuiteId) ||
                string.Equals(SuiteId, suite.Id, StringComparison.OrdinalIgnoreCase);
            if (!matchesRecommendation)
            {
                // 用户选了别的版本：不再报错。MAC 约束与烧录时的清单核对已覆盖安全。
                return;
            }
            FirmwareSpec boat = suite.SpecFor(BoardRole.Boat);
            FirmwareSpec remote = suite.SpecFor(BoardRole.Remote);
            if (!string.IsNullOrEmpty(BoatFwid) && (boat == null || boat.Fwid != BoatFwid))
                Problems.Add("preset 的推荐小船 FWID 与该套装清单不一致（打包错误）");
            if (!string.IsNullOrEmpty(RemoteFwid) && (remote == null || remote.Fwid != RemoteFwid))
                Problems.Add("preset 的推荐遥控器 FWID 与该套装清单不一致（打包错误）");
        }

        public string Describe()
        {
            if (!IsCategory) return "通用包：允许任意芯片（仍按 MAC 核对身份）";
            return Title + "：小船 " + BoatMac + "，遥控器 " + RemoteMac;
        }
    }
}
