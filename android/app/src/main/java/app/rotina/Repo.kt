package app.rotina

import android.content.Context
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
)

/** Single source of truth inside the app process (UI, alarm, boot, accessibility and session service share it). */
object Repo {
    private lateinit var file: File
    @Volatile var data = LocalData()
        private set

    @Synchronized fun init(context: Context) {
        if (::file.isInitialized) return
        file = File(context.filesDir, "rotina.json")
        data = if (file.exists()) json.decodeFromString(file.readText()) else LocalData().also(::write)
    }

    val engine: Engine
        get() = Engine(ZoneId.of(data.zoneId), if (data.development) Duration.ofSeconds(1) else Duration.ofMinutes(1), data.deviceId)

    fun current(): Session? = data.sessions.lastOrNull { it.endedAt == null }
    fun activities(): Map<String, Activity> = data.activities.associateBy { it.id }
    fun state(now: Instant = Instant.now()) = Engine.stateOf(current(), now)

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

    @Synchronized fun tick(now: Instant = Instant.now()): TickResult {
        val ids = data.sessions.mapTo(HashSet()) { it.id }
        return engine.tick(current(), data.schedule, activities(), { it in ids }, now).also(::apply)
    }

    @Synchronized fun startManual(activityId: String) =
        apply(engine.startManual(current(), activities().getValue(activityId), Instant.now()))

    @Synchronized fun end() = current()?.let { apply(TickResult(Engine.end(it, Instant.now()), null)) }

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
