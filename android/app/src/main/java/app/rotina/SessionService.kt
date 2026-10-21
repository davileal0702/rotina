package app.rotina

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.Build
import android.os.Handler
import android.os.IBinder
import android.os.Looper

/**
 * Runs only during an activity: a fixed notification keeps the process alive so blocking keeps working,
 * and a short loop applies queued or replacing scheduled starts.
 */
class SessionService : Service() {
    private val handler = Handler(Looper.getMainLooper())
    private var shown: Pair<String, RuleState>? = null

    private val loop = object : Runnable {
        override fun run() {
            Repo.tick()
            val cur = Repo.current()
            if (cur == null) {
                stopForeground(STOP_FOREGROUND_REMOVE)
                stopSelf()
                return
            }
            val state = Repo.state()
            if (shown != cur.id to state) {
                shown = cur.id to state
                getSystemService(NotificationManager::class.java).notify(ID, notification(cur, state))
            }
            handler.postDelayed(this, 2000)
        }
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        Repo.init(this)
        val cur = Repo.current()
        if (cur == null) { stopSelf(); return START_NOT_STICKY }
        val n = notification(cur, Repo.state())
        if (Build.VERSION.SDK_INT >= 34) startForeground(ID, n, ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE)
        else startForeground(ID, n)
        handler.removeCallbacks(loop)
        handler.post(loop)
        return START_STICKY
    }

    override fun onDestroy() { handler.removeCallbacks(loop); super.onDestroy() }
    override fun onBind(intent: Intent?): IBinder? = null

    private fun notification(s: Session, state: RuleState): Notification {
        val nm = getSystemService(NotificationManager::class.java)
        nm.createNotificationChannel(NotificationChannel(CHANNEL, "Atividade em andamento", NotificationManager.IMPORTANCE_LOW))
        val open = PendingIntent.getActivity(this, 0, Intent(this, MainActivity::class.java), PendingIntent.FLAG_IMMUTABLE)
        val locked = state == RuleState.LOCKED
        return Notification.Builder(this, CHANNEL)
            .setSmallIcon(R.mipmap.ic_launcher)
            .setContentTitle(s.rulesSnapshot.name)
            .setContentText(if (locked) "Tempo restante para poder encerrar" else "Você já pode encerrar a atividade")
            // The system counts down by itself; no need to update the notification every second.
            .setUsesChronometer(locked)
            .setChronometerCountDown(locked)
            .setWhen(ts(s.minEndsAt).toEpochMilli())
            .setShowWhen(locked)
            .setOngoing(true)
            .setContentIntent(open)
            .build()
    }

    companion object {
        private const val ID = 1
        private const val CHANNEL = "session"
        /** Never throws: from the background Android may refuse; blocking still works without the notification. */
        fun start(context: Context) {
            try { context.startForegroundService(Intent(context, SessionService::class.java)) }
            catch (e: IllegalStateException) { android.util.Log.w("Rotina", "Serviço da sessão não iniciado agora: ${e.message}") }
        }
    }
}
