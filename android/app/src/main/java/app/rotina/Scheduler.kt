package app.rotina

import android.app.AlarmManager
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.Application
import android.app.PendingIntent
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.os.Build
import java.time.Instant

class RotinaApp : Application() {
    // Only loads data: the process may start in the background (to bind the accessibility service),
    // where Android refuses to start a foreground service. Allowed entry points call Scheduler.sync.
    override fun onCreate() {
        super.onCreate()
        Repo.init(this)
        Sync.watchNetwork(this)
    }
}

object Scheduler {
    /**
     * Brings everything in line with the rules: applies due starts, keeps the session service running
     * while there is an activity, and sets one exact alarm for the next thing that must happen.
     */
    fun sync(context: Context) {
        val now = Repo.now()
        Repo.tick(now)
        if (Repo.current() != null) SessionService.start(context)

        val engine = Repo.engine
        val acts = Repo.activities()
        val nextStart = Repo.data.schedule
            .filter { it.enabled && !it.deleted && acts[it.activityId]?.deleted == false }
            .mapNotNull { engine.nextOccurrence(it, now) }
            .minOrNull()
        // While locked, a queued start may begin exactly when the minimum ends.
        val minEnds = Repo.current()?.let { ts(it.minEndsAt) }?.takeIf { it > now }
        val next = listOfNotNull(nextStart, minEnds).minOrNull()

        setAlarm(context, 0, next, null)
        // Heads-up 5 minutes and again 1 minute before the next scheduled start: the next of those still ahead.
        val warnAt = nextStart?.let { s -> WARN_MINUTES.map { s.minusSeconds(it * 60) }.firstOrNull { it > now } }
        setAlarm(context, 1, warnAt, AlarmReceiver.WARN)
    }

    val WARN_MINUTES = listOf(5L, 1L)

    private fun setAlarm(context: Context, code: Int, at: Instant?, action: String?) {
        val alarms = context.getSystemService(AlarmManager::class.java)
        val pi = PendingIntent.getBroadcast(
            context, code, Intent(context, AlarmReceiver::class.java).setAction(action),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
        )
        alarms.cancel(pi)
        if (at == null) return
        // Exact and allowed while idle: on time with the screen off.
        if (Build.VERSION.SDK_INT < 31 || alarms.canScheduleExactAlarms())
            alarms.setExactAndAllowWhileIdle(AlarmManager.RTC_WAKEUP, at.toEpochMilli(), pi)
        else
            alarms.setAndAllowWhileIdle(AlarmManager.RTC_WAKEUP, at.toEpochMilli(), pi)
    }
}

class AlarmReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        Repo.init(context)
        if (intent.action == WARN) warn(context)
        Scheduler.sync(context)
    }

    private fun warn(context: Context) {
        val now = Repo.now()
        val acts = Repo.activities()
        // Seconds until each start within the next 5 minutes (plus a little slack for a late alarm).
        val soon = Repo.data.schedule
            .filter { it.enabled && !it.deleted && acts[it.activityId]?.deleted == false }
            .mapNotNull { e -> Repo.engine.nextOccurrence(e, now)?.let { acts.getValue(e.activityId).name to it.epochSecond - now.epochSecond } }
            .filter { (_, secs) -> secs in 0..6 * 60 }
        if (soon.isEmpty()) return
        val minutes = ((soon.minOf { it.second } + 59) / 60).coerceAtLeast(1)
        val names = soon.map { it.first }.distinct().joinToString(", ")
        val nm = context.getSystemService(NotificationManager::class.java)
        nm.createNotificationChannel(NotificationChannel("warn", "Aviso antes da atividade", NotificationManager.IMPORTANCE_DEFAULT))
        nm.notify(2, Notification.Builder(context, "warn")
            .setSmallIcon(R.mipmap.ic_launcher)
            .setContentTitle("$names começa em $minutes ${if (minutes == 1L) "minuto" else "minutos"}")
            .setContentText("Salve o que estiver fazendo.")
            .setAutoCancel(true)
            .build())
    }

    companion object { const val WARN = "app.rotina.WARN" }
}

/** After a reboot, an app update or a clock change: restore the session and the next alarm. */
class BootReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        Repo.init(context)
        Scheduler.sync(context)
    }
}
