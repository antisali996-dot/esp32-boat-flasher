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
        private readonly FirmwareCatalog catalog;
        private readonly RoleStore roles;
        private readonly IBoardTransport transport;
        private readonly Workflow workflow;

        private readonly ListView portList = new ListView();
        private readonly ComboBox roleChoice = new ComboBox();
        private readonly ComboBox boatPortChoice = new ComboBox();
        private readonly ComboBox remotePortChoice = new ComboBox();
        private readonly CheckBox backupBeforeFlash = new CheckBox();
        private readonly Button refreshButton = new Button();
        private readonly Button flashButton = new Button();
        private readonly Button pairButton = new Button();
        private readonly Button backupButton = new Button();
        private readonly RichTextBox logBox = new RichTextBox();
        private readonly Label statusLabel = new Label();
        private readonly Label catalogLabel = new Label();
        private readonly TabControl tabs = new TabControl();
        private readonly Dictionary<string, PortScan> scans = new Dictionary<string, PortScan>(StringComparer.OrdinalIgnoreCase);
        private bool busy;

        public MainForm(AppPaths paths, FirmwareCatalog catalog, RoleStore roles, IBoardTransport transport)
        {
            this.paths = paths;
            this.catalog = catalog;
            this.roles = roles;
            this.transport = transport;
            this.workflow = new Workflow(paths, catalog, roles, transport, new SystemClock(), this);

            BuildUi();
            AppendLog("工具已就绪。固件包：" + catalog.Version);
            AppendLog(catalog.Summary);
            if (catalog.Problems.Count > 0)
            {
                foreach (string problem in catalog.Problems) AppendLog("固件包自检问题：" + problem);
                SetStatus("固件包自检未通过，烧录与配对已禁用", true);
            }
            if (!string.IsNullOrEmpty(roles.LoadError)) AppendLog(roles.LoadError);
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("BOATTOOL_NO_AUTOSCAN")))
            {
                AppendLog("已按环境变量跳过自动扫描串口（测试模式）。");
                return;
            }
            RefreshPorts();
        }

        private void BuildUi()
        {
            Text = "ESP32 小船 / 遥控器烧录配对工具";
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(1000, 720);
            Size = new Size(1100, 780);
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
            BackColor = Color.FromArgb(246, 247, 250);

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 1;
            root.RowCount = 4;
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 84));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 300));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            root.Padding = new Padding(12, 10, 12, 8);
            Controls.Add(root);

            root.Controls.Add(BuildHeader(), 0, 0);
            root.Controls.Add(BuildPortPanel(), 0, 1);
            root.Controls.Add(BuildActions(), 0, 2);
            root.Controls.Add(statusLabel, 0, 3);

            statusLabel.Dock = DockStyle.Fill;
            statusLabel.TextAlign = ContentAlignment.MiddleLeft;
            statusLabel.ForeColor = Color.FromArgb(60, 64, 72);
            statusLabel.Padding = new Padding(2, 6, 2, 0);
            statusLabel.Text = "请先断开动力电池，只保留 USB 供电；识别串口时板子可能复位。记录目录在“记录”文件夹。";
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
            catalogLabel.Text = "固件包 " + catalog.Version + Environment.NewLine +
                               "中位 " + catalog.CenterUs + "us，左右各 " + catalog.SteerDeg + "°（" +
                               catalog.AngleMin + "～" + catalog.AngleMax + "）" + Environment.NewLine +
                               "脉宽 " + catalog.PulseMinUs + "～" + catalog.PulseMaxUs + "us，配对协议 v" + catalog.PairProtocol;
            panel.Controls.Add(catalogLabel);
            return panel;
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
            portList.Columns.Add("串口", 70);
            portList.Columns.Add("板子角色", 150);
            portList.Columns.Add("芯片 MAC", 165);
            portList.Columns.Add("固件版本", 230);
            portList.Columns.Add("转向角度", 120);
            portList.Columns.Add("对端地址", 150);
            portList.Columns.Add("说明", 260);
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

            layout.Controls.Add(buttons, 0, 1);
            return group;
        }

        private Control BuildActions()
        {
            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.ColumnCount = 2;
            layout.RowCount = 1;
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 330));
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

            pairButton.Text = "配对两块板";
            pairButton.Dock = DockStyle.Fill;
            pairButton.Font = new Font(Font.FontFamily, 10F, FontStyle.Bold);
            pairButton.Click += delegate { RunPair(); };
            actions.Controls.Add(pairButton, 0, 4);

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
            PortScan scan = SelectedScan();
            if (scan == null) { SetStatus("请先选择一块板子的串口", true); return; }
            BoardRole role = scan.RememberedRole != BoardRole.Unknown ? scan.RememberedRole : ChosenRole();
            SetBusy(true, "正在烧录……");
            try
            {
                OperationResult result = workflow.FlashCurrent(scan, role, backupBeforeFlash.Checked);
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
            SetBusy(true, "正在配对……");
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

        private void ShowResult(OperationResult result)
        {
            if (result == null) return;
            foreach (string step in result.Steps) AppendLog(step);
            if (!string.IsNullOrEmpty(result.Summary)) AppendLog(result.Summary);
            if (!result.Ok && !string.IsNullOrEmpty(result.Error)) AppendLog("未完成：" + result.Error);

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
            flashButton.Enabled = !value && catalog.Problems.Count == 0;
            backupButton.Enabled = !value;
            pairButton.Enabled = !value && catalog.Problems.Count == 0;
            boatPortChoice.Enabled = !value;
            remotePortChoice.Enabled = !value;
            backupBeforeFlash.Enabled = !value;
            if (value) roleChoice.Enabled = false;
            Cursor = value ? Cursors.WaitCursor : Cursors.Default;
            if (!string.IsNullOrEmpty(status)) SetStatus(status, false);
            Application.DoEvents();
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
            StringBuilder text = new StringBuilder();
            text.AppendLine("即将烧录，请核对以下唯一目标：");
            text.AppendLine();
            text.AppendLine("串口（仅本次连接）：" + plan.Port);
            text.AppendLine("芯片 MAC（身份依据）：" + plan.Identity.MacText);
            text.AppendLine("角色：" + RoleNames.Chinese(plan.Role));
            text.AppendLine("板上当前固件：" + plan.Identity.FirmwareText);
            text.AppendLine("为什么需要烧录：" + plan.Check.Reason);
            text.AppendLine("将写入固件：" + plan.FirmwareVersion);
            text.AppendLine("固件身份：" + plan.Fwid);
            text.AppendLine("烧录前备份：" + (plan.DoBackup ? "执行（8MB 约 3～5 分钟）" : "不执行（默认）"));
            text.AppendLine();
            text.AppendLine("写入后会强制校验，并重启读回身份，才判定成功。");
            text.AppendLine("确认 MAC 与眼前这块板一致后再继续。");
            return MessageBox.Show(this, text.ToString(), "确认烧录这一块板",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.OK;
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
            text.AppendLine("遥控器：串口 " + plan.RemotePort + "，MAC " + plan.Remote.Mac);
            text.AppendLine();
            text.AppendLine("将把小船对端改为 " + plan.Remote.Mac + "，遥控器对端改为 " + plan.Boat.Mac + "。");
            text.AppendLine("只修改持久配对地址，不重新烧录固件。");
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
