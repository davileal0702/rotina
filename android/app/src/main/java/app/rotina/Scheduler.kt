package app.rotina

import android.app.AlarmManager
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
    }
}

object Scheduler {
    /**
     * Brings everything in line with the rules: applies due starts, keeps the session service running
     * while there is an activity, and sets one exact alarm for the next thing that must happen.
     */
    fun sync(context: Context) {
        val now = Instant.now()
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

        val alarms = context.getSystemService(AlarmManager::class.java)
        val pi = PendingIntent.getBroadcast(
            context, 0, Intent(context, AlarmReceiver::class.java),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
        )
        alarms.cancel(pi)
        if (next == null) return
        // Exact and allowed while idle: starts on time with the screen off.
        if (Build.VERSION.SDK_INT < 31 || alarms.canScheduleExactAlarms())
            alarms.setExactAndAllowWhileIdle(AlarmManager.RTC_WAKEUP, next.toEpochMilli(), pi)
        else
            alarms.setAndAllowWhileIdle(AlarmManager.RTC_WAKEUP, next.toEpochMilli(), pi)
    }
}

class AlarmReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        Repo.init(context)
        Scheduler.sync(context)
    }
}

/** After a reboot, an app update or a clock change: restore the session and the next alarm. */
class BootReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        Repo.init(context)
        Scheduler.sync(context)
    }
}
