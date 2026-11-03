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

        // 与所选套装交叉核对：分类包声明的 FWID 必须就是本包固件。
        public void CrossCheck(FirmwareSuite suite)
        {
            if (suite == null) { Problems.Add("固件库没有可用套装"); return; }
            if (!string.IsNullOrEmpty(SuiteId) &&
                !string.Equals(SuiteId, suite.Id, StringComparison.OrdinalIgnoreCase))
                Problems.Add("preset 期望套装 " + SuiteId + "，实际是 " + suite.Id);
            FirmwareSpec boat = suite.SpecFor(BoardRole.Boat);
            FirmwareSpec remote = suite.SpecFor(BoardRole.Remote);
            if (!string.IsNullOrEmpty(BoatFwid) && (boat == null || boat.Fwid != BoatFwid))
                Problems.Add("preset 的小船 FWID 与本包固件不一致");
            if (!string.IsNullOrEmpty(RemoteFwid) && (remote == null || remote.Fwid != RemoteFwid))
                Problems.Add("preset 的遥控器 FWID 与本包固件不一致");
        }

        public string Describe()
        {
            if (!IsCategory) return "通用包：允许任意芯片（仍按 MAC 核对身份）";
            return Title + "：小船 " + BoatMac + "，遥控器 " + RemoteMac;
        }
    }
}
