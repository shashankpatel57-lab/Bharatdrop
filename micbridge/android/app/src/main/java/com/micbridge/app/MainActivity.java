package com.micbridge.app;

import android.Manifest;
import android.app.Activity;
import android.content.*;
import android.content.pm.PackageManager;
import android.graphics.Color;
import android.net.wifi.WifiManager;
import android.os.*;
import android.view.View;
import android.widget.*;
import java.net.*;
import java.nio.charset.StandardCharsets;
import java.util.concurrent.atomic.AtomicBoolean;

public class MainActivity extends Activity {
    private static final int DISCOVERY_PORT = 49721;
    private EditText ipField;
    private TextView status, foundText;
    private Button startButton;
    private Switch ns, agc, aec;
    private Spinner mode;
    private boolean streaming;
    private final AtomicBoolean discovering = new AtomicBoolean(false);
    private DatagramSocket discoverySocket;
    private WifiManager.MulticastLock multicastLock;

    @Override protected void onCreate(Bundle state) {
        super.onCreate(state);
        setContentView(buildUi());
        requestPermissionsIfNeeded();
        startDiscovery();
    }

    private View buildUi() {
        ScrollView scroll = new ScrollView(this);
        LinearLayout root = new LinearLayout(this);
        root.setOrientation(LinearLayout.VERTICAL);
        root.setPadding(dp(22), dp(28), dp(22), dp(28));
        root.setBackgroundColor(Color.rgb(245,247,251));
        scroll.addView(root);

        root.addView(text("MicBridge", 30, true, Color.rgb(15,23,42)));
        TextView sub = text("Use your Android phone as a clean wireless microphone for Windows", 15, false, Color.rgb(71,85,105));
        LinearLayout.LayoutParams subLp = lp(); subLp.topMargin=dp(4); subLp.bottomMargin=dp(22); root.addView(sub, subLp);

        LinearLayout pc = card(); root.addView(pc);
        pc.addView(text("WINDOWS PC",12,true,Color.rgb(100,116,139)));
        foundText = text("Searching for MicBridge on your network…",15,false,Color.rgb(37,99,235));
        foundText.setPadding(0,dp(12),0,dp(12));
        foundText.setOnClickListener(v -> { Object tag=v.getTag(); if(tag!=null) ipField.setText(tag.toString()); });
        pc.addView(foundText);
        ipField = new EditText(this);
        ipField.setSingleLine(true);
        ipField.setHint("PC IP address, e.g. 192.168.1.5");
        pc.addView(ipField, new LinearLayout.LayoutParams(-1,dp(52)));

        LinearLayout voice = card(); LinearLayout.LayoutParams vLp=lp(); vLp.topMargin=dp(16); root.addView(voice,vLp);
        voice.addView(text("VOICE PROCESSING",12,true,Color.rgb(100,116,139)));
        ns=toggle("Noise suppression",true); agc=toggle("Automatic gain",true); aec=toggle("Echo cancellation",true);
        voice.addView(ns); voice.addView(agc); voice.addView(aec);
        TextView ml=text("Capture mode",14,true,Color.rgb(30,41,59)); ml.setPadding(0,dp(12),0,dp(6)); voice.addView(ml);
        mode=new Spinner(this);
        mode.setAdapter(new ArrayAdapter<String>(this,android.R.layout.simple_spinner_dropdown_item,
                new String[]{"Clear Voice","Studio / Natural","Lowest Latency"}));
        voice.addView(mode);

        status=text("Ready",14,false,Color.rgb(71,85,105)); LinearLayout.LayoutParams sLp=lp(); sLp.topMargin=dp(18); root.addView(status,sLp);
        startButton=new Button(this); startButton.setText("START MICROPHONE"); startButton.setTextSize(16); startButton.setAllCaps(false);
        startButton.setOnClickListener(v -> toggleStreaming());
        LinearLayout.LayoutParams bLp=new LinearLayout.LayoutParams(-1,dp(58)); bLp.topMargin=dp(12); root.addView(startButton,bLp);
        TextView tip=text("Same Wi-Fi is easiest. You can also connect the Windows PC to your phone hotspot. If auto-discovery is blocked by Windows Firewall, enter the PC IP shown in the Windows app.",13,false,Color.rgb(100,116,139));
        LinearLayout.LayoutParams tLp=lp(); tLp.topMargin=dp(14); root.addView(tip,tLp);
        return scroll;
    }

    private void toggleStreaming() {
        if(streaming) {
            startService(new Intent(this,MicService.class).setAction(MicService.ACTION_STOP));
            streaming=false; startButton.setText("START MICROPHONE"); status.setText("Stopped"); return;
        }
        String ip=ipField.getText().toString().trim();
        if(ip.isEmpty()) { Toast.makeText(this,"Select or enter the Windows PC IP",Toast.LENGTH_SHORT).show(); return; }
        if(checkSelfPermission(Manifest.permission.RECORD_AUDIO)!=PackageManager.PERMISSION_GRANTED) { requestPermissionsIfNeeded(); return; }
        String m=mode.getSelectedItemPosition()==1?"studio":mode.getSelectedItemPosition()==2?"latency":"voice";
        Intent i=new Intent(this,MicService.class).setAction(MicService.ACTION_START)
                .putExtra(MicService.EXTRA_IP,ip).putExtra(MicService.EXTRA_NS,ns.isChecked())
                .putExtra(MicService.EXTRA_AGC,agc.isChecked()).putExtra(MicService.EXTRA_AEC,aec.isChecked())
                .putExtra(MicService.EXTRA_MODE,m);
        if(Build.VERSION.SDK_INT>=26) startForegroundService(i); else startService(i);
        streaming=true; startButton.setText("STOP MICROPHONE"); status.setText("Streaming to "+ip+" • 48 kHz");
    }

    private void requestPermissionsIfNeeded() {
        java.util.ArrayList<String> list=new java.util.ArrayList<>();
        if(checkSelfPermission(Manifest.permission.RECORD_AUDIO)!=PackageManager.PERMISSION_GRANTED) list.add(Manifest.permission.RECORD_AUDIO);
        if(Build.VERSION.SDK_INT>=33 && checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS)!=PackageManager.PERMISSION_GRANTED) list.add(Manifest.permission.POST_NOTIFICATIONS);
        if(!list.isEmpty()) requestPermissions(list.toArray(new String[0]),11);
    }

    private void startDiscovery() {
        if(!discovering.compareAndSet(false,true)) return;
        new Thread(() -> {
            try {
                WifiManager wm=(WifiManager)getApplicationContext().getSystemService(WIFI_SERVICE);
                if(wm!=null){ multicastLock=wm.createMulticastLock("micbridge-discovery"); multicastLock.setReferenceCounted(false); multicastLock.acquire(); }
                discoverySocket=new DatagramSocket(); discoverySocket.setBroadcast(true); discoverySocket.setSoTimeout(900);
                byte[] msg="MICBRIDGE_DISCOVER".getBytes(StandardCharsets.UTF_8);
                while(discovering.get()) {
                    try {
                        discoverySocket.send(new DatagramPacket(msg,msg.length,InetAddress.getByName("255.255.255.255"),DISCOVERY_PORT));
                        byte[] buf=new byte[512]; DatagramPacket in=new DatagramPacket(buf,buf.length);
                        discoverySocket.receive(in);
                        String s=new String(in.getData(),0,in.getLength(),StandardCharsets.UTF_8);
                        if(s.startsWith("MICBRIDGE_OFFER|")) {
                            String[] p=s.split("\\|",3); String name=p.length>1?p[1]:"Windows PC"; String ip=in.getAddress().getHostAddress();
                            runOnUiThread(() -> { foundText.setText("Found: "+name+"  •  "+ip+"\\nTap here to use it"); foundText.setTag(ip); if(ipField.getText().toString().trim().isEmpty()) ipField.setText(ip); });
                        }
                    } catch(SocketTimeoutException ignored) {} catch(Exception ignored) {}
                    try { Thread.sleep(700); } catch(InterruptedException ignored) {}
                }
            } catch(Exception ignored) {} finally { stopDiscovery(); }
        },"MicBridgeDiscovery").start();
    }

    private void stopDiscovery() {
        discovering.set(false);
        if(discoverySocket!=null){discoverySocket.close();discoverySocket=null;}
        if(multicastLock!=null && multicastLock.isHeld()) multicastLock.release();
        multicastLock=null;
    }

    private LinearLayout card(){ LinearLayout c=new LinearLayout(this); c.setOrientation(LinearLayout.VERTICAL); c.setPadding(dp(18),dp(18),dp(18),dp(18)); c.setBackgroundColor(Color.WHITE); c.setElevation(dp(2)); return c; }
    private Switch toggle(String label,boolean checked){ Switch s=new Switch(this); s.setText(label); s.setTextSize(15); s.setChecked(checked); s.setPadding(0,dp(8),0,dp(8)); return s; }
    private TextView text(String s,int size,boolean bold,int color){ TextView v=new TextView(this); v.setText(s); v.setTextSize(size); v.setTextColor(color); if(bold)v.setTypeface(null,1); return v; }
    private LinearLayout.LayoutParams lp(){ return new LinearLayout.LayoutParams(-1,-2); }
    private int dp(int n){ return Math.round(n*getResources().getDisplayMetrics().density); }

    @Override protected void onDestroy(){ stopDiscovery(); super.onDestroy(); }
}
