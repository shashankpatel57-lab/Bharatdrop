using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace FileSetuDesktop;

internal static class Protocol
{
    public const int TcpPort = 47821;
    public const int DiscoveryPort = 47820;
    public const int BufferSize = 1024 * 1024;
    public const string DiscoverToken = "FS_DISCOVER_V2";

    public static string B64(string s)
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(s))
            .TrimEnd('=').Replace('+','-').Replace('/','_');
    }

    public static string UnB64(string s)
    {
        s = s.Replace('-','+').Replace('_','/');
        while (s.Length % 4 != 0) s += "=";
        return Encoding.UTF8.GetString(Convert.FromBase64String(s));
    }

    public static async Task WriteLineAsync(Stream stream, string line, CancellationToken ct = default)
    {
        byte[] data = Encoding.UTF8.GetBytes(line + "\n");
        await stream.WriteAsync(data, ct);
        await stream.FlushAsync(ct);
    }

    public static async Task<string?> ReadLineAsync(Stream stream, CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        byte[] one = new byte[1];
        while (true)
        {
            int n = await stream.ReadAsync(one.AsMemory(0,1), ct);
            if (n == 0) return ms.Length == 0 ? null : Encoding.UTF8.GetString(ms.ToArray());
            if (one[0] == (byte)'\n') break;
            if (one[0] != (byte)'\r') ms.WriteByte(one[0]);
            if (ms.Length > 16384) throw new IOException("Protocol line too long");
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public static async Task CopyExactAsync(Stream input, Stream output, long bytes, Action<long>? onDelta, CancellationToken ct)
    {
        byte[] buffer = new byte[BufferSize];
        long remain = bytes;
        while (remain > 0)
        {
            int want = (int)Math.Min(buffer.Length, remain);
            int n = await input.ReadAsync(buffer.AsMemory(0, want), ct);
            if (n <= 0) throw new EndOfStreamException("Connection closed during transfer");
            await output.WriteAsync(buffer.AsMemory(0, n), ct);
            remain -= n;
            onDelta?.Invoke(n);
        }
        await output.FlushAsync(ct);
    }
}

internal sealed record RemoteEntry(string Name, bool IsDirectory, long Size, long ModifiedUtc);
internal sealed record DiscoveredPeer(string Host, int Port, string Pin, string Kind, string Name)
{
    public override string ToString() => $"{Name}  •  {Host}";
}

internal sealed record SendItem(string LocalPath, string RemotePath, long Size, long ModifiedUtc);

internal sealed class PackedClient
{
    private readonly string host;
    private readonly int port;
    private readonly string pin;
    private volatile bool paused;
    private volatile bool cancelled;

    public PackedClient(string host, string pin, int port = Protocol.TcpPort)
    {
        this.host = host.Trim();
        this.pin = pin.Trim();
        this.port = port;
    }

    public void Pause(bool value) => paused = value;
    public void Cancel() => cancelled = true;

    private sealed class Conn : IAsyncDisposable
    {
        public TcpClient Client { get; }
        public NetworkStream Stream { get; }
        public Conn(TcpClient c) { Client = c; Stream = c.GetStream(); }
        public ValueTask DisposeAsync()
        {
            try { Stream.Dispose(); } catch {}
            try { Client.Dispose(); } catch {}
            return ValueTask.CompletedTask;
        }
    }

    private async Task<Conn> ConnectAsync(CancellationToken ct)
    {
        var tcp = new TcpClient();
        tcp.NoDelay = true;
        tcp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        tcp.ReceiveBufferSize = Protocol.BufferSize * 2;
        tcp.SendBufferSize = Protocol.BufferSize * 2;
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectCts.CancelAfter(TimeSpan.FromSeconds(8));
        await tcp.ConnectAsync(host, port, connectCts.Token);

        var c = new Conn(tcp);
        await Protocol.WriteLineAsync(c.Stream, "FS2 " + pin, ct);
        string? reply = await Protocol.ReadLineAsync(c.Stream, ct);
        if (reply == null || !reply.StartsWith("OK ", StringComparison.Ordinal))
        {
            await c.DisposeAsync();
            throw new IOException("Wrong PIN or incompatible FileSetu receiver");
        }
        return c;
    }

    public static List<SendItem> Collect(string selected)
    {
        var list = new List<SendItem>();
        if (File.Exists(selected))
        {
            var fi = new FileInfo(selected);
            list.Add(new SendItem(fi.FullName, fi.Name, fi.Length, new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeMilliseconds()));
            return list;
        }
        if (!Directory.Exists(selected)) throw new FileNotFoundException("Selected path no longer exists", selected);

        var root = new DirectoryInfo(selected);
        foreach (var file in Directory.EnumerateFiles(root.FullName, "*", SearchOption.AllDirectories))
        {
            var fi = new FileInfo(file);
            string rel = Path.GetRelativePath(root.Parent?.FullName ?? root.FullName, fi.FullName).Replace('\\','/');
            list.Add(new SendItem(fi.FullName, rel, fi.Length, new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeMilliseconds()));
        }
        return list;
    }

    public async Task SendSelectionAsync(string selected, Action<string> status, Action<string,long,long> progress, CancellationToken ct)
    {
        List<SendItem> items = Collect(selected); // exact file stays exact; folders recurse only when a folder was selected.
        if (items.Count == 0) throw new IOException("No files found in the selected folder");

        long grandTotal = items.Sum(x => x.Size);
        long completed = 0;
        Conn? conn = null;
        try
        {
            foreach (var item in items)
            {
                int attempt = 0;
                bool finished = false;
                while (!finished)
                {
                    ct.ThrowIfCancellationRequested();
                    if (cancelled) throw new OperationCanceledException();
                    while (paused && !cancelled)
                        await Task.Delay(150, ct);

                    try
                    {
                        conn ??= await ConnectAsync(ct);
                        status($"Sending {item.RemotePath}");
                        await Protocol.WriteLineAsync(conn.Stream,
                            $"PUSH {Protocol.B64(item.RemotePath)} {item.Size} {item.ModifiedUtc}", ct);
                        string? response = await Protocol.ReadLineAsync(conn.Stream, ct);
                        if (response == null || !response.StartsWith("OFFSET "))
                            throw new IOException("Receiver did not provide a resume offset");

                        long offset = long.Parse(response[7..].Trim());
                        if (offset < 0 || offset > item.Size) offset = 0;

                        await using var fs = new FileStream(item.LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                            Protocol.BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
                        fs.Seek(offset, SeekOrigin.Begin);

                        byte[] buffer = new byte[Protocol.BufferSize];
                        long pos = offset;
                        while (pos < item.Size)
                        {
                            ct.ThrowIfCancellationRequested();
                            if (cancelled) throw new OperationCanceledException();
                            while (paused && !cancelled)
                                await Task.Delay(150, ct);

                            int want = (int)Math.Min(buffer.Length, item.Size - pos);
                            int n = await fs.ReadAsync(buffer.AsMemory(0, want), ct);
                            if (n <= 0) throw new EndOfStreamException("Source file ended unexpectedly");
                            await conn.Stream.WriteAsync(buffer.AsMemory(0, n), ct);
                            pos += n;
                            progress(item.RemotePath, completed + pos, grandTotal);
                        }
                        await conn.Stream.FlushAsync(ct);

                        string? done = await Protocol.ReadLineAsync(conn.Stream, ct);
                        if (!string.Equals(done, "DONE", StringComparison.Ordinal))
                            throw new IOException("Receiver did not confirm the file");

                        completed += item.Size;
                        finished = true;
                    }
                    catch (Exception ex) when (ex is IOException || ex is SocketException || ex is EndOfStreamException)
                    {
                        if (conn != null) await conn.DisposeAsync();
                        conn = null;
                        attempt++;
                        if (attempt > 60) throw;
                        int delay = Math.Min(8000, 500 * (1 << Math.Min(4, attempt - 1)));
                        status($"Connection interrupted. Auto-resuming {item.RemotePath}…");
                        await Task.Delay(delay, ct);
                    }
                }
            }
            status("Transfer complete");
        }
        finally
        {
            if (conn != null) await conn.DisposeAsync();
        }
    }

    public async Task<List<RemoteEntry>> ListAsync(string remoteDirectory, CancellationToken ct)
    {
        await using var conn = await ConnectAsync(ct);
        await Protocol.WriteLineAsync(conn.Stream, "LIST " + Protocol.B64(remoteDirectory ?? ""), ct);
        string? head = await Protocol.ReadLineAsync(conn.Stream, ct);
        if (head == null || !head.StartsWith("COUNT ")) throw new IOException("Invalid directory response");
        var result = new List<RemoteEntry>();
        while (true)
        {
            string? line = await Protocol.ReadLineAsync(conn.Stream, ct);
            if (line == null) throw new EndOfStreamException();
            if (line == "END") break;
            string[] p = line.Split(' ', 5);
            if (p.Length != 5 || p[0] != "E") continue;
            result.Add(new RemoteEntry(Protocol.UnB64(p[4]), p[1] == "D", long.Parse(p[2]), long.Parse(p[3])));
        }
        return result;
    }

    public async Task DownloadFileAsync(string remotePath, string finalPath, Action<string> status, Action<long,long> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
        string part = finalPath + ".filesetu.part";
        int attempt = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            long offset = File.Exists(part) ? new FileInfo(part).Length : 0;
            try
            {
                await using var conn = await ConnectAsync(ct);
                await Protocol.WriteLineAsync(conn.Stream, $"PULL {Protocol.B64(remotePath)} {offset}", ct);
                string? head = await Protocol.ReadLineAsync(conn.Stream, ct);
                if (head == null || !head.StartsWith("FILE ")) throw new IOException("Remote file unavailable");
                string[] p = head.Split(' ');
                long total = long.Parse(p[1]);
                long mtime = long.Parse(p[2]);
                long accepted = long.Parse(p[3]);
                if (accepted != offset)
                {
                    offset = accepted;
                    using var reset = new FileStream(part, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
                    reset.SetLength(offset);
                }

                status($"Receiving {remotePath}");
                await using (var fs = new FileStream(part, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None,
                    Protocol.BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    fs.Seek(offset, SeekOrigin.Begin);
                    long done = offset;
                    await Protocol.CopyExactAsync(conn.Stream, fs, total - offset, delta =>
                    {
                        done += delta;
                        progress(done, total);
                    }, ct);
                }

                if (File.Exists(finalPath)) File.Delete(finalPath);
                File.Move(part, finalPath);
                try { File.SetLastWriteTimeUtc(finalPath, DateTimeOffset.FromUnixTimeMilliseconds(mtime).UtcDateTime); } catch {}
                status("Download complete");
                return;
            }
            catch (Exception ex) when (ex is IOException || ex is SocketException || ex is EndOfStreamException)
            {
                attempt++;
                if (attempt > 60) throw;
                status("Connection interrupted. Auto-resuming download…");
                await Task.Delay(Math.Min(8000, 500 * (1 << Math.Min(4, attempt-1))), ct);
            }
        }
    }

    public static async Task<List<DiscoveredPeer>> DiscoverAsync(int waitMs = 1400)
    {
        var peers = new Dictionary<string,DiscoveredPeer>(StringComparer.OrdinalIgnoreCase);
        using var udp = new UdpClient(0);
        udp.EnableBroadcast = true;
        byte[] msg = Encoding.UTF8.GetBytes(Protocol.DiscoverToken);
        await udp.SendAsync(msg, msg.Length, new IPEndPoint(IPAddress.Broadcast, Protocol.DiscoveryPort));

        using var cts = new CancellationTokenSource(waitMs);
        while (!cts.IsCancellationRequested)
        {
            try
            {
                UdpReceiveResult r = await udp.ReceiveAsync(cts.Token);
                string text = Encoding.UTF8.GetString(r.Buffer);
                if (!text.StartsWith("FS2|")) continue;
                string[] p = text.Split('|', 5);
                if (p.Length < 5) continue;
                var peer = new DiscoveredPeer(r.RemoteEndPoint.Address.ToString(), int.Parse(p[1]), p[2], p[3], p[4]);
                peers[peer.Host + ":" + peer.Port] = peer;
            }
            catch (OperationCanceledException) { break; }
        }
        return peers.Values.ToList();
    }
}

internal sealed class PackedServer : IAsyncDisposable
{
    private readonly string pin;
    private readonly string receiveRoot;
    private TcpListener? listener;
    private UdpClient? discovery;
    private CancellationTokenSource? cts;
    private readonly List<Task> tasks = new();

    public event Action<string>? Status;

    public PackedServer(string pin, string receiveRoot)
    {
        this.pin = pin;
        this.receiveRoot = receiveRoot;
        Directory.CreateDirectory(receiveRoot);
    }

    public Task StartAsync()
    {
        if (cts != null) return Task.CompletedTask;
        cts = new CancellationTokenSource();
        listener = new TcpListener(IPAddress.Any, Protocol.TcpPort);
        listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        listener.Start();
        tasks.Add(Task.Run(() => AcceptLoop(cts.Token)));
        tasks.Add(Task.Run(() => DiscoveryLoop(cts.Token)));
        Status?.Invoke("Receiver active");
        return Task.CompletedTask;
    }

    private async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                TcpClient client = await listener!.AcceptTcpClientAsync(ct);
                tasks.Add(Task.Run(() => HandleClient(client, ct), ct));
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Status?.Invoke("Receiver: " + ex.Message); await Task.Delay(500, ct); }
        }
    }

    private async Task DiscoveryLoop(CancellationToken ct)
    {
        try
        {
            discovery = new UdpClient();
            discovery.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            discovery.Client.Bind(new IPEndPoint(IPAddress.Any, Protocol.DiscoveryPort));
            while (!ct.IsCancellationRequested)
            {
                UdpReceiveResult r = await discovery.ReceiveAsync(ct);
                string q = Encoding.UTF8.GetString(r.Buffer);
                if (q != Protocol.DiscoverToken) continue;
                string answer = $"FS2|{Protocol.TcpPort}|{pin}|windows|{Environment.MachineName}";
                byte[] bytes = Encoding.UTF8.GetBytes(answer);
                await discovery.SendAsync(bytes, bytes.Length, r.RemoteEndPoint);
            }
        }
        catch (OperationCanceledException) {}
        catch (Exception ex) { Status?.Invoke("Discovery: " + ex.Message); }
    }

    private async Task HandleClient(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            client.NoDelay = true;
            client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            NetworkStream s = client.GetStream();
            string? hello = await Protocol.ReadLineAsync(s, ct);
            if (hello != "FS2 " + pin)
            {
                await Protocol.WriteLineAsync(s, "ERR PIN", ct);
                return;
            }
            await Protocol.WriteLineAsync(s, "OK FileSetu " + Protocol.B64(Environment.MachineName), ct);

            while (!ct.IsCancellationRequested)
            {
                string? line;
                try { line = await Protocol.ReadLineAsync(s, ct); }
                catch { return; }
                if (line == null) return;
                if (line == "PING") { await Protocol.WriteLineAsync(s, "PONG", ct); continue; }

                string[] p = line.Split(' ', 4);
                if (p[0] == "PUSH" && p.Length >= 4)
                {
                    await HandlePush(s, p, ct);
                }
                else
                {
                    await Protocol.WriteLineAsync(s, "ERR COMMAND", ct);
                }
            }
        }
    }

    private async Task HandlePush(NetworkStream stream, string[] p, CancellationToken ct)
    {
        string rel = Protocol.UnB64(p[1]).Replace('/',
            Path.DirectorySeparatorChar);
        long size = long.Parse(p[2]);
        long mtime = long.Parse(p[3]);

        string finalPath = SafeUnder(receiveRoot, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
        string part = finalPath + ".filesetu.part";
        long offset = File.Exists(part) ? new FileInfo(part).Length : 0;
        if (offset < 0 || offset > size)
        {
            if (File.Exists(part)) File.Delete(part);
            offset = 0;
        }

        await Protocol.WriteLineAsync(stream, "OFFSET " + offset, ct);
        await using (var fs = new FileStream(part, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None,
            Protocol.BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            fs.Seek(offset, SeekOrigin.Begin);
            await Protocol.CopyExactAsync(stream, fs, size - offset, null, ct);
            await fs.FlushAsync(ct);
        }

        if (File.Exists(finalPath)) File.Delete(finalPath);
        File.Move(part, finalPath);
        try { File.SetLastWriteTimeUtc(finalPath, DateTimeOffset.FromUnixTimeMilliseconds(mtime).UtcDateTime); } catch {}
        await Protocol.WriteLineAsync(stream, "DONE", ct);
        Status?.Invoke("Received " + rel);
    }

    private static string SafeUnder(string root, string rel)
    {
        string baseFull = Path.GetFullPath(root);
        string full = Path.GetFullPath(Path.Combine(baseFull, rel.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
        if (!full.Equals(baseFull, StringComparison.OrdinalIgnoreCase) &&
            !full.StartsWith(baseFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Invalid transfer path");
        return full;
    }

    public async ValueTask DisposeAsync()
    {
        if (cts == null) return;
        cts.Cancel();
        try { listener?.Stop(); } catch {}
        try { discovery?.Dispose(); } catch {}
        try { await Task.WhenAll(tasks.ToArray()).WaitAsync(TimeSpan.FromSeconds(2)); } catch {}
        cts.Dispose();
        cts = null;
    }
}
