using System;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace BoatTool
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            string root = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            if (args.Length > 0 && string.Equals(args[0], "--self-check", StringComparison.OrdinalIgnoreCase))
            {
                Environment.ExitCode = RunSelfCheck(root);
                return;
            }

            AppPaths paths = AppPaths.Detect();
            FirmwareLibrary library = FirmwareLibrary.Load(paths.FirmwareLibraryDir);
            RoleStore roles = new RoleStore(paths.RoleStorePath);

            // 分类包带 preset.json；通用包没有这个文件，按任意芯片处理（仍核对 MAC）。
            PresetConfig preset = null;
            if (File.Exists(paths.PresetPath))
            {
                preset = PresetConfig.Load(paths.PresetPath);
                // 按 preset 声明的套装 ID 找对应套装，而不是取“第一个可用套装”：
                // 固件库里同时有 r10 和 r11 时，目录顺序会把 r10 排在前，按序号取会误判。
                FirmwareSuite target = null;
                if (!string.IsNullOrEmpty(preset.SuiteId))
                {
                    foreach (FirmwareSuite s in library.Suites)
                        if (string.Equals(s.Id, preset.SuiteId, StringComparison.OrdinalIgnoreCase))
                        { target = s; break; }
                }
                if (target == null)
                {
                    foreach (FirmwareSuite s in library.Suites) if (s.Usable) { target = s; break; }
                }
                preset.CrossCheck(target);
            }

            StringBuilder warn = new StringBuilder();
            foreach (string problem in library.Problems) warn.AppendLine("· " + problem);
            foreach (FirmwareSuite suite in library.Suites)
            {
                foreach (string problem in suite.Problems) warn.AppendLine("· 套装「" + suite.Id + "」：" + problem);
            }
            bool anyUsable = false;
            foreach (FirmwareSuite suite in library.Suites) if (suite.Usable) { anyUsable = true; break; }

            if (warn.Length > 0)
            {
                warn.Insert(0, "固件库自检发现问题，相关套装会显示为不可用（不可用的套装不能用来烧录）：" +
                               Environment.NewLine + Environment.NewLine);
                warn.AppendLine();
                if (!anyUsable) warn.AppendLine("当前没有任何可用套装，烧录按钮已禁用。请使用完整解压的 ZIP。");
                MessageBox.Show(warn.ToString(), anyUsable ? "部分套装不可用" : "没有可用固件套装",
                    MessageBoxButtons.OK, anyUsable ? MessageBoxIcon.Warning : MessageBoxIcon.Error);
            }

            HardwareTransport transport = new HardwareTransport(paths, new SystemClock());
            MainForm form = new MainForm(paths, library, roles, transport);
            if (preset != null)
            {
                if (preset.Problems.Count > 0)
                {
                    MessageBox.Show("分类包预设自检未通过，已禁用烧录与配对：" + Environment.NewLine +
                                    string.Join(Environment.NewLine, preset.Problems.ToArray()),
                        "分类包配置有误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                else
                {
                    form.ApplyPreset(preset);
                }
            }
            Application.Run(form);
        }

        // 供打包与交付后自检：不打开串口，只核对固件库、清单、镜像与 FWID。
        private static int RunSelfCheck(string root)
        {
            AppPaths paths = new AppPaths();
            paths.AppRoot = root;
            paths.FirmwareLibraryDir = Path.Combine(root, "固件库");

            FirmwareLibrary library = FirmwareLibrary.Load(paths.FirmwareLibraryDir);
            StringBuilder text = new StringBuilder();
            text.AppendLine("固件库：" + library.Root);
            foreach (string problem in library.Problems) text.AppendLine("固件库问题：" + problem);

            string presetPath = Path.Combine(root, "preset.json");
            if (File.Exists(presetPath))
            {
                PresetConfig preset = PresetConfig.Load(presetPath);
                text.AppendLine();
                text.AppendLine("分类包预设：" + preset.Id + " / " + preset.Kind);
                text.AppendLine("  " + preset.Describe());
                foreach (string problem in preset.Problems) text.AppendLine("  预设问题：" + problem);
            }
            else
            {
                text.AppendLine();
                text.AppendLine("分类包预设：无（通用包）");
            }

            int usable = 0;
            foreach (FirmwareSuite suite in library.Suites)
            {
                text.AppendLine();
                text.AppendLine("套装 " + suite.Id + "：" + (suite.Usable ? "可用" : "不可用"));
                if (!string.IsNullOrEmpty(suite.DisplayName)) text.AppendLine("  显示名：" + suite.DisplayName);
                text.AppendLine("  版本：" + suite.Version + "，目标 " + suite.TargetRpm + " 转，左右各 " + suite.SteerDeg + "°");
                if (suite.Catalog != null) text.AppendLine("  " + suite.Catalog.Summary);
                foreach (BoardRole role in new BoardRole[] { BoardRole.Boat, BoardRole.Remote })
                {
                    FirmwareSpec spec = suite.SpecFor(role);
                    text.AppendLine("  " + RoleNames.Chinese(role) + "：" +
                                    (spec == null ? "缺失" : spec.Fwid + " / " + spec.Version));
                    if (spec != null) text.AppendLine("    写入位置：" + spec.DescribeImages());
                    PartitionLayout layout = suite.LayoutFor(role);
                    if (layout != null)
                        text.AppendLine("    " + (string.IsNullOrEmpty(layout.Error) ? "分区：" + layout.DescribeNvs() : "分区问题：" + layout.Error));
                }
                text.AppendLine("  esptool：" + (File.Exists(suite.EsptoolPath) ? "存在" : "缺失"));
                foreach (string problem in suite.Problems) text.AppendLine("  问题：" + problem);
                if (suite.Usable) usable++;
            }

            // 两套镜像不能混用：同一角色在两个套装里的应用镜像不能是同一个文件。
            for (int i = 0; i < library.Suites.Count; i++)
            {
                for (int j = i + 1; j < library.Suites.Count; j++)
                {
                    foreach (BoardRole role in new BoardRole[] { BoardRole.Boat, BoardRole.Remote })
                    {
                        FirmwareSpec a = library.Suites[i].SpecFor(role);
                        FirmwareSpec b = library.Suites[j].SpecFor(role);
                        if (a == null || b == null) continue;
                        if (string.Equals(a.AppPath, b.AppPath, StringComparison.OrdinalIgnoreCase))
                            text.AppendLine("镜像混用：套装 " + library.Suites[i].Id + " 与 " + library.Suites[j].Id +
                                            " 的" + RoleNames.Chinese(role) + "镜像路径相同");
                    }
                }
            }

            text.AppendLine();
            text.AppendLine("可用套装数：" + usable + " / " + library.Suites.Count);
            bool ok = library.Problems.Count == 0 && usable > 0 && usable == library.Suites.Count;
            text.AppendLine(ok ? "结论：自检通过" : "结论：自检未通过");

            string report = Path.Combine(root, "自检报告.txt");
            File.WriteAllText(report, text.ToString(), new UTF8Encoding(false));
            return ok ? 0 : 2;
        }
    }
}
