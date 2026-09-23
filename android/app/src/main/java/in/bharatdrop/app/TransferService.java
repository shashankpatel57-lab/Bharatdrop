package in.bharatdrop.app;

import android.app.*;
import android.content.*;
import android.os.*;
import java.util.Locale;
import java.util.concurrent.ThreadLocalRandom;

public class TransferService extends Service {
    public static final String CHANNEL_ID = "filesetu_transfer";
    public static final String PREFS = "filesetu";
    public static final String KEY_PIN = "pin";
    private PackedTransferEngine.Server server;
    private PowerManager.WakeLock wakeLock;

    public static String ensurePin(Context c) {
        android.content.SharedPreferences p = c.getSharedPreferences(PREFS, MODE_PRIVATE);
        String pin = p.getString(KEY_PIN, null);
        if (pin == null || pin.length()!=6) {
            pin = String.format(Locale.US, "%06d", ThreadLocalRandom.current().nextInt(0, 1000000));
            p.edit().putString(KEY_PIN, pin).apply();
        }
        return pin;
    }

    @Override public void onCreate() {
        super.onCreate();
        createChannel();
        String pin = ensurePin(this);
        Notification n = new Notification.Builder(this, CHANNEL_ID)
                .setSmallIcon(android.R.drawable.stat_sys_upload_done)
                .setContentTitle("FileSetu is ready")
                .setContentText("Secure local transfer receiver is active")
                .setOngoing(true)
                .build();
        startForeground(54, n);
        try {
            PowerManager pm = (PowerManager)getSystemService(POWER_SERVICE);
            wakeLock = pm.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "FileSetu:Transfer");
            wakeLock.acquire();
        } catch (Exception ignored) {}
        try {
            server = new PackedTransferEngine.Server(this, pin);
            server.start();
        } catch (Exception e) {
            stopSelf();
        }
    }

    private void createChannel() {
        if (Build.VERSION.SDK_INT >= 26) {
            NotificationChannel c = new NotificationChannel(CHANNEL_ID, "FileSetu transfers", NotificationManager.IMPORTANCE_LOW);
            c.setDescription("Keeps local file transfers active while the app is in the background.");
            ((NotificationManager)getSystemService(NOTIFICATION_SERVICE)).createNotificationChannel(c);
        }
    }

    @Override public int onStartCommand(Intent intent, int flags, int startId) {
        return START_STICKY;
    }

    @Override public void onDestroy() {
        if (server != null) server.stop();
        if (wakeLock != null && wakeLock.isHeld()) wakeLock.release();
        super.onDestroy();
    }

    @Override public android.os.IBinder onBind(Intent intent) { return null; }
}
