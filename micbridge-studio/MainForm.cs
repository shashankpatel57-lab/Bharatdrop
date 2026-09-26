using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using NAudio.Dsp;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MicBridgeStudio;

public sealed class MainForm : Form
{
    private readonly PhoneReceiver phone = new();
    private readonly ComboBox modeBox = new();
    private readonly ComboBox micBox = new();
    private readonly ComboBox profileBox = new();
    private readonly ComboBox fpsBox = new();
    private readonly NumericUpDown countdownBox = new();
    private readonly TextBox folderBox = new();
    private readonly CheckBox minimizeBox = new();
    private readonly Label phoneLabel = new();
    private readonly Label statusLabel = new();
    private readonly ProgressBar meter = new();
    private readonly Button startButton = new();
    private readonly Button stopButton = new();
    private readonly Button openFolderButton = new();
    private readonly FlowLayoutPanel recentPanel = new();
    private readonly NotifyIcon tray;
    private readonly System.Windows.Forms.Timer uiTimer = new();
    private readonly List<DeviceItem> microphones = new();

    private AudioMixEngine? audioEngine;
    private Process? ffmpeg;
    private bool recording;
    private string? currentOutput;
    private StopOverlayForm? stopOverlay;

    private static readonly Color Bg = Color.FromArgb(243, 246, 250);
    private static readonly Color Surface = Color.White;
    private static readonly Color Ink = Color.FromArgb(25, 32, 43);
    private static readonly Color Muted = Color.FromArgb(94, 105, 122);
    private static readonly Color Accent = Color.FromArgb(0, 95, 184);
    private static readonly Color AccentHover = Color.FromArgb(0, 84, 163);
    private static readonly Color Border = Color.FromArgb(223, 228, 235);
    private static readonly Color Success = Color.FromArgb(16, 124, 16);
    private static readonly Color Danger = Color.FromArgb(196, 43, 28);

    public MainForm()
    {
        Text = "MicBridge Studio";
        Width = 1040;
        Height = 780;
        MinimumSize = new Size(900, 680);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Bg;
        Font = new Font("Segoe UI Variable Text", 10F);
        AutoScaleMode = AutoScaleMode.Dpi;

        BuildUi();

        tray = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "MicBridge Studio",
            Visible = true
        };

        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("Open MicBridge Studio", null, (_, _) => ShowFromTray());
        trayMenu.Items.Add("Stop recording", null, async (_, _) => await StopRecordingAsync());
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add("Exit", null, async (_, _) =>
        {
            if (recording) await StopRecordingAsync();
            tray.Visible = false;
            Application.Exit();
        });
        tray.ContextMenuStrip = trayMenu;
        tray.DoubleClick += (_, _) => ShowFromTray();

        Load += (_, _) =>
        {
            folderBox.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "MicBridge Recordings");
            RefreshMicrophones();
            RefreshRecentRecordings();
            phone.Start();
        };

        FormClosing += async (_, e) =>
        {
            if (recording)
            {
                e.Cancel = true;
                await StopRecordingAsync();
                BeginInvoke(Close);
                return;
            }
            phone.Dispose();
            tray.Visible = false;
        };

        uiTimer.Interval = 300;
        uiTimer.Tick += (_, _) =>
        {
            phoneLabel.Text = phone.IsConnected
                ? $"Connected • {phone.LastDeviceName}"
                : "Waiting for phone…";
            phoneLabel.ForeColor = phone.IsConnected ? Success : Muted;
            meter.Value = Math.Clamp((int)(phone.Level * 100), 0, 100);
        };
        uiTimer.Start();
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = Bg
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 104));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 96));
        Controls.Add(root);

        root.Controls.Add(BuildHeader(), 0, 0);
        root.Controls.Add(BuildWorkspace(), 0, 1);
        root.Controls.Add(BuildCommandBar(), 0, 2);
    }

    private Control BuildHeader()
    {
        var panel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Surface,
            Padding = new Padding(28, 20, 28, 16)
        };

        var title = new Label
        {
            Text = "MicBridge Studio",
            Font = new Font("Segoe UI Variable Display Semibold", 25F),
            ForeColor = Ink,
            AutoSize = true,
            Location = new Point(28, 18)
        };
        panel.Controls.Add(title);

        var subtitle = new Label
        {
            Text = "Screen recording with your phone microphone, Windows audio and local microphones",
            Font = new Font("Segoe UI Variable Text", 10.5F),
            ForeColor = Muted,
            AutoSize = true,
            Location = new Point(31, 63)
        };
        panel.Controls.Add(subtitle);

        var badge = new Label
        {
            Text = "  v2.2  ",
            AutoSize = true,
            BackColor = Color.FromArgb(232, 242, 252),
            ForeColor = Accent,
            Font = new Font("Segoe UI Semibold", 9F),
            Padding = new Padding(6, 4, 6, 4),
            Anchor = AnchorStyles.Top | AnchorStyles.Right
        };
        badge.Location = new Point(panel.Width - badge.Width - 28, 26);
        panel.Resize += (_, _) => badge.Location = new Point(panel.ClientSize.Width - badge.Width - 28, 27);
        panel.Controls.Add(badge);

        var line = new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = Border };
        panel.Controls.Add(line);
        return panel;
    }

    private Control BuildWorkspace()
    {
        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Padding = new Padding(24, 20, 24, 16),
            BackColor = Bg
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 54));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 46));

        var left = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(0, 0, 9, 0),
            BackColor = Bg
        };

        var right = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(9, 0, 0, 0),
            BackColor = Bg
        };

        left.Controls.Add(MakeCard("Phone microphone", "Connect your Android phone over the same Wi-Fi or hotspot.", BuildPhonePanel(), 470));
        left.Controls.Add(MakeCard("Audio", "Choose what gets recorded and how microphone voice is processed.", BuildAudioPanel(), 470));

        right.Controls.Add(MakeCard("Screen", "Record the complete Windows desktop.", BuildVideoPanel(), 390));
        right.Controls.Add(MakeCard("Recent recordings", "Your five most recent MicBridge videos.", BuildRecentPanel(), 390));

        body.Controls.Add(left, 0, 0);
        body.Controls.Add(right, 1, 0);
        return body;
    }

    private Control BuildCommandBar()
    {
        var bar = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Surface,
            Padding = new Padding(28, 17, 28, 16)
        };
        bar.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 1, BackColor = Border });

        statusLabel.Text = "Ready to record";
        statusLabel.ForeColor = Muted;
        statusLabel.AutoSize = true;
        statusLabel.Location = new Point(29, 19);
        statusLabel.Font = new Font("Segoe UI Variable Text", 10F);
        bar.Controls.Add(statusLabel);

        startButton.Text = "●  Start recording";
        startButton.Size = new Size(210, 50);
        startButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        startButton.Location = new Point(bar.Width - 238, 22);
        StylePrimaryButton(startButton);
        startButton.Click += async (_, _) => await StartRecordingAsync();
        bar.Resize += (_, _) => startButton.Location = new Point(bar.ClientSize.Width - 238, 22);
        bar.Controls.Add(startButton);

        stopButton.Text = "■  Stop";
        stopButton.Size = new Size(110, 50);
        stopButton.Enabled = false;
        stopButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        stopButton.Location = new Point(bar.Width - 356, 22);
        StyleSecondaryButton(stopButton);
        stopButton.Click += async (_, _) => await StopRecordingAsync();
        bar.Resize += (_, _) => stopButton.Location = new Point(bar.ClientSize.Width - 356, 22);
        bar.Controls.Add(stopButton);

        return bar;
    }

    private Control BuildPhonePanel()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, BackColor = Surface };

        var connectionRow = new Panel { Height = 38, Dock = DockStyle.Top, BackColor = Surface };
        var dot = new Label
        {
            Text = "●",
            AutoSize = true,
            ForeColor = Color.FromArgb(148, 163, 184),
            Font = new Font("Segoe UI", 10F),
            Location = new Point(0, 7)
        };
        connectionRow.Controls.Add(dot);

        phoneLabel.Text = "Waiting for phone…";
        phoneLabel.AutoSize = true;
        phoneLabel.Location = new Point(22, 7);
        phoneLabel.ForeColor = Muted;
        connectionRow.Controls.Add(phoneLabel);
        panel.Controls.Add(connectionRow);

        meter.Minimum = 0;
        meter.Maximum = 100;
        meter.Height = 8;
        meter.Dock = DockStyle.Top;
        meter.Margin = new Padding(0, 4, 0, 10);
        panel.Controls.Add(meter);

        var tip = new Label
        {
            Text = "Open MicBridge on Android and tap Start Microphone. Automatic discovery works on the same Wi-Fi or when the PC uses the phone hotspot.",
            ForeColor = Muted,
            AutoSize = true,
            MaximumSize = new Size(410, 0),
            Margin = new Padding(0, 8, 0, 0)
        };
        panel.Controls.Add(tip);
        return panel;
    }

    private Control BuildAudioPanel()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, BackColor = Surface };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 155));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        panel.Controls.Add(RowLabel("Audio source"), 0, 0);
        modeBox.DropDownStyle = ComboBoxStyle.DropDownList;
        modeBox.Items.AddRange(new object[]
        {
            "Phone mic only",
            "Phone mic + internal sound",
            "Internal sound only",
            "Other microphone only",
            "Other microphone + internal sound"
        });
        modeBox.SelectedIndex = 1;
        modeBox.Dock = DockStyle.Fill;
        modeBox.Margin = new Padding(0, 2, 0, 8);
        modeBox.SelectedIndexChanged += (_, _) => UpdateMicControlState();
        panel.Controls.Add(modeBox, 1, 0);

        panel.Controls.Add(RowLabel("Voice style"), 0, 1);
        profileBox.DropDownStyle = ComboBoxStyle.DropDownList;
        profileBox.Items.AddRange(new object[] { "Natural", "Studio Voice", "Cinematic Voice", "Deep Bass", "Documentary Voice" });
        profileBox.SelectedIndex = 0;
        profileBox.Dock = DockStyle.Fill;
        profileBox.Margin = new Padding(0, 2, 0, 8);
        panel.Controls.Add(profileBox, 1, 1);

        panel.Controls.Add(RowLabel("Other mic"), 0, 2);
        micBox.DropDownStyle = ComboBoxStyle.DropDownList;
        micBox.Dock = DockStyle.Fill;
        micBox.Margin = new Padding(0, 2, 0, 8);
        panel.Controls.Add(micBox, 1, 2);

        var refresh = new Button { Text = "Refresh microphones", AutoSize = true, Height = 34, Margin = new Padding(0, 4, 0, 0) };
        StyleSecondaryButton(refresh);
        refresh.Click += (_, _) => RefreshMicrophones();
        panel.Controls.Add(refresh, 1, 3);

        var note = new Label
        {
            Text = "Voice styles affect microphone audio only. Internal/system audio stays natural.",
            AutoSize = true,
            MaximumSize = new Size(410, 0),
            ForeColor = Muted,
            Margin = new Padding(0, 12, 0, 0)
        };
        panel.SetColumnSpan(note, 2);
        panel.Controls.Add(note, 0, 4);
        return panel;
    }

    private Control BuildVideoPanel()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, BackColor = Surface };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 125));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        panel.Controls.Add(RowLabel("Capture"), 0, 0);
        var capture = new Label
        {
            Text = "Entire desktop",
            AutoSize = true,
            ForeColor = Ink,
            Padding = new Padding(0, 7, 0, 0)
        };
        panel.Controls.Add(capture, 1, 0);

        panel.Controls.Add(RowLabel("Frame rate"), 0, 1);
        fpsBox.DropDownStyle = ComboBoxStyle.DropDownList;
        fpsBox.Items.AddRange(new object[] { "30 FPS", "60 FPS" });
        fpsBox.SelectedIndex = 0;
        fpsBox.Dock = DockStyle.Fill;
        fpsBox.Margin = new Padding(0, 2, 0, 8);
        panel.Controls.Add(fpsBox, 1, 1);

        panel.Controls.Add(RowLabel("Countdown"), 0, 2);
        countdownBox.Minimum = 0;
        countdownBox.Maximum = 10;
        countdownBox.Value = 3;
        countdownBox.Width = 90;
        countdownBox.Margin = new Padding(0, 2, 0, 8);
        panel.Controls.Add(countdownBox, 1, 2);

        minimizeBox.Text = "Hide the main window while recording";
        minimizeBox.Checked = true;
        minimizeBox.AutoSize = true;
        minimizeBox.ForeColor = Ink;
        minimizeBox.Margin = new Padding(0, 12, 0, 4);
        panel.SetColumnSpan(minimizeBox, 2);
        panel.Controls.Add(minimizeBox, 0, 3);

        var overlayNote = new Label
        {
            Text = "A small floating Stop control remains available at the top-right while recording.",
            AutoSize = true,
            MaximumSize = new Size(340, 0),
            ForeColor = Muted,
            Margin = new Padding(0, 6, 0, 0)
        };
        panel.SetColumnSpan(overlayNote, 2);
        panel.Controls.Add(overlayNote, 0, 4);

        var saveLabel = new Label
        {
            Text = "Save to",
            AutoSize = true,
            ForeColor = Muted,
            Margin = new Padding(0, 16, 0, 6)
        };
        panel.SetColumnSpan(saveLabel, 2);
        panel.Controls.Add(saveLabel, 0, 5);

        var folderEditor = BuildFolderEditor();
        panel.SetColumnSpan(folderEditor, 2);
        panel.Controls.Add(folderEditor, 0, 6);

        return panel;
    }

    private Control BuildRecentPanel()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 1,
            BackColor = Surface
        };

        recentPanel.FlowDirection = FlowDirection.TopDown;
        recentPanel.WrapContents = false;
        recentPanel.AutoSize = true;
        recentPanel.Dock = DockStyle.Top;
        recentPanel.BackColor = Surface;
        panel.Controls.Add(recentPanel);

        var actions = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = new Padding(0, 12, 0, 0)
        };

        openFolderButton.Text = "Open recordings folder";
        openFolderButton.AutoSize = true;
        openFolderButton.Height = 36;
        StyleSecondaryButton(openFolderButton);
        openFolderButton.Click += (_, _) => OpenRecordingsFolder();
        actions.Controls.Add(openFolderButton);

        var refresh = new Button { Text = "Refresh", AutoSize = true, Height = 36 };
        StyleSecondaryButton(refresh);
        refresh.Click += (_, _) => RefreshRecentRecordings();
        actions.Controls.Add(refresh);

        panel.Controls.Add(actions);
        return panel;
    }

    private static Control MakeCard(string title, string description, Control content, int width)
    {
        var card = new FluentCard
        {
            Width = width,
            AutoSize = true,
            Padding = new Padding(20),
            Margin = new Padding(0, 0, 0, 16),
            BackColor = Surface
        };

        var flow = new FlowLayoutPanel
        {
            Width = width - 40,
            AutoSize = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            BackColor = Surface
        };

        var h = new Label
        {
            Text = title,
            AutoSize = true,
            Font = new Font("Segoe UI Variable Display Semibold", 14F),
            ForeColor = Ink,
            Margin = new Padding(0, 0, 0, 3)
        };
        var d = new Label
        {
            Text = description,
            AutoSize = true,
            MaximumSize = new Size(width - 48, 0),
            Font = new Font("Segoe UI Variable Text", 9.5F),
            ForeColor = Muted,
            Margin = new Padding(0, 0, 0, 16)
        };

        content.Width = width - 40;
        content.Margin = new Padding(0);
        flow.Controls.Add(h);
        flow.Controls.Add(d);
        flow.Controls.Add(content);
        card.Controls.Add(flow);
        return card;
    }

    private static Label RowLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = Muted,
        Padding = new Padding(0, 7, 0, 0),
        Margin = new Padding(0, 2, 10, 8)
    };

    private static void StylePrimaryButton(Button button)
    {
        button.BackColor = Accent;
        button.ForeColor = Color.White;
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.Font = new Font("Segoe UI Variable Text Semibold", 10.5F);
        button.Cursor = Cursors.Hand;
        button.MouseEnter += (_, _) => { if (button.Enabled) button.BackColor = AccentHover; };
        button.MouseLeave += (_, _) => { if (button.Enabled) button.BackColor = Accent; };
    }

    private static void StyleSecondaryButton(Button button)
    {
        button.BackColor = Surface;
        button.ForeColor = Ink;
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderColor = Border;
        button.FlatAppearance.BorderSize = 1;
        button.Font = new Font("Segoe UI Variable Text", 9.5F);
        button.Cursor = Cursors.Hand;
    }

    private void RefreshMicrophones()
    {
        microphones.Clear();
        micBox.Items.Clear();
        try
        {
            using var e = new MMDeviceEnumerator();
            foreach (var d in e.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                var item = new DeviceItem(d.ID, d.FriendlyName);
                microphones.Add(item);
                micBox.Items.Add(item);
            }
            if (micBox.Items.Count > 0) micBox.SelectedIndex = 0;
        }
        catch { }
        UpdateMicControlState();
    }

    private void UpdateMicControlState()
    {
        bool needsOther = modeBox.SelectedIndex is 3 or 4;
        micBox.Enabled = needsOther && !recording;
        profileBox.Enabled = modeBox.SelectedIndex != 2 && !recording;
    }

    private string GetRecordingFolder()
    {
        string folder = folderBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(folder))
            folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "MicBridge Recordings");
        return folder;
    }

    private void RefreshRecentRecordings()
    {
        recentPanel.Controls.Clear();
        string folder = GetRecordingFolder();

        if (!Directory.Exists(folder))
        {
            recentPanel.Controls.Add(new Label
            {
                Text = "No recordings yet",
                AutoSize = true,
                ForeColor = Muted,
                Margin = new Padding(0, 4, 0, 8)
            });
            return;
        }

        FileInfo[] files;
        try
        {
            files = new DirectoryInfo(folder)
                .GetFiles("*.mp4")
                .OrderByDescending(x => x.LastWriteTime)
                .Take(5)
                .ToArray();
        }
        catch
        {
            files = Array.Empty<FileInfo>();
        }

        if (files.Length == 0)
        {
            recentPanel.Controls.Add(new Label
            {
                Text = "No recordings yet",
                AutoSize = true,
                ForeColor = Muted,
                Margin = new Padding(0, 4, 0, 8)
            });
            return;
        }

        foreach (var file in files)
            recentPanel.Controls.Add(BuildRecentRow(file));
    }

    private Control BuildRecentRow(FileInfo file)
    {
        var row = new Panel
        {
            Width = 342,
            Height = 56,
            BackColor = Color.FromArgb(248, 250, 252),
            Margin = new Padding(0, 0, 0, 7),
            Cursor = Cursors.Hand
        };

        var name = new Label
        {
            Text = file.Name,
            AutoEllipsis = true,
            Width = 245,
            Height = 23,
            Location = new Point(12, 7),
            ForeColor = Ink,
            Font = new Font("Segoe UI Variable Text Semibold", 9.2F),
            Cursor = Cursors.Hand
        };

        var meta = new Label
        {
            Text = $"{file.LastWriteTime:dd MMM, hh:mm tt}  •  {Math.Max(1, file.Length / 1024 / 1024)} MB",
            AutoSize = true,
            Location = new Point(12, 31),
            ForeColor = Muted,
            Font = new Font("Segoe UI Variable Text", 8.5F),
            Cursor = Cursors.Hand
        };

        var open = new Button
        {
            Text = "▶",
            Size = new Size(42, 36),
            Location = new Point(292, 10),
            FlatStyle = FlatStyle.Flat,
            BackColor = Surface,
            ForeColor = Accent,
            Cursor = Cursors.Hand
        };
        open.FlatAppearance.BorderColor = Border;
        open.FlatAppearance.BorderSize = 1;

        void Launch(object? _, EventArgs __) => OpenRecording(file.FullName);
        row.Click += Launch;
        name.Click += Launch;
        meta.Click += Launch;
        open.Click += Launch;

        row.Controls.Add(name);
        row.Controls.Add(meta);
        row.Controls.Add(open);
        return row;
    }

    private void OpenRecording(string path)
    {
        if (!File.Exists(path))
        {
            RefreshRecentRecordings();
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not open recording", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OpenRecordingsFolder()
    {
        string folder = GetRecordingFolder();
        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not open folder", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private Control BuildFolderEditor()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, BackColor = Surface };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        folderBox.Dock = DockStyle.Fill;
        folderBox.Margin = new Padding(0, 0, 8, 0);
        folderBox.Leave += (_, _) => RefreshRecentRecordings();
        panel.Controls.Add(folderBox, 0, 0);

        var browse = new Button { Text = "Browse…", AutoSize = true, Height = 34 };
        StyleSecondaryButton(browse);
        browse.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog();
            dlg.SelectedPath = Directory.Exists(GetRecordingFolder()) ? GetRecordingFolder() : Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                folderBox.Text = dlg.SelectedPath;
                RefreshRecentRecordings();
            }
        };
        panel.Controls.Add(browse, 1, 0);
        return panel;
    }

    private async Task StartRecordingAsync()
    {
        if (recording) return;

        bool phoneMic = modeBox.SelectedIndex is 0 or 1;
        bool systemAudio = modeBox.SelectedIndex is 1 or 2 or 4;
        bool otherMic = modeBox.SelectedIndex is 3 or 4;

        if (phoneMic && !phone.IsConnected)
        {
            var r = MessageBox.Show(this,
                "The phone microphone is not currently detected. Start MicBridge on the phone first.\n\nStart anyway and wait for the phone?",
                "Phone microphone not connected",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);
            if (r != DialogResult.Yes) return;
        }

        DeviceItem? selectedDevice = null;
        if (otherMic)
        {
            selectedDevice = micBox.SelectedItem as DeviceItem;
            if (selectedDevice is null)
            {
                MessageBox.Show(this, "No other microphone is selected.", "MicBridge Studio", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
        }

        string folder = GetRecordingFolder();
        Directory.CreateDirectory(folder);

        int countdown = (int)countdownBox.Value;
        if (countdown > 0) await ShowCountdownAsync(countdown);

        try
        {
            string ffmpegPath = ExtractFfmpeg();
            int port = FindFreeUdpPort();
            int fps = fpsBox.SelectedIndex == 1 ? 60 : 30;
            string file = Path.Combine(folder, $"MicBridge_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.mp4");
            currentOutput = file;

            var profile = (VoiceProfile)Math.Clamp(profileBox.SelectedIndex, 0, 4);
            audioEngine = new AudioMixEngine(phone, phoneMic, selectedDevice?.Id, systemAudio, port, profile);
            ffmpeg = StartFfmpeg(ffmpegPath, file, fps, port);
            await Task.Delay(350);
            audioEngine.Start();

            recording = true;
            startButton.Enabled = false;
            startButton.Text = "Recording…";
            stopButton.Enabled = true;
            modeBox.Enabled = false;
            micBox.Enabled = false;
            profileBox.Enabled = false;
            fpsBox.Enabled = false;
            countdownBox.Enabled = false;
            statusLabel.Text = $"Recording • {Path.GetFileName(file)}";
            statusLabel.ForeColor = Danger;
            tray.Text = "MicBridge Studio — Recording";

            if (minimizeBox.Checked)
            {
                WindowState = FormWindowState.Minimized;
                Hide();
                stopOverlay?.Close();
                stopOverlay = new StopOverlayForm(async () => await StopRecordingAsync());
                stopOverlay.Show();
            }
        }
        catch (Exception ex)
        {
            try { audioEngine?.Dispose(); } catch { }
            audioEngine = null;
            try { if (ffmpeg is { HasExited: false }) ffmpeg.Kill(true); } catch { }
            ffmpeg = null;
            startButton.Enabled = true;
            startButton.Text = "●  Start recording";
            MessageBox.Show(this, ex.Message, "Could not start recording", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task StopRecordingAsync()
    {
        if (!recording) return;

        recording = false;
        statusLabel.Text = "Saving recording…";
        statusLabel.ForeColor = Muted;

        try
        {
            if (ffmpeg is { HasExited: false })
            {
                try
                {
                    ffmpeg.StandardInput.WriteLine("q");
                    ffmpeg.StandardInput.Flush();
                }
                catch { }

                var wait = ffmpeg.WaitForExitAsync();
                if (await Task.WhenAny(wait, Task.Delay(6000)) != wait)
                {
                    try { ffmpeg.Kill(true); } catch { }
                }
            }
        }
        finally
        {
            try { audioEngine?.Dispose(); } catch { }
            audioEngine = null;
            try { ffmpeg?.Dispose(); } catch { }
            ffmpeg = null;

            try { stopOverlay?.Close(); } catch { }
            stopOverlay = null;

            startButton.Enabled = true;
            startButton.Text = "●  Start recording";
            stopButton.Enabled = false;
            modeBox.Enabled = true;
            fpsBox.Enabled = true;
            countdownBox.Enabled = true;
            UpdateMicControlState();

            statusLabel.Text = currentOutput is null ? "Ready to record" : $"Saved • {Path.GetFileName(currentOutput)} • Ready for another recording";
            statusLabel.ForeColor = currentOutput is null ? Muted : Success;
            tray.Text = "MicBridge Studio";

            ShowFromTray();
            RefreshRecentRecordings();
            startButton.Focus();

            if (currentOutput is not null)
                tray.ShowBalloonTip(1600, "Recording saved", Path.GetFileName(currentOutput), ToolTipIcon.Info);
        }
    }

    private Process StartFfmpeg(string ffmpegPath, string output, int fps, int audioPort)
    {
        var p = new Process();
        p.StartInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };

        void A(string x) => p.StartInfo.ArgumentList.Add(x);
        A("-hide_banner"); A("-loglevel"); A("warning"); A("-y");
        A("-thread_queue_size"); A("1024");
        A("-f"); A("gdigrab");
        A("-framerate"); A(fps.ToString());
        A("-draw_mouse"); A("1");
        A("-i"); A("desktop");

        A("-thread_queue_size"); A("1024");
        A("-f"); A("s16le");
        A("-ar"); A("48000");
        A("-ac"); A("2");
        A("-i"); A($"udp://127.0.0.1:{audioPort}?fifo_size=1000000&overrun_nonfatal=1");

        A("-map"); A("0:v:0");
        A("-map"); A("1:a:0");
        A("-vf"); A("scale=trunc(iw/2)*2:trunc(ih/2)*2:flags=lanczos:in_range=full:out_range=tv:out_color_matrix=bt709,format=yuv420p");
        A("-c:v"); A("libx264");
        A("-preset"); A("veryfast");
        A("-crf"); A("22");
        A("-pix_fmt"); A("yuv420p");
        A("-color_primaries"); A("bt709");
        A("-color_trc"); A("bt709");
        A("-colorspace"); A("bt709");
        A("-color_range"); A("tv");
        A("-c:a"); A("aac");
        A("-b:a"); A("192k");
        A("-movflags"); A("+faststart");
        A(output);

        if (!p.Start()) throw new InvalidOperationException("Could not launch the recording engine.");
        _ = Task.Run(async () =>
        {
            try { while (!p.HasExited) await p.StandardError.ReadLineAsync(); } catch { }
        });
        return p;
    }

    private static string ExtractFfmpeg()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MicBridge Studio");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "ffmpeg.exe");

        using Stream? src = Assembly.GetExecutingAssembly().GetManifestResourceStream("MicBridge.ffmpeg.exe");
        if (src is null) throw new InvalidOperationException("Recording engine is missing from MicBridge Studio.");

        bool write = !File.Exists(path) || new FileInfo(path).Length != src.Length;
        if (write)
        {
            using var dst = File.Create(path);
            src.CopyTo(dst);
        }
        return path;
    }

    private static int FindFreeUdpPort()
    {
        using var s = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)s.Client.LocalEndPoint!).Port;
    }

    private async Task ShowCountdownAsync(int seconds)
    {
        using var f = new Form
        {
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.CenterScreen,
            Size = new Size(300, 220),
            TopMost = true,
            BackColor = Color.FromArgb(17, 24, 39),
            ShowInTaskbar = false
        };
        var l = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.White,
            Font = new Font("Segoe UI Variable Display Semibold", 72)
        };
        f.Controls.Add(l);
        f.Show();

        for (int i = seconds; i >= 1; i--)
        {
            l.Text = i.ToString();
            l.Refresh();
            await Task.Delay(700);
        }

        l.Text = "REC";
        l.Font = new Font("Segoe UI Variable Display Semibold", 42);
        l.ForeColor = Color.FromArgb(255, 99, 99);
        l.Refresh();
        await Task.Delay(450);
        f.Close();
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private sealed record DeviceItem(string Id, string Name)
    {
        public override string ToString() => Name;
    }
}

internal sealed class FluentCard : Panel
{
    public FluentCard()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        BackColor = Color.White;
    }

    protected override void OnResize(EventArgs eventargs)
    {
        base.OnResize(eventargs);
        using var path = RoundedRect(ClientRectangle, 12);
        Region = new Region(path);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), 12);
        using var pen = new Pen(Color.FromArgb(220, 226, 234), 1);
        e.Graphics.DrawPath(pen, path);
    }

    private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
    {
        int d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
        p.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
        p.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        p.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}

internal sealed class PhoneReceiver : IDisposable
{
    private const int AudioPort = 49720;
    private const int DiscoveryPort = 49721;
    private readonly CancellationTokenSource cts = new();
    private readonly BufferedWaveProvider buffer;
    private UdpClient? audioUdp;
    private UdpClient? discoveryUdp;
    private DateTime lastPacket = DateTime.MinValue;
    private float level;
    public string LastDeviceName { get; private set; } = "Android phone";
    public bool IsConnected => DateTime.UtcNow - lastPacket < TimeSpan.FromSeconds(3);
    public float Level => level;

    public PhoneReceiver()
    {
        buffer = new BufferedWaveProvider(new WaveFormat(48000, 16, 1))
        {
            BufferDuration = TimeSpan.FromSeconds(2),
            DiscardOnBufferOverflow = true,
            ReadFully = true
        };
    }

    public void Start()
    {
        _ = Task.Run(AudioLoop);
        _ = Task.Run(DiscoveryLoop);
    }

    public ISampleProvider CreateSampleProvider()
    {
        buffer.ClearBuffer();
        ISampleProvider p = buffer.ToSampleProvider();
        p = new MonoToStereoSampleProvider(p);
        return p;
    }

    private async Task AudioLoop()
    {
        try
        {
            audioUdp = new UdpClient(AudioPort);
            while (!cts.IsCancellationRequested)
            {
                var r = await audioUdp.ReceiveAsync(cts.Token);
                var data = r.Buffer;

                if (data.Length >= 16 && data[0] == (byte)'M' && data[1] == (byte)'B' && data[2] == (byte)'1' && data[3] == (byte)'!')
                {
                    int len = data.Length - 16;
                    buffer.AddSamples(data, 16, len);
                    lastPacket = DateTime.UtcNow;
                    level = EstimateLevel(data, 16, len);
                }
                else
                {
                    string s = Encoding.UTF8.GetString(data);
                    if (s.StartsWith("MICBRIDGE_HELLO|", StringComparison.Ordinal))
                    {
                        var parts = s.Split('|');
                        if (parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1])) LastDeviceName = parts[1];
                        lastPacket = DateTime.UtcNow;
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    private async Task DiscoveryLoop()
    {
        try
        {
            discoveryUdp = new UdpClient(new IPEndPoint(IPAddress.Any, DiscoveryPort));
            discoveryUdp.EnableBroadcast = true;
            while (!cts.IsCancellationRequested)
            {
                var r = await discoveryUdp.ReceiveAsync(cts.Token);
                string s = Encoding.UTF8.GetString(r.Buffer);
                if (s == "MICBRIDGE_DISCOVER")
                {
                    byte[] offer = Encoding.UTF8.GetBytes($"MICBRIDGE_OFFER|{Environment.MachineName}");
                    await discoveryUdp.SendAsync(offer, offer.Length, r.RemoteEndPoint);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    private static float EstimateLevel(byte[] data, int offset, int count)
    {
        if (count < 2) return 0;
        double sum = 0;
        int n = 0;
        for (int i = offset; i + 1 < offset + count; i += 2)
        {
            short s = (short)(data[i] | (data[i + 1] << 8));
            double f = s / 32768.0;
            sum += f * f;
            n++;
        }
        if (n == 0) return 0;
        return (float)Math.Min(1.0, Math.Sqrt(sum / n) * 4.0);
    }

    public void Dispose()
    {
        cts.Cancel();
        try { audioUdp?.Dispose(); } catch { }
        try { discoveryUdp?.Dispose(); } catch { }
        cts.Dispose();
    }
}

internal enum VoiceProfile
{
    Natural = 0,
    Studio = 1,
    Cinematic = 2,
    DeepBass = 3,
    Documentary = 4
}

internal sealed class AudioMixEngine : IDisposable
{
    private readonly PhoneReceiver phone;
    private readonly bool includePhone;
    private readonly string? micDeviceId;
    private readonly bool includeSystem;
    private readonly int udpPort;
    private readonly VoiceProfile profile;
    private readonly List<IDisposable> disposables = new();
    private readonly List<WaveInEvent> legacy = new();
    private WasapiCapture? micCapture;
    private WasapiLoopbackCapture? loopCapture;
    private UdpClient? sender;
    private CancellationTokenSource? cts;
    private MixingSampleProvider? mixer;

    public AudioMixEngine(PhoneReceiver phone, bool includePhone, string? micDeviceId, bool includeSystem, int udpPort, VoiceProfile profile)
    {
        this.phone = phone;
        this.includePhone = includePhone;
        this.micDeviceId = micDeviceId;
        this.includeSystem = includeSystem;
        this.udpPort = udpPort;
        this.profile = profile;
    }

    public void Start()
    {
        var inputs = new List<ISampleProvider>();

        if (includePhone)
            inputs.Add(new VoiceProfileSampleProvider(phone.CreateSampleProvider(), profile));

        if (!string.IsNullOrWhiteSpace(micDeviceId))
        {
            using var enumerator = new MMDeviceEnumerator();
            var device = enumerator.GetDevice(micDeviceId);
            micCapture = new WasapiCapture(device);
            var b = NewBuffer(micCapture.WaveFormat);
            micCapture.DataAvailable += (_, e) => b.AddSamples(e.Buffer, 0, e.BytesRecorded);
            inputs.Add(new VoiceProfileSampleProvider(ToStereo48k(b), profile));
            micCapture.StartRecording();
        }

        if (includeSystem)
        {
            loopCapture = new WasapiLoopbackCapture();
            var b = NewBuffer(loopCapture.WaveFormat);
            loopCapture.DataAvailable += (_, e) => b.AddSamples(e.Buffer, 0, e.BytesRecorded);
            inputs.Add(ToStereo48k(b));
            loopCapture.StartRecording();
        }

        if (inputs.Count == 0)
            throw new InvalidOperationException("No audio source is selected.");

        mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)) { ReadFully = true };
        foreach (var p in inputs) mixer.AddMixerInput(p);

        sender = new UdpClient();
        sender.Connect(IPAddress.Loopback, udpPort);
        cts = new CancellationTokenSource();
        _ = Task.Run(() => SendLoop(cts.Token));
    }

    private static BufferedWaveProvider NewBuffer(WaveFormat f) => new(f)
    {
        BufferDuration = TimeSpan.FromSeconds(2),
        DiscardOnBufferOverflow = true,
        ReadFully = true
    };

    private static ISampleProvider ToStereo48k(BufferedWaveProvider b)
    {
        ISampleProvider p = b.ToSampleProvider();
        if (p.WaveFormat.SampleRate != 48000)
            p = new WdlResamplingSampleProvider(p, 48000);

        if (p.WaveFormat.Channels == 1)
            p = new MonoToStereoSampleProvider(p);
        else if (p.WaveFormat.Channels > 2)
            p = new FirstTwoChannelsSampleProvider(p);

        return p;
    }

    private async Task SendLoop(CancellationToken token)
    {
        if (mixer is null || sender is null) return;
        float[] samples = new float[1920];
        byte[] pcm = new byte[3840];
        var sw = Stopwatch.StartNew();
        long nextMs = 0;

        while (!token.IsCancellationRequested)
        {
            mixer.Read(samples, 0, samples.Length);
            for (int i = 0, j = 0; i < samples.Length; i++, j += 2)
            {
                float f = Math.Clamp(samples[i], -1f, 1f);
                short s = (short)(f * 32767f);
                pcm[j] = (byte)(s & 0xff);
                pcm[j + 1] = (byte)((s >> 8) & 0xff);
            }

            try { await sender.SendAsync(pcm, pcm.Length); }
            catch when (token.IsCancellationRequested) { break; }

            nextMs += 20;
            int delay = (int)(nextMs - sw.ElapsedMilliseconds);
            if (delay > 0)
            {
                try { await Task.Delay(delay, token); } catch { break; }
            }
            else if (delay < -500)
            {
                nextMs = sw.ElapsedMilliseconds;
            }
        }
    }

    public void Dispose()
    {
        try { cts?.Cancel(); } catch { }
        try { micCapture?.StopRecording(); } catch { }
        try { loopCapture?.StopRecording(); } catch { }
        try { micCapture?.Dispose(); } catch { }
        try { loopCapture?.Dispose(); } catch { }
        try { sender?.Dispose(); } catch { }
        try { cts?.Dispose(); } catch { }
        foreach (var d in disposables) try { d.Dispose(); } catch { }
        foreach (var l in legacy) try { l.Dispose(); } catch { }
    }
}


internal sealed class StopOverlayForm : Form
{
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

    public StopOverlayForm(Func<Task> stopAction)
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        Size = new Size(112, 42);
        BackColor = Color.FromArgb(15, 23, 42);

        var button = new Button
        {
            Dock = DockStyle.Fill,
            Text = "■  STOP",
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(220, 38, 38),
            ForeColor = Color.White,
            Font = new Font("Segoe UI Semibold", 10),
            Cursor = Cursors.Hand
        };
        button.FlatAppearance.BorderSize = 0;
        button.Click += async (_, _) =>
        {
            button.Enabled = false;
            button.Text = "Saving…";
            await stopAction();
        };
        Controls.Add(button);

        Shown += (_, _) =>
        {
            var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
            Location = new Point(wa.Right - Width - 16, wa.Top + 16);
            try { SetWindowDisplayAffinity(Handle, WDA_EXCLUDEFROMCAPTURE); } catch { }
        };
    }

    protected override bool ShowWithoutActivation => true;
}

internal sealed class VoiceProfileSampleProvider : ISampleProvider
{
    private readonly ISampleProvider source;
    private readonly BiQuadFilter[][] filters;
    private readonly float threshold;
    private readonly float ratio;
    private readonly float makeup;
    private readonly float[] envelope;
    private readonly float attack;
    private readonly float release;

    public WaveFormat WaveFormat => source.WaveFormat;

    public VoiceProfileSampleProvider(ISampleProvider source, VoiceProfile profile)
    {
        this.source = source;
        int sr = source.WaveFormat.SampleRate;
        int ch = source.WaveFormat.Channels;
        filters = new BiQuadFilter[ch][];
        envelope = new float[ch];

        (threshold, ratio, makeup, attack, release) = profile switch
        {
            VoiceProfile.Studio => (0.16f, 3.0f, 1.18f, 0.010f, 0.140f),
            VoiceProfile.Cinematic => (0.20f, 2.4f, 1.10f, 0.018f, 0.220f),
            VoiceProfile.DeepBass => (0.20f, 2.2f, 1.08f, 0.015f, 0.180f),
            VoiceProfile.Documentary => (0.15f, 3.4f, 1.20f, 0.008f, 0.120f),
            _ => (0.98f, 1.0f, 1.0f, 0.010f, 0.120f)
        };

        for (int c = 0; c < ch; c++)
        {
            filters[c] = profile switch
            {
                VoiceProfile.Studio => new[]
                {
                    BiQuadFilter.HighPassFilter(sr, 75, 0.707f),
                    BiQuadFilter.PeakingEQ(sr, 220, 0.9f, -1.5f),
                    BiQuadFilter.PeakingEQ(sr, 3000, 1.0f, 2.5f)
                },
                VoiceProfile.Cinematic => new[]
                {
                    BiQuadFilter.HighPassFilter(sr, 55, 0.707f),
                    BiQuadFilter.PeakingEQ(sr, 120, 0.8f, 2.5f),
                    BiQuadFilter.PeakingEQ(sr, 2500, 1.0f, 1.4f)
                },
                VoiceProfile.DeepBass => new[]
                {
                    BiQuadFilter.HighPassFilter(sr, 40, 0.707f),
                    BiQuadFilter.PeakingEQ(sr, 110, 0.75f, 5.0f),
                    BiQuadFilter.PeakingEQ(sr, 220, 0.9f, 1.5f),
                    BiQuadFilter.PeakingEQ(sr, 3500, 1.0f, 0.8f)
                },
                VoiceProfile.Documentary => new[]
                {
                    BiQuadFilter.HighPassFilter(sr, 85, 0.707f),
                    BiQuadFilter.PeakingEQ(sr, 250, 0.9f, -2.5f),
                    BiQuadFilter.PeakingEQ(sr, 2200, 0.9f, 2.5f),
                    BiQuadFilter.PeakingEQ(sr, 4500, 1.0f, 2.0f)
                },
                _ => Array.Empty<BiQuadFilter>()
            };
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int read = source.Read(buffer, offset, count);
        int ch = WaveFormat.Channels;
        int sr = WaveFormat.SampleRate;
        float attackCoef = MathF.Exp(-1f / (Math.Max(0.001f, attack) * sr));
        float releaseCoef = MathF.Exp(-1f / (Math.Max(0.001f, release) * sr));

        for (int i = 0; i < read; i++)
        {
            int c = i % ch;
            float x = buffer[offset + i];

            foreach (var f in filters[c])
                x = f.Transform(x);

            if (ratio > 1.01f)
            {
                float a = MathF.Abs(x);
                float coef = a > envelope[c] ? attackCoef : releaseCoef;
                envelope[c] = coef * envelope[c] + (1 - coef) * a;
                float gain = 1f;
                if (envelope[c] > threshold)
                {
                    float compressed = threshold + (envelope[c] - threshold) / ratio;
                    gain = compressed / Math.Max(envelope[c], 0.000001f);
                }
                x *= gain * makeup;
            }

            buffer[offset + i] = Math.Clamp(x, -0.98f, 0.98f);
        }
        return read;
    }
}

internal sealed class FirstTwoChannelsSampleProvider : ISampleProvider
{
    private readonly ISampleProvider source;
    private readonly float[] sourceBuffer;
    public WaveFormat WaveFormat { get; }

    public FirstTwoChannelsSampleProvider(ISampleProvider source)
    {
        this.source = source;
        if (source.WaveFormat.Channels < 2) throw new ArgumentException("Source must have at least 2 channels.");
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 2);
        sourceBuffer = new float[8192 * source.WaveFormat.Channels];
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int framesWanted = count / 2;
        int sourceSamplesWanted = Math.Min(sourceBuffer.Length, framesWanted * source.WaveFormat.Channels);
        int got = source.Read(sourceBuffer, 0, sourceSamplesWanted);
        int frames = got / source.WaveFormat.Channels;
        for (int f = 0; f < frames; f++)
        {
            int si = f * source.WaveFormat.Channels;
            int di = offset + f * 2;
            buffer[di] = sourceBuffer[si];
            buffer[di + 1] = sourceBuffer[si + 1];
        }
        int written = frames * 2;
        if (written < count) Array.Clear(buffer, offset + written, count - written);
        return count;
    }
}
