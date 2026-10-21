package app.rotina

import android.accessibilityservice.AccessibilityService
import android.content.Intent
import android.os.Handler
import android.os.Looper
import android.provider.Settings
import android.telecom.TelecomManager
import android.view.accessibility.AccessibilityEvent

/**
 * Watches which app comes to the front. During an activity, an app outside the allowed list gets the
 * block screen on top. The screen content is never read.
 */
class BlockerService : AccessibilityService() {
    private val handler = Handler(Looper.getMainLooper())
    private var exempt: Set<String> = emptySet()
    private var exemptAt = 0L

    // The app already open when an activity starts sends no event, so check the front app every 2 s too.
    private val poll = object : Runnable {
        override fun run() {
            rootInActiveWindow?.packageName?.toString()?.let(::check)
            handler.postDelayed(this, 2000)
        }
    }

    override fun onServiceConnected() {
        Repo.init(this)
        Scheduler.sync(this) // applies due starts and re-arms the alarm whenever the process comes back
        handler.post(poll)
    }

    override fun onAccessibilityEvent(event: AccessibilityEvent) {
        if (event.eventType == AccessibilityEvent.TYPE_WINDOW_STATE_CHANGED) event.packageName?.toString()?.let(::check)
    }

    private fun check(pkg: String) {
        if (Repo.state() == RuleState.FREE) return
        if (!Blocking.shouldBlock(pkg, Repo.allowedPackages(), exempt())) return
        startActivity(
            Intent(this, BlockActivity::class.java)
                .putExtra(BlockActivity.EXTRA_PACKAGE, pkg)
                .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TOP),
        )
    }

    /** Never blocked: Rotina, the system UI, the home screen, the phone app and the keyboard. */
    private fun exempt(): Set<String> {
        val now = System.currentTimeMillis()
        if (now - exemptAt < 60_000) return exempt
        val pm = packageManager
        // Settings also answers HOME (its boot-time FallbackHome), so it must not ride along as a "launcher".
        val homes = pm.queryIntentActivities(Intent(Intent.ACTION_MAIN).addCategory(Intent.CATEGORY_HOME), 0)
            .map { it.activityInfo.packageName }
            .filter { it != "com.android.settings" }
        val dialer = getSystemService(TelecomManager::class.java)?.defaultDialerPackage
        val keyboard = Settings.Secure.getString(contentResolver, Settings.Secure.DEFAULT_INPUT_METHOD)?.substringBefore('/')
        exempt = setOfNotNull(
            packageName, "android", "com.android.systemui", "com.android.phone", "com.android.server.telecom",
            "com.android.emergency", dialer, keyboard,
        ) + homes
        exemptAt = now
        return exempt
    }

    override fun onInterrupt() {}
    override fun onDestroy() { handler.removeCallbacks(poll); super.onDestroy() }
}
