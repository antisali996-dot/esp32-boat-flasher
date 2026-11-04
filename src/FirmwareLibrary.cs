using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

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
        // 该套装的超速锁存状态（以 FWID 为准，见 FirmwareCatalog.SelfCheck）。
        public OverspeedLockout Lockout = OverspeedLockout.Unknown;
        public string CatalogDir = "";
        public string EsptoolPath = "";
        public FirmwareCatalog Catalog;
        public PartitionLayout BoatLayout;
        public PartitionLayout RemoteLayout;
        public List<string> Problems = new List<string>();
        // 提示（不禁止使用）：重复身份说明、同镜像多发布等。界面与自检报告都会显示。
        public List<string> Notes = new List<string>();
        // 加载时的应用镜像哈希快照（烧录前复核用：快照 vs 磁盘当前值）。
        // 不能在复核时重新读旧盘——那会自己跟自己比，检测不到加载后的替换。
        public string BoatAppHashSnapshot = "";
        public string RemoteAppHashSnapshot = "";
        // 清单与导入记录的加载时快照（元数据变化如契约/角色能力/写入地址也触发 changed）。
        public string ManifestHashSnapshot = "";
        public string ImportHashSnapshot = "";
        // 实际写入镜像的逐条快照（来自 FirmwareSpec.Images() 逐角色遍历，
        // 自动覆盖两端共享或独立的 boot_app0——不再手工列举字段）。
        public sealed class ImageSnapshot
        {
            public string Role;        // BOAT / REMOTE
            public string Kind;        // bootloader / partitions / boot_app0 / app
            public string RelPath;     // 相对"固件"目录
            public long Offset;        // 写入地址
            public long Length;        // 文件字节长度
            public string Sha256;
        }
        public readonly List<ImageSnapshot> ImageSnapshots = new List<ImageSnapshot>();
        public string EsptoolHashSnapshot = "";
        public string RemoteAppRelPath = "";

        public bool Usable { get { return Problems.Count == 0 && Catalog != null && Catalog.Problems.Count == 0; } }

        public string SpeedText
        {
            get { return TargetRpm > 0 ? TargetRpm + " 转" : "未知转速"; }
        }

        public string SteerText
        {
            get { return SteerDeg > 0 ? "左右各 " + SteerDeg + "°" : "角度未知"; }
        }

        public string LockoutText
        {
            get { return OverspeedLockoutText.Chinese(Lockout); }
        }

        // 下拉与说明里使用：明确区分“锁存开启 / 已关闭 / 未声明”。
        public string DisplayNameWithLockout
        {
            get { return DisplayName + " / " + LockoutText; }
        }

        // 下拉框里显示的一行文字：以清单为准，不用文件名。
        public string Display
        {
            get
            {
                return DisplayName + "（" + SpeedText + " / " + SteerText + "，" + LockoutText +
                       "，版本 " + Version + "）";
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
            suite.Lockout = suite.Catalog.ForRole(BoardRole.Boat) == null
                ? OverspeedLockout.Unknown
                : suite.Catalog.ForRole(BoardRole.Boat).Lockout;
            // 镜像哈希快照：只在本套自检基本通过时才有意义；即使有问题也先记下，
            // 复核时“快照 vs 磁盘”不一致仍然能检测到“加载后被替换”。
            FirmwareSpec boatSnapshot = suite.Catalog.ForRole(BoardRole.Boat);
            FirmwareSpec remoteSnapshot = suite.Catalog.ForRole(BoardRole.Remote);
            suite.BoatAppHashSnapshot = boatSnapshot == null ? "" : FirmwareLibrary.ComputeFileHash(boatSnapshot.AppPath);
            suite.RemoteAppHashSnapshot = remoteSnapshot == null ? "" : FirmwareLibrary.ComputeFileHash(remoteSnapshot.AppPath);
            suite.ManifestHashSnapshot = FirmwareLibrary.ComputeFileHash(manifestPath);
            suite.ImportHashSnapshot = File.Exists(importPath) ? FirmwareLibrary.ComputeFileHash(importPath) : "";
            // 实际写入镜像逐条快照：对每个角色遍历 FirmwareSpec.Images()（bootloader/
            // partitions/boot_app0/app 的真实写入序列），共享或独立文件都会各自成条。
            suite.ImageSnapshots.Clear();
            foreach (KeyValuePair<BoardRole, FirmwareSpec> pair in suite.Catalog.Roles)
            {
                FirmwareSpec spec = pair.Value;
                foreach (KeyValuePair<long, string> img in spec.Images())
                {
                    if (img.Key < 0 || string.IsNullOrEmpty(img.Value)) continue;
                    ImageSnapshot snap = new ImageSnapshot();
                    snap.Role = RoleNames.Code(spec.Role);
                    snap.Kind = Path.GetFileNameWithoutExtension(img.Value);
                    snap.RelPath = FirmwareLibrary.MakeRelative(suite.CatalogDir, img.Value);
                    snap.Offset = img.Key;
                    FileInfo info = new FileInfo(img.Value);
                    snap.Length = info.Exists ? info.Length : -1;
                    snap.Sha256 = FirmwareLibrary.ComputeFileHash(img.Value);
                    suite.ImageSnapshots.Add(snap);
                }
            }
            string esptoolPath = Path.Combine(suiteDir, "工具", "esptool.exe");
            suite.EsptoolHashSnapshot = File.Exists(esptoolPath) ? FirmwareLibrary.ComputeFileHash(esptoolPath) : "";
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

            CheckDuplicates(library);

            // 至少要有两套可切换的套装，才算“套装选择”功能可用；只有一套也算合法，但要在界面说明。
            return library;
        }

        // 重复身份处理（分三类，不一刀切）：
        //   A. 两个目录套装 ID 相同           → 冲突，全部标不可用，不自动覆盖或任选。
        //   B. 不同 ID、同角色 FWID 相同、镜像哈希相同 → 允许并存，只在提示里说明“同一镜像的两个发布套装”。
        //   C. 不同 ID、同角色 FWID 相同、镜像哈希不同 → 保留，但标上“身份相同、镜像不同”提示：
        //      不能仅凭板上 FWID 判断“已是所选镜像”，必须用户明确选择。
        //   D. 角色错误 / FWID 不在镜像 / 哈希不符 → 已由各套自检拒绝，这里不重复。
        public static void CheckDuplicates(FirmwareLibrary library)
        {
            if (library == null) return;

            // A. 同 ID
            Dictionary<string, List<FirmwareSuite>> byId =
                new Dictionary<string, List<FirmwareSuite>>(StringComparer.OrdinalIgnoreCase);
            foreach (FirmwareSuite suite in library.Suites)
            {
                List<FirmwareSuite> list;
                if (!byId.TryGetValue(suite.Id, out list)) { list = new List<FirmwareSuite>(); byId[suite.Id] = list; }
                list.Add(suite);
            }
            foreach (KeyValuePair<string, List<FirmwareSuite>> kv in byId)
            {
                if (kv.Value.Count < 2) continue;
                foreach (FirmwareSuite suite in kv.Value)
                    suite.Problems.Add("固件库里有两个同名套装 ID：" + kv.Key +
                                       "（目录 " + suite.CatalogDir + "）。请只保留一个，或各自改 import.json 里的 id。");
            }

            // B/C. 不同 ID、同角色同 FWID：按实际镜像哈希区分。
            Dictionary<string, List<FirmwareSuite>> byFwid =
                new Dictionary<string, List<FirmwareSuite>>(StringComparer.Ordinal);
            foreach (FirmwareSuite suite in library.Suites)
            {
                foreach (BoardRole role in new BoardRole[] { BoardRole.Boat, BoardRole.Remote })
                {
                    FirmwareSpec spec = suite.SpecFor(role);
                    if (spec == null || string.IsNullOrEmpty(spec.Fwid)) continue;
                    string key = RoleNames.Code(role) + ":" + spec.Fwid;
                    List<FirmwareSuite> list;
                    if (!byFwid.TryGetValue(key, out list)) { list = new List<FirmwareSuite>(); byFwid[key] = list; }
                    list.Add(suite);
                }
            }
            foreach (KeyValuePair<string, List<FirmwareSuite>> kv in byFwid)
            {
                if (kv.Value.Count < 2) continue;
                // 按 id 去重后的套装参与比较（同 ID 已在 A 中处理）。
                List<FirmwareSuite> distinct = new List<FirmwareSuite>();
                foreach (FirmwareSuite s in kv.Value)
                {
                    bool seen = false;
                    foreach (FirmwareSuite x in distinct)
                        if (string.Equals(x.Id, s.Id, StringComparison.OrdinalIgnoreCase)) { seen = true; break; }
                    if (!seen) distinct.Add(s);
                }
                if (distinct.Count < 2) continue;
                foreach (BoardRole role in new BoardRole[] { BoardRole.Boat, BoardRole.Remote })
                {
                    FirmwareSpec first = distinct[0].SpecFor(role);
                    if (first == null || string.IsNullOrEmpty(first.Fwid) ||
                        first.Fwid != distinct[0].SpecFor(role).Fwid) continue;
                    string key = RoleNames.Code(role) + ":" + first.Fwid;
                    if (!byFwid.ContainsKey(key) || byFwid[key].Count < 2) continue;

                    string hash0 = ImageHash(distinct[0].SpecFor(role));
                    bool identical = true;
                    foreach (FirmwareSuite s in distinct)
                    {
                        if (ImageHash(s.SpecFor(role)) != hash0) { identical = false; break; }
                    }
                    string noteText = identical
                        ? RoleNames.Chinese(role) + " FWID " + first.Fwid +
                          " 与其它套装相同，且镜像哈希一致（同一镜像的发布套装）。"
                        : RoleNames.Chinese(role) + " FWID " + first.Fwid +
                          " 与其它套装相同，但镜像哈希不同：板上 FWID 一致不能证明镜像一致，" +
                          "请确认要写入哪一套；重刷请勾选“即使已匹配也重刷”。";
                    foreach (FirmwareSuite s in distinct)
                        if (!s.Notes.Contains(noteText)) s.Notes.Add(noteText);
                }
            }
        }

        private static string ImageHash(FirmwareSpec spec)
        {
            return spec == null ? "" : ComputeFileHash(spec.AppPath);
        }

        // 相对套装"固件"目录的路径（用于快照比对路径变化）。
        public static string MakeRelative(string baseDir, string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath)) return "";
            string b = Path.GetFullPath(baseDir ?? "").TrimEnd(Path.DirectorySeparatorChar);
            string f = Path.GetFullPath(fullPath);
            if (f.StartsWith(b, StringComparison.OrdinalIgnoreCase))
            {
                string rel = f.Substring(b.Length).TrimStart(Path.DirectorySeparatorChar);
                return rel.Replace('\\', '/');
            }
            return f;   // 越界路径原样记录（IsSafeRelative 已拒绝，正常不会到这里）
        }

        public static string ComputeFileHash(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return "";
            using (System.Security.Cryptography.SHA256 sha = System.Security.Cryptography.SHA256.Create())
            using (FileStream fs = File.OpenRead(path))
            {
                byte[] h = sha.ComputeHash(fs);
                StringBuilder sb = new StringBuilder(h.Length * 2);
                foreach (byte b in h) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        // 套装内容指纹：只用“加载时的快照字段”，绝不现场重读磁盘。
        // 现场重读会让新旧都读到替换后的内容，错误地判定 keep（复审复现）。
        // 覆盖：导入 ID、清单版本、两端应用镜像哈希、清单与导入记录内容哈希
        //（元数据如诊断契约/角色能力/写入地址变化都会体现为清单文件哈希变化）。
        public static string SuiteImageStamp(FirmwareSuite suite)
        {
            if (suite == null) return "";
            StringBuilder sb = new StringBuilder();
            sb.Append("id=").Append(suite.Id).Append(';');
            sb.Append("ver=").Append(suite.Version).Append(';');
            sb.Append("manifest=").Append(suite.ManifestHashSnapshot).Append(';');
            sb.Append("import=").Append(suite.ImportHashSnapshot).Append(';');
            // 镜像逐条快照（角色/路径/地址/长度/SHA256），排序后拼接保证稳定顺序。
            List<FirmwareSuite.ImageSnapshot> imgs = new List<FirmwareSuite.ImageSnapshot>(suite.ImageSnapshots);
            imgs.Sort(delegate(FirmwareSuite.ImageSnapshot a, FirmwareSuite.ImageSnapshot b)
            {
                int c = string.CompareOrdinal(a.Role ?? "", b.Role ?? "");
                if (c != 0) return c;
                c = string.CompareOrdinal(a.Kind ?? "", b.Kind ?? "");
                if (c != 0) return c;
                return a.Offset.CompareTo(b.Offset);
            });
            foreach (FirmwareSuite.ImageSnapshot img in imgs)
            {
                sb.Append("img=").Append(img.Role).Append(':').Append(img.Kind)
                  .Append(':').Append(img.RelPath).Append('@').Append(img.Offset)
                  .Append(':').Append(img.Length).Append(':').Append(img.Sha256).Append(';');
            }
            sb.Append("esptool=").Append(suite.EsptoolHashSnapshot).Append(';');
            return sb.ToString();
        }

        // 刷新固件库的共享生产入口：GUI（MainForm.RefreshLibrary）与测试都调用这里，
        // 保证“替换库 + 通知所有使用方 + 清除能力缓存 + 处理旧选择”只有一份实现。
        // 返回决策（keep/missing/changed/unusable/blocked）；newSelection 为应保留的新库对象（null=清除）。
        public static string ApplyFirmwareLibraryRefresh(
            FirmwareLibrary currentLib,
            FirmwareLibrary freshLib,
            FirmwareSuite previousSelection,
            Action<FirmwareLibrary> bindLibrary,
            out FirmwareSuite newSelection)
        {
            newSelection = null;
            if (freshLib == null) return "blocked";
            // 1) 统一替换：MainForm.library、Workflow、诊断控制器都通过 bindLibrary 收到同一个新库；
            //    诊断控制器已创建时绑定新库并清除能力缓存（未创建时不创建、不开串口）。
            if (bindLibrary != null) bindLibrary(freshLib);

            // 2) 旧选择处理：不存在 → missing；内容变化 → changed；自检失败 → unusable；否则保留。
            if (previousSelection == null) return "keep";   // 无旧选择，无可保留对象
            FirmwareSuite kept = null;
            foreach (FirmwareSuite s in freshLib.Suites)
                if (string.Equals(s.Id, previousSelection.Id, StringComparison.OrdinalIgnoreCase))
                { kept = s; break; }
            string oldStamp = SuiteImageStamp(previousSelection);
            string newStamp = kept == null ? "" : SuiteImageStamp(kept);
            string decision = RefreshDecision(previousSelection, oldStamp, kept, newStamp);
            if (decision == "keep") newSelection = kept;
            return decision;
        }

        // 刷新后的下拉重建协调（GUI 与测试共用同一实现）：
        //   1) 抑制 SelectedIndexChanged，重建全部下拉项（新库对象）；
        //   2) 按决策恢复/清除选中：keep→新库对象；其余→-1；
        //   3) 恢复事件并执行一次统一回调（写回 Workflow.SelectedSuite、刷新摘要/按钮/备注/诊断显示）；
        // 不从旧下拉项恢复旧对象；newSelection 为空时绝不访问其成员。
        // 选择器抽象：host 提供“读选中项/清空/追加/设选中/恢复事件 + 统一回调”。
        public class SuiteChoiceHost
        {
            public Func<FirmwareSuite> GetSelected;
            public Action ClearItems;
            public Action<FirmwareSuite> AddItem;
            public Action<int> SetSelectedIndex;
            public Action ResumeEvents;
            public Action OnSelectionFinalized;   // 统一回调（等价 OnSuiteChanged）
        }

        public static void RebuildSuiteChoicesAfterRefresh(
            SuiteChoiceHost host, FirmwareLibrary freshLib,
            string decision, FirmwareSuite newSelection)
        {
            if (host == null || host.ClearItems == null || host.AddItem == null ||
                host.SetSelectedIndex == null || host.ResumeEvents == null) return;

            // 1) 清空并按新库重建全部项目（重建期间不触发选择事件——事件已由 host 抑制）。
            host.ClearItems();
            foreach (FirmwareSuite suite in freshLib.Suites) host.AddItem(suite);

            // 2) 按决策设置选中项（keep 且有新对象才选中；其余一律 -1）。
            int targetIndex = -1;
            if (decision == "keep" && newSelection != null)
            {
                for (int i = 0; i < freshLib.Suites.Count; i++)
                {
                    if (ReferenceEquals(freshLib.Suites[i], newSelection)) { targetIndex = i; break; }
                }
            }
            host.SetSelectedIndex(targetIndex);   // -1 = 无选中

            // 3) 恢复事件 + 统一回调一次（此刻 GetSelected 返回的就是最终对象或 null）。
            host.ResumeEvents();
            if (host.OnSelectionFinalized != null) host.OnSelectionFinalized();
        }

        // 刷新后选择去留的统一判定（GUI 与测试共用）：keep / missing / changed / unusable。
        //   previous      刷新前用户选的套装对象
        //   previousStamp 刷新前该套装的镜像指纹
        //   kept          fresh 库里同 ID 的套装（null = 目录已不存在）
        //   keptStamp     kept 现在的镜像指纹
        public static string RefreshDecision(FirmwareSuite previous, string previousStamp,
                                             FirmwareSuite kept, string keptStamp)
        {
            if (kept == null) return "missing";
            if (!string.Equals(keptStamp, previousStamp, StringComparison.Ordinal)) return "changed";
            if (!kept.Usable) return "unusable";
            return "keep";
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
