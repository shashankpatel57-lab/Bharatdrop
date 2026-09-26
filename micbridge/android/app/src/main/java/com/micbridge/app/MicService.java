package com.micbridge.app;

import android.app.*;
import android.content.*;
import android.content.pm.ServiceInfo;
import android.media.*;
import android.media.audiofx.*;
import android.net.wifi.WifiManager;
import android.os.*;
import java.net.*;
import java.nio.*;
import java.nio.charset.StandardCharsets;
import java.util.concurrent.atomic.AtomicBoolean;

public class MicService extends Service {
    public static final String ACTION_START="com.micbridge.START";
    public static final String ACTION_STOP="com.micbridge.STOP";
    public static final String EXTRA_IP="ip", EXTRA_NS="ns", EXTRA_AGC="agc", EXTRA_AEC="aec", EXTRA_MODE="mode";
    private static final int AUDIO_PORT=49720, RATE=48000, FRAME_BYTES=960;
    private final AtomicBoolean running=new AtomicBoolean(false);
    private AudioRecord record; private DatagramSocket socket;
    private NoiseSuppressor ns; private AutomaticGainControl agc; private AcousticEchoCanceler aec;
    private PowerManager.WakeLock wakeLock; private WifiManager.WifiLock wifiLock;

    @Override public void onCreate(){ super.onCreate(); createChannel(); }

    @Override public int onStartCommand(Intent intent,int flags,int startId) {
        if(intent==null) return START_NOT_STICKY;
        if(ACTION_STOP.equals(intent.getAction())) { stopStreaming(); stopSelf(); return START_NOT_STICKY; }
        if(ACTION_START.equals(intent.getAction()) && !running.get()) {
            String ip=intent.getStringExtra(EXTRA_IP);
            Notification n=notification("Streaming to "+ip);
            if(Build.VERSION.SDK_INT>=29) startForeground(41,n,ServiceInfo.FOREGROUND_SERVICE_TYPE_MICROPHONE); else startForeground(41,n);
            startStreaming(ip,intent.getBooleanExtra(EXTRA_NS,true),intent.getBooleanExtra(EXTRA_AGC,true),
                    intent.getBooleanExtra(EXTRA_AEC,true),intent.getStringExtra(EXTRA_MODE));
        }
        return START_NOT_STICKY;
    }

    private void startStreaming(String ip,boolean useNs,boolean useAgc,boolean useAec,String mode) {
        running.set(true);
        new Thread(() -> {
            try {
                PowerManager pm=(PowerManager)getSystemService(POWER_SERVICE);
                wakeLock=pm.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK,"MicBridge::Mic"); wakeLock.acquire();
                WifiManager wm=(WifiManager)getApplicationContext().getSystemService(WIFI_SERVICE);
                if(wm!=null){ wifiLock=wm.createWifiLock(WifiManager.WIFI_MODE_FULL_HIGH_PERF,"MicBridge::Wifi"); wifiLock.acquire(); }

                int source=MediaRecorder.AudioSource.VOICE_COMMUNICATION;
                if("studio".equals(mode)) source=MediaRecorder.AudioSource.MIC;
                else if("latency".equals(mode) && Build.VERSION.SDK_INT>=29) source=MediaRecorder.AudioSource.VOICE_PERFORMANCE;
                int min=AudioRecord.getMinBufferSize(RATE,AudioFormat.CHANNEL_IN_MONO,AudioFormat.ENCODING_PCM_16BIT);
                int bufferSize=Math.max(min*2,FRAME_BYTES*8);
                record=new AudioRecord.Builder().setAudioSource(source)
                        .setAudioFormat(new AudioFormat.Builder().setEncoding(AudioFormat.ENCODING_PCM_16BIT)
                                .setSampleRate(RATE).setChannelMask(AudioFormat.CHANNEL_IN_MONO).build())
                        .setBufferSizeInBytes(bufferSize).build();

                int session=record.getAudioSessionId();
                if(useNs && NoiseSuppressor.isAvailable()){ns=NoiseSuppressor.create(session);if(ns!=null)ns.setEnabled(true);}
                if(useAgc && AutomaticGainControl.isAvailable()){agc=AutomaticGainControl.create(session);if(agc!=null)agc.setEnabled(true);}
                if(useAec && AcousticEchoCanceler.isAvailable()){aec=AcousticEchoCanceler.create(session);if(aec!=null)aec.setEnabled(true);}

                InetAddress host=InetAddress.getByName(ip);
                socket=new DatagramSocket(); socket.connect(host,AUDIO_PORT); socket.setSendBufferSize(256*1024);
                String hello="MICBRIDGE_HELLO|"+Build.MANUFACTURER+" "+Build.MODEL+"|"+RATE;
                byte[] hb=hello.getBytes(StandardCharsets.UTF_8); socket.send(new DatagramPacket(hb,hb.length));

                byte[] pcm=new byte[FRAME_BYTES]; byte[] packet=new byte[16+FRAME_BYTES]; int seq=0;
                record.startRecording();
                while(running.get()) {
                    int got=0;
                    while(got<pcm.length && running.get()) {
                        int n=record.read(pcm,got,pcm.length-got,AudioRecord.READ_BLOCKING);
                        if(n>0) got+=n; else if(n<0) throw new IllegalStateException("AudioRecord read error "+n);
                    }
                    if(!running.get()) break;
                    ByteBuffer bb=ByteBuffer.wrap(packet).order(ByteOrder.BIG_ENDIAN);
                    bb.put((byte)'M').put((byte)'B').put((byte)'1').put((byte)'!');
                    bb.putInt(seq++); bb.putLong(System.nanoTime()); bb.put(pcm);
                    socket.send(new DatagramPacket(packet,packet.length));
                }
            } catch(Exception ignored) {
            } finally { stopStreaming(); stopSelf(); }
        },"MicBridgeAudio").start();
    }

    private synchronized void stopStreaming() {
        running.set(false);
        try{if(record!=null && record.getRecordingState()==AudioRecord.RECORDSTATE_RECORDING)record.stop();}catch(Exception ignored){}
        if(record!=null){record.release();record=null;} if(ns!=null){ns.release();ns=null;} if(agc!=null){agc.release();agc=null;} if(aec!=null){aec.release();aec=null;}
        if(socket!=null){socket.close();socket=null;}
        if(wifiLock!=null && wifiLock.isHeld())wifiLock.release(); wifiLock=null;
        if(wakeLock!=null && wakeLock.isHeld())wakeLock.release(); wakeLock=null;
        stopForeground(STOP_FOREGROUND_REMOVE);
    }

    @Override public void onDestroy(){stopStreaming();super.onDestroy();}
    @Override public IBinder onBind(Intent intent){return null;}

    private void createChannel(){if(Build.VERSION.SDK_INT>=26)getSystemService(NotificationManager.class).createNotificationChannel(new NotificationChannel("micbridge","MicBridge streaming",NotificationManager.IMPORTANCE_LOW));}
    private Notification notification(String text){
        Intent stop=new Intent(this,MicService.class).setAction(ACTION_STOP);
        PendingIntent pi=PendingIntent.getService(this,8,stop,PendingIntent.FLAG_UPDATE_CURRENT|PendingIntent.FLAG_IMMUTABLE);
        return new Notification.Builder(this,Build.VERSION.SDK_INT>=26?"micbridge":"").setContentTitle("MicBridge microphone active")
                .setContentText(text).setSmallIcon(android.R.drawable.ic_btn_speak_now)
                .addAction(new Notification.Action.Builder(android.R.drawable.ic_media_pause,"Stop",pi).build()).setOngoing(true).build();
    }
}
