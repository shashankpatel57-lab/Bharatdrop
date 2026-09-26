using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MicBridgeStudio;

public sealed class MainForm : Form
{
    private readonly PhoneReceiver phone = new();
    private readonly ComboBox modeBox = new();
    private readonly ComboBox micBox = new();
    private readonly ComboBox fpsBox = new();
    private readonly NumericUpDown countdownBox = new();
    private readonly TextBox folderBox = new();
    private readonly CheckBox minimizeBox = new();
    private readonly Label phoneLabel = new();
    private readonly Label statusLabel = new();
    private readonly ProgressBar meter = new();
    private readonly Button startButton = new();
    private readonly Button stopButton = new();
    private readonly NotifyIcon tray;
    private readonly System.Windows.Forms.Timer uiTimer = new();
    private readonly List<DeviceItem> microphones = new();

    private AudioMixEngine? audioEngine;
    private Process? ffmpeg;
    private bool recording;
    private string? currentOutput;

    public MainForm()
    {
        Text = "MicBridge Studio";
        Width = 760;
        Height = 700;
        MinimumSize = new Size(700, 620);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(245, 247, 251);
        Font = new Font("Segoe UI", 10F);
        BuildUi();

        tray = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "MicBridge Studio",
            Visible = true
        };
        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("Show", null, (_, _) => ShowFromTray());
        trayMenu.Items.Add("Stop recording", null, async (_, _) => await StopRecordingAsync());
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
            RefreshMicrophones();
            phone.Start();
            folderBox.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "MicBridge Recordings");
        };

        FormClosing += async (_, e) =>
        {
            if (recording)
            {
                e.Cancel = true;
                await StopRecordingAsync();
                Close();
                return;
            }
            phone.Dispose();
            tray.Visible = false;
        };

        uiTimer.Interval = 300;
        uiTimer.Tick += (_, _) =>
        {
            phoneLabel.Text = phone.IsConnected
                ? $"Phone mic: connected • {phone.LastDeviceName}"
                : "Phone mic: waiting for Android app…";
            phoneLabel.ForeColor = phone.IsConnected ? Color.FromArgb(22, 163, 74) : Color.FromArgb(100, 116, 139);
            meter.Value = Math.Clamp((int)(phone.Level * 100), 0, 100);
        };
        uiTimer.Start();
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(28),
            ColumnCount = 1,
            RowCount = 10,
            AutoScroll = true
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(root);

        var title = new Label
        {
            Text = "MicBridge Studio",
            Font = new Font("Segoe UI Semibold", 26),
            ForeColor = Color.FromArgb(15, 23, 42),
            AutoSize = true
        };
        root.Controls.Add(title);

        var subtitle = new Label
        {
            Text = "Record your whole Windows screen with phone mic, another microphone, system audio, or any combination.",
            ForeColor = Color.FromArgb(71, 85, 105),
            AutoSize = true,
            Margin = new Padding(0, 4, 0, 18)
        };
        root.Controls.Add(subtitle);

        root.Controls.Add(MakeCard("PHONE MICROPHONE", BuildPhonePanel()));
        root.Controls.Add(MakeCard("RECORDING AUDIO", BuildAudioPanel()));
        root.Controls.Add(MakeCard("SCREEN RECORDING", BuildVideoPanel()));
        root.Controls.Add(MakeCard("SAVE LOCATION", BuildFolderPanel()));

        statusLabel.Text = "Ready";
        statusLabel.ForeColor = Color.FromArgb(71, 85, 105);
        statusLabel.AutoSize = true;
        statusLabel.Margin = new Padding(2, 18, 2, 8);
        root.Controls.Add(statusLabel);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false
        };
        startButton.Text = "Start Recording";
        startButton.Height = 48;
        startButton.Width = 190;
        startButton.BackColor = Color.FromArgb(37, 99, 235);
        startButton.ForeColor = Color.White;
        startButton.FlatStyle = FlatStyle.Flat;
        startButton.FlatAppearance.BorderSize = 0;
        startButton.Click += async (_, _) => await StartRecordingAsync();

        stopButton.Text = "Stop";
        stopButton.Height = 48;
        stopButton.Width = 110;
        stopButton.Enabled = false;
        stopButton.Click += async (_, _) => await StopRecordingAsync();

        buttons.Controls.Add(startButton);
        buttons.Controls.Add(stopButton);
        root.Controls.Add(buttons);
    }

    private Control BuildPhonePanel()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1 };
        phoneLabel.Text = "Phone mic: waiting for Android app…";
        phoneLabel.AutoSize = true;
        phoneLabel.Margin = new Padding(0, 6, 0, 8);
        panel.Controls.Add(phoneLabel);

        meter.Minimum = 0;
        meter.Maximum = 100;
        meter.Height = 12;
        meter.Dock = DockStyle.Top;
        panel.Controls.Add(meter);

        var tip = new Label
        {
            Text = "Keep the phone and PC on the same Wi-Fi, or connect the PC to the phone hotspot. Start the microphone from the Android MicBridge app.",
            ForeColor = Color.FromArgb(100, 116, 139),
            AutoSize = true,
            MaximumSize = new Size(620, 0),
            Margin = new Padding(0, 10, 0, 0)
        };
        panel.Controls.Add(tip);
        return panel;
    }

    private Control BuildAudioPanel()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2 };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        panel.Controls.Add(RowLabel("Audio mode"), 0, 0);
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
        modeBox.SelectedIndexChanged += (_, _) => UpdateMicControlState();
        panel.Controls.Add(modeBox, 1, 0);

        panel.Controls.Add(RowLabel("Other microphone"), 0, 1);
        micBox.DropDownStyle = ComboBoxStyle.DropDownList;
        micBox.Dock = DockStyle.Fill;
        panel.Controls.Add(micBox, 1, 1);

        var refresh = new Button { Text = "Refresh microphones", AutoSize = true, Margin = new Padding(0, 8, 0, 0) };
        refresh.Click += (_, _) => RefreshMicrophones();
        panel.Controls.Add(refresh, 1, 2);

        var note = new Label
        {
            Text = "Internal sound captures the audio playing through Windows speakers/headphones. Phone mic audio is received directly over the local network.",
            AutoSize = true,
            MaximumSize = new Size(600, 0),
            ForeColor = Color.FromArgb(100, 116, 139),
            Margin = new Padding(0, 12, 0, 0)
        };
        panel.SetColumnSpan(note, 2);
        panel.Controls.Add(note, 0, 3);
        return panel;
    }

    private Control BuildVideoPanel()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2 };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        panel.Controls.Add(RowLabel("Capture"), 0, 0);
        panel.Controls.Add(new Label { Text = "Whole desktop / full screen", AutoSize = true, Padding = new Padding(0, 7, 0, 0) }, 1, 0);

        panel.Controls.Add(RowLabel("Frame rate"), 0, 1);
        fpsBox.DropDownStyle = ComboBoxStyle.DropDownList;
        fpsBox.Items.AddRange(new object[] { "30 FPS", "60 FPS" });
        fpsBox.SelectedIndex = 0;
        fpsBox.Dock = DockStyle.Fill;
        panel.Controls.Add(fpsBox, 1, 1);

        panel.Controls.Add(RowLabel("Countdown"), 0, 2);
        countdownBox.Minimum = 0;
        countdownBox.Maximum = 10;
        countdownBox.Value = 3;
        countdownBox.Dock = DockStyle.Left;
        panel.Controls.Add(countdownBox, 1, 2);

        minimizeBox.Text = "Minimize MicBridge Studio when recording starts";
        minimizeBox.Checked = true;
        minimizeBox.AutoSize = true;
        minimizeBox.Margin = new Padding(0, 10, 0, 0);
        panel.SetColumnSpan(minimizeBox, 2);
        panel.Controls.Add(minimizeBox, 0, 3);
        return panel;
    }

    private Control BuildFolderPanel()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2 };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        folderBox.Dock = DockStyle.Fill;
        panel.Controls.Add(folderBox, 0, 0);

        var browse = new Button { Text = "Browse…", AutoSize = true, Margin = new Padding(8, 0, 0, 0) };
        browse.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog();
            dlg.SelectedPath = Directory.Exists(folderBox.Text) ? folderBox.Text : Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            if (dlg.ShowDialog(this) == DialogResult.OK) folderBox.Text = dlg.SelectedPath;
        };
        panel.Controls.Add(browse, 1, 0);
        return panel;
    }

    private static Control MakeCard(string heading, Control content)
    {
        var card = new Panel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(18),
            BackColor = Color.White,
            Margin = new Padding(0, 0, 0, 14)
        };
        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false
        };
        var h = new Label
        {
            Text = heading,
            AutoSize = true,
            Font = new Font("Segoe UI Semibold", 9),
            ForeColor = Color.FromArgb(100, 116, 139),
            Margin = new Padding(0, 0, 0, 8)
        };
        content.Width = 630;
        flow.Controls.Add(h);
        flow.Controls.Add(content);
        card.Controls.Add(flow);
        return card;
    }

    private static Label RowLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = Color.FromArgb(51, 65, 85),
        Padding = new Padding(0, 7, 0, 0),
        Margin = new Padding(0, 4, 10, 8)
    };

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
        micBox.Enabled = needsOther;
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

        string folder = folderBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(folder)) return;
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

            audioEngine = new AudioMixEngine(phone, phoneMic, selectedDevice?.Id, systemAudio, port);
            ffmpeg = StartFfmpeg(ffmpegPath, file, fps, port);
            await Task.Delay(350);
            audioEngine.Start();

            recording = true;
            startButton.Enabled = false;
            stopButton.Enabled = true;
            modeBox.Enabled = false;
            micBox.Enabled = false;
            statusLabel.Text = $"Recording • {Path.GetFileName(file)}";
            statusLabel.ForeColor = Color.FromArgb(220, 38, 38);
            tray.Text = "MicBridge Studio — Recording";
            tray.ShowBalloonTip(1500, "MicBridge Studio", "Screen recording started.", ToolTipIcon.Info);

            if (minimizeBox.Checked)
            {
                WindowState = FormWindowState.Minimized;
                Hide();
            }
        }
        catch (Exception ex)
        {
            try { audioEngine?.Dispose(); } catch { }
            audioEngine = null;
            try { if (ffmpeg is { HasExited: false }) ffmpeg.Kill(true); } catch { }
            ffmpeg = null;
            MessageBox.Show(this, ex.Message, "Could not start recording", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task StopRecordingAsync()
    {
        if (!recording) return;

        recording = false;
        statusLabel.Text = "Finishing recording…";
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

            startButton.Enabled = true;
            stopButton.Enabled = false;
            modeBox.Enabled = true;
            UpdateMicControlState();
            statusLabel.Text = currentOutput is null ? "Ready" : $"Saved: {currentOutput}";
            statusLabel.ForeColor = Color.FromArgb(22, 163, 74);
            tray.Text = "MicBridge Studio";
            ShowFromTray();

            if (currentOutput is not null)
                tray.ShowBalloonTip(1800, "Recording saved", currentOutput, ToolTipIcon.Info);
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
        A("-vf"); A("scale=trunc(iw/2)*2:trunc(ih/2)*2");
        A("-c:v"); A("libx264");
        A("-preset"); A("veryfast");
        A("-crf"); A("22");
        A("-pix_fmt"); A("yuv420p");
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
            BackColor = Color.FromArgb(15, 23, 42),
            ShowInTaskbar = false
        };
        var l = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.White,
            Font = new Font("Segoe UI Semibold", 72)
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
        l.Font = new Font("Segoe UI Semibold", 46);
        l.ForeColor = Color.FromArgb(248, 113, 113);
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

internal sealed class AudioMixEngine : IDisposable
{
    private readonly PhoneReceiver phone;
    private readonly bool includePhone;
    private readonly string? micDeviceId;
    private readonly bool includeSystem;
    private readonly int udpPort;
    private readonly List<IDisposable> disposables = new();
    private readonly List<WaveInEvent> legacy = new();
    private WasapiCapture? micCapture;
    private WasapiLoopbackCapture? loopCapture;
    private UdpClient? sender;
    private CancellationTokenSource? cts;
    private MixingSampleProvider? mixer;

    public AudioMixEngine(PhoneReceiver phone, bool includePhone, string? micDeviceId, bool includeSystem, int udpPort)
    {
        this.phone = phone;
        this.includePhone = includePhone;
        this.micDeviceId = micDeviceId;
        this.includeSystem = includeSystem;
        this.udpPort = udpPort;
    }

    public void Start()
    {
        var inputs = new List<ISampleProvider>();

        if (includePhone)
            inputs.Add(phone.CreateSampleProvider());

        if (!string.IsNullOrWhiteSpace(micDeviceId))
        {
            using var enumerator = new MMDeviceEnumerator();
            var device = enumerator.GetDevice(micDeviceId);
            micCapture = new WasapiCapture(device);
            var b = NewBuffer(micCapture.WaveFormat);
            micCapture.DataAvailable += (_, e) => b.AddSamples(e.Buffer, 0, e.BytesRecorded);
            inputs.Add(ToStereo48k(b));
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
