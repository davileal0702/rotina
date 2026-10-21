package app.rotina

import android.content.Context
import android.content.Intent
import android.net.ConnectivityManager
import android.net.Network
import kotlinx.serialization.Serializable
import kotlinx.serialization.builtins.ListSerializer
import java.net.HttpURLConnection
import java.net.URI
import java.net.URL
import java.net.URLDecoder
import java.time.Instant
import java.util.concurrent.Executors

@Serializable data class SyncPull(
    val rev: Long, val mode: String = "development", val activities: List<Activity> = emptyList(),
    val schedule: List<ScheduleEntry> = emptyList(), val alwaysAllowed: List<AlwaysAllowed>? = null,
    val sessions: List<Session> = emptyList(),
    val recoveryKeyHash: String? = null,
    val timeZone: String? = null, // the PC's zone; the schedule is read in it
)
@Serializable data class SyncPush(val sessions: List<Session>)
@Serializable data class SyncPushResult(val accepted: List<String> = emptyList(), val rejected: List<String> = emptyList())
@Serializable data class CatalogItem(val identifier: String, val label: String)

/** "rotina://pair?h=192.168.0.10&p=47800&t=TOKEN" from the PC's QR code, as (base URL, token); null if not a pairing code. */
fun parsePairing(code: String): Pair<String, String>? {
    val uri = runCatching { URI(code.trim()) }.getOrNull() ?: return null
    if (uri.scheme != "rotina" || uri.host != "pair") return null
    val q = uri.rawQuery.orEmpty().split('&').mapNotNull {
        val (k, v) = it.split('=', limit = 2).takeIf { p -> p.size == 2 } ?: return@mapNotNull null
        k to URLDecoder.decode(v, "UTF-8")
    }.toMap()
    val host = q["h"]?.takeIf { it.isNotBlank() } ?: return null
    val port = q["p"]?.toIntOrNull()?.takeIf { it in 1..65535 } ?: return null
    val token = q["t"]?.takeIf { it.isNotBlank() } ?: return null
    return "http://$host:$port/api/v1/" to token
}

/**
 * Two-way sync with the PC: first send the starts and ends made here (maybe offline), then take what changed on the PC.
 * Runs one at a time on a background thread; triggered by the accessibility service loop, network changes and the UI.
 */
object Sync {
    private val worker = Executors.newSingleThreadExecutor()

    fun soon(context: Context) {
        if (Repo.data.token.isEmpty()) return
        val app = context.applicationContext
        worker.execute { run(app) }
    }

    /** Re-runs sync whenever a network comes up (back home on the Wi-Fi). */
    fun watchNetwork(context: Context) {
        val app = context.applicationContext
        app.getSystemService(ConnectivityManager::class.java).registerDefaultNetworkCallback(object : ConnectivityManager.NetworkCallback() {
            override fun onAvailable(network: Network) = soon(app)
        })
    }

    fun pair(context: Context, code: String): String {
        val (base, token) = parsePairing(code) ?: return "Código inválido. Ele começa com rotina://pair"
        // Re-pairing must not become a way out of a running activity.
        if (Repo.state() == RuleState.LOCKED) return "Termine a atividade atual antes de parear de novo."
        // Another PC (or a fresh database): drop what came from the old one; local sessions still get sent.
        Repo.update {
            it.copy(pcBaseUrl = base, token = token, lastRev = 0, catalogHash = 0, lastSyncError = "",
                activities = emptyList(), schedule = emptyList(), alwaysAllowed = emptyList(), recoveryKeyHash = null)
        }
        soon(context)
        return "Pareado. Sincronizando..."
    }

    private fun run(context: Context) {
        try {
            val sent = Repo.data.let { d -> d.sessions.filter { it.id in d.outbox } }
            if (sent.isNotEmpty()) {
                val res = json.decodeFromString<SyncPushResult>(http("POST", "sync", json.encodeToString(SyncPush.serializer(), SyncPush(sent))))
                Repo.afterPush(sent, res.rejected.toSet())
            }
            var pull = json.decodeFromString<SyncPull>(http("GET", "sync?since=${Repo.data.lastRev}"))
            // The PC is behind what we saw (another PC, or its database was recreated): start over from 0.
            if (pull.rev < Repo.data.lastRev) pull = json.decodeFromString<SyncPull>(http("GET", "sync?since=0"))
            Repo.applyPull(pull)
            val catalog = installedApps(context)
            if (catalog.hashCode() != Repo.data.catalogHash) {
                http("POST", "catalog/android", json.encodeToString(ListSerializer(CatalogItem.serializer()), catalog))
                Repo.update { it.copy(catalogHash = catalog.hashCode()) }
            }
            Repo.update { it.copy(lastSyncAt = Instant.now().toString(), lastSyncError = "") }
            Scheduler.sync(context) // a start made on the PC begins blocking here too
        } catch (e: Exception) {
            Repo.update { it.copy(lastSyncError = e.message ?: e.javaClass.simpleName) }
        }
    }

    /** Apps with a launcher icon: what the PC shows when choosing allowed Android apps. */
    private fun installedApps(context: Context): List<CatalogItem> {
        val pm = context.packageManager
        return pm.queryIntentActivities(Intent(Intent.ACTION_MAIN).addCategory(Intent.CATEGORY_LAUNCHER), 0)
            .map { CatalogItem(it.activityInfo.packageName, it.loadLabel(pm).toString()) }
            .distinctBy { it.identifier }
            .sortedBy { it.identifier }
    }

    private fun http(method: String, path: String, body: String? = null): String {
        val d = Repo.data
        val c = URL(d.pcBaseUrl + path).openConnection() as HttpURLConnection
        c.requestMethod = method
        c.connectTimeout = 4000
        c.readTimeout = 6000
        c.setRequestProperty("Authorization", "Bearer ${d.token}")
        try {
            if (body != null) {
                c.doOutput = true
                c.setRequestProperty("Content-Type", "application/json; charset=utf-8")
                c.outputStream.use { it.write(body.toByteArray()) }
            }
            when (c.responseCode) {
                in 200..299 -> return c.inputStream.bufferedReader().readText()
                401 -> error("O PC recusou o pareamento. Gere um código novo no PC e pareie de novo.")
                else -> error("O PC respondeu ${c.responseCode}.")
            }
        } finally { c.disconnect() }
    }
}
