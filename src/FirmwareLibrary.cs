using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace BoatTool
{
    // 一个可选的固件套装（= firmware-library 里导入的一个发布包）。
    // 所有版本信息都来自导入时记录的发布清单，绝不按文件名猜。
    public class FirmwareSuite
    {
        public string Id = "";
        public string DisplayName = "";
        public string Version = "";
        public int TargetRpm = -1;
        public int SteerDeg = -1;
        public string CatalogDir = "";
        public string EsptoolPath = "";
        public FirmwareCatalog Catalog;
        public PartitionLayout BoatLayout;
        public PartitionLayout RemoteLayout;
        public List<string> Problems = new List<string>();

        public bool Usable { get { return Problems.Count == 0 && Catalog != null && Catalog.Problems.Count == 0; } }

        public string SpeedText
        {
            get { return TargetRpm > 0 ? TargetRpm + " 转" : "未知转速"; }
        }

        public string SteerText
        {
            get { return SteerDeg > 0 ? "左右各 " + SteerDeg + "°" : "角度未知"; }
        }

        // 下拉框里显示的一行文字：以清单为准，不用文件名。
        public string Display
        {
            get
            {
                return DisplayName + "（" + SpeedText + " / " + SteerText + "，版本 " + Version + "）";
            }
        }

        public FirmwareSpec SpecFor(BoardRole role)
        {
            return Catalog == null ? null : Catalog.ForRole(role);
        }

        public PartitionLayout LayoutFor(BoardRole role)
        {
            return role == BoardRole.Boat ? BoatLayout : (role == BoardRole.Remote ? RemoteLayout : null);
        }

        public static FirmwareSuite Load(string suiteDir)
        {
            FirmwareSuite suite = new FirmwareSuite();
            suite.Id = Path.GetFileName(suiteDir.TrimEnd(Path.DirectorySeparatorChar));
            suite.CatalogDir = Path.Combine(suiteDir, "固件");
            suite.EsptoolPath = Path.Combine(suiteDir, "工具", "esptool.exe");

            string manifestPath = Path.Combine(suite.CatalogDir, "固件清单.json");
            suite.Catalog = FirmwareCatalog.Load(manifestPath);
            foreach (string problem in suite.Catalog.Problems) suite.Problems.Add(problem);

            // import.json 是导入时的记录；与当前清单不一致说明被改动过。
            string importPath = Path.Combine(suiteDir, "import.json");
            if (!File.Exists(importPath))
            {
                suite.Problems.Add("缺少导入记录 import.json，无法确认这套固件的来源与版本");
            }
            else
            {
                try
                {
                    Dictionary<string, object> record = Json.AsObject(Json.Parse(File.ReadAllText(importPath)));
                    suite.Id = Json.GetString(record, "id", suite.Id);
                    string recordedVersion = Json.GetString(record, "version", "");
                    int recordedRpm = Json.GetInt(record, "target_rpm", -1);
                    int recordedSteer = Json.GetInt(record, "steer_deg", -1);
                    string recordedBoat = Json.GetString(record, "boat_fwid", "");
                    string recordedRemote = Json.GetString(record, "remote_fwid", "");

                    if (!string.IsNullOrEmpty(recordedVersion) && recordedVersion != suite.Catalog.Version)
                        suite.Problems.Add("导入记录版本 " + recordedVersion + " 与清单版本 " + suite.Catalog.Version + " 不一致");
                    if (recordedRpm > 0 && recordedRpm != suite.Catalog.TargetRpm)
                        suite.Problems.Add("导入记录目标转速 " + recordedRpm + " 与清单 " + suite.Catalog.TargetRpm + " 不一致");
                    if (recordedSteer > 0 && recordedSteer != suite.Catalog.SteerDeg)
                        suite.Problems.Add("导入记录转向角 " + recordedSteer + "° 与清单 " + suite.Catalog.SteerDeg + "° 不一致");
                    FirmwareSpec boat = suite.Catalog.ForRole(BoardRole.Boat);
                    FirmwareSpec remote = suite.Catalog.ForRole(BoardRole.Remote);
                    if (!string.IsNullOrEmpty(recordedBoat) && boat != null && recordedBoat != boat.Fwid)
                        suite.Problems.Add("导入记录小船 FWID 与清单不一致");
                    if (!string.IsNullOrEmpty(recordedRemote) && remote != null && recordedRemote != remote.Fwid)
                        suite.Problems.Add("导入记录遥控器 FWID 与清单不一致");
                }
                catch (Exception ex)
                {
                    suite.Problems.Add("导入记录无法解析：" + ex.Message);
                }
            }

            suite.Version = suite.Catalog.Version;
            suite.TargetRpm = suite.Catalog.TargetRpm;
            suite.SteerDeg = suite.Catalog.SteerDeg;
            suite.DisplayName = BuildDisplayName(suite);

            if (!File.Exists(suite.EsptoolPath))
                suite.Problems.Add("这套固件缺少烧录组件：工具\\esptool.exe");

            // 分区布局：确认“保留 NVS 配对区”这个承诺在本套装上成立。
            FirmwareSpec boatSpec = suite.Catalog.ForRole(BoardRole.Boat);
            FirmwareSpec remoteSpec = suite.Catalog.ForRole(BoardRole.Remote);
            if (boatSpec != null)
            {
                suite.BoatLayout = PartitionLayout.Analyze(boatSpec.PartitionsPath, boatSpec.AppOffset,
                    File.Exists(boatSpec.AppPath) ? new FileInfo(boatSpec.AppPath).Length : 0, boatSpec);
                if (!string.IsNullOrEmpty(suite.BoatLayout.Error))
                    suite.Problems.Add("小船分区布局不兼容：" + suite.BoatLayout.Error);
            }
            if (remoteSpec != null)
            {
                suite.RemoteLayout = PartitionLayout.Analyze(remoteSpec.PartitionsPath, remoteSpec.AppOffset,
                    File.Exists(remoteSpec.AppPath) ? new FileInfo(remoteSpec.AppPath).Length : 0, remoteSpec);
                if (!string.IsNullOrEmpty(suite.RemoteLayout.Error))
                    suite.Problems.Add("遥控器分区布局不兼容：" + suite.RemoteLayout.Error);
            }

            // 两套镜像不能混用：本套装的小船和遥控器必须都来自同一个清单，
            // 且各自应用镜像里真的含清单声明的 FWID（FirmwareCatalog 已校验一次，这里做交叉确认）。
            if (boatSpec != null && remoteSpec != null)
            {
                if (boatSpec.Fwid == remoteSpec.Fwid)
                    suite.Problems.Add("小船与遥控器的 FWID 相同，镜像可能放错");
                FwidInfo boatFwid = FwidInfo.Parse(boatSpec.Fwid);
                FwidInfo remoteFwid = FwidInfo.Parse(remoteSpec.Fwid);
                if (boatFwid.Role != BoardRole.Boat) suite.Problems.Add("小船 FWID 里不是小船角色：" + boatSpec.Fwid);
                if (remoteFwid.Role != BoardRole.Remote) suite.Problems.Add("遥控器 FWID 里不是遥控器角色：" + remoteSpec.Fwid);
                if (!FwidInfo.SameSteeringContract(boatFwid, remoteFwid))
                    suite.Problems.Add("小船与遥控器的转向契约不一致，两端不能配套使用");
                if (boatFwid.Parsed && boatFwid.TargetRpm != suite.TargetRpm)
                    suite.Problems.Add("小船 FWID 目标转速 " + boatFwid.TargetRpm + " 与清单 " + suite.TargetRpm + " 不一致");
            }
            return suite;
        }

        private static string BuildDisplayName(FirmwareSuite suite)
        {
            int rpm = suite.TargetRpm;
            int steer = suite.SteerDeg;
            if (rpm > 0 && steer > 0) return rpm + " 转 / " + steer + "°";
            return "套装 " + suite.Id;
        }
    }

    // 固件库：扫描 固件库根目录 下每个导入的套装。
    public class FirmwareLibrary
    {
        public List<FirmwareSuite> Suites = new List<FirmwareSuite>();
        public List<string> Problems = new List<string>();
        public string Root = "";

        public static FirmwareLibrary Load(string root)
        {
            FirmwareLibrary library = new FirmwareLibrary();
            library.Root = root;
            if (!Directory.Exists(root))
            {
                library.Problems.Add("缺少固件库目录：" + root + "。请使用完整解压的 ZIP，不要单独复制 EXE。");
                return library;
            }

            string[] dirs = Directory.GetDirectories(root);
            Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);
            foreach (string dir in dirs)
            {
                // 跳过隐藏目录与临时目录。
                string name = Path.GetFileName(dir);
                if (name.StartsWith(".", StringComparison.Ordinal)) continue;
                if (!File.Exists(Path.Combine(dir, "import.json")) &&
                    !File.Exists(Path.Combine(dir, "固件", "固件清单.json"))) continue;

                FirmwareSuite suite = FirmwareSuite.Load(dir);
                library.Suites.Add(suite);
            }

            if (library.Suites.Count == 0)
                library.Problems.Add("固件库里没有可用的固件套装。请把发布包导入 firmware-library 后再打包。");

            // 同一库内 id 与 FWID 都不能重复，否则无法判断“镜像混用”。
            Dictionary<string, string> seenIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> seenFwids = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (FirmwareSuite suite in library.Suites)
            {
                string previous;
                if (seenIds.TryGetValue(suite.Id, out previous))
                    suite.Problems.Add("固件库里有两个同名套装 ID：" + suite.Id);
                else seenIds[suite.Id] = suite.Id;

                foreach (BoardRole role in new BoardRole[] { BoardRole.Boat, BoardRole.Remote })
                {
                    FirmwareSpec spec = suite.SpecFor(role);
                    if (spec == null || string.IsNullOrEmpty(spec.Fwid)) continue;
                    string key = RoleNames.Code(role) + ":" + spec.Fwid;
                    if (seenFwids.TryGetValue(key, out previous) && previous != suite.Id)
                        suite.Problems.Add(RoleNames.Chinese(role) + " FWID 与套装 " + previous + " 重复，镜像来源可能混淆：" + spec.Fwid);
                    else seenFwids[key] = suite.Id;
                }
            }

            // 至少要有两套可切换的套装，才算“套装选择”功能可用；只有一套也算合法，但要在界面说明。
            return library;
        }

        public FirmwareSuite Find(string id)
        {
            foreach (FirmwareSuite suite in Suites)
                if (string.Equals(suite.Id, id, StringComparison.OrdinalIgnoreCase)) return suite;
            return null;
        }

        // 按板上的 FWID 找出它属于哪个套装；找不到返回 null（可能是不认识的旧固件）。
        public FirmwareSuite FindByFwid(BoardRole role, string fwid)
        {
            if (string.IsNullOrEmpty(fwid) || role == BoardRole.Unknown) return null;
            foreach (FirmwareSuite suite in Suites)
            {
                FirmwareSpec spec = suite.SpecFor(role);
                if (spec != null && string.Equals(spec.Fwid, fwid, StringComparison.Ordinal)) return suite;
            }
            return null;
        }

        // 找出所有使用该 FWID 的套装（用于提示“这块板属于另一套”）。
        public List<FirmwareSuite> AllWithFwid(BoardRole role, string fwid)
        {
            List<FirmwareSuite> matches = new List<FirmwareSuite>();
            if (string.IsNullOrEmpty(fwid)) return matches;
            foreach (FirmwareSuite suite in Suites)
            {
                FirmwareSpec spec = suite.SpecFor(role);
                if (spec != null && string.Equals(spec.Fwid, fwid, StringComparison.Ordinal)) matches.Add(suite);
            }
            return matches;
        }
    }
}
