package app.rotina

import android.content.Context
import kotlinx.serialization.Serializable
import java.io.File
import java.net.HttpURLConnection
import java.net.URL
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
    // Dev: `adb reverse tcp:47800 tcp:47800` makes the phone's 127.0.0.1 reach the PC (emulator or USB cable).
    val pcBaseUrl: String = "http://127.0.0.1:47800/api/v1/",
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

    @Synchronized fun apply(r: TickResult) {
        if (r.ended == null && r.started == null) return
        val ids = setOfNotNull(r.ended?.id, r.started?.id)
        update { d -> d.copy(sessions = d.sessions.filterNot { it.id in ids } + listOfNotNull(r.ended, r.started)) }
    }

    @Synchronized fun tick(now: Instant = Instant.now()): TickResult {
        val ids = data.sessions.mapTo(HashSet()) { it.id }
        return engine.tick(current(), data.schedule, activities(), { it in ids }, now).also(::apply)
    }

    @Synchronized fun startManual(activityId: String) =
        apply(engine.startManual(current(), activities().getValue(activityId), Instant.now()))

    @Synchronized fun end() = current()?.let { apply(TickResult(Engine.end(it, Instant.now()), null)) }

    /**
     * Dev-only copy of activities, schedule and global list from the PC (read only, no pairing yet).
     * shortcut: no token and no session merge; phase 5 replaces this with the real sync.
     */
    fun importFromPc(): String {
        val base = data.pcBaseUrl.trimEnd('/') + "/"
        val acts = json.decodeFromString<List<Activity>>(get(base + "activities"))
        val schedule = json.decodeFromString<List<ScheduleEntry>>(get(base + "schedule"))
        val always = json.decodeFromString<List<AlwaysAllowed>>(get(base + "always-allowed"))
        val mode = json.decodeFromString<PcState>(get(base + "state")).mode
        update { it.copy(activities = acts, schedule = schedule, alwaysAllowed = always, development = mode != "production") }
        return "${acts.size} atividades e ${schedule.size} horários importados."
    }

    @Serializable private data class PcState(val mode: String = "development")

    private fun get(url: String): String {
        val c = URL(url).openConnection() as HttpURLConnection
        c.connectTimeout = 4000
        c.readTimeout = 4000
        try {
            if (c.responseCode !in 200..299) error("O PC respondeu ${c.responseCode} em $url")
            return c.inputStream.bufferedReader().readText()
        } finally { c.disconnect() }
    }

    // Write to a temp file and rename, so a crash never leaves half a file.
    private fun write(d: LocalData) {
        val tmp = File(file.parentFile, "rotina.json.tmp")
        tmp.writeText(json.encodeToString(LocalData.serializer(), d))
        if (!tmp.renameTo(file)) { file.delete(); tmp.renameTo(file) }
    }
}
