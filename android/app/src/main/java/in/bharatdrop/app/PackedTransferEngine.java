package in.bharatdrop.app;

import android.content.Context;
import android.os.Build;
import android.os.Environment;
import android.provider.Settings;

import java.io.*;
import java.net.*;
import java.nio.charset.StandardCharsets;
import java.util.*;
import java.util.concurrent.*;
import java.util.concurrent.atomic.AtomicBoolean;

public final class PackedTransferEngine {
    public static final int TCP_PORT = 47821;
    public static final int DISCOVERY_PORT = 47820;
    public static final int BUFFER_SIZE = 1024 * 1024;
    private static final String DISCOVER = "FS_DISCOVER_V2";

    public interface Listener {
        void onStatus(String text);
        void onProgress(String name, long done, long total);
    }

    public static final class TransferItem {
        public final File file;
        public final String remotePath;
        public TransferItem(File file, String remotePath) {
            this.file = file;
            this.remotePath = remotePath.replace('\\','/');
        }
    }

    public static String b64(String s) {
        return Base64.getUrlEncoder().withoutPadding().encodeToString(s.getBytes(StandardCharsets.UTF_8));
    }

    public static String unb64(String s) {
        return new String(Base64.getUrlDecoder().decode(s), StandardCharsets.UTF_8);
    }

    public static String getDeviceName() {
        String model = Build.MANUFACTURER + " " + Build.MODEL;
        return model.trim().replace("|", " ");
    }

    public static List<String> localIPv4() {
        List<String> ips = new ArrayList<>();
        try {
            Enumeration<NetworkInterface> en = NetworkInterface.getNetworkInterfaces();
            while (en.hasMoreElements()) {
                NetworkInterface ni = en.nextElement();
                if (!ni.isUp() || ni.isLoopback()) continue;
                Enumeration<InetAddress> addrs = ni.getInetAddresses();
                while (addrs.hasMoreElements()) {
                    InetAddress a = addrs.nextElement();
                    if (a instanceof Inet4Address && !a.isLoopbackAddress()) ips.add(a.getHostAddress());
                }
            }
        } catch (Exception ignored) {}
        return ips;
    }

    public static final class Peer {
        public final String host, pin, name;
        public final int port;
        public Peer(String host, int port, String pin, String name) {
            this.host=host; this.port=port; this.pin=pin; this.name=name;
        }
        @Override public String toString() { return name + "  •  " + host; }
    }

    public static List<Peer> discoverPeers(int waitMs) throws Exception {
        List<Peer> out = new ArrayList<>();
        Set<String> seen = new HashSet<>();
        DatagramSocket sock = new DatagramSocket();
        sock.setBroadcast(true);
        sock.setSoTimeout(250);
        byte[] q = DISCOVER.getBytes(StandardCharsets.UTF_8);
        sock.send(new DatagramPacket(q, q.length, InetAddress.getByName("255.255.255.255"), DISCOVERY_PORT));
        long until = System.currentTimeMillis() + waitMs;
        byte[] buf = new byte[512];
        while (System.currentTimeMillis() < until) {
            try {
                DatagramPacket p = new DatagramPacket(buf, buf.length);
                sock.receive(p);
                String s = new String(p.getData(), 0, p.getLength(), StandardCharsets.UTF_8);
                if (!s.startsWith("FS2|")) continue;
                String[] a = s.split("\\|", 5);
                if (a.length < 5) continue;
                String key = p.getAddress().getHostAddress() + ":" + a[1];
                if (seen.add(key)) out.add(new Peer(p.getAddress().getHostAddress(), Integer.parseInt(a[1]), a[2], a[4]));
            } catch (SocketTimeoutException ignored) {}
        }
        sock.close();
        return out;
    }

    public static List<TransferItem> collectFiles(File selected) {
        List<TransferItem> out = new ArrayList<>();
        if (selected.isFile()) {
            out.add(new TransferItem(selected, selected.getName()));
            return out;
        }
        final String base = selected.getName();
        walk(selected, base, out);
        return out;
    }

    private static void walk(File f, String rel, List<TransferItem> out) {
        File[] kids = f.listFiles();
        if (kids == null) return;
        Arrays.sort(kids, (a,b) -> {
            if (a.isDirectory()!=b.isDirectory()) return a.isDirectory() ? -1 : 1;
            return a.getName().compareToIgnoreCase(b.getName());
        });
        for (File k : kids) {
            String child = rel + "/" + k.getName();
            if (k.isDirectory()) walk(k, child, out);
            else if (k.isFile()) out.add(new TransferItem(k, child));
        }
    }

    public static final class Client {
        private final String host, pin;
        private final int port;
        private volatile boolean cancelled;
        private volatile boolean paused;

        public Client(String host, int port, String pin) {
            this.host=host; this.port=port; this.pin=pin;
        }
        public void cancel() { cancelled = true; }
        public void setPaused(boolean value) { paused = value; }

        private Socket connect() throws Exception {
            Socket s = new Socket();
            s.setKeepAlive(true);
            s.setTcpNoDelay(true);
            s.setReceiveBufferSize(BUFFER_SIZE * 2);
            s.setSendBufferSize(BUFFER_SIZE * 2);
            s.connect(new InetSocketAddress(host, port), 7000);
            // Deliberately no read timeout for payload transfers.
            OutputStream out = new BufferedOutputStream(s.getOutputStream(), 64 * 1024);
            InputStream in = new BufferedInputStream(s.getInputStream(), 64 * 1024);
            writeLine(out, "FS2 " + pin);
            String hello = readLine(in);
            if (hello == null || !hello.startsWith("OK ")) throw new IOException("Authentication failed");
            return s;
        }

        public void sendSelection(File selected, Listener listener) throws Exception {
            List<TransferItem> items = collectFiles(selected);
            if (items.isEmpty()) throw new IOException("No files found");
            sendItems(items, listener);
        }

        public void sendItems(List<TransferItem> items, Listener listener) throws Exception {
            long grandTotal = 0;
            for (TransferItem i : items) grandTotal += i.file.length();
            long completedBefore = 0;
            int index = 0;
            Socket socket = null;
            InputStream netIn = null;
            OutputStream netOut = null;
            try {
                while (index < items.size()) {
                    if (cancelled) throw new IOException("Cancelled");
                    TransferItem item = items.get(index);
                    int attempts = 0;
                    boolean done = false;
                    while (!done) {
                        while (paused && !cancelled) Thread.sleep(150);
                        if (cancelled) throw new IOException("Cancelled");
                        try {
                            if (socket == null || socket.isClosed()) {
                                listener.onStatus("Connecting to " + host + "…");
                                socket = connect();
                                netIn = new BufferedInputStream(socket.getInputStream(), 64 * 1024);
                                netOut = new BufferedOutputStream(socket.getOutputStream(), 64 * 1024);
                            }
                            long size = item.file.length();
                            writeLine(netOut, "PUSH " + b64(item.remotePath) + " " + size + " " + item.file.lastModified());
                            String r = readLine(netIn);
                            if (r == null || !r.startsWith("OFFSET ")) throw new EOFException("Receiver disconnected");
                            long offset = Long.parseLong(r.substring(7).trim());
                            if (offset < 0 || offset > size) offset = 0;

                            listener.onStatus("Sending " + item.remotePath);
                            try (RandomAccessFile raf = new RandomAccessFile(item.file, "r")) {
                                raf.seek(offset);
                                byte[] buffer = new byte[BUFFER_SIZE];
                                long pos = offset;
                                while (pos < size) {
                                    while (paused && !cancelled) Thread.sleep(150);
                                    if (cancelled) throw new IOException("Cancelled");
                                    int want = (int)Math.min(buffer.length, size-pos);
                                    int n = raf.read(buffer, 0, want);
                                    if (n < 0) throw new EOFException("Source file ended early");
                                    netOut.write(buffer, 0, n);
                                    pos += n;
                                    listener.onProgress(item.remotePath, completedBefore + pos, grandTotal);
                                }
                                netOut.flush();
                            }
                            String ack = readLine(netIn);
                            if (!"DONE".equals(ack)) throw new IOException("Receiver did not confirm file");
                            done = true;
                            completedBefore += size;
                            index++;
                        } catch (Exception ex) {
                            closeQuietly(socket); socket=null; netIn=null; netOut=null;
                            attempts++;
                            if (attempts > 60 || cancelled) throw ex;
                            int delay = Math.min(8000, 500 * (1 << Math.min(4, attempts-1)));
                            listener.onStatus("Connection interrupted. Auto-resuming " + item.remotePath + "…");
                            Thread.sleep(delay);
                        }
                    }
                }
                listener.onStatus("Transfer complete");
            } finally { closeQuietly(socket); }
        }
    }

    public static final class Server {
        private final Context context;
        private final String pin;
        private final File storageRoot;
        private final File receiveRoot;
        private final AtomicBoolean running = new AtomicBoolean(false);
        private ServerSocket serverSocket;
        private DatagramSocket discoverySocket;
        private ExecutorService pool;

        public Server(Context context, String pin) {
            this.context=context.getApplicationContext();
            this.pin=pin;
            this.storageRoot=Environment.getExternalStorageDirectory();
            this.receiveRoot=new File(Environment.getExternalStoragePublicDirectory(Environment.DIRECTORY_DOWNLOADS), "FileSetu");
            this.receiveRoot.mkdirs();
        }

        public File getReceiveRoot() { return receiveRoot; }

        public void start() throws Exception {
            if (!running.compareAndSet(false, true)) return;
            pool = Executors.newCachedThreadPool();
            serverSocket = new ServerSocket();
            serverSocket.setReuseAddress(true);
            serverSocket.bind(new InetSocketAddress(TCP_PORT));
            pool.submit(this::acceptLoop);
            pool.submit(this::discoveryLoop);
        }

        public void stop() {
            running.set(false);
            closeQuietly(serverSocket);
            if (discoverySocket != null) discoverySocket.close();
            if (pool != null) pool.shutdownNow();
        }

        private void acceptLoop() {
            while (running.get()) {
                try {
                    Socket s = serverSocket.accept();
                    s.setKeepAlive(true);
                    s.setTcpNoDelay(true);
                    s.setReceiveBufferSize(BUFFER_SIZE*2);
                    s.setSendBufferSize(BUFFER_SIZE*2);
                    pool.submit(() -> handle(s));
                } catch (Exception e) {
                    if (running.get()) e.printStackTrace();
                }
            }
        }

        private void discoveryLoop() {
            try {
                discoverySocket = new DatagramSocket(null);
                discoverySocket.setReuseAddress(true);
                discoverySocket.bind(new InetSocketAddress(DISCOVERY_PORT));
                byte[] buf = new byte[512];
                while (running.get()) {
                    DatagramPacket p = new DatagramPacket(buf, buf.length);
                    discoverySocket.receive(p);
                    String s = new String(p.getData(), 0, p.getLength(), StandardCharsets.UTF_8);
                    if (!DISCOVER.equals(s)) continue;
                    String reply = "FS2|" + TCP_PORT + "|" + pin + "|android|" + getDeviceName();
                    byte[] data = reply.getBytes(StandardCharsets.UTF_8);
                    discoverySocket.send(new DatagramPacket(data, data.length, p.getAddress(), p.getPort()));
                }
            } catch (Exception e) {
                if (running.get()) e.printStackTrace();
            }
        }

        private void handle(Socket socket) {
            try (Socket s = socket;
                 InputStream in = new BufferedInputStream(s.getInputStream(), 64*1024);
                 OutputStream out = new BufferedOutputStream(s.getOutputStream(), 64*1024)) {
                String hello = readLine(in);
                if (hello == null || !hello.equals("FS2 " + pin)) {
                    writeLine(out, "ERR PIN");
                    return;
                }
                writeLine(out, "OK FileSetu " + b64(getDeviceName()));
                while (running.get()) {
                    String line = readLine(in);
                    if (line == null) return;
                    if (line.equals("PING")) { writeLine(out, "PONG"); continue; }
                    String[] p = line.split(" ", 4);
                    switch (p[0]) {
                        case "LIST": handleList(out, p.length>1 ? unb64(p[1]) : ""); break;
                        case "PULL": handlePull(out, p); break;
                        case "PUSH": handlePush(in, out, p); break;
                        default: writeLine(out, "ERR COMMAND");
                    }
                }
            } catch (Exception ignored) {
                // A broken socket is expected during Wi-Fi changes; partial .filesetu.part is retained.
            }
        }

        private void handleList(OutputStream out, String rel) throws Exception {
            File dir = safeUnder(storageRoot, rel);
            File[] kids = dir.isDirectory() ? dir.listFiles() : null;
            if (kids == null) { writeLine(out, "COUNT 0"); writeLine(out, "END"); return; }
            Arrays.sort(kids, (a,b) -> {
                if (a.isDirectory()!=b.isDirectory()) return a.isDirectory() ? -1 : 1;
                return a.getName().compareToIgnoreCase(b.getName());
            });
            writeLine(out, "COUNT " + kids.length);
            for (File f : kids) {
                writeLine(out, "E " + (f.isDirectory()?"D":"F") + " " + f.length() + " " + f.lastModified() + " " + b64(f.getName()));
            }
            writeLine(out, "END");
        }

        private void handlePull(OutputStream out, String[] p) throws Exception {
            if (p.length < 3) { writeLine(out, "ERR ARG"); return; }
            String rel = unb64(p[1]);
            long offset = Long.parseLong(p[2]);
            File f = safeUnder(storageRoot, rel);
            if (!f.isFile()) { writeLine(out, "ERR FILE"); return; }
            if (offset < 0 || offset > f.length()) offset = 0;
            writeLine(out, "FILE " + f.length() + " " + f.lastModified() + " " + offset);
            try (RandomAccessFile raf = new RandomAccessFile(f, "r")) {
                raf.seek(offset);
                byte[] buf = new byte[BUFFER_SIZE];
                long remain = f.length()-offset;
                while (remain>0) {
                    int n = raf.read(buf,0,(int)Math.min(buf.length,remain));
                    if (n<0) throw new EOFException();
                    out.write(buf,0,n);
                    remain-=n;
                }
                out.flush();
            }
        }

        private void handlePush(InputStream in, OutputStream out, String[] p) throws Exception {
            if (p.length < 4) { writeLine(out, "ERR ARG"); return; }
            String rel = unb64(p[1]);
            String[] rest = p[3].split(" ", 2);
            long size = Long.parseLong(p[2]);
            long mtime = Long.parseLong(rest[0]);

            File finalFile = safeUnder(receiveRoot, rel);
            File parent = finalFile.getParentFile();
            if (parent != null) parent.mkdirs();
            File part = new File(finalFile.getAbsolutePath() + ".filesetu.part");
            long offset = part.exists() ? part.length() : 0;
            if (offset < 0 || offset > size) {
                if (part.exists()) part.delete();
                offset = 0;
            }
            writeLine(out, "OFFSET " + offset);
            try (RandomAccessFile raf = new RandomAccessFile(part, "rw")) {
                raf.seek(offset);
                byte[] buf = new byte[BUFFER_SIZE];
                long remain = size-offset;
                while (remain>0) {
                    int n = in.read(buf,0,(int)Math.min(buf.length,remain));
                    if (n<0) throw new EOFException("sender disconnected");
                    raf.write(buf,0,n);
                    remain-=n;
                }
                raf.getFD().sync();
            }
            if (finalFile.exists() && !finalFile.delete()) throw new IOException("Cannot replace target");
            if (!part.renameTo(finalFile)) {
                copyFile(part, finalFile);
                part.delete();
            }
            if (mtime > 0) finalFile.setLastModified(mtime);
            writeLine(out, "DONE");
        }

        private static File safeUnder(File base, String rel) throws Exception {
            String clean = rel == null ? "" : rel.replace('\\','/').replaceFirst("^/+", "");
            File f = clean.isEmpty() ? base : new File(base, clean);
            String b = base.getCanonicalPath();
            String c = f.getCanonicalPath();
            if (!c.equals(b) && !c.startsWith(b + File.separator)) throw new SecurityException("Bad path");
            return f;
        }
    }

    private static void copyFile(File a, File b) throws Exception {
        try (InputStream in = new FileInputStream(a); OutputStream out = new FileOutputStream(b)) {
            byte[] buf = new byte[BUFFER_SIZE]; int n;
            while ((n=in.read(buf))>0) out.write(buf,0,n);
            out.flush();
        }
    }

    public static void writeLine(OutputStream out, String s) throws IOException {
        out.write((s + "\n").getBytes(StandardCharsets.UTF_8));
        out.flush();
    }

    public static String readLine(InputStream in) throws IOException {
        ByteArrayOutputStream b = new ByteArrayOutputStream(128);
        int c;
        while ((c=in.read())!=-1) {
            if (c=='\n') break;
            if (c!='\r') b.write(c);
            if (b.size()>16384) throw new IOException("Protocol line too long");
        }
        if (c==-1 && b.size()==0) return null;
        return b.toString(StandardCharsets.UTF_8.name());
    }

    private static void closeQuietly(Closeable c) {
        if (c!=null) try { c.close(); } catch (Exception ignored) {}
    }
}
