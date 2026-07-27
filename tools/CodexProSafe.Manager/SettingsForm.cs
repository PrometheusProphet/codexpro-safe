using System;
using System.Drawing;
using System.Windows.Forms;

namespace CodexProSafeManager
{
    internal sealed class SettingsForm : Form
    {
        private readonly TextBox repository = new TextBox();
        private readonly TextBox workspace = new TextBox();
        private readonly TextBox allowed = new TextBox();
        private readonly TextBox node = new TextBox();
        private readonly TextBox tunnelClient = new TextBox();
        private readonly TextBox tunnelProfile = new TextBox();
        private readonly TextBox apiKey = new TextBox();
        private readonly TextBox organization = new TextBox();
        private readonly CheckBox startWithWindows = new CheckBox();
        private readonly CheckBox startMinimized = new CheckBox();
        private readonly CheckBox autoStart = new CheckBox();
        private readonly CheckBox restartOnFailure = new CheckBox();

        public AppSettings Result { get; private set; }

        public SettingsForm(AppSettings value)
        {
            Text = "CodexPro-Safe Manager Settings";
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(720, 545);
            MinimumSize = new Size(620, 520);
            Font = new Font("Segoe UI", 9F);
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = false;

            TableLayoutPanel table = new TableLayoutPanel();
            table.Dock = DockStyle.Fill;
            table.Padding = new Padding(18);
            table.ColumnCount = 3;
            table.RowCount = 13;
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 86));

            AddPathRow(table, 0, "Repository", repository, true);
            AddPathRow(table, 1, "Workspace root", workspace, true);
            AddPathRow(table, 2, "Allowed root", allowed, true);
            AddPathRow(table, 3, "Node.js", node, false);
            AddPathRow(table, 4, "Tunnel client", tunnelClient, false);
            AddTextRow(table, 5, "Tunnel profile", tunnelProfile);
            AddTextRow(table, 6, "Control Plane API key", apiKey);
            apiKey.UseSystemPasswordChar = true;
            AddTextRow(table, 7, "Organization ID", organization);

            Label secretNote = new Label();
            secretNote.Text = "The key is encrypted for your Windows account with DPAPI. It is never stored in the repository or written to logs.";
            secretNote.AutoSize = true;
            secretNote.ForeColor = Color.DimGray;
            secretNote.Margin = new Padding(3, 0, 3, 8);
            table.Controls.Add(secretNote, 1, 8);
            table.SetColumnSpan(secretNote, 2);

            FlowLayoutPanel checks = new FlowLayoutPanel();
            checks.AutoSize = true;
            checks.FlowDirection = FlowDirection.TopDown;
            checks.WrapContents = false;
            ConfigureCheck(startWithWindows, "Launch manager when I sign in");
            ConfigureCheck(startMinimized, "Start minimized to the notification area");
            ConfigureCheck(autoStart, "Start connector and tunnel when manager opens");
            ConfigureCheck(restartOnFailure, "Restart manager-owned services after an unexpected exit");
            checks.Controls.Add(startWithWindows);
            checks.Controls.Add(startMinimized);
            checks.Controls.Add(autoStart);
            checks.Controls.Add(restartOnFailure);
            table.Controls.Add(checks, 1, 9);
            table.SetColumnSpan(checks, 2);

            Label takeover = new Label();
            takeover.Text = "Safety: external processes are stopped only after their listening port, executable/profile, command line, and configured roots match exactly.";
            takeover.AutoSize = true;
            takeover.ForeColor = Color.DimGray;
            takeover.Margin = new Padding(3, 8, 3, 8);
            table.Controls.Add(takeover, 1, 10);
            table.SetColumnSpan(takeover, 2);

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.AutoSize = true;
            buttons.FlowDirection = FlowDirection.RightToLeft;
            buttons.Dock = DockStyle.Fill;
            Button save = new Button { Text = "Save", Width = 90, Height = 30 };
            Button cancel = new Button { Text = "Cancel", Width = 90, Height = 30, DialogResult = DialogResult.Cancel };
            save.Click += SaveClicked;
            buttons.Controls.Add(save);
            buttons.Controls.Add(cancel);
            table.Controls.Add(buttons, 1, 11);
            table.SetColumnSpan(buttons, 2);

            Controls.Add(table);
            AcceptButton = save;
            CancelButton = cancel;

            repository.Text = value.RepositoryPath;
            workspace.Text = value.WorkspaceRoot;
            allowed.Text = value.AllowedRoot;
            node.Text = value.NodePath;
            tunnelClient.Text = value.TunnelClientPath;
            tunnelProfile.Text = value.TunnelProfile;
            apiKey.Text = value.ControlPlaneApiKey;
            organization.Text = value.OrganizationId;
            startWithWindows.Checked = value.StartWithWindows;
            startMinimized.Checked = value.StartMinimized;
            autoStart.Checked = value.AutoStartServices;
            restartOnFailure.Checked = value.RestartOnFailure;
        }

        private static void ConfigureCheck(CheckBox box, string text)
        {
            box.Text = text;
            box.AutoSize = true;
            box.Margin = new Padding(0, 0, 0, 5);
        }

        private static void AddTextRow(TableLayoutPanel table, int row, string label, TextBox box)
        {
            Label caption = new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left };
            box.Dock = DockStyle.Fill;
            box.Margin = new Padding(3, 3, 3, 7);
            table.Controls.Add(caption, 0, row);
            table.Controls.Add(box, 1, row);
            table.SetColumnSpan(box, 2);
        }

        private void AddPathRow(TableLayoutPanel table, int row, string label, TextBox box, bool directory)
        {
            Label caption = new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left };
            box.Dock = DockStyle.Fill;
            box.Margin = new Padding(3, 3, 3, 7);
            Button browse = new Button { Text = "Browse…", Dock = DockStyle.Top, Height = 27 };
            browse.Click += delegate
            {
                if (directory)
                {
                    using (FolderBrowserDialog dialog = new FolderBrowserDialog())
                    {
                        dialog.SelectedPath = box.Text;
                        if (dialog.ShowDialog(this) == DialogResult.OK) box.Text = dialog.SelectedPath;
                    }
                }
                else
                {
                    using (OpenFileDialog dialog = new OpenFileDialog())
                    {
                        dialog.FileName = box.Text;
                        dialog.Filter = "Executable (*.exe)|*.exe|All files (*.*)|*.*";
                        if (dialog.ShowDialog(this) == DialogResult.OK) box.Text = dialog.FileName;
                    }
                }
            };
            table.Controls.Add(caption, 0, row);
            table.Controls.Add(box, 1, row);
            table.Controls.Add(browse, 2, row);
        }

        private void SaveClicked(object sender, EventArgs args)
        {
            Result = new AppSettings
            {
                RepositoryPath = repository.Text.Trim(),
                WorkspaceRoot = workspace.Text.Trim(),
                AllowedRoot = allowed.Text.Trim(),
                NodePath = node.Text.Trim(),
                TunnelClientPath = tunnelClient.Text.Trim(),
                TunnelProfile = tunnelProfile.Text.Trim(),
                ControlPlaneApiKey = apiKey.Text,
                OrganizationId = organization.Text.Trim(),
                StartWithWindows = startWithWindows.Checked,
                StartMinimized = startMinimized.Checked,
                AutoStartServices = autoStart.Checked,
                RestartOnFailure = restartOnFailure.Checked
            };
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
