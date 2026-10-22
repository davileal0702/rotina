package app.rotina

import android.content.Context
import android.os.SystemClock
import android.provider.Settings
import kotlinx.serialization.Serializable
import java.io.File
import java.time.Duration
import java.time.Instant
import java.time.ZoneId
import java.util.UUID

/** Everything the phone keeps, so it follows the schedule away from home. One JSON file, same shapes as the PC API. */
@Serializable
data class LocalData(
    val activities: List<Activity> = emptyList(),
    val schedule: List<ScheduleEntry> = emptyList(),
    val alwaysAllowed: List<AlwaysAllowed> = emptyList(),
    val sessions: List<Session> = emptyList(),
    val development: Boolean = true,
    // Fixed at first run so changing the phone's time zone cannot shift the schedule.
    val zoneId: String = ZoneId.systemDefault().id,
    val deviceId: String = UUID.randomUUID().toString(),
    // Pairing (from the PC's QR code). Dev: `adb reverse tcp:47800 tcp:47800` and host 127.0.0.1.
    val pcBaseUrl: String = "",
    val token: String = "",
    // Sync: the PC revision already received, and the sessions changed here that the PC has not taken yet.
    val lastRev: Long = 0,
    val outbox: List<String> = emptyList(),
    val catalogHash: Int = 0,
    val lastSyncAt: String = "",
    val lastSyncError: String = "",
    // Hash of the PC's recovery key, so the key also works here without a connection.
    val recoveryKeyHash: String? = null,
    // Last trusted time, to notice the clock being pushed forward during an activity (see Clock.kt).
    val clock: ClockAnchor? = null,
)

/** Single source of truth inside the app process (UI, alarm, boot, accessibility and session service share it). */
object Repo {
    private lateinit var file: File
    @Volatile var data = LocalData()
        private set

    private lateinit var app: Context

    @Synchronized fun init(context: Context) {
        if (::file.isInitialized) return
        app = context.applicationContext
        file = File(context.filesDir, "rotina.json")
        data = if (file.exists()) json.decodeFromString(file.readText()) else LocalData().also(::write)
    }

    val engine: Engine
        get() = Engine(ZoneId.of(data.zoneId), if (data.development) Duration.ofSeconds(1) else Duration.ofMinutes(1), data.deviceId)

    fun current(): Session? = data.sessions.lastOrNull { it.endedAt == null }
    fun activities(): Map<String, Activity> = data.activities.associateBy { it.id }
    fun state(now: Instant = now()) = Engine.stateOf(current(), now)

    /** The time the rules use: the wall clock, unless it was pushed forward during an activity. */
    @Synchronized fun now(): Instant {
        val wall = System.currentTimeMillis()
        val elapsed = SystemClock.elapsedRealtime()
        val boot = Settings.Global.getInt(app.contentResolver, Settings.Global.BOOT_COUNT, 0)
        val anchor = data.clock
        val trusted = trustedMillis(wall, elapsed, boot, anchor)
        val free = Engine.stateOf(current(), Instant.ofEpochMilli(trusted)) == RuleState.FREE
        // New anchor after a reboot, or while free (then a clock fixed by the network simply counts).
        if (anchor == null || anchor.boot != boot || (free && trusted != wall)) {
            update { it.copy(clock = ClockAnchor(boot, wall, elapsed)) }
            return Instant.ofEpochMilli(wall)
        }
        return Instant.ofEpochMilli(trusted)
    }

    /** Packages allowed right now: the running activity's own list plus the global list. */
    fun allowedPackages(): Set<String> =
        (current()?.rulesSnapshot?.allowedAndroidPackages.orEmpty() +
            data.alwaysAllowed.filter { it.platform == "android" }.map { it.identifier }).toSet()

    @Synchronized fun update(change: (LocalData) -> LocalData) {
        data = change(data)
        write(data)
    }

    /** A start or end made on this phone: saved, and queued for the PC. */
    @Synchronized fun apply(r: TickResult) {
        if (r.ended == null && r.started == null) return
        val changed = listOfNotNull(r.ended, r.started)
        val ids = changed.mapTo(HashSet()) { it.id }
        update { d -> d.copy(sessions = d.sessions.filterNot { it.id in ids } + changed, outbox = (d.outbox + ids).distinct()) }
    }

    @Synchronized fun tick(now: Instant = now()): TickResult {
        val ids = data.sessions.mapTo(HashSet()) { it.id }
        return engine.tick(current(), data.schedule, activities(), { it in ids }, now).also(::apply)
    }

    @Synchronized fun startManual(activityId: String) =
        apply(engine.startManual(current(), activities().getValue(activityId), now()))

    fun nextStart(now: Instant = now()): PendingStart? {
        val ids = data.sessions.mapTo(HashSet()) { it.id }
        return engine.nextStart(data.schedule, activities(), { it in ids }, now)
    }

    @Synchronized fun startEarly() {
        val now = now()
        val next = nextStart(now) ?: throw RuleException("Nenhuma atividade agendada.")
        apply(engine.startEarly(current(), next, now))
    }

    @Synchronized fun end() = current()?.let { apply(TickResult(Engine.end(it, now()), null)) }

    /** Recovery key: ends the running activity now, even before its minimum; the PC takes it on the next sync. */
    @Synchronized fun recover(key: String): String {
        if (!Recovery.matches(key, data.recoveryKeyHash))
            return if (data.recoveryKeyHash == null) "O celular ainda não recebeu a chave. Sincronize com o PC." else "Chave de recuperação incorreta."
        val cur = current() ?: return "Nenhuma atividade em andamento."
        apply(TickResult(Engine.recover(cur, now()), null))
        return "Atividade encerrada. A agenda volta a valer na próxima atividade."
    }

    /** After a push: what the PC took leaves the queue (unless it changed again meanwhile); what it refused is dropped. */
    @Synchronized fun afterPush(sent: List<Session>, rejected: Set<String>) = update { d ->
        val byId = d.sessions.associateBy { it.id }
        val delivered = sent.filter { byId[it.id] == it }.mapTo(HashSet()) { it.id } + rejected
        d.copy(outbox = d.outbox - delivered, sessions = d.sessions.filterNot { it.id in rejected })
    }

    /** The PC's changes. Sessions still waiting to be sent keep the local version until the PC has them. */
    @Synchronized fun applyPull(p: SyncPull) = update { d ->
        d.copy(
            activities = mergeById(d.activities, p.activities) { it.id },
            schedule = mergeById(d.schedule, p.schedule) { it.id },
            alwaysAllowed = p.alwaysAllowed ?: d.alwaysAllowed,
            sessions = mergeById(d.sessions, p.sessions.filter { it.id !in d.outbox }) { it.id },
            development = p.mode != "production",
            lastRev = p.rev,
            recoveryKeyHash = p.recoveryKeyHash ?: d.recoveryKeyHash,
            // Follow the PC's zone (if valid), so both devices start the same activity at the same moment.
            zoneId = p.timeZone?.takeIf { runCatching { ZoneId.of(it) }.isSuccess } ?: d.zoneId,
        )
    }

    // Write to a temp file and rename, so a crash never leaves half a file.
    private fun write(d: LocalData) {
        val tmp = File(file.parentFile, "rotina.json.tmp")
        tmp.writeText(json.encodeToString(LocalData.serializer(), d))
        if (!tmp.renameTo(file)) { file.delete(); tmp.renameTo(file) }
    }
}

/** Replaces items with the same id and adds new ones, keeping the local order. */
fun <T> mergeById(local: List<T>, incoming: List<T>, id: (T) -> String): List<T> {
    val merged = LinkedHashMap<String, T>()
    local.forEach { merged[id(it)] = it }
    incoming.forEach { merged[id(it)] = it }
    return merged.values.toList()
}
