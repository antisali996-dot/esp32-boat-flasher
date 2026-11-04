using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace BoatTool
{
    public class MainForm : Form, IUserInteraction
    {
        private readonly AppPaths paths;
        // 固件库允许“刷新固件库”时整体替换（构造函数初始化，ReplaceLibrary 统一替换）。
        // 必须是普通字段：readonly 会让刷新路径 CS0191。
        private FirmwareLibrary library;
        private readonly RoleStore roles;
        private readonly IBoardTransport transport;
        private readonly Workflow workflow;

        private readonly ListView portList = new ListView();
        private readonly ComboBox suiteChoice = new ComboBox();
        private bool suppressSuiteSelectionEvent;   // 重建下拉期间抑制选择事件写回
        private sealed class SuiteChoiceItem
        {
            public readonly FirmwareSuite Suite;
            public SuiteChoiceItem(FirmwareSuite suite) { Suite = suite; }
            public override string ToString() { return Suite == null ? "" : Suite.Display; }
        }
        private readonly Label suiteInfo = new Label();
        private readonly CheckBox forceReflashCheck = new CheckBox();
        private readonly ComboBox roleChoice = new ComboBox();
        private readonly ComboBox boatPortChoice = new ComboBox();
        private readonly ComboBox remotePortChoice = new ComboBox();
        private readonly CheckBox backupBeforeFlash = new CheckBox();
        private readonly Button refreshButton = new Button();
        private readonly Button refreshLibraryButton = new Button();
        private readonly Button flashButton = new Button();
        private readonly Button pairButton = new Button();
        private readonly Button verifyButton = new Button();
        private readonly Button backupButton = new Button();
        private readonly RichTextBox logBox = new RichTextBox();
        private readonly Label statusLabel = new Label();
        private readonly Label catalogLabel = new Label();
        private readonly Dictionary<string, PortScan> scans = new Dictionary<string, PortScan>(StringComparer.OrdinalIgnoreCase);
        // ---- 现场诊断 ----
        private readonly TabControl mainTabs = new TabControl();
        private readonly ComboBox diagPortChoice = new ComboBox();
        private readonly Button diagConnectButton = new Button();
        private readonly Button diagStartButton = new Button();
        private readonly Button diagStopExportButton = new Button();
        private readonly Button diagMarkerStopButton = new Button();
        private readonly Button diagMarkerRestartButton = new Button();
        private readonly Button diagMarkerServoButton = new Button();
        private readonly Button diagOpenFolderButton = new Button();
        private readonly Label diagStateLabel = new Label();
        private readonly Label diagConnLabel = new Label();
        private readonly Label diagRecLabel = new Label();
        private readonly Label diagRoleLabel = new Label();
        private readonly Label diagMacLabel = new Label();
        private readonly Label diagFwidLabel = new Label();
        private readonly Label diagResetLabel = new Label();
        private readonly Label diagRunLabel = new Label();
        private readonly Label diagDirLabel = new Label();
        private readonly Label diagSupportLabel = new Label();
        private readonly Label diagFaultLabel = new Label();
        private readonly TextBox diagLogBox = new TextBox();
        private readonly System.Windows.Forms.Timer diagTimer = new System.Windows.Forms.Timer();
        private readonly PortLockRegistry portLocks = new PortLockRegistry();
        private DiagnosticsController diag;
        // 只对获批 r10 快照开放自动查询；白名单为空时保持纯被动记录。
        private QueryPolicy diagPolicy = QueryPolicy.ApprovedR11();
        private bool busy;

        public MainForm(AppPaths paths, FirmwareLibrary library, RoleStore roles, IBoardTransport transport)
        {
            this.paths = paths;
            this.library = library;
            this.roles = roles;
            this.transport = transport;
            this.workflow = new Workflow(paths, library, roles, transport, new SystemClock(), this);

            BuildUi();
            FillSuites();
            AppendLog("工具已就绪。固件库：" + library.Root);
            foreach (FirmwareSuite suite in library.Suites)
            {
                if (suite.Usable) AppendLog("可用套装：" + suite.Display + "；" + suite.Catalog.Summary);
                else AppendLog("套装不可用：" + suite.Id + " —— " + string.Join("；", suite.Problems.ToArray()));
            }
            foreach (string problem in library.Problems) AppendLog("固件库问题：" + problem);
            if (library.Suites.Count == 0 || currentSuite() == null)
                SetStatus("没有可用的固件套装，烧录与配对已禁用", true);
            if (!string.IsNullOrEmpty(roles.LoadError)) AppendLog(roles.LoadError);
            if (NoDeviceMode)
            {
                AppendLog("已按环境变量跳过自动扫描串口（测试模式），现场诊断页也不会枚举串口。");
                return;
            }
            RefreshPorts();
        }

        private void BuildUi()
        {
            Text = "ESP32 小船 / 遥控器烧录配对工具";
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(980, 720);
            Size = new Size(1060, 790);
            AutoScaleMode = AutoScaleMode.None;
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
            BackColor = Color.FromArgb(246, 247, 250);

            mainTabs.Dock = DockStyle.Fill;
            mainTabs.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
            Controls.Add(mainTabs);

            TabPage flashPage = new TabPage("烧录与配对");
            flashPage.BackColor = Color.FromArgb(246, 247, 250);
            mainTabs.TabPages.Add(flashPage);

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 1;
            root.RowCount = 5;
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 84));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 232));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            root.Padding = new Padding(12, 10, 12, 8);
            flashPage.Controls.Add(root);

            root.Controls.Add(BuildHeader(), 0, 0);
            root.Controls.Add(BuildSuitePanel(), 0, 1);
            root.Controls.Add(BuildPortPanel(), 0, 2);
            root.Controls.Add(BuildActions(), 0, 3);
            root.Controls.Add(statusLabel, 0, 4);

            TabPage diagPage = new TabPage("现场诊断");
            diagPage.BackColor = Color.FromArgb(246, 247, 250);
            mainTabs.TabPages.Add(diagPage);
            diagPage.Controls.Add(BuildDiagnosticsPage());
            mainTabs.SelectedIndexChanged += delegate
            {
                if (mainTabs.SelectedTab == diagPage) RefreshDiagPorts();
            };
            // 无设备检查时可以直接定位到诊断页（仅界面选择，不访问设备）。
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("BOATTOOL_OPEN_DIAG")))
                mainTabs.SelectedTab = diagPage;

            statusLabel.Dock = DockStyle.Fill;
            statusLabel.TextAlign = ContentAlignment.MiddleLeft;
            statusLabel.ForeColor = Color.FromArgb(60, 64, 72);
            statusLabel.Padding = new Padding(2, 6, 2, 0);
            statusLabel.Text = "请先断开动力电池，只保留 USB 供电；识别串口时板子可能复位。记录目录在“记录”文件夹。";
        }

        // ---------- 现场诊断页 ----------
        private Control BuildDiagnosticsPage()
        {
            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 2;
            root.RowCount = 1;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 420));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.Padding = new Padding(12, 10, 12, 8);

            GroupBox left = new GroupBox();
            left.Text = "现场诊断（只读记录，不烧录、不改参数）";
            left.Dock = DockStyle.Fill;
            left.Padding = new Padding(10, 6, 10, 6);

            TableLayoutPanel l = new TableLayoutPanel();
            l.Dock = DockStyle.Fill;
            l.ColumnCount = 1;
            l.RowCount = 8;
            l.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            l.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            l.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            l.RowStyles.Add(new RowStyle(SizeType.Absolute, 104));
            l.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            l.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            l.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            l.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            left.Controls.Add(l);

            FlowLayoutPanel portRow = new FlowLayoutPanel();
            portRow.Dock = DockStyle.Fill;
            portRow.FlowDirection = FlowDirection.LeftToRight;
            portRow.WrapContents = false;
            Label pl = new Label();
            pl.Text = "串口：";
            pl.AutoSize = true;
            pl.Margin = new Padding(0, 8, 2, 0);
            portRow.Controls.Add(pl);
            diagPortChoice.DropDownStyle = ComboBoxStyle.DropDownList;
            diagPortChoice.Width = 140;
            diagPortChoice.Margin = new Padding(0, 4, 6, 0);
            portRow.Controls.Add(diagPortChoice);
            diagOpenFolderButton.Text = "打开记录目录";
            diagOpenFolderButton.Width = 118;
            diagOpenFolderButton.Height = 26;
            diagOpenFolderButton.Margin = new Padding(0, 3, 0, 0);
            diagOpenFolderButton.Click += delegate { OpenDiagnosticsFolder(); };
            portRow.Controls.Add(diagOpenFolderButton);
            l.Controls.Add(portRow, 0, 0);

            diagConnectButton.Text = "1. 连接并核对版本";
            diagConnectButton.Dock = DockStyle.Fill;
            diagConnectButton.Click += delegate { DiagConnect(); };
            l.Controls.Add(diagConnectButton, 0, 1);

            diagStartButton.Text = "2. 开始记录";
            diagStartButton.Dock = DockStyle.Fill;
            diagStartButton.Click += delegate { DiagStart(); };
            l.Controls.Add(diagStartButton, 0, 2);

            GroupBox markerBox = new GroupBox();
            markerBox.Text = "3. 标记异常（只写本地，不发串口命令）";
            markerBox.Dock = DockStyle.Fill;
            FlowLayoutPanel markers = new FlowLayoutPanel();
            markers.Dock = DockStyle.Fill;
            markers.FlowDirection = FlowDirection.LeftToRight;
            markers.WrapContents = true;
            diagMarkerStopButton.Text = "电机停下";
            diagMarkerStopButton.Width = 120;
            diagMarkerStopButton.Height = 30;
            diagMarkerStopButton.Click += delegate { DiagMarker(MarkerKind.MotorStopped); };
            markers.Controls.Add(diagMarkerStopButton);
            diagMarkerRestartButton.Text = "电机重新启动";
            diagMarkerRestartButton.Width = 120;
            diagMarkerRestartButton.Height = 30;
            diagMarkerRestartButton.Click += delegate { DiagMarker(MarkerKind.MotorRestarted); };
            markers.Controls.Add(diagMarkerRestartButton);
            diagMarkerServoButton.Text = "舵机微动";
            diagMarkerServoButton.Width = 120;
            diagMarkerServoButton.Height = 30;
            diagMarkerServoButton.Click += delegate { DiagMarker(MarkerKind.ServoMicroMove); };
            markers.Controls.Add(diagMarkerServoButton);
            markerBox.Controls.Add(markers);
            l.Controls.Add(markerBox, 0, 3);

            diagStopExportButton.Text = "4. 停止并导出";
            diagStopExportButton.Dock = DockStyle.Fill;
            diagStopExportButton.Click += delegate { DiagStopAndExport(); };
            l.Controls.Add(diagStopExportButton, 0, 4);

            diagStateLabel.AutoSize = false;
            diagStateLabel.Dock = DockStyle.Fill;
            diagStateLabel.Text = "状态：未连接";
            diagStateLabel.ForeColor = Color.FromArgb(60, 64, 72);
            l.Controls.Add(diagStateLabel, 0, 5);

            GroupBox infoBox = new GroupBox();
            infoBox.Text = "当前设备与接口";
            infoBox.Dock = DockStyle.Fill;
            TableLayoutPanel info = new TableLayoutPanel();
            info.Dock = DockStyle.Fill;
            info.ColumnCount = 1;
            info.RowCount = 8;
            info.Padding = new Padding(6, 4, 4, 4);
            infoBox.Controls.Add(info);
            diagRoleLabel.Dock = DockStyle.Fill; diagRoleLabel.Text = "角色：未知"; info.Controls.Add(diagRoleLabel, 0, 0);
            diagMacLabel.Dock = DockStyle.Fill; diagMacLabel.Text = "自身 MAC：未知"; info.Controls.Add(diagMacLabel, 0, 1);
            diagFwidLabel.Dock = DockStyle.Fill; diagFwidLabel.Text = "FWID：未知"; info.Controls.Add(diagFwidLabel, 0, 2);
            diagResetLabel.Dock = DockStyle.Fill; diagResetLabel.Text = "最近一次复位原因：未知"; info.Controls.Add(diagResetLabel, 0, 3);
            diagRunLabel.Dock = DockStyle.Fill; diagRunLabel.Text = "最近一次可识别运行状态：未知"; info.Controls.Add(diagRunLabel, 0, 4);
            diagDirLabel.Dock = DockStyle.Fill; diagDirLabel.Text = "本地记录目录：未开始"; info.Controls.Add(diagDirLabel, 0, 5);
            diagSupportLabel.Dock = DockStyle.Fill; diagSupportLabel.Text = "接口支持：未确认"; diagSupportLabel.ForeColor = Color.FromArgb(150, 90, 0);
            info.Controls.Add(diagSupportLabel, 0, 6);
            // 故障证据状态：只展示关联结果，不提供任何控制操作。
            diagFaultLabel.Dock = DockStyle.Fill;
            diagFaultLabel.Text = "故障证据：尚未收到 FAULT 行";
            diagFaultLabel.ForeColor = Color.FromArgb(60, 64, 72);
            info.Controls.Add(diagFaultLabel, 0, 7);
            l.Controls.Add(infoBox, 0, 6);

            Label note = new Label();
            note.Dock = DockStyle.Fill;
            note.Text = "提示：软件保持 DTR、RTS 为 false，但不能保证所有 USB 串口硬件在打开连接时都绝不复位。";
            note.ForeColor = Color.FromArgb(120, 70, 30);
            note.AutoSize = false;
            l.Controls.Add(note, 0, 7);

            root.Controls.Add(left, 0, 0);

            GroupBox logGroup = new GroupBox();
            logGroup.Text = "诊断过程（同样写入本地记录目录）";
            logGroup.Dock = DockStyle.Fill;
            logGroup.Padding = new Padding(8);
            diagLogBox.Dock = DockStyle.Fill;
            diagLogBox.Multiline = true;
            diagLogBox.ScrollBars = ScrollBars.Vertical;
            diagLogBox.ReadOnly = true;
            diagLogBox.BackColor = Color.White;
            diagLogBox.Font = new Font("Consolas", 9F, FontStyle.Regular, GraphicsUnit.Point);
            logGroup.Controls.Add(diagLogBox);
            root.Controls.Add(logGroup, 1, 0);

            diagTimer.Interval = 100;
            diagTimer.Tick += delegate { DiagTick(); };
            UpdateDiagButtons();
            return root;
        }

        private void DiagAppend(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            diagLogBox.AppendText("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + text + Environment.NewLine);
            diagLogBox.SelectionStart = diagLogBox.TextLength;
            diagLogBox.ScrollToCaret();
        }

        // 无设备模式：连串口名称都不枚举，确保不会触碰任何设备。
        private static bool NoDeviceMode
        {
            get { return !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("BOATTOOL_NO_AUTOSCAN")); }
        }

        private void RefreshDiagPorts()
        {
            if (NoDeviceMode) return;
            if (diag != null && diag.State == DiagState.Recording) return;
            string keep = diagPortChoice.SelectedItem as string;
            diagPortChoice.Items.Clear();
            try
            {
                foreach (string p in System.IO.Ports.SerialPort.GetPortNames()) diagPortChoice.Items.Add(p);
            }
            catch (Exception ex)
            {
                DiagAppend("枚举串口失败：" + ex.Message);
            }
            if (keep != null && diagPortChoice.Items.Contains(keep)) diagPortChoice.SelectedItem = keep;
            else if (diagPortChoice.Items.Count > 0) diagPortChoice.SelectedIndex = 0;
        }

        // 诊断能力解析（界面显示与控制器 PumpQueries 共用同一实现）：
        // 规则统一放在 DiagnosticsController.ResolveFromLibrary；此处只做转发，
        // 保证界面显示与实际查询用的是同一份结论。
        private DiagCapability ResolveCapabilityFor(string boardFwid, BoardRole boardRole)
        {
            return DiagnosticsController.ResolveFromLibrary(library, diagPolicy, boardFwid, boardRole);
        }

        private DiagCapability ResolveCapabilityForLegacyImpl(string boardFwid, BoardRole boardRole)
        {
            if (string.IsNullOrEmpty(boardFwid) || boardRole == BoardRole.Unknown)
                return DiagCapability.Denied("未读到板上固件身份或角色");
            FirmwareLibrary lib = library;
            if (lib == null || lib.Suites.Count == 0)
                return DiagCapability.Denied("固件库为空，无法核对板上固件条目");

            List<FirmwareSuite> hits = lib.AllWithFwid(boardRole, boardFwid);
            if (hits.Count == 0)
                return DiagCapability.Denied("板上 FWID " + boardFwid + " 不在固件库任何已校验套装中，只被动记录");

            // 逐条目按角色能力判定；出现“允许/不允许”矛盾即停止自动查询。
            bool anyAllowed = false;
            bool allAllowed = true;
            string contract = "";
            string suiteIds = "";
            string denyReason = "";
            foreach (FirmwareSuite suite in hits)
            {
                if (!suite.Usable) { allAllowed = false; if (denyReason.Length == 0) denyReason = "套装 " + suite.Id + " 自检未通过"; continue; }
                FirmwareSpec spec = suite.SpecFor(boardRole);
                string reason;
                bool ok = diagPolicy.AllowsSuiteRole(spec, boardFwid, boardRole, out reason);
                if (ok)
                {
                    anyAllowed = true;
                    if (contract.Length == 0) contract = string.IsNullOrEmpty(spec.DiagContract) ? "approved-map" : spec.DiagContract;
                }
                else allAllowed = false;
                if (suiteIds.Length > 0) suiteIds += "、";
                suiteIds += suite.Id;
            }
            if (hits.Count > 1 && (anyAllowed && !allAllowed))
                return DiagCapability.Denied("板上 FWID 命中多个条目（" + suiteIds + "）且诊断能力声明矛盾，已停止自动查询");

            if (anyAllowed)
            {
                DiagCapability cap = new DiagCapability();
                cap.Allowed = true;
                cap.Reason = "命中固件库条目（" + suiteIds + "）";
                cap.Contract = contract;
                cap.MatchedSuiteId = suiteIds;
                cap.Conflicted = hits.Count > 1;
                return cap;
            }
            return DiagCapability.Denied("板上 FWID " + boardFwid + " 的条目未启用诊断能力" +
                                        (denyReason.Length > 0 ? "（" + denyReason + "）" : "") + "，只被动记录");
        }

        private void EnsureDiag()
        {
            if (diag != null) return;
            IDiagChannel channel = new SerialDiagChannel(115200, DiagAppend);
            diag = new DiagnosticsController(paths, channel, new SystemDiagClock(), diagPolicy, DiagAppend);
            // 库引用 + 解析器：控制器内部回退路径与 GUI resolver 都走 ResolveFromLibrary。
            diag.SetFirmwareLibrary(library);
            diag.SetCapabilityResolver(ResolveCapabilityFor);
            diag.PortLocks = portLocks;
            workflow.PortLocks = portLocks;
        }

        // 真实按钮分派：与测试共用 DiagButtonActions，不存在第二套逻辑。
        private void DiagConnect()
        {
            EnsureDiag();
            string port = diagPortChoice.SelectedItem as string;
            if (string.IsNullOrEmpty(port)) { DiagAppend("请先选择串口。"); return; }

            DiagConnectOutcome outcome = DiagButtonActions.ConnectOrReconnect(diag, port);
            switch (outcome)
            {
                case DiagConnectOutcome.Connected:
                    DiagAppend("已连接并核对版本。可以点“开始记录”。");
                    break;
                case DiagConnectOutcome.Reconnected:
                    DiagAppend("已重连到原会话，继续记录（无需再点“开始记录”）。");
                    if (!diagTimer.Enabled) diagTimer.Start();
                    break;
                case DiagConnectOutcome.RefusedRecording:
                    DiagAppend("正在记录中，未重复连接。需要重连请先等待掉线，或停止后再操作。");
                    break;
                case DiagConnectOutcome.NoPort:
                    DiagAppend("请先选择串口。");
                    break;
                default:
                    DiagAppend("重连/连接未完成：" + DiagStateText.Chinese(diag.State) +
                               "。原会话与已保存文件仍保留，可重试或“停止并导出”。");
                    break;
            }
            UpdateDiagLabels();
            UpdateDiagButtons();
        }

        private void DiagStart()
        {
            EnsureDiag();
            if (diag == null) return;
            DiagStartOutcome outcome = DiagButtonActions.StartNewOrResume(diag);
            switch (outcome)
            {
                case DiagStartOutcome.Started:
                    diagTimer.Start();
                    DiagAppend("已开始新的记录会话。");
                    break;
                case DiagStartOutcome.RefusedRecording:
                    DiagAppend("已经在记录中，未创建第二个会话。");
                    break;
                case DiagStartOutcome.RefusedUnclosedSession:
                    DiagAppend("上一会话尚未关闭：请先“停止并导出”，或直接重连继续原会话；" +
                               "不会自动新建会话覆盖旧记录。");
                    break;
                default:
                    DiagAppend("尚未连接，无法开始记录。");
                    break;
            }
            UpdateDiagLabels();
            UpdateDiagButtons();
        }

        private void DiagTick()
        {
            if (diag == null) return;
            try { diag.Pump(); }
            catch (Exception ex) { DiagAppend("采集异常：" + ex.Message); }
            UpdateDiagLabels();
            UpdateDiagButtons();
        }

        private void DiagMarker(MarkerKind kind)
        {
            if (diag == null) { DiagAppend("尚未开始记录，标记未保存。"); return; }
            diag.AddMarker(kind);
            UpdateDiagLabels();
        }

        private void DiagStopAndExport()
        {
            if (diag == null || diag.Session == null) { DiagAppend("没有可导出的记录。"); return; }
            diagTimer.Stop();
            List<string> stopErrors = diag.StopRecording();
            foreach (string e in stopErrors) DiagAppend("停止时出现异常：" + e);
            UpdateDiagLabels();
            UpdateDiagButtons();

            using (SaveFileDialog dlg = new SaveFileDialog())
            {
                dlg.Title = "导出诊断记录";
                dlg.Filter = "ZIP 压缩包 (*.zip)|*.zip";
                dlg.FileName = diag.Session.SessionId + ".zip";
                if (dlg.ShowDialog(this) != DialogResult.OK)
                {
                    DiagAppend("已取消导出。本地记录仍保留在：" + diag.Session.RootDir);
                    return;
                }
                ExportResult result = DiagnosticsExporter.Export(diag.Session, paths, AppVersion(), DateTime.Now);
                if (!result.Ok)
                {
                    DiagAppend("导出失败：" + result.Error);
                    MessageBox.Show(this, result.Error + Environment.NewLine + Environment.NewLine +
                        "原始记录保留在：" + diag.Session.RootDir + Environment.NewLine + "可以重新导出。",
                        "导出失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                try
                {
                    if (File.Exists(dlg.FileName)) File.Delete(dlg.FileName);
                    File.Move(result.ZipPath, dlg.FileName);
                }
                catch (Exception ex)
                {
                    DiagAppend("移动导出文件失败：" + ex.Message + "（临时文件：" + result.ZipPath + "）");
                    return;
                }
                DiagAppend("导出完成：" + dlg.FileName);
                DiagAppend("SHA256：" + DiagnosticsExporter.Sha256File(dlg.FileName));
                MessageBox.Show(this,
                    "导出完成：" + Environment.NewLine + dlg.FileName + Environment.NewLine + Environment.NewLine +
                    "SHA256：" + DiagnosticsExporter.Sha256File(dlg.FileName) + Environment.NewLine +
                    "大小：" + new FileInfo(dlg.FileName).Length + " 字节" + Environment.NewLine + Environment.NewLine +
                    "本导出只包含工具实际接收到的数据，不代表已找到根因或问题已解决。",
                    "导出完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private void OpenDiagnosticsFolder()
        {
            try
            {
                string dir = diag != null && diag.Session != null
                    ? diag.Session.RootDir
                    : Path.Combine(paths.WritableRoot, "诊断记录");
                Directory.CreateDirectory(dir);
                System.Diagnostics.Process.Start("explorer.exe", "\"" + dir + "\"");
            }
            catch (Exception ex) { DiagAppend("打开目录失败：" + ex.Message); }
        }

        private string AppVersion()
        {
            return "esp32-boat-flasher/诊断-" + System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        }

        private void UpdateDiagLabels()
        {
            if (diag == null)
            {
                diagStateLabel.Text = "状态：未连接";
                return;
            }
            diagStateLabel.Text = "状态：" + DiagStateText.Chinese(diag.State);
            diagStateLabel.ForeColor = diag.State == DiagState.Recording
                ? Color.FromArgb(20, 110, 60) : Color.FromArgb(60, 64, 72);
            BoardIdentity id = diag.Identity;
            diagRoleLabel.Text = "角色：" + (id == null ? "未知" : id.RoleText);
            diagMacLabel.Text = "自身 MAC：" + (id == null || string.IsNullOrEmpty(id.Mac) ? "未知" : id.Mac);
            diagFwidLabel.Text = "FWID：" + (id == null || !id.HasFirmwareId ? "未知" : id.FirmwareId);
            diagResetLabel.Text = "最近一次复位原因：" +
                (string.IsNullOrEmpty(diag.LastResetReason) ? "未知（板子未输出）" : diag.LastResetReason);
            diagRunLabel.Text = "最近一次可识别运行状态：" +
                (string.IsNullOrEmpty(diag.LastRunState) ? "未知" : diag.LastRunState);
            diagDirLabel.Text = "本地记录目录：" + (diag.Session == null ? "未开始" : diag.Session.RootDir);
            // 与控制器实际查询能力共用同一份解析结果（ResolveCapability 缓存，身份变化自动重算）。
            DiagCapability cap = diag.ResolveCapability();
            if (cap.Allowed)
            {
                diagSupportLabel.Text = "接口支持：已启用（契约 " + cap.Contract +
                    (string.IsNullOrEmpty(cap.MatchedSuiteId) ? "" : "，条目 " + cap.MatchedSuiteId) +
                    "）；记录中会低频查询 COUNT/FAULT";
            }
            else if (id != null && id.Role == BoardRole.Remote)
            {
                diagSupportLabel.Text = "接口支持：遥控器不提供 COUNT/FAULT（只被动记录）";
            }
            else
            {
                diagSupportLabel.Text = "接口支持：未启用（" + cap.Reason + "）";
            }
            diagSupportLabel.ForeColor = cap.Allowed ? Color.FromArgb(20, 110, 60) : Color.FromArgb(150, 90, 0);

            // 故障证据：只读汇总 + 最近一行的状态（完整/不完整/冲突/启动未确认/待适配）。
            FaultSnapshot latest = diag.LatestFault;
            if (latest == null)
            {
                diagFaultLabel.Text = "故障证据：尚未收到 FAULT 行";
                diagFaultLabel.ForeColor = Color.FromArgb(60, 64, 72);
            }
            else
            {
                string groupText;
                if (latest.Group != null)
                    groupText = "id=" + latest.Group.Id + " " + latest.Group.DescribeState();
                else if (latest.Kind == "NONE")
                    groupText = "板上报告无故障（保留历史故障与冲突）";
                else if (latest.Kind == "UNSUPPORTED")
                    groupText = "接口不支持 FAULT 查询";
                else if (latest.State == FaultAdapterState.Malformed)
                    groupText = "解析失败（" +
                        (string.IsNullOrEmpty(latest.ParseFailureField) ? "字段格式不符合协议"
                            : "字段 " + latest.ParseFailureField + " " + latest.ParseFailureReason) +
                        "），不计入有效证据";
                else
                    groupText = "待适配（未识别格式）";
                diagFaultLabel.Text = "故障证据：" + groupText + "｜汇总：" + diag.FaultSummary;
                diagFaultLabel.ForeColor =
                    latest.Group != null && latest.Group.State == FaultGroupState.Complete
                        ? Color.FromArgb(20, 110, 60)
                        : (latest.Group != null && latest.Group.State == FaultGroupState.Conflicting
                            ? Color.FromArgb(178, 58, 40) : Color.FromArgb(150, 90, 0));
            }
            diagFaultLabel.Text = diagFaultLabel.Text +
                "（证据完整只表示两行配套，不代表根因成立或问题已解决）";
        }

        private void UpdateDiagButtons()
        {
            DiagState st = diag == null ? DiagState.NotConnected : diag.State;
            bool idle = st == DiagState.NotConnected || st == DiagState.Connected ||
                        st == DiagState.ConnectFailed || st == DiagState.Disconnected ||
                        st == DiagState.WaitingReconnect;
            diagConnectButton.Enabled = st != DiagState.Recording && st != DiagState.Stopped;
            diagStartButton.Enabled = st == DiagState.Connected;
            bool recording = st == DiagState.Recording;
            diagMarkerStopButton.Enabled = recording;
            diagMarkerRestartButton.Enabled = recording;
            diagMarkerServoButton.Enabled = recording;
            diagStopExportButton.Enabled = recording || st == DiagState.Disconnected ||
                                           st == DiagState.WaitingReconnect ||
                                           (diag != null && diag.Session != null && st == DiagState.Stopped);
            diagPortChoice.Enabled = idle;
        }

        private Control BuildHeader()
        {
            Panel panel = new Panel();
            panel.Dock = DockStyle.Fill;
            panel.BackColor = Color.FromArgb(28, 42, 66);
            panel.Padding = new Padding(16, 10, 16, 10);

            Label title = new Label();
            title.Text = "ESP32 小船 / 遥控器烧录配对工具";
            title.ForeColor = Color.White;
            title.Font = new Font("Microsoft YaHei UI", 15F, FontStyle.Bold, GraphicsUnit.Point);
            title.AutoSize = true;
            title.Location = new Point(16, 10);
            panel.Controls.Add(title);

            Label hint = new Label();
            hint.Text = "按芯片 MAC 认板，不按串口号认板。烧录默认不备份；配对只改地址，不重刷固件。";
            hint.ForeColor = Color.FromArgb(196, 208, 226);
            hint.AutoSize = true;
            hint.Location = new Point(18, 44);
            panel.Controls.Add(hint);

            catalogLabel.Text = "";
            catalogLabel.ForeColor = Color.FromArgb(255, 214, 130);
            catalogLabel.AutoSize = false;
            catalogLabel.TextAlign = ContentAlignment.MiddleRight;
            catalogLabel.Dock = DockStyle.Right;
            catalogLabel.Width = 470;
            catalogLabel.Font = new Font(Font.FontFamily, 8F, FontStyle.Regular);
            catalogLabel.Text = "固件套装：" + (library.Suites.Count > 0 ? "见下方选择" : "无可用套装");
            panel.Controls.Add(catalogLabel);
            return panel;
        }

        // 分类包：把预设交给 Workflow 做芯片地址核对，并在标题与日志里明确标出。
        public void ApplyPreset(PresetConfig preset)
        {
            if (preset == null || preset.Problems.Count > 0) return;
            workflow.Preset = preset;
            Text = "ESP32 小船 / 遥控器烧录配对工具（" + preset.Title + "）";
            AppendLog("分类包预设：" + preset.Describe());
            AppendLog("本包只允许操作上述两块芯片；地址不符会拒绝写入与绑定。");
            // 推荐信息：只提示，不锁死版本。默认仍选推荐/最新套装，但用户改选有效版本不会被拒绝。
            string rec = preset.RecommendText(library);
            if (rec.Length > 0) AppendLog(rec);
        }

        // 固件套装独立成组：这是“要写入什么”的唯一来源，避免和串口选择混在一起被误读。
        private Control BuildSuitePanel()
        {
            GroupBox group = new GroupBox();
            group.Text = "固件套装（决定写入哪一套固件；版本信息全部来自发布清单）";
            group.Dock = DockStyle.Fill;
            group.Padding = new Padding(10, 6, 10, 6);

            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.ColumnCount = 2;
            layout.RowCount = 1;
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 400));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            group.Controls.Add(layout);

            FlowLayoutPanel left = new FlowLayoutPanel();
            left.Dock = DockStyle.Fill;
            left.FlowDirection = FlowDirection.LeftToRight;
            left.WrapContents = false;
            left.Padding = new Padding(0, 6, 0, 0);

            Label suiteLabel = new Label();
            suiteLabel.Text = "当前套装：";
            suiteLabel.AutoSize = true;
            suiteLabel.Margin = new Padding(0, 8, 4, 0);
            left.Controls.Add(suiteLabel);

            suiteChoice.DropDownStyle = ComboBoxStyle.DropDownList;
            suiteChoice.Width = 290;
            suiteChoice.Margin = new Padding(0, 4, 6, 0);
            suiteChoice.SelectedIndexChanged += delegate { OnSuiteChanged(); };
            left.Controls.Add(suiteChoice);

            // 刷新固件库：重新扫描 固件库\ 下全部套装目录并重新校验文件。
            // 只读磁盘：不打开串口、不复位板子、不写设备（与“刷新列表”的串口刷新互不影响）。
            refreshLibraryButton.Text = "刷新固件库";
            refreshLibraryButton.Width = 110;
            refreshLibraryButton.Height = 28;
            refreshLibraryButton.Margin = new Padding(0, 4, 0, 0);
            refreshLibraryButton.Click += delegate { RefreshLibrary(); };
            left.Controls.Add(refreshLibraryButton);
            layout.Controls.Add(left, 0, 0);

            suiteInfo.AutoSize = false;
            suiteInfo.Dock = DockStyle.Fill;
            suiteInfo.Font = new Font(Font.FontFamily, 8F, FontStyle.Regular);
            suiteInfo.Padding = new Padding(8, 4, 4, 0);
            suiteInfo.TextAlign = ContentAlignment.TopLeft;
            suiteInfo.ForeColor = Color.FromArgb(60, 64, 72);
            layout.Controls.Add(suiteInfo, 1, 0);
            return group;
        }

        private Control BuildPortPanel()
        {
            GroupBox group = new GroupBox();
            group.Text = "1. 串口与板子身份（串口号只用于连接，换插后会变）";
            group.Dock = DockStyle.Fill;
            group.Padding = new Padding(10);

            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.ColumnCount = 1;
            layout.RowCount = 2;
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            group.Controls.Add(layout);

            portList.Dock = DockStyle.Fill;
            portList.View = View.Details;
            portList.FullRowSelect = true;
            portList.MultiSelect = false;
            portList.HideSelection = false;
            portList.GridLines = false;
            portList.Columns.Add("串口", 64);
            portList.Columns.Add("板子角色", 132);
            portList.Columns.Add("芯片 MAC", 152);
            portList.Columns.Add("固件版本", 168);
            portList.Columns.Add("转向角度", 104);
            portList.Columns.Add("对端地址", 152);
            portList.Columns.Add("说明", 300);
            portList.SelectedIndexChanged += delegate { OnPortSelected(); };
            layout.Controls.Add(portList, 0, 0);

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.Dock = DockStyle.Fill;
            buttons.FlowDirection = FlowDirection.LeftToRight;
            buttons.WrapContents = false;
            buttons.Padding = new Padding(0, 8, 0, 0);

            refreshButton.Text = "刷新列表";
            refreshButton.Width = 110;
            refreshButton.Height = 30;
            refreshButton.Click += delegate { RefreshPorts(); };
            buttons.Controls.Add(refreshButton);

            Label roleLabel = new Label();
            roleLabel.Text = "本板用途（首次使用请选择）：";
            roleLabel.AutoSize = true;
            roleLabel.Margin = new Padding(14, 9, 4, 0);
            buttons.Controls.Add(roleLabel);

            roleChoice.DropDownStyle = ComboBoxStyle.DropDownList;
            roleChoice.Width = 130;
            roleChoice.Items.Add("未选择");
            roleChoice.Items.Add("小船");
            roleChoice.Items.Add("遥控器");
            roleChoice.SelectedIndex = 0;
            roleChoice.Margin = new Padding(0, 5, 6, 0);
            roleChoice.SelectedIndexChanged += delegate { OnRoleChosen(); };
            buttons.Controls.Add(roleChoice);

            backupBeforeFlash.Text = "烧录前先备份原固件（默认关闭，8MB 约 3～5 分钟）";
            backupBeforeFlash.AutoSize = true;
            backupBeforeFlash.Checked = false;
            backupBeforeFlash.Margin = new Padding(12, 8, 0, 0);
            buttons.Controls.Add(backupBeforeFlash);

            forceReflashCheck.Text = "即使已匹配也重刷";
            forceReflashCheck.AutoSize = true;
            forceReflashCheck.Checked = false;
            forceReflashCheck.Margin = new Padding(12, 8, 0, 0);
            forceReflashCheck.CheckedChanged += delegate
            {
                if (forceReflashCheck.Checked)
                    AppendLog("已选择“即使已匹配也重刷”：套装一致时仍会写入，确认框会再次提示。");
            };
            buttons.Controls.Add(forceReflashCheck);

            layout.Controls.Add(buttons, 0, 1);
            return group;
        }

        private Control BuildActions()
        {
            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.ColumnCount = 2;
            layout.RowCount = 1;
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 300));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            GroupBox actionGroup = new GroupBox();
            actionGroup.Text = "2. 操作";
            actionGroup.Dock = DockStyle.Fill;
            actionGroup.Padding = new Padding(12, 8, 12, 8);

            TableLayoutPanel actions = new TableLayoutPanel();
            actions.Dock = DockStyle.Fill;
            actions.ColumnCount = 1;
            actions.RowCount = 6;
            actions.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
            actions.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
            actions.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            actions.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            actions.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            actions.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            flashButton.Text = "烧录当前板";
            flashButton.Dock = DockStyle.Fill;
            flashButton.Font = new Font(Font.FontFamily, 11F, FontStyle.Bold);
            flashButton.Click += delegate { RunFlash(); };
            actions.Controls.Add(flashButton, 0, 0);

            backupButton.Text = "备份原固件";
            backupButton.Dock = DockStyle.Fill;
            backupButton.Click += delegate { RunBackup(); };
            actions.Controls.Add(backupButton, 0, 1);

            Label pairTitle = new Label();
            pairTitle.Text = "两块板同时配对（只改地址，不重刷）";
            pairTitle.AutoSize = true;
            pairTitle.Font = new Font(Font.FontFamily, 9.5F, FontStyle.Bold);
            actions.Controls.Add(pairTitle, 0, 2);

            FlowLayoutPanel ports = new FlowLayoutPanel();
            ports.Dock = DockStyle.Fill;
            ports.FlowDirection = FlowDirection.LeftToRight;
            ports.WrapContents = false;
            Label boatLabel = new Label();
            boatLabel.Text = "小船：";
            boatLabel.AutoSize = true;
            boatLabel.Margin = new Padding(0, 5, 2, 0);
            ports.Controls.Add(boatLabel);
            boatPortChoice.DropDownStyle = ComboBoxStyle.DropDownList;
            boatPortChoice.Width = 96;
            boatPortChoice.Margin = new Padding(0, 1, 8, 0);
            ports.Controls.Add(boatPortChoice);
            Label remoteLabel = new Label();
            remoteLabel.Text = "遥控器：";
            remoteLabel.AutoSize = true;
            remoteLabel.Margin = new Padding(0, 5, 2, 0);
            ports.Controls.Add(remoteLabel);
            remotePortChoice.DropDownStyle = ComboBoxStyle.DropDownList;
            remotePortChoice.Width = 96;
            remotePortChoice.Margin = new Padding(0, 1, 0, 0);
            ports.Controls.Add(remotePortChoice);
            actions.Controls.Add(ports, 0, 3);

            TableLayoutPanel pairRow = new TableLayoutPanel();
            pairRow.Dock = DockStyle.Fill;
            pairRow.ColumnCount = 2;
            pairRow.RowCount = 1;
            pairRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            pairRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 118));

            pairButton.Text = "配对两块板";
            pairButton.Dock = DockStyle.Fill;
            pairButton.Font = new Font(Font.FontFamily, 10F, FontStyle.Bold);
            pairButton.Click += delegate { RunPair(); };
            pairRow.Controls.Add(pairButton, 0, 0);

            verifyButton.Text = "只读核验";
            verifyButton.Dock = DockStyle.Fill;
            verifyButton.Margin = new Padding(6, 0, 0, 0);
            verifyButton.Click += delegate { RunVerify(); };
            pairRow.Controls.Add(verifyButton, 1, 0);
            actions.Controls.Add(pairRow, 0, 4);

            actionGroup.Controls.Add(actions);
            layout.Controls.Add(actionGroup, 0, 0);

            GroupBox logGroup = new GroupBox();
            logGroup.Text = "3. 过程与结果（同样内容写入“记录”目录）";
            logGroup.Dock = DockStyle.Fill;
            logGroup.Padding = new Padding(8);
            logBox.Dock = DockStyle.Fill;
            logBox.ReadOnly = true;
            logBox.BackColor = Color.White;
            logBox.Font = new Font("Consolas", 9F, FontStyle.Regular, GraphicsUnit.Point);
            logBox.WordWrap = false;
            logBox.DetectUrls = false;
            logGroup.Controls.Add(logBox);
            layout.Controls.Add(logGroup, 1, 0);
            return layout;
        }

        // 套装下拉：只列可用套装；不可用的也显示出来并标红说明原因，避免用户以为“没有这套”。
        // 重新扫描固件库。规则：
        //   * 只读磁盘：不打开串口、不发送命令、不复位；
        //   * 烧录/配对进行中（busy）或诊断记录中：拒绝刷新，防止替换正在使用的计划；
        //   * 原选择仍存在且内容未变 → 保留选择；
        //   * 原选择消失/失效/内容变化 → 清除旧计划并提示重新选择；
        //   * 不保留指向旧镜像的缓存：library 对象整体重建。
        private void RefreshLibrary()
        {
            if (busy)
            {
                AppendLog("操作进行中，暂不能刷新固件库。");
                return;
            }
            if (diag != null && (diag.State == DiagState.Recording ||
                (diag.Session != null && !diag.Session.IsClosed)))
            {
                AppendLog("现场诊断记录中（或会话尚未关闭），暂不能刷新固件库（防止替换正在使用的记录环境）。");
                return;
            }

            FirmwareSuite previous = workflow.SelectedSuite;
            string previousId = previous == null ? "" : previous.Id;

            FirmwareLibrary fresh;
            try { fresh = FirmwareLibrary.Load(library.Root); }
            catch (Exception ex)
            {
                AppendLog("刷新固件库失败：" + ex.Message);
                return;
            }

            // 共享生产入口：替换库 + 通知所有使用方（Workflow、诊断控制器、能力缓存）
            // + 旧选择去留判定，只有这一份实现（测试也直接调用它）。
            FirmwareSuite newSelection;
            string decision = FirmwareLibrary.ApplyFirmwareLibraryRefresh(
                library, fresh, previous,
                delegate(FirmwareLibrary newLib)
                {
                    // 1) MainForm 使用的库
                    library = newLib;
                    // 2) Workflow 使用的库（同时清除旧烧录计划引用）
                    workflow.UpdateLibrary(newLib);
                    // 3) 诊断控制器已创建时绑定同一个新库并清除能力缓存；
                    //    尚未创建时不创建、不打开任何串口（EnsureDiag 首次使用时自会绑定当前库）。
                    if (diag != null) diag.SetFirmwareLibrary(newLib);
                },
                out newSelection);

            int usable = 0;
            foreach (FirmwareSuite s in library.Suites) if (s.Usable) usable++;
            AppendLog("已刷新固件库：共 " + library.Suites.Count + " 套，可用 " + usable + " 套。");
            foreach (string problem in library.Problems) AppendLog("固件库问题：" + problem);
            foreach (FirmwareSuite s in library.Suites)
            {
                if (!s.Usable) AppendLog("套装不可用：" + s.Id + " —— " + string.Join("；", s.Problems.ToArray()));
                foreach (string note in s.Notes) AppendLog("套装提示：" + s.Id + " —— " + note);
            }

            // 共享协调方法重建下拉（抑制事件）+ 按决策恢复/清除选中 + 统一回调一次。
            FirmwareLibrary.RebuildSuiteChoicesAfterRefresh(new FirmwareLibrary.SuiteChoiceHost
            {
                GetSelected = delegate { SuiteChoiceItem item = suiteChoice.SelectedItem as SuiteChoiceItem; return item == null ? null : item.Suite; },
                ClearItems = delegate { suppressSuiteSelectionEvent = true; suiteChoice.Items.Clear(); },
                AddItem = delegate(FirmwareSuite suite) { suiteChoice.Items.Add(new SuiteChoiceItem(suite)); },
                SetSelectedIndex = delegate(int index)
                {
                    suppressSuiteSelectionEvent = true;
                    suiteChoice.SelectedIndex = index;   // -1 = 无选中
                },
                ResumeEvents = delegate { suppressSuiteSelectionEvent = false; },
                // 重建结束、事件已恢复后只调用一次统一实现（此刻下拉里是新库对象或 -1）。
                OnSelectionFinalized = ApplySuiteSelection,
            }, library, decision, newSelection);

            // 中文提示（newSelection 为空绝不访问 Display）。
            switch (decision)
            {
                case "keep":
                    if (newSelection != null)
                        AppendLog("已保留原选择：" + newSelection.Display);
                    else
                        AppendLog("固件库已刷新，当前未选择套装。");
                    break;
                case "missing":
                    AppendLog("原选择的套装 " + previousId + " 已不存在，请重新选择。");
                    break;
                case "changed":
                    AppendLog("套装 " + previousId + " 的内容在刷新后已变化，旧烧录计划已清除；请重新选择。");
                    break;
                case "unusable":
                    FirmwareSuite failed = library.Find(previousId);
                    AppendLog("套装 " + previousId + " 现在自检未通过：" +
                              (failed == null ? "" : string.Join("；", failed.Problems.ToArray())) +
                              "。请重新选择。");
                    break;
                default:
                    if (library.Suites.Count == 0)
                        AppendLog("固件库为空。请把发布包解压到固件库目录后重新刷新。");
                    else
                        AppendLog("固件库已刷新，当前未选择套装。");
                    break;
            }
        }

        // 套装最终状态的唯一实现：首次初始化、用户手动选择、刷新固件库完成，三条入口都只调用这里。
        // 以“下拉框当前选中项”为唯一事实来源写回 Workflow，并同步摘要、按钮、板子备注与诊断标签。
        // 只读界面与内存中的固件库：不创建诊断控制器、不枚举串口、不打开串口、不发送命令。
        private void ApplySuiteSelection()
        {
            SuiteChoiceItem item = suiteChoice.SelectedItem as SuiteChoiceItem;
            FirmwareSuite suite = item == null ? null : item.Suite;
            workflow.SelectedSuite = suite;
            UpdateSuiteInfo(suite);
            UpdateActionState();
            RescanNotes();
            // diag 未创建时只写“未连接”，绝不在这里创建控制器（避免顺带打开串口）。
            UpdateDiagLabels();
        }

        // 该套装两端应用镜像的复合指纹（刷新前后比较用）。

        private void FillSuites()
        {
            suiteChoice.Items.Clear();
            int usableIndex = -1;
            int preferredIndex = -1;
            for (int i = 0; i < library.Suites.Count; i++)
            {
                FirmwareSuite suite = library.Suites[i];
                SuiteChoiceItem item = new SuiteChoiceItem(suite);
                suiteChoice.Items.Add(item);
                if (suite.Usable && usableIndex < 0) usableIndex = i;
                // 默认优先选 r11（锁存已关闭的当前版本），但不能因此把不可用套装选中。
                if (suite.Usable && preferredIndex < 0 &&
                    suite.Version.IndexOf("r11", StringComparison.OrdinalIgnoreCase) >= 0)
                    preferredIndex = i;
            }
            if (preferredIndex >= 0) usableIndex = preferredIndex;
            suppressSuiteSelectionEvent = true;
            // 空库时不能给 ComboBox 赋 0（会抛 ArgumentOutOfRangeException），显式置 -1。
            if (usableIndex >= 0) suiteChoice.SelectedIndex = usableIndex;
            else if (suiteChoice.Items.Count > 0) suiteChoice.SelectedIndex = 0;
            else suiteChoice.SelectedIndex = -1;
            suppressSuiteSelectionEvent = false;
            // 默认选中后同样走统一实现：首次启动的摘要必须与实际选择一致。
            ApplySuiteSelection();
        }


        private FirmwareSuite currentSuite()
        {
            SuiteChoiceItem item = suiteChoice.SelectedItem as SuiteChoiceItem;
            return item == null ? null : item.Suite;
        }

        private void OnSuiteChanged()
        {
            if (suppressSuiteSelectionEvent) return;   // 重建下拉期间不写回
            ApplySuiteSelection();
        }

        private void UpdateSuiteInfo(FirmwareSuite suite)
        {
            if (suite == null)
            {
                // 无选择时要区分“库里根本没有套装”和“有套装但未选中”，避免用户误以为选错了。
                suiteInfo.Text = library.Suites.Count == 0
                    ? "固件库里没有可用套装。"
                    : "未选择套装。";
                suiteInfo.ForeColor = Color.FromArgb(178, 58, 40);
            }
            else if (!suite.Usable)
            {
                suiteInfo.Text = "这套不可用，已禁止烧录：" + string.Join("；", suite.Problems.ToArray());
                suiteInfo.ForeColor = Color.FromArgb(178, 58, 40);
            }
            else
            {
                FirmwareSpec boat = suite.SpecFor(BoardRole.Boat);
                FirmwareSpec remote = suite.SpecFor(BoardRole.Remote);
                suiteInfo.Text = "版本 " + suite.Version + "｜目标 " + suite.SpeedText + "｜" + suite.SteerText +
                                 Environment.NewLine + "小船  " + (boat == null ? "缺失" : boat.Fwid) +
                                 Environment.NewLine + "遥控器  " + (remote == null ? "缺失" : remote.Fwid);
                suiteInfo.ForeColor = Color.FromArgb(60, 64, 72);
            }
        }

        // 不重新访问串口，只按已扫描到的身份重算说明文字。
        private void RescanNotes()
        {
            foreach (ListViewItem item in portList.Items)
            {
                PortScan scan = item.Tag as PortScan;
                if (scan == null) continue;
                if (!scan.Responded) continue;
                FirmwareSuite suite = workflow.SelectedSuite;
                if (suite == null) continue;
                FirmwareSpec spec = suite.SpecFor(scan.Identity.Role);
                if (spec == null) continue;
                string note;
                if (string.Equals(scan.Identity.FirmwareId, spec.Fwid, StringComparison.Ordinal))
                    note = "固件身份匹配，板上镜像内容未核验" +
                        (workflow.IsImageAmbiguous(scan.Identity.Role, scan.Identity)
                            ? "（库内存在同身份异镜像）" : "");
                else
                {
                    List<FirmwareSuite> owners = library.AllWithFwid(scan.Identity.Role, scan.Identity.FirmwareId);
                    if (owners.Count > 0)
                    {
                        string names = "";
                        foreach (FirmwareSuite owner in owners)
                        {
                            if (names.Length > 0) names += "、";
                            names += owner.DisplayName;
                        }
                        FwidInfo info = FwidInfo.Parse(scan.Identity.FirmwareId);
                        note = "板上是 " + names + (info.TargetRpm > 0 ? "（" + info.TargetRpm + " 转）" : "") +
                               "，与所选套装不同";
                    }
                    else note = scan.Identity.HasFirmwareId ? "板上固件不在固件库中" : "板上无固件身份（旧版）";
                }
                if (note == item.SubItems[6].Text) continue;
                item.SubItems[6].Text = note;
                // 与所选套装不同就标黄提醒，但不算错误。
                item.ForeColor = Color.FromArgb(150, 90, 0);
            }
        }

        private void RefreshPorts()
        {
            if (busy) return;
            SetBusy(true, "正在读取串口与板子身份……（此步可能让板子复位，请勿同时插拔）");
            try
            {
                scans.Clear();
                portList.Items.Clear();
                boatPortChoice.Items.Clear();
                remotePortChoice.Items.Clear();
                // 扫描逻辑集中在 Workflow，界面不重复实现识别规则。
                List<PortScan> results = workflow.Scan();
                if (workflow.LastScanCancelled)
                {
                    AppendLog("已取消：尚未确认断开动力电池，没有访问任何串口。");
                    SetStatus("已取消，没有访问串口", true);
                    return;
                }
                if (results.Count == 0)
                {
                    AppendLog("没有检测到串口。请确认 USB 数据线、CH340 驱动和板子供电。");
                    SetStatus("没有检测到串口", true);
                    return;
                }
                foreach (PortScan scan in results)
                {
                    scans[scan.Port.Name] = scan;
                    AppendLog("串口 " + scan.Port.Name + "：" + scan.MacText + " / " + scan.RoleText + " / " +
                              scan.FirmwareText + " / " + scan.SteerText + "。备注：" + scan.Note);

                    ListViewItem item = new ListViewItem(scan.Port.Name);
                    item.SubItems.Add(scan.RoleText);
                    item.SubItems.Add(scan.MacText);
                    item.SubItems.Add(scan.FirmwareText);
                    item.SubItems.Add(scan.SteerText);
                    item.SubItems.Add(scan.Responded ? scan.Identity.PeerText : "未知");
                    item.SubItems.Add(scan.Note);
                    item.Tag = scan;
                    if (!scan.HasChip) item.ForeColor = Color.FromArgb(150, 150, 150);
                    else if (scan.RememberedRole != BoardRole.Unknown && scan.BoardRoleReported != BoardRole.Unknown &&
                             scan.RememberedRole != scan.BoardRoleReported)
                        item.ForeColor = Color.FromArgb(190, 70, 40);
                    portList.Items.Add(item);

                    boatPortChoice.Items.Add(scan.Port.Name);
                    remotePortChoice.Items.Add(scan.Port.Name);
                    if (scan.RememberedRole == BoardRole.Boat && boatPortChoice.SelectedIndex < 0)
                        boatPortChoice.SelectedItem = scan.Port.Name;
                    if (scan.RememberedRole == BoardRole.Remote && remotePortChoice.SelectedIndex < 0)
                        remotePortChoice.SelectedItem = scan.Port.Name;
                }
                if (portList.Items.Count > 0) portList.Items[0].Selected = true;
                SetStatus("已读取 " + portList.Items.Count + " 个串口；角色只依据芯片 MAC 记忆。", false);
            }
            catch (Exception ex)
            {
                AppendLog("刷新串口失败：" + ex.Message);
                SetStatus("刷新串口失败：" + ex.Message, true);
            }
            finally
            {
                SetBusy(false, null);
                // 按钮恢复后再按当前选中项刷新下拉可用性，避免角色选择被永久禁用。
                OnPortSelected();
            }
        }

        private void OnPortSelected()
        {
            if (portList.SelectedItems.Count == 0) return;
            PortScan scan = portList.SelectedItems[0].Tag as PortScan;
            if (scan == null) return;
            // 已记忆的板子把用途下拉锁成记忆值；首次见到的板子让用户选。
            int index = 0;
            if (scan.RememberedRole == BoardRole.Boat) index = 1;
            else if (scan.RememberedRole == BoardRole.Remote) index = 2;
            roleChoice.SelectedIndex = index;
            roleChoice.Enabled = !busy && scan.RememberedRole == BoardRole.Unknown && scan.HasChip;
            if (scan.RememberedRole != BoardRole.Unknown)
            {
                SetStatus("串口 " + scan.Port.Name + " 已按 MAC " + scan.MacText + " 记忆为" +
                          RoleNames.Chinese(scan.RememberedRole) + "。烧录前会再核对一次。", false);
            }
            else if (scan.HasChip)
            {
                SetStatus("串口 " + scan.Port.Name + " 是首次使用（MAC " + scan.MacText + "），请先选择用途。", false);
            }
            else
            {
                SetStatus("串口 " + scan.Port.Name + " 没有读到身份，无法操作。", true);
            }
        }

        private void OnRoleChosen()
        {
            if (portList.SelectedItems.Count == 0) return;
            PortScan scan = portList.SelectedItems[0].Tag as PortScan;
            if (scan == null || !scan.Responded) return;
            if (roleChoice.SelectedIndex == 0) return;
            BoardRole role = roleChoice.SelectedIndex == 1 ? BoardRole.Boat : BoardRole.Remote;
            KnownBoard known = roles.Find(scan.Identity.Mac);
            if (known != null && known.Role != role)
            {
                AppendLog("注意：MAC " + scan.Identity.Mac + " 本机记忆为" + RoleNames.Chinese(known.Role) +
                          "，不能当作" + RoleNames.Chinese(role) + "。");
                return;
            }
            if (known == null)
                AppendLog("已选择用途：" + RoleNames.Chinese(role) + "（MAC " + scan.Identity.Mac +
                          "，将在成功烧录后记住）。");
        }

        private PortScan SelectedScan()
        {
            if (portList.SelectedItems.Count == 0) return null;
            return portList.SelectedItems[0].Tag as PortScan;
        }

        private BoardRole ChosenRole()
        {
            if (roleChoice.SelectedIndex == 1) return BoardRole.Boat;
            if (roleChoice.SelectedIndex == 2) return BoardRole.Remote;
            return BoardRole.Unknown;
        }

        private void RunFlash()
        {
            if (busy) return;
            FirmwareSuite suite = currentSuite();
            if (suite == null || !suite.Usable)
            {
                SetStatus("请先选择一个可用的固件套装", true);
                return;
            }
            PortScan scan = SelectedScan();
            if (scan == null) { SetStatus("请先选择一块板子的串口", true); return; }
            BoardRole role = scan.RememberedRole != BoardRole.Unknown ? scan.RememberedRole : ChosenRole();
            SetBusy(true, "正在烧录……（套装：" + suite.DisplayName + "）");
            try
            {
                OperationResult result = workflow.FlashCurrent(scan, role, backupBeforeFlash.Checked,
                                                               forceReflashCheck.Checked);
                ShowResult(result);
                RefreshPorts();
            }
            catch (Exception ex)
            {
                AppendLog("烧录异常：" + ex.Message);
                SetStatus("烧录异常：" + ex.Message, true);
            }
            finally { SetBusy(false, null); }
        }

        private void RunBackup()
        {
            if (busy) return;
            PortScan scan = SelectedScan();
            if (scan == null) { SetStatus("请先选择一块板子的串口", true); return; }
            SetBusy(true, "正在备份原固件……读取整片 Flash 时请勿拔线");
            try
            {
                OperationResult result = workflow.BackupOnly(scan);
                ShowResult(result);
            }
            catch (Exception ex)
            {
                AppendLog("备份异常：" + ex.Message);
                SetStatus("备份异常：" + ex.Message, true);
            }
            finally { SetBusy(false, null); }
        }

        // 只读核验：不写配置，用于解除“已保存但未确认生效”等状态。
        private void RunVerify()
        {
            if (busy) return;
            PortScan scan = SelectedScan();
            if (scan == null) { SetStatus("请先选择要核验的那块板子的串口", true); return; }
            BoardRole role = scan.RememberedRole != BoardRole.Unknown ? scan.RememberedRole : ChosenRole();
            if (role == BoardRole.Unknown)
            {
                SetStatus("请先选择这块板是小船还是遥控器", true);
                return;
            }
            // 期望对端：优先取另一个已扫描到、角色互补的板子；否则用该端当前对端地址。
            string peer = "";
            foreach (PortScan other in scans.Values)
            {
                if (other == scan || !other.Responded) continue;
                if (role == BoardRole.Boat && other.Identity.Role == BoardRole.Remote) peer = other.Identity.Mac;
                if (role == BoardRole.Remote && other.Identity.Role == BoardRole.Boat) peer = other.Identity.Mac;
                if (peer.Length > 0) break;
            }
            if (peer.Length == 0 && scan.Responded) peer = scan.Identity.Peer;
            if (string.IsNullOrEmpty(peer) || peer == "00:00:00:00:00:00")
            {
                SetStatus("没有可用的期望对端地址：请同时插上另一端，或先完成一次配对", true);
                return;
            }
            string question = "只读核验（不会写入任何配置）：" + Environment.NewLine + Environment.NewLine +
                              "串口：" + scan.Port.Name + Environment.NewLine +
                              "角色：" + RoleNames.Chinese(role) + Environment.NewLine +
                              "期望本机 MAC：" + scan.MacText + Environment.NewLine +
                              "期望对端 MAC：" + peer + Environment.NewLine + Environment.NewLine +
                              "如果板子刚写入过配对，请先断开动力、重新上电再做核验。";
            if (MessageBox.Show(this, question, "确认只读核验", MessageBoxButtons.OKCancel,
                    MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.OK) return;

            SetBusy(true, "正在只读核验……（不写入配置）");
            try
            {
                OperationResult result = workflow.VerifyOnly(scan, role, peer);
                ShowResult(result);
                RefreshPorts();
            }
            catch (Exception ex)
            {
                AppendLog("只读核验异常：" + ex.Message);
                SetStatus("只读核验异常：" + ex.Message, true);
            }
            finally { SetBusy(false, null); }
        }

        private void RunPair()
        {
            if (busy) return;
            string boat = boatPortChoice.SelectedItem as string;
            string remote = remotePortChoice.SelectedItem as string;
            if (string.IsNullOrEmpty(boat) || string.IsNullOrEmpty(remote))
            {
                SetStatus("请分别选择小船和遥控器的串口", true);
                return;
            }
            if (string.Equals(boat, remote, StringComparison.OrdinalIgnoreCase))
            {
                AppendLog("小船和遥控器不能选同一个串口（" + boat + "）。");
                SetStatus("两块板必须使用不同串口；串口号只是连接，身份按 MAC 核对", true);
                return;
            }
            PortScan boatScan, remoteScan;
            scans.TryGetValue(boat, out boatScan);
            scans.TryGetValue(remote, out remoteScan);
            SetBusy(true, "正在配对……（只改对端地址，不切换固件、不重刷）");
            try
            {
                OperationResult result = workflow.PairTwoBoards(boatScan, remoteScan);
                ShowResult(result);
                RefreshPorts();
            }
            catch (Exception ex)
            {
                AppendLog("配对异常：" + ex.Message);
                SetStatus("配对异常：" + ex.Message, true);
            }
            finally { SetBusy(false, null); }
        }

        // 汇总只认 result.Ok。部分完成、超时、待核验一律走警告分支，
        // 不会因为文案里出现“已保存”等字样就被显示成绿色完成。
        private void ShowResult(OperationResult result)
        {
            if (result == null) return;
            foreach (string step in result.Steps) AppendLog(step);
            if (!string.IsNullOrEmpty(result.Summary)) AppendLog(result.Summary);
            if (!result.Ok && !string.IsNullOrEmpty(result.Error)) AppendLog("未完成：" + result.Error);

            if (result.Ok && result.Partial)
            {
                // 防御：状态自相矛盾时按未完成处理，绝不显示绿色。
                SetStatus(FirstLine(result.Summary), true);
                MessageBox.Show(this, result.Summary, "部分完成，需要重试",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (result.Ok)
            {
                SetStatus(FirstLine(result.Summary), false);
                MessageBox.Show(this, result.Summary, "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else if (result.Partial)
            {
                SetStatus(FirstLine(result.Summary), true);
                MessageBox.Show(this, result.Summary, "部分完成，需要重试", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            else
            {
                string message = string.IsNullOrEmpty(result.Error) ? result.Summary : result.Error;
                SetStatus(FirstLine(message), true);
                MessageBox.Show(this, message, "未完成", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private static string FirstLine(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            int index = text.IndexOfAny(new char[] { '\r', '\n' });
            return index < 0 ? text : text.Substring(0, index);
        }

        private void OpenRecords()
        {
            try
            {
                paths.EnsureDirectories();
                System.Diagnostics.Process.Start("explorer.exe", "\"" + paths.RecordsDir + "\"");
            }
            catch (Exception ex)
            {
                AppendLog("打开记录目录失败：" + ex.Message);
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (busy && e.CloseReason == CloseReason.UserClosing)
            {
                // 读整片 Flash 或写固件时关窗口会中断操作，必须先确认。
                DialogResult answer = MessageBox.Show(this,
                    "现在正在操作板子（可能正在读备份或写固件）。中途关闭窗口会中断操作，可能让板子停在不确定状态。" +
                    Environment.NewLine + Environment.NewLine + "确定要中断并关闭吗？",
                    "操作进行中", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                if (answer != DialogResult.Yes) { e.Cancel = true; return; }
            }
            base.OnFormClosing(e);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            // 记录入口放在日志区右键菜单，主操作区只保留三个按钮。
            ContextMenu menu = new ContextMenu();
            menu.MenuItems.Add(new MenuItem("打开记录目录", delegate { OpenRecords(); }));
            menu.MenuItems.Add(new MenuItem("复制全部日志", delegate { CopyLog(); }));
            logBox.ContextMenu = menu;
        }

        private void CopyLog()
        {
            try { Clipboard.SetText(logBox.Text); SetStatus("日志已复制到剪贴板", false); }
            catch (Exception ex) { AppendLog("复制失败：" + ex.Message); }
        }

        private void SetBusy(bool value, string status)
        {
            busy = value;
            refreshButton.Enabled = !value;
            suiteChoice.Enabled = !value;
            backupButton.Enabled = !value;
            verifyButton.Enabled = !value && library.Suites.Count > 0;
            boatPortChoice.Enabled = !value;
            remotePortChoice.Enabled = !value;
            backupBeforeFlash.Enabled = !value;
            forceReflashCheck.Enabled = !value;
            if (value) roleChoice.Enabled = false;
            Cursor = value ? Cursors.WaitCursor : Cursors.Default;
            if (!string.IsNullOrEmpty(status)) SetStatus(status, false);
            UpdateActionState();
            Application.DoEvents();
        }

        // 烧录需要可用套装；配对与备份不需要——配对只改地址，备份只读。
        private void UpdateActionState()
        {
            FirmwareSuite suite = currentSuite();
            bool suiteOk = suite != null && suite.Usable;
            flashButton.Enabled = !busy && suiteOk;
            pairButton.Enabled = !busy && library.Suites.Count > 0;
            if (!busy && !suiteOk)
                flashButton.Text = "烧录当前板（需先选可用套装）";
            else
                flashButton.Text = "烧录当前板";
        }

        private void SetStatus(string text, bool warn)
        {
            statusLabel.Text = text;
            statusLabel.ForeColor = warn ? Color.FromArgb(178, 58, 40) : Color.FromArgb(60, 64, 72);
            Application.DoEvents();
        }

        private void AppendLog(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            logBox.AppendText(text + Environment.NewLine);
            logBox.SelectionStart = logBox.TextLength;
            logBox.ScrollToCaret();
            // 长操作期间刷新界面，避免看起来像卡死；按钮在 busy 时已禁用，关闭窗口会被拦截。
            Application.DoEvents();
        }

        // ---- IUserInteraction ----

        public bool ConfirmSafetyNotice(string detail)
        {
            string text = detail + Environment.NewLine + Environment.NewLine +
                          "这条提示每次运行只出现一次，同一次操作不会反复确认。" + Environment.NewLine +
                          "确认后开始读取串口身份。";
            return MessageBox.Show(this, text, "开始前确认（只需一次）",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.OK;
        }

        public bool ConfirmFlash(FlashPlan plan)
        {
            FirmwareSpec spec = plan.Suite == null ? null : plan.Suite.SpecFor(plan.Role);
            StringBuilder text = new StringBuilder();
            text.AppendLine("即将烧录，请核对以下唯一目标：");
            text.AppendLine();
            text.AppendLine("串口（仅本次连接）：" + plan.Port);
            text.AppendLine("芯片 MAC（身份依据）：" + plan.Identity.MacText);
            text.AppendLine();
            text.AppendLine("【板上现在】");
            text.AppendLine("  角色：" + RoleNames.Chinese(plan.Role));
            text.AppendLine("  目标转速：" + plan.BoardSpeedText);
            text.AppendLine("  转向角度：" + plan.BoardSteerText);
            text.AppendLine("  FWID：" + (string.IsNullOrEmpty(plan.BoardFwid) ? "无（旧版程序）" : plan.BoardFwid));
            text.AppendLine();
            text.AppendLine("【将写入】");
            text.AppendLine("  套装：" + (plan.Suite == null ? "未选择" : plan.Suite.DisplayName));
            text.AppendLine("  版本：" + plan.FirmwareVersion);
            text.AppendLine("  角色：" + RoleNames.Chinese(plan.Role));
            text.AppendLine("  目标转速：" + (plan.Suite == null ? "未知" : plan.Suite.TargetRpm + " 转"));
            text.AppendLine("  转向角度：" + (plan.Suite == null ? "未知" : plan.Suite.SteerText));
            text.AppendLine("  FWID：" + plan.Fwid);
            text.AppendLine();
            if (!string.IsNullOrEmpty(plan.CrossSuiteWarning))
            {
                text.AppendLine("【跨套装提示】");
                text.AppendLine(plan.CrossSuiteWarning);
                text.AppendLine();
            }
            if (!string.IsNullOrEmpty(plan.LockoutChangeText))
            {
                text.AppendLine("【超速锁存】");
                text.AppendLine(plan.LockoutChangeText);
                text.AppendLine();
            }
            if (plan.Downgrade)
            {
                text.AppendLine("【降级警告】");
                text.AppendLine("写入后的目标转速低于板上现有固件。确认这是你要的结果，再继续。");
                text.AppendLine();
            }
            if (plan.ExplicitReflash)
            {
                text.AppendLine("【明确重刷】");
                text.AppendLine("你已勾选“即使已匹配也重刷”，即使板上固件与所选套装一致也会写入。");
                text.AppendLine();
            }
            text.AppendLine("为什么需要烧录：" + plan.Check.Reason);
            text.AppendLine("烧录前备份：" + (plan.DoBackup ? "执行（8MB 约 3～5 分钟）" : "不执行（默认）"));
            if (!string.IsNullOrEmpty(plan.NvsNote))
            {
                text.AppendLine();
                text.AppendLine(plan.NvsNote);
            }
            text.AppendLine();
            text.AppendLine("写入后会强制校验，并重启读回身份，才判定成功。");
            text.AppendLine("确认 MAC 与眼前这块板一致后再继续。");
            return MessageBox.Show(this, text.ToString(),
                plan.Downgrade ? "确认烧录（降级）" : "确认烧录这一块板",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.OK;
        }

        public bool ConfirmBackup(BackupPlan plan)
        {
            string text = "即将读取整片原固件备份：" + Environment.NewLine + Environment.NewLine +
                          "串口（仅本次连接）：" + plan.Port + Environment.NewLine +
                          "芯片 MAC：" + plan.Identity.MacText + Environment.NewLine +
                          "角色：" + plan.Identity.RoleText + Environment.NewLine +
                          "大小：" + (plan.FlashBytes / 1024 / 1024) + "MB，约需 3～5 分钟" + Environment.NewLine + Environment.NewLine +
                          "备份只读取，不写入、不擦除。";
            return MessageBox.Show(this, text, "确认备份",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.OK;
        }

        public bool ConfirmPair(PairPlan plan)
        {
            StringBuilder text = new StringBuilder();
            text.AppendLine("即将配对两块板，请核对：");
            text.AppendLine();
            text.AppendLine("小船：串口 " + plan.BoatPort + "，MAC " + plan.Boat.Mac);
            text.AppendLine("  固件：" + plan.Boat.FirmwareText + "，" + plan.Boat.SteeringText);
            text.AppendLine("  现在对端：" + plan.Boat.PeerText + (plan.SkipBoat ? "（已正确，本次跳过）" : "（将改为 " + plan.Remote.Mac + "）"));
            text.AppendLine("遥控器：串口 " + plan.RemotePort + "，MAC " + plan.Remote.Mac);
            text.AppendLine("  固件：" + plan.Remote.FirmwareText + "，" + plan.Remote.SteeringText);
            text.AppendLine("  现在对端：" + plan.Remote.PeerText + (plan.SkipRemote ? "（已正确，本次跳过）" : "（将改为 " + plan.Boat.Mac + "）"));
            text.AppendLine();
            text.AppendLine("兼容性：" + plan.Description);
            text.AppendLine();
            text.AppendLine("只修改两块板保存的对端地址：不切换固件套装、不重刷程序。");
            text.AppendLine("已经指向正确对端的一端会跳过写入与重启。");
            text.AppendLine("写入前后都会按 MAC 核对；同一芯片或同一串口会被拒绝。");
            return MessageBox.Show(this, text.ToString(), "确认配对",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.OK;
        }

        public void Report(string text)
        {
            AppendLog(text);
        }
    }
}
