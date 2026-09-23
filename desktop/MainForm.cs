using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace FileSetuDesktop;

internal sealed class MainForm : Form
{
    private readonly Color Navy = Color.FromArgb(17,32,61);
    private readonly Color Blue = Color.FromArgb(55,91,210);
    private readonly Color Green = Color.FromArgb(20,157,118);
    private readonly Color Bg = Color.FromArgb(246,248,252);
    private readonly Color Muted = Color.FromArgb(103,116,139);

    private readonly Panel content = new() { Dock = DockStyle.Fill };
    private readonly TextBox hostBox = new();
    private readonly TextBox pinBox = new();
    private readonly Label topStatus = new();
    private readonly string receiveFolder;
    private readonly string localPin;
    private PackedServer? server;

    private string? selectedPath;
    private Label? selectedPathLabel;
    private Label? sendStatus;
    private ProgressBar? sendProgress;
    private Button? pauseButton;
    private PackedClient? activeClient;
    private CancellationTokenSource? transferCts;
    private bool paused;

    private ListView? remoteList;
    private Label? remotePathLabel;
    private string remotePath = "";
    private List<RemoteEntry> remoteEntries = new();

    private readonly string settingsDir;
    private readonly string settingsFile;

    public MainForm(string? explorerPath)
    {
        Text = "FileSetu Desktop 5.4";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1040,720);
        MinimumSize = new Size(900,620);
        BackColor = Bg;
        Font = new Font("Segoe UI", 10f);
        Icon = SystemIcons.Application;

        settingsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileSetu");
        Directory.CreateDirectory(settingsDir);
        settingsFile = Path.Combine(settingsDir, "settings.txt");
        receiveFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "FileSetu");
        Directory.CreateDirectory(receiveFolder);
        localPin = GetOrCreatePin();

        BuildShell();
        LoadDeviceSettings();

        if (!string.IsNullOrWhiteSpace(explorerPath) && (File.Exists(explorerPath) || Directory.Exists(explorerPath)))
        {
            selectedPath = explorerPath;
            ShowSendPage();
        }
        else
        {
            ShowSendPage();
        }

        Shown += async (_,__) => await StartReceiverAsync();
        FormClosing += async (_,__) => {
            try { if (server != null) await server.DisposeAsync(); } catch {}
        };
    }

    private void BuildShell()
    {
        var header = new Panel { Dock=DockStyle.Top, Height=78, BackColor=Navy };
        var brand = new Label {
            Text="FileSetu", ForeColor=Color.White, Font=new Font("Segoe UI Semibold",24f),
            AutoSize=true, Location=new Point(26,14)
        };
        var subtitle = new Label {
            Text="High-speed local transfer  •  PC ↔ Phone ↔ Phone",
            ForeColor=Color.FromArgb(205,218,244), Font=new Font("Segoe UI",9.5f),
            AutoSize=true, Location=new Point(29,49)
        };
        topStatus.Text="Receiver starting…";
        topStatus.ForeColor=Color.FromArgb(216,232,255);
        topStatus.AutoSize=true;
        topStatus.Anchor=AnchorStyles.Top|AnchorStyles.Right;
        topStatus.Location=new Point(780,30);
        header.Controls.AddRange(new Control[]{brand,subtitle,topStatus});

        var nav = new Panel { Dock=DockStyle.Left, Width=205, BackColor=Color.White, Padding=new Padding(14,20,14,20) };
        var navTitle = new Label { Text="TRANSFER", ForeColor=Muted, Font=new Font("Segoe UI Semibold",9f), Dock=DockStyle.Top, Height=28 };
        nav.Controls.Add(navTitle);
        AddNav(nav, "Send to phone", 36, (_,__)=>ShowSendPage());
        AddNav(nav, "Browse phone", 88, async (_,__)=>{ ShowBrowsePage(); await RefreshRemoteAsync(); });
        AddNav(nav, "Receive on PC", 140, (_,__)=>ShowReceivePage());
        AddNav(nav, "Settings", 192, (_,__)=>ShowSettingsPage());

        var footer = new Label {
            Text="FileSetu 5.4\nPackedStream v2", ForeColor=Muted, AutoSize=false,
            Height=50, Dock=DockStyle.Bottom, TextAlign=ContentAlignment.MiddleLeft
        };
        nav.Controls.Add(footer);

        content.BackColor=Bg;
        content.Padding=new Padding(26,22,26,24);
        Controls.Add(content);
        Controls.Add(nav);
        Controls.Add(header);
    }

    private void AddNav(Panel nav, string text, int top, EventHandler click)
    {
        var b = new Button {
            Text=text, Left=14, Top=top, Width=175, Height=42,
            FlatStyle=FlatStyle.Flat, BackColor=Color.White, ForeColor=Navy,
            TextAlign=ContentAlignment.MiddleLeft, Padding=new Padding(12,0,0,0),
            Cursor=Cursors.Hand, Font=new Font("Segoe UI Semibold",10f)
        };
        b.FlatAppearance.BorderSize=0;
        b.Click += click;
        nav.Controls.Add(b);
    }

    private Panel Card(int x, int y, int w, int h)
    {
        return new Panel {
            Left=x, Top=y, Width=w, Height=h, BackColor=Color.White,
            BorderStyle=BorderStyle.FixedSingle, Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right
        };
    }

    private Label H(string text, int x, int y, float size=17f)
        => new() { Text=text, Left=x, Top=y, AutoSize=true, ForeColor=Navy, Font=new Font("Segoe UI Semibold",size) };

    private Label T(string text, int x, int y, int width=600)
        => new() { Text=text, Left=x, Top=y, Width=width, Height=24, ForeColor=Muted, Font=new Font("Segoe UI",9.5f) };

    private Button Primary(string text, int x, int y, int w=160)
    {
        var b = new Button { Text=text, Left=x, Top=y, Width=w, Height=42, FlatStyle=FlatStyle.Flat, BackColor=Blue, ForeColor=Color.White, Cursor=Cursors.Hand, Font=new Font("Segoe UI Semibold",10f) };
        b.FlatAppearance.BorderSize=0; return b;
    }

    private Button Secondary(string text, int x, int y, int w=150)
    {
        var b = new Button { Text=text, Left=x, Top=y, Width=w, Height=40, FlatStyle=FlatStyle.Flat, BackColor=Color.FromArgb(239,243,250), ForeColor=Navy, Cursor=Cursors.Hand, Font=new Font("Segoe UI Semibold",9.5f) };
        b.FlatAppearance.BorderSize=0; return b;
    }

    private void ClearPage()
    {
        content.Controls.Clear();
        selectedPathLabel=null; sendStatus=null; sendProgress=null; pauseButton=null; remoteList=null; remotePathLabel=null;
    }

    private Panel DeviceCard(int y)
    {
        var card=Card(0,y,content.ClientSize.Width-52,138);
        card.Controls.Add(H("Connect to a FileSetu device",18,14,16));
        card.Controls.Add(T("Use Find Devices or enter the receiver address and 6-digit PIN.",18,44,640));

        hostBox.SetBounds(18,76,210,38);
        hostBox.PlaceholderText="Receiver IP";
        pinBox.SetBounds(238,76,130,38);
        pinBox.PlaceholderText="PIN";
        pinBox.MaxLength=6;

        var detect=Secondary("Find devices",382,75,135);
        detect.Click += async (_,__) => await DetectDevicesAsync();
        card.Controls.Add(hostBox);
        card.Controls.Add(pinBox);
        card.Controls.Add(detect);
        return card;
    }

    private void ShowSendPage()
    {
        ClearPage();
        content.Controls.Add(H("Send from this PC",0,0,21));
        content.Controls.Add(T("Choose exactly one file or one folder. A file selection never expands to its parent folder.",0,36,760));
        content.Controls.Add(DeviceCard(72));

        var card=Card(0,224,content.ClientSize.Width-52,300);
        card.Controls.Add(H("What do you want to send?",18,16,16));
        var chooseFile=Secondary("Choose file",18,54,130);
        var chooseFolder=Secondary("Choose folder",158,54,140);
        card.Controls.Add(chooseFile); card.Controls.Add(chooseFolder);

        selectedPathLabel=T(selectedPath==null ? "Nothing selected" : selectedPath,18,106,760);
        selectedPathLabel.Height=42;
        selectedPathLabel.ForeColor=selectedPath==null ? Muted : Navy;
        card.Controls.Add(selectedPathLabel);

        sendProgress=new ProgressBar { Left=18,Top=154,Width=Math.Max(500,card.Width-38),Height=13,Maximum=1000,Anchor=AnchorStyles.Left|AnchorStyles.Top|AnchorStyles.Right };
        sendStatus=T("Ready",18,176,760);
        card.Controls.Add(sendProgress); card.Controls.Add(sendStatus);

        var send=Primary("Send with FileSetu",18,218,180);
        pauseButton=Secondary("Pause",208,219,100);
        var cancel=Secondary("Cancel",318,219,100);
        card.Controls.Add(send); card.Controls.Add(pauseButton); card.Controls.Add(cancel);

        chooseFile.Click += (_,__) => {
            using var d=new OpenFileDialog { Title="Choose one file to send", Multiselect=false };
            if (d.ShowDialog(this)==DialogResult.OK) SetSelectedPath(d.FileName);
        };
        chooseFolder.Click += (_,__) => {
            using var d=new FolderBrowserDialog { Description="Choose one folder to send", UseDescriptionForTitle=true };
            if (d.ShowDialog(this)==DialogResult.OK) SetSelectedPath(d.SelectedPath);
        };
        send.Click += async (_,__) => await BeginSendAsync();
        pauseButton.Click += (_,__) => TogglePause();
        cancel.Click += (_,__) => { activeClient?.Cancel(); transferCts?.Cancel(); };

        content.Controls.Add(card);
    }

    private void SetSelectedPath(string path)
    {
        selectedPath=path;
        if (selectedPathLabel!=null)
        {
            selectedPathLabel.Text=path;
            selectedPathLabel.ForeColor=Navy;
        }
    }

    private async Task BeginSendAsync()
    {
        if (selectedPath==null || (!File.Exists(selectedPath) && !Directory.Exists(selectedPath)))
        {
            MessageBox.Show(this,"Choose a valid file or folder first.","FileSetu",MessageBoxButtons.OK,MessageBoxIcon.Information);
            return;
        }
        string host=hostBox.Text.Trim(), pin=pinBox.Text.Trim();
        if (host.Length==0 || pin.Length!=6)
        {
            MessageBox.Show(this,"Enter the receiver IP and 6-digit PIN.","FileSetu",MessageBoxButtons.OK,MessageBoxIcon.Information);
            return;
        }
        SaveDeviceSettings();
        transferCts?.Cancel();
        transferCts=new CancellationTokenSource();
        activeClient=new PackedClient(host,pin);
        paused=false;
        if (pauseButton!=null) pauseButton.Text="Pause";
        var sw=Stopwatch.StartNew();
        long lastDone=0;
        try
        {
            if (sendProgress!=null) sendProgress.Value=0;
            await activeClient.SendSelectionAsync(selectedPath,
                s => UI(()=> { if(sendStatus!=null) sendStatus.Text=s; }),
                (name,done,total) => UI(() => {
                    if(sendProgress!=null) sendProgress.Value=total<=0?0:(int)Math.Min(1000,done*1000L/total);
                    double sec=Math.Max(.1,sw.Elapsed.TotalSeconds);
                    double speed=done/sec;
                    lastDone=done;
                    if(sendStatus!=null) sendStatus.Text=$"{Path.GetFileName(name)}  •  {FormatBytes(done)} / {FormatBytes(total)}  •  {FormatBytes((long)speed)}/s";
                }), transferCts.Token);
            if (sendProgress!=null) sendProgress.Value=1000;
        }
        catch (OperationCanceledException) { if(sendStatus!=null) sendStatus.Text="Transfer cancelled"; }
        catch (Exception ex) { if(sendStatus!=null) sendStatus.Text="Transfer stopped: "+ex.Message; }
        finally { activeClient=null; }
    }

    private void TogglePause()
    {
        if (activeClient==null) return;
        paused=!paused;
        activeClient.Pause(paused);
        if (pauseButton!=null) pauseButton.Text=paused?"Resume":"Pause";
        if (sendStatus!=null && paused) sendStatus.Text="Paused";
    }

    private void ShowBrowsePage()
    {
        ClearPage();
        content.Controls.Add(H("Browse this phone",0,0,21));
        content.Controls.Add(T("Browse phone storage from Windows and pull files with byte-level auto-resume.",0,36,760));
        content.Controls.Add(DeviceCard(72));

        var card=Card(0,224,content.ClientSize.Width-52,390);
        remotePathLabel=H("Phone storage /",18,14,14);
        card.Controls.Add(remotePathLabel);
        var up=Secondary("Up",18,48,74);
        var refresh=Secondary("Refresh",102,48,90);
        var download=Primary("Download selected",202,47,170);
        card.Controls.Add(up); card.Controls.Add(refresh); card.Controls.Add(download);

        remoteList=new ListView {
            Left=18,Top=100,Width=Math.Max(600,card.Width-38),Height=260,
            View=View.Details,FullRowSelect=true,GridLines=false,
            Anchor=AnchorStyles.Left|AnchorStyles.Top|AnchorStyles.Right|AnchorStyles.Bottom
        };
        remoteList.Columns.Add("Name",380);
        remoteList.Columns.Add("Type",90);
        remoteList.Columns.Add("Size",120);
        card.Controls.Add(remoteList);

        up.Click += async (_,__) => {
            if (string.IsNullOrEmpty(remotePath)) return;
            int i=remotePath.LastIndexOf('/');
            remotePath=i<0?"":remotePath[..i];
            await RefreshRemoteAsync();
        };
        refresh.Click += async (_,__) => await RefreshRemoteAsync();
        remoteList.DoubleClick += async (_,__) => {
            if(remoteList.SelectedIndices.Count==0) return;
            var e=remoteEntries[remoteList.SelectedIndices[0]];
            if(e.IsDirectory) { remotePath=JoinRemote(remotePath,e.Name); await RefreshRemoteAsync(); }
        };
        download.Click += async (_,__) => await DownloadSelectedAsync();
        content.Controls.Add(card);
    }

    private async Task RefreshRemoteAsync()
    {
        if (remoteList==null) return;
        string host=hostBox.Text.Trim(), pin=pinBox.Text.Trim();
        if(host.Length==0 || pin.Length!=6) return;
        SaveDeviceSettings();
        try
        {
            var client=new PackedClient(host,pin);
            remoteEntries=await client.ListAsync(remotePath,CancellationToken.None);
            remoteList.Items.Clear();
            foreach(var e in remoteEntries)
            {
                var item=new ListViewItem(e.Name);
                item.SubItems.Add(e.IsDirectory?"Folder":"File");
                item.SubItems.Add(e.IsDirectory?"":FormatBytes(e.Size));
                remoteList.Items.Add(item);
            }
            if(remotePathLabel!=null) remotePathLabel.Text="Phone storage / "+remotePath;
        }
        catch(Exception ex)
        {
            MessageBox.Show(this,ex.Message,"Cannot browse phone",MessageBoxButtons.OK,MessageBoxIcon.Warning);
        }
    }

    private async Task DownloadSelectedAsync()
    {
        if(remoteList==null || remoteList.SelectedIndices.Count==0) return;
        var e=remoteEntries[remoteList.SelectedIndices[0]];
        using var d=new FolderBrowserDialog { Description="Choose where to save from phone",UseDescriptionForTitle=true };
        if(d.ShowDialog(this)!=DialogResult.OK) return;
        transferCts?.Cancel(); transferCts=new CancellationTokenSource();
        try
        {
            await DownloadEntryRecursiveAsync(new PackedClient(hostBox.Text.Trim(),pinBox.Text.Trim()),
                JoinRemote(remotePath,e.Name),e,d.SelectedPath,transferCts.Token);
            MessageBox.Show(this,"Download complete.","FileSetu",MessageBoxButtons.OK,MessageBoxIcon.Information);
        }
        catch(OperationCanceledException){}
        catch(Exception ex){ MessageBox.Show(this,ex.Message,"Download stopped",MessageBoxButtons.OK,MessageBoxIcon.Warning); }
    }

    private async Task DownloadEntryRecursiveAsync(PackedClient client,string remote,RemoteEntry e,string localParent,CancellationToken ct)
    {
        if(!e.IsDirectory)
        {
            string final=Path.Combine(localParent,e.Name);
            await client.DownloadFileAsync(remote,final,
                s=>UI(()=>topStatus.Text=s),
                (done,total)=>UI(()=>topStatus.Text=$"Receiving {e.Name}  •  {FormatBytes(done)} / {FormatBytes(total)}"),ct);
            return;
        }
        string dir=Path.Combine(localParent,e.Name);
        Directory.CreateDirectory(dir);
        var children=await client.ListAsync(remote,ct);
        foreach(var child in children)
            await DownloadEntryRecursiveAsync(client,JoinRemote(remote,child.Name),child,dir,ct);
    }

    private void ShowReceivePage()
    {
        ClearPage();
        content.Controls.Add(H("Receive on this PC",0,0,21));
        content.Controls.Add(T("Send from FileSetu on any phone. Interrupted files remain as .filesetu.part and resume automatically.",0,36,800));

        var card=Card(0,82,content.ClientSize.Width-52,320);
        card.Controls.Add(H("Receiver is active",20,18,17));
        card.Controls.Add(T("PAIRING PIN",20,62,200));
        var pin=new Label {Text=localPin,Left=18,Top=84,AutoSize=true,ForeColor=Blue,Font=new Font("Segoe UI Semibold",32f)};
        card.Controls.Add(pin);
        card.Controls.Add(T("PC ADDRESS",20,143,200));
        var ips=new Label {Text=string.Join("  •  ",LocalIPv4()),Left=18,Top=166,Width=760,Height=28,ForeColor=Navy,Font=new Font("Segoe UI Semibold",12f)};
        card.Controls.Add(ips);
        card.Controls.Add(T("RECEIVED FILES",20,208,200));
        var folder=new Label {Text=receiveFolder,Left=18,Top=231,Width=700,Height=26,ForeColor=Navy};
        card.Controls.Add(folder);
        var open=Secondary("Open folder",18,266,120);
        open.Click += (_,__) => Process.Start(new ProcessStartInfo("explorer.exe",$"\"{receiveFolder}\""){UseShellExecute=true});
        card.Controls.Add(open);
        content.Controls.Add(card);
    }

    private void ShowSettingsPage()
    {
        ClearPage();
        content.Controls.Add(H("Settings",0,0,21));
        content.Controls.Add(T("Windows integration and FileSetu identity.",0,36,760));
        var card=Card(0,82,content.ClientSize.Width-52,260);
        card.Controls.Add(H("Windows Explorer integration",18,18,16));
        card.Controls.Add(T("Adds “Send with FileSetu” to the right-click menu for both files and folders.",18,50,700));
        var install=Primary(ExplorerIntegration.IsInstalled()?"Reinstall integration":"Install integration",18,88,170);
        var remove=Secondary("Remove integration",198,89,150);
        var state=T(ExplorerIntegration.IsInstalled()?"Installed for this Windows user":"Not installed",18,140,500);
        card.Controls.Add(install); card.Controls.Add(remove); card.Controls.Add(state);
        install.Click += (_,__) => {
            ExplorerIntegration.Install(Application.ExecutablePath);
            state.Text="Installed for this Windows user";
            MessageBox.Show(this,"Windows Explorer integration installed. Right-click any file or folder and choose “Send with FileSetu”.","FileSetu");
        };
        remove.Click += (_,__) => { ExplorerIntegration.Remove(); state.Text="Not installed"; };

        card.Controls.Add(H("About",18,178,14));
        card.Controls.Add(T("FileSetu 5.4  •  PackedStream v2  •  Local-network only  •  No BharatDrop branding",18,207,720));
        content.Controls.Add(card);
    }

    private async Task DetectDevicesAsync()
    {
        topStatus.Text="Finding FileSetu devices…";
        try
        {
            var peers=await PackedClient.DiscoverAsync();
            peers=peers.Where(p=>!IsLocalAddress(p.Host)).ToList();
            if(peers.Count==0){ topStatus.Text="No nearby receiver found"; return; }
            using var menu=new Form {Text="Nearby FileSetu devices",StartPosition=FormStartPosition.CenterParent,Size=new Size(520,340),BackColor=Bg,Font=Font,FormBorderStyle=FormBorderStyle.FixedDialog,MaximizeBox=false,MinimizeBox=false};
            var list=new ListBox {Left=18,Top=18,Width=468,Height=220,Font=new Font("Segoe UI",11f)};
            foreach(var p in peers) list.Items.Add(p);
            var use=Primary("Use selected",316,248,170);
            menu.Controls.Add(list); menu.Controls.Add(use);
            use.Click += (_,__) => {
                if(list.SelectedIndex<0) return;
                var p=peers[list.SelectedIndex];
                hostBox.Text=p.Host; pinBox.Text=p.Pin; SaveDeviceSettings();
                menu.DialogResult=DialogResult.OK; menu.Close();
            };
            menu.ShowDialog(this);
            topStatus.Text="Receiver active";
        }
        catch(Exception ex){ topStatus.Text="Discovery failed: "+ex.Message; }
    }

    private async Task StartReceiverAsync()
    {
        try
        {
            server=new PackedServer(localPin,receiveFolder);
            server.Status += s => UI(()=>topStatus.Text=s);
            await server.StartAsync();
            topStatus.Text="Receiver active  •  PIN "+localPin;
        }
        catch(Exception ex){ topStatus.Text="Receiver unavailable: "+ex.Message; }
    }

    private string GetOrCreatePin()
    {
        string file=Path.Combine(settingsDir,"receiver-pin.txt");
        if(File.Exists(file))
        {
            string p=File.ReadAllText(file).Trim();
            if(p.Length==6 && p.All(char.IsDigit)) return p;
        }
        string pin=Random.Shared.Next(0,1000000).ToString("D6");
        File.WriteAllText(file,pin); return pin;
    }

    private void SaveDeviceSettings()
    {
        try { File.WriteAllLines(settingsFile,new[]{hostBox.Text.Trim(),pinBox.Text.Trim()}); } catch {}
    }

    private void LoadDeviceSettings()
    {
        try
        {
            if(!File.Exists(settingsFile)) return;
            var a=File.ReadAllLines(settingsFile);
            if(a.Length>0) hostBox.Text=a[0];
            if(a.Length>1) pinBox.Text=a[1];
        } catch {}
    }

    private static string JoinRemote(string a,string b)
        => string.IsNullOrEmpty(a)?b:(a.TrimEnd('/')+"/"+b);

    private static IEnumerable<string> LocalIPv4()
    {
        foreach(var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if(ni.OperationalStatus!=OperationalStatus.Up || ni.NetworkInterfaceType==NetworkInterfaceType.Loopback) continue;
            foreach(var ua in ni.GetIPProperties().UnicastAddresses)
                if(ua.Address.AddressFamily==AddressFamily.InterNetwork) yield return ua.Address.ToString();
        }
    }

    private static bool IsLocalAddress(string host)
    {
        if(host=="127.0.0.1") return true;
        return LocalIPv4().Contains(host);
    }

    private void UI(Action a)
    {
        if(IsDisposed) return;
        if(InvokeRequired) BeginInvoke(a); else a();
    }

    private static string FormatBytes(long n)
    {
        if(n>=1024L*1024*1024) return $"{n/(1024d*1024*1024):0.00} GB";
        if(n>=1024L*1024) return $"{n/(1024d*1024):0.0} MB";
        if(n>=1024L) return $"{n/1024d:0.0} KB";
        return n+" B";
    }
}
