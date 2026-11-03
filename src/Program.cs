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
            FirmwareCatalog catalog = FirmwareCatalog.Load(paths.ManifestPath);
            RoleStore roles = new RoleStore(paths.RoleStorePath);

            if (catalog.Problems.Count > 0)
            {
                StringBuilder warn = new StringBuilder();
                warn.AppendLine("工具包自检没有通过，为避免写错板子，烧录与配对已禁用：");
                warn.AppendLine();
                foreach (string problem in catalog.Problems) warn.AppendLine("· " + problem);
                warn.AppendLine();
                warn.AppendLine("请重新解压完整的原始 ZIP 包，不要单独复制本程序。");
                MessageBox.Show(warn.ToString(), "工具包不完整", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }

            HardwareTransport transport = new HardwareTransport(paths, new SystemClock());
            Application.Run(new MainForm(paths, catalog, roles, transport));
        }

        // 供打包与交付后自检：不打开串口，只核对清单、镜像与 FWID。
        private static int RunSelfCheck(string root)
        {
            AppPaths paths = new AppPaths();
            paths.AppRoot = root;
            paths.FirmwareDir = Path.Combine(root, "固件");
            paths.ToolDir = Path.Combine(root, "工具");
            paths.ManifestPath = Path.Combine(paths.FirmwareDir, "固件清单.json");
            paths.EsptoolPath = Path.Combine(paths.ToolDir, "esptool.exe");

            FirmwareCatalog catalog = FirmwareCatalog.Load(paths.ManifestPath);
            StringBuilder text = new StringBuilder();
            text.AppendLine("固件包：" + catalog.Version);
            text.AppendLine(catalog.Summary);
            foreach (string problem in catalog.Problems) text.AppendLine("问题：" + problem);
            foreach (BoardRole role in new BoardRole[] { BoardRole.Boat, BoardRole.Remote })
            {
                FirmwareSpec spec = catalog.ForRole(role);
                text.AppendLine(RoleNames.Chinese(role) + "：" + (spec == null ? "缺失" : spec.Fwid + " / " + spec.Version));
                if (spec != null) text.AppendLine("  写入位置：" + spec.DescribeImages());
            }
            text.AppendLine("esptool：" + (File.Exists(paths.EsptoolPath) ? "存在" : "缺失"));
            text.AppendLine(catalog.Problems.Count == 0 ? "结论：自检通过" : "结论：自检未通过");

            string report = Path.Combine(root, "自检报告.txt");
            File.WriteAllText(report, text.ToString(), new UTF8Encoding(false));
            return catalog.Problems.Count == 0 ? 0 : 2;
        }
    }
}
