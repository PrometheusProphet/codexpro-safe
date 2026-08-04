using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CodexProSafeManager
{
    internal sealed class MainForm : Form
    {
        private readonly SecureSettingsStore store;
        private AppSettings settings;
        private readonly ProcessSupervisor supervisor;
        private readonly Label overall = new Label();
        private readonly Label connectorState = new Label();
        private readonly Label connectorDetail = new Label();
        private readonly Label tunnelState = new Label();
        private readonly Label tunnelDetail = new Label();
        private readonly PrivateLogView log = new PrivateLogView();
        private readonly NotifyIcon tray = new NotifyIcon();
        private readonly Timer timer = new Timer();
        private bool busy;
        private bool exiting;
        private bool firstShown = true;

        public MainForm(AppSettings settings, SecureSettingsStore store, bool startup)
        {
            this.settings = settings;
            this.store = store;
            supervisor = new ProcessSupervisor(settings);
            supervisor.LogLine += OnLogLine;
            supervisor.StateChanged += delegate
            {
                BeginInvokeIfReady(async delegate { await RefreshStatusAsync(); });
            };

            Text = "CodexPro-Safe Manager";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(760, 590);
            MinimumSize = new Size(680, 520);
            Font = new Font("Segoe UI", 9F);
            Icon = SystemIcons.Shield;

            BuildUi();
            BuildTray();

            timer.Interval = 3000;
            timer.Tick += async delegate
            {
                await RefreshStatusAsync();
                await supervisor.MonitorAndRecoverAsync();
            };
            timer.Start();

            Shown += async delegate
            {
                await RefreshStatusAsync();
                if (firstShown && (settings.StartMinimized || startup))
                {
                    firstShown = false;
                    HideToTray();
                }
                else firstShown = false;

                if (settings.AutoStartServices)
                    await RunOperationAsync("Start", supervisor.StartAllAsync);
            };
            FormClosing += OnFormClosing;
        }

        private void BuildUi()
        {
            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.Padding = new Padding(20);
            root.ColumnCount = 1;
            root.RowCount = 7;
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 82));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 82));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));

            Panel heading = new Panel { Dock = DockStyle.Fill };
            Label title = new Label
            {
                Text = "CodexPro-Safe Connector",
                Font = new Font("Segoe UI Semibold", 16F),
                AutoSize = true,
                Location = new Point(0, 0)
            };
            overall.Text = "Checking…";
            overall.AutoSize = true;
            overall.ForeColor = Color.DimGray;
            overall.Location = new Point(3, 32);
            heading.Controls.Add(title);
            heading.Controls.Add(overall);
            root.Controls.Add(heading, 0, 0);

            root.Controls.Add(BuildStatusCard("Connector", connectorState, connectorDetail, "http://127.0.0.1:8787/mcp"), 0, 1);
            root.Controls.Add(BuildStatusCard("OpenAI tunnel", tunnelState, tunnelDetail, "http://127.0.0.1:8080/ui"), 0, 2);

            FlowLayoutPanel primary = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight };
            primary.Controls.Add(ActionButton("Start All", 112, async delegate { await RunOperationAsync("Start", supervisor.StartAllAsync); }));
            primary.Controls.Add(ActionButton("Restart All", 112, async delegate
            {
                if (await ConfirmTakeoverIfNeededAsync("restart"))
                    await RunOperationAsync("Restart", delegate { return supervisor.RestartAllAsync(true); });
            }));
            primary.Controls.Add(ActionButton("Stop All", 112, async delegate
            {
                if (await ConfirmTakeoverIfNeededAsync("stop"))
                    await RunOperationAsync("Stop", delegate { return supervisor.StopAllAsync(true); });
            }));
            root.Controls.Add(primary, 0, 3);

            FlowLayoutPanel tools = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight };
            tools.Controls.Add(ActionButton("Open Tunnel UI", 125, delegate { OpenUrl("http://127.0.0.1:8080/ui"); return Task.FromResult(0); }));
            tools.Controls.Add(ActionButton("Open Logs", 105, delegate { OpenPath(LogWriter.DirectoryPath); return Task.FromResult(0); }));
            tools.Controls.Add(ActionButton("Settings", 95, delegate { OpenSettings(); return Task.FromResult(0); }));
            root.Controls.Add(tools, 0, 4);

            log.Dock = DockStyle.Fill;
            log.BackColor = Color.FromArgb(25, 25, 28);
            log.ForeColor = Color.Gainsboro;
            log.Font = new Font("Consolas", 8.5F);
            root.Controls.Add(log, 0, 5);

            Label footer = new Label
            {
                Text = "Closing the window minimizes to the notification area. Use Stop All to shut down the connector.",
                Dock = DockStyle.Fill,
                ForeColor = Color.DimGray,
                TextAlign = ContentAlignment.MiddleLeft
            };
            root.Controls.Add(footer, 0, 6);
            Controls.Add(root);
        }

        private static Panel BuildStatusCard(string title, Label state, Label detail, string endpoint)
        {
            Panel card = new Panel { Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(0, 3, 0, 6) };
            Label name = new Label
            {
                Text = title,
                Font = new Font("Segoe UI Semibold", 11F),
                AutoSize = true,
                Location = new Point(14, 10)
            };
            state.Text = "Checking";
            state.Font = new Font("Segoe UI Semibold", 9F);
            state.AutoSize = true;
            state.Location = new Point(585, 12);
            state.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            detail.Text = endpoint;
            detail.AutoSize = true;
            detail.ForeColor = Color.DimGray;
            detail.Location = new Point(15, 42);
            card.Controls.Add(name);
            card.Controls.Add(state);
            card.Controls.Add(detail);
            return card;
        }

        private Button ActionButton(string text, int width, Func<Task> action)
        {
            Button button = new Button { Text = text, Width = width, Height = 32, Margin = new Padding(0, 3, 8, 3) };
            button.Click += async delegate { await action(); };
            return button;
        }

        private void BuildTray()
        {
            tray.Icon = SystemIcons.Shield;
            tray.Text = "CodexPro-Safe Manager";
            tray.Visible = true;
            tray.DoubleClick += delegate { RestoreFromTray(); };
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("Open", null, delegate { RestoreFromTray(); });
            menu.Items.Add("Start All", null, async delegate { await RunOperationAsync("Start", supervisor.StartAllAsync); });
            menu.Items.Add("Restart All", null, async delegate
            {
                if (await ConfirmTakeoverIfNeededAsync("restart"))
                    await RunOperationAsync("Restart", delegate { return supervisor.RestartAllAsync(true); });
            });
            menu.Items.Add("Stop All", null, async delegate
            {
                if (await ConfirmTakeoverIfNeededAsync("stop"))
                    await RunOperationAsync("Stop", delegate { return supervisor.StopAllAsync(true); });
            });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Open Tunnel UI", null, delegate { OpenUrl("http://127.0.0.1:8080/ui"); });
            menu.Items.Add("Settings", null, delegate { OpenSettings(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit manager", null, delegate { ExitManager(); });
            tray.ContextMenuStrip = menu;
        }

        private async Task RunOperationAsync(string name, Func<Task> operation)
        {
            if (busy) return;
            busy = true;
            overall.Text = name + " in progress…";
            try
            {
                await operation();
            }
            catch (Exception exception)
            {
                LogWriter.Append("manager", name + " failed: " + exception.Message);
                MessageBox.Show(this, exception.Message, name + " failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                busy = false;
            }
            await RefreshStatusAsync();
        }

        private async Task<bool> ConfirmTakeoverIfNeededAsync(string action)
        {
            ServiceSnapshot snapshot = await supervisor.GetSnapshotAsync();
            bool external = snapshot.ConnectorState == ServiceState.RunningExternal ||
                            snapshot.TunnelState == ServiceState.RunningExternal;
            if (!external) return true;
            DialogResult result = MessageBox.Show(
                this,
                "One or both services are healthy but were started outside this manager.\n\n" +
                "To " + action + " them, the manager will verify the exact listening process, executable/profile, command line, and configured roots before stopping anything. It refuses the action if any check differs.\n\n" +
                "Proceed with exact-process takeover?",
                "Take control of existing CodexPro-Safe services",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            return result == DialogResult.Yes;
        }

        private async Task RefreshStatusAsync()
        {
            if (IsDisposed) return;
            ServiceSnapshot snapshot = await supervisor.GetSnapshotAsync();
            SetState(connectorState, snapshot.ConnectorState);
            SetState(tunnelState, snapshot.TunnelState);
            connectorDetail.Text = snapshot.ConnectorDetail + " · 127.0.0.1:8787";
            tunnelDetail.Text = snapshot.TunnelDetail + " · 127.0.0.1:8080";

            if (snapshot.ConnectorHealthy && snapshot.TunnelHealthy)
            {
                overall.Text = "Connected and ready";
                tray.Text = "CodexPro-Safe · Ready";
            }
            else if (snapshot.ConnectorHealthy)
            {
                overall.Text = "Connector ready · tunnel stopped";
                tray.Text = "CodexPro-Safe · Connector only";
            }
            else
            {
                overall.Text = "Stopped";
                tray.Text = "CodexPro-Safe · Stopped";
            }
        }

        private static void SetState(Label label, ServiceState state)
        {
            switch (state)
            {
                case ServiceState.RunningOwned:
                    label.Text = "RUNNING";
                    label.ForeColor = Color.ForestGreen;
                    break;
                case ServiceState.RunningExternal:
                    label.Text = "EXTERNAL";
                    label.ForeColor = Color.DarkOrange;
                    break;
                case ServiceState.Starting:
                    label.Text = "STARTING";
                    label.ForeColor = Color.RoyalBlue;
                    break;
                case ServiceState.Faulted:
                    label.Text = "ERROR";
                    label.ForeColor = Color.Firebrick;
                    break;
                default:
                    label.Text = "STOPPED";
                    label.ForeColor = Color.DimGray;
                    break;
            }
        }

        private void OnLogLine(string source, string message)
        {
            BeginInvokeIfReady(delegate
            {
                log.AppendLine(String.Format("{0:T} [{1}] {2}", DateTime.Now, source, message));
            });
        }

        private void BeginInvokeIfReady(Action action)
        {
            if (IsDisposed || !IsHandleCreated) return;
            BeginInvoke(action);
        }

        private void OpenSettings()
        {
            RestoreFromTray();
            using (SettingsForm dialog = new SettingsForm(settings))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    store.Save(dialog.Result, Application.ExecutablePath);
                    settings = dialog.Result;
                    supervisor.UpdateSettings(settings);
                    LogWriter.Append("manager", "Settings updated. Secrets remained encrypted and were not logged.");
                    MessageBox.Show(this, "Settings saved.", "CodexPro-Safe Manager", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception exception)
                {
                    MessageBox.Show(this, exception.Message, "Could not save settings", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private static void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception exception)
            {
                MessageBox.Show(exception.Message, "Could not open URL", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static void OpenPath(string path)
        {
            try
            {
                Directory.CreateDirectory(path);
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception exception)
            {
                MessageBox.Show(exception.Message, "Could not open folder", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void HideToTray()
        {
            Hide();
            ShowInTaskbar = false;
        }

        private void RestoreFromTray()
        {
            ShowInTaskbar = true;
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        private void OnFormClosing(object sender, FormClosingEventArgs args)
        {
            if (exiting) return;
            args.Cancel = true;
            HideToTray();
            tray.ShowBalloonTip(1500, "CodexPro-Safe Manager", "Still running in the notification area.", ToolTipIcon.Info);
        }

        private async void ExitManager()
        {
            ServiceSnapshot snapshot = await supervisor.GetSnapshotAsync();
            if (snapshot.ConnectorState == ServiceState.RunningOwned ||
                snapshot.TunnelState == ServiceState.RunningOwned)
            {
                MessageBox.Show(
                    this,
                    "This manager still owns a running connector or tunnel.\n\nUse Stop All first, then exit. This keeps child logging and shutdown behavior deterministic.",
                    "Stop managed services before exit",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }
            DialogResult result = MessageBox.Show(
                this,
                "Exit the manager?\n\nServices that were started outside this manager are not changed.",
                "Exit CodexPro-Safe Manager",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2);
            if (result != DialogResult.Yes) return;
            exiting = true;
            timer.Stop();
            tray.Visible = false;
            supervisor.Dispose();
            Application.Exit();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                timer.Dispose();
                tray.Dispose();
                supervisor.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
