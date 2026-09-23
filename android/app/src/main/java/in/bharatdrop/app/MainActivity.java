package in.bharatdrop.app;

import android.Manifest;
import android.app.*;
import android.content.*;
import android.content.pm.PackageManager;
import android.graphics.Color;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.net.Uri;
import android.os.*;
import android.provider.Settings;
import android.view.*;
import android.widget.*;

import java.io.File;
import java.util.*;
import java.util.concurrent.*;

public class MainActivity extends Activity {
    private final int NAVY = Color.rgb(17, 32, 61);
    private final int BLUE = Color.rgb(55, 91, 210);
    private final int GREEN = Color.rgb(20, 157, 118);
    private final int BG = Color.rgb(246, 248, 252);
    private final int MUTED = Color.rgb(103, 116, 139);

    private LinearLayout receivePanel, sendPanel;
    private EditText hostBox, pinBox;
    private TextView selectedLabel, sendStatus, receiveIp;
    private ProgressBar progress;
    private File selectedFile;
    private ExecutorService worker = Executors.newSingleThreadExecutor();

    @Override public void onCreate(Bundle state) {
        super.onCreate(state);
        getWindow().setStatusBarColor(NAVY);
        if (Build.VERSION.SDK_INT >= 23) getWindow().getDecorView().setSystemUiVisibility(0);
        requestPermissionsIfNeeded();
        startReceiverService();
        buildUi();
    }

    private void requestPermissionsIfNeeded() {
        if (Build.VERSION.SDK_INT >= 33 && checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) {
            requestPermissions(new String[]{Manifest.permission.POST_NOTIFICATIONS}, 20);
        }
        if (Build.VERSION.SDK_INT >= 30 && !Environment.isExternalStorageManager()) {
            try {
                Intent i = new Intent(Settings.ACTION_MANAGE_APP_ALL_FILES_ACCESS_PERMISSION,
                        Uri.parse("package:" + getPackageName()));
                startActivity(i);
            } catch (Exception e) {
                startActivity(new Intent(Settings.ACTION_MANAGE_ALL_FILES_ACCESS_PERMISSION));
            }
        }
    }

    private void startReceiverService() {
        Intent s = new Intent(this, TransferService.class);
        if (Build.VERSION.SDK_INT >= 26) startForegroundService(s); else startService(s);
    }

    private void buildUi() {
        ScrollView scroll = new ScrollView(this);
        scroll.setFillViewport(true);
        LinearLayout root = new LinearLayout(this);
        root.setOrientation(LinearLayout.VERTICAL);
        root.setPadding(dp(20), dp(18), dp(20), dp(32));
        root.setBackgroundColor(BG);
        scroll.addView(root, new ScrollView.LayoutParams(-1,-2));

        TextView brand = text("FileSetu", 30, NAVY, true);
        root.addView(brand);
        TextView tag = text("Fast local transfer. Phone ↔ PC ↔ Phone.", 14, MUTED, false);
        tag.setPadding(0, dp(3),0,dp(18));
        root.addView(tag);

        LinearLayout hero = card();
        TextView heroTitle = text("PackedStream v2", 17, Color.WHITE, true);
        TextView heroText = text("Persistent high-speed stream • automatic byte-resume • no cloud", 13, Color.rgb(226,235,255), false);
        hero.setBackground(round(NAVY, 22));
        hero.addView(heroTitle); hero.addView(heroText);
        root.addView(hero, lpMatchWrap(dp(0), dp(14)));

        LinearLayout tabs = new LinearLayout(this);
        tabs.setOrientation(LinearLayout.HORIZONTAL);
        tabs.setPadding(0,0,0,dp(14));
        Button receiveTab = button("Receive", BLUE);
        Button sendTab = button("Send", Color.WHITE);
        receiveTab.setTextColor(Color.WHITE);
        sendTab.setTextColor(NAVY);
        tabs.addView(receiveTab, new LinearLayout.LayoutParams(0, dp(48),1));
        Space gap = new Space(this); tabs.addView(gap, new LinearLayout.LayoutParams(dp(10),1));
        tabs.addView(sendTab, new LinearLayout.LayoutParams(0, dp(48),1));
        root.addView(tabs);

        receivePanel = buildReceivePanel();
        sendPanel = buildSendPanel();
        root.addView(receivePanel);
        root.addView(sendPanel);
        sendPanel.setVisibility(View.GONE);

        receiveTab.setOnClickListener(v -> {
            receivePanel.setVisibility(View.VISIBLE);
            sendPanel.setVisibility(View.GONE);
            receiveTab.setBackground(round(BLUE,14)); receiveTab.setTextColor(Color.WHITE);
            sendTab.setBackground(round(Color.WHITE,14)); sendTab.setTextColor(NAVY);
        });
        sendTab.setOnClickListener(v -> {
            receivePanel.setVisibility(View.GONE);
            sendPanel.setVisibility(View.VISIBLE);
            sendTab.setBackground(round(BLUE,14)); sendTab.setTextColor(Color.WHITE);
            receiveTab.setBackground(round(Color.WHITE,14)); receiveTab.setTextColor(NAVY);
        });

        setContentView(scroll);
    }

    private LinearLayout buildReceivePanel() {
        LinearLayout panel = new LinearLayout(this);
        panel.setOrientation(LinearLayout.VERTICAL);

        LinearLayout card = card();
        card.addView(text("Ready to receive", 19, NAVY, true));
        TextView sub = text("Keep this screen or the FileSetu service running. Transfers continue in the background.", 13, MUTED, false);
        sub.setPadding(0,dp(5),0,dp(14)); card.addView(sub);

        TextView pinLabel = text("PAIRING PIN", 11, MUTED, true);
        card.addView(pinLabel);
        TextView pin = text(TransferService.ensurePin(this), 34, BLUE, true);
        pin.setLetterSpacing(.20f); pin.setPadding(0,dp(2),0,dp(12)); card.addView(pin);

        card.addView(text("THIS PHONE", 11, MUTED, true));
        receiveIp = text(String.join("  •  ", PackedTransferEngine.localIPv4()), 16, NAVY, true);
        receiveIp.setPadding(0,dp(4),0,dp(12)); card.addView(receiveIp);

        card.addView(text("Received files", 12, MUTED, true));
        TextView path = text("Downloads / FileSetu", 15, NAVY, false);
        path.setPadding(0,dp(4),0,0); card.addView(path);

        Button refresh = button("Refresh network address", Color.rgb(236,241,255));
        refresh.setTextColor(BLUE);
        refresh.setOnClickListener(v -> receiveIp.setText(String.join("  •  ", PackedTransferEngine.localIPv4())));
        card.addView(refresh, lpMatchWrap(dp(0),dp(0)));

        panel.addView(card);
        return panel;
    }

    private LinearLayout buildSendPanel() {
        LinearLayout panel = new LinearLayout(this);
        panel.setOrientation(LinearLayout.VERTICAL);

        LinearLayout device = card();
        device.addView(text("Send to another device", 19, NAVY, true));
        TextView hint = text("Both devices must be on the same Wi-Fi or hotspot.", 13, MUTED, false);
        hint.setPadding(0,dp(4),0,dp(12)); device.addView(hint);

        Button detect = button("Find FileSetu devices", Color.rgb(232,240,255));
        detect.setTextColor(BLUE);
        device.addView(detect, lpMatchWrap(0,dp(10)));

        hostBox = input("Receiver IP address");
        pinBox = input("6-digit PIN");
        pinBox.setInputType(android.text.InputType.TYPE_CLASS_NUMBER);
        device.addView(hostBox, lpMatchWrap(0,dp(8)));
        device.addView(pinBox, lpMatchWrap(0,dp(0)));
        panel.addView(device, lpMatchWrap(0,dp(12)));

        LinearLayout source = card();
        source.addView(text("Choose what to send", 19, NAVY, true));
        LinearLayout row = new LinearLayout(this); row.setOrientation(LinearLayout.HORIZONTAL);
        Button file = button("Choose file", Color.rgb(238,242,248)); file.setTextColor(NAVY);
        Button folder = button("Choose folder", Color.rgb(238,242,248)); folder.setTextColor(NAVY);
        row.addView(file, new LinearLayout.LayoutParams(0,dp(48),1));
        row.addView(new Space(this), new LinearLayout.LayoutParams(dp(10),1));
        row.addView(folder, new LinearLayout.LayoutParams(0,dp(48),1));
        source.addView(row, lpMatchWrap(0,dp(10)));
        selectedLabel = text("Nothing selected", 13, MUTED, false);
        selectedLabel.setPadding(dp(2),dp(6),dp(2),dp(8));
        source.addView(selectedLabel);

        progress = new ProgressBar(this, null, android.R.attr.progressBarStyleHorizontal);
        progress.setMax(1000); progress.setProgress(0);
        source.addView(progress, new LinearLayout.LayoutParams(-1,dp(8)));

        sendStatus = text("Ready", 12, MUTED, false);
        sendStatus.setPadding(0,dp(8),0,dp(10)); source.addView(sendStatus);

        Button send = button("Send with FileSetu", BLUE); send.setTextColor(Color.WHITE);
        source.addView(send, new LinearLayout.LayoutParams(-1,dp(52)));
        panel.addView(source);

        detect.setOnClickListener(v -> discover());
        file.setOnClickListener(v -> showBrowser(false, Environment.getExternalStorageDirectory()));
        folder.setOnClickListener(v -> showBrowser(true, Environment.getExternalStorageDirectory()));
        send.setOnClickListener(v -> beginSend());
        return panel;
    }

    private void discover() {
        sendStatus.setText("Looking for nearby FileSetu devices…");
        worker.submit(() -> {
            try {
                List<PackedTransferEngine.Peer> peers = PackedTransferEngine.discoverPeers(1500);
                runOnUiThread(() -> {
                    if (peers.isEmpty()) {
                        sendStatus.setText("No devices found. Enter IP and PIN manually.");
                        return;
                    }
                    String[] labels = new String[peers.size()];
                    for (int i=0;i<peers.size();i++) labels[i]=peers.get(i).toString();
                    new AlertDialog.Builder(this)
                            .setTitle("Nearby FileSetu devices")
                            .setItems(labels, (d,which) -> {
                                PackedTransferEngine.Peer p = peers.get(which);
                                hostBox.setText(p.host);
                                pinBox.setText(p.pin);
                                sendStatus.setText("Selected " + p.name);
                            })
                            .setNegativeButton("Cancel",null).show();
                });
            } catch (Exception e) {
                runOnUiThread(() -> sendStatus.setText("Device scan failed. Enter IP manually."));
            }
        });
    }

    private void beginSend() {
        String host = hostBox.getText().toString().trim();
        String pin = pinBox.getText().toString().trim();
        if (selectedFile==null) { toast("Choose a file or folder first"); return; }
        if (host.isEmpty() || pin.length()!=6) { toast("Enter receiver IP and 6-digit PIN"); return; }
        progress.setProgress(0);
        worker.submit(() -> {
            try {
                PackedTransferEngine.Client c = new PackedTransferEngine.Client(host, PackedTransferEngine.TCP_PORT, pin);
                c.sendSelection(selectedFile, new PackedTransferEngine.Listener() {
                    public void onStatus(String s) { runOnUiThread(() -> sendStatus.setText(s)); }
                    public void onProgress(String name, long done, long total) {
                        int p = total<=0 ? 0 : (int)Math.min(1000, (done*1000L)/total);
                        runOnUiThread(() -> progress.setProgress(p));
                    }
                });
                runOnUiThread(() -> {
                    progress.setProgress(1000);
                    sendStatus.setText("Transfer complete");
                });
            } catch (Exception e) {
                runOnUiThread(() -> sendStatus.setText("Transfer stopped: " + e.getMessage()));
            }
        });
    }

    private void showBrowser(boolean chooseFolder, File current) {
        File root = Environment.getExternalStorageDirectory();
        File[] all = current.listFiles();
        if (all==null) all = new File[0];
        List<File> visible = new ArrayList<>();
        for (File f: all) {
            if (f.isDirectory() || (!chooseFolder && f.isFile())) visible.add(f);
        }
        Collections.sort(visible, (a,b) -> {
            if (a.isDirectory()!=b.isDirectory()) return a.isDirectory()?-1:1;
            return a.getName().compareToIgnoreCase(b.getName());
        });
        List<String> names = new ArrayList<>();
        if (!current.equals(root) && current.getParentFile()!=null) names.add("↰  Up");
        for (File f: visible) names.add((f.isDirectory()?"▣  ":"     ") + f.getName());

        AlertDialog.Builder b = new AlertDialog.Builder(this)
                .setTitle(chooseFolder ? "Choose folder" : "Choose file")
                .setItems(names.toArray(new String[0]), null)
                .setNegativeButton("Cancel", null);
        if (chooseFolder) b.setPositiveButton("Use this folder", null);
        AlertDialog dlg = b.create();
        dlg.setOnShowListener(x -> {
            ListView lv = dlg.getListView();
            lv.setOnItemClickListener((parent,view,pos,id) -> {
                int offset = (!current.equals(root) && current.getParentFile()!=null) ? 1 : 0;
                if (offset==1 && pos==0) {
                    dlg.dismiss(); showBrowser(chooseFolder, current.getParentFile()); return;
                }
                File f = visible.get(pos-offset);
                if (f.isDirectory()) {
                    dlg.dismiss(); showBrowser(chooseFolder, f);
                } else {
                    selectedFile=f;
                    selectedLabel.setText("Selected file: " + f.getName() + "  •  " + human(f.length()));
                    dlg.dismiss();
                }
            });
            if (chooseFolder) {
                dlg.getButton(AlertDialog.BUTTON_POSITIVE).setOnClickListener(v -> {
                    selectedFile=current;
                    selectedLabel.setText("Selected folder: " + current.getName());
                    dlg.dismiss();
                });
            }
        });
        dlg.show();
    }

    private LinearLayout card() {
        LinearLayout x = new LinearLayout(this);
        x.setOrientation(LinearLayout.VERTICAL);
        x.setPadding(dp(18),dp(18),dp(18),dp(18));
        x.setBackground(round(Color.WHITE,20));
        x.setElevation(dp(2));
        return x;
    }

    private Button button(String s, int color) {
        Button b = new Button(this);
        b.setText(s); b.setTextSize(14); b.setAllCaps(false); b.setTypeface(Typeface.DEFAULT, Typeface.BOLD);
        b.setGravity(Gravity.CENTER); b.setPadding(dp(12),0,dp(12),0); b.setBackground(round(color,14));
        b.setStateListAnimator(null);
        return b;
    }

    private EditText input(String hint) {
        EditText e = new EditText(this);
        e.setHint(hint); e.setTextSize(15); e.setTextColor(NAVY); e.setHintTextColor(Color.rgb(145,156,176));
        e.setSingleLine(true); e.setPadding(dp(14),0,dp(14),0); e.setBackground(round(Color.rgb(241,244,249),14));
        return e;
    }

    private TextView text(String s, int sp, int color, boolean bold) {
        TextView t = new TextView(this);
        t.setText(s); t.setTextSize(sp); t.setTextColor(color);
        if (bold) t.setTypeface(Typeface.DEFAULT, Typeface.BOLD);
        t.setLineSpacing(0,1.08f);
        return t;
    }

    private GradientDrawable round(int color, int radiusDp) {
        GradientDrawable g = new GradientDrawable();
        g.setColor(color); g.setCornerRadius(dp(radiusDp));
        return g;
    }

    private LinearLayout.LayoutParams lpMatchWrap(int top, int bottom) {
        LinearLayout.LayoutParams p = new LinearLayout.LayoutParams(-1,-2);
        p.setMargins(0,top,0,bottom); return p;
    }

    private int dp(int v) { return (int)(v*getResources().getDisplayMetrics().density + .5f); }
    private void toast(String s) { Toast.makeText(this,s,Toast.LENGTH_SHORT).show(); }
    private String human(long n) {
        if (n>=1024L*1024*1024) return String.format(Locale.US,"%.2f GB", n/(1024d*1024*1024));
        if (n>=1024L*1024) return String.format(Locale.US,"%.1f MB", n/(1024d*1024));
        if (n>=1024) return String.format(Locale.US,"%.1f KB", n/1024d);
        return n+" B";
    }

    @Override protected void onDestroy() {
        super.onDestroy();
        worker.shutdownNow();
    }
}
