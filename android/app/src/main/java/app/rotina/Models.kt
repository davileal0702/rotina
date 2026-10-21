package app.rotina

import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json
import java.time.Instant
import java.time.OffsetDateTime

// Same JSON shape as the PC API (docs/api.yaml): camelCase, ISO 8601 dates, enums in camelCase.

@Serializable
data class Activity(
    val id: String,
    val name: String,
    val color: String = "#0F766E",
    val minDurationMinutes: Int,
    val allowedAndroidPackages: List<String> = emptyList(),
    val allowedWindowsExecutables: List<String> = emptyList(),
    val allowedSites: List<String> = emptyList(),
    val updatedAt: String = "",
    val deleted: Boolean = false,
)

@Serializable
data class ScheduleEntry(
    val id: String,
    val activityId: String,
    val daysOfWeek: List<String>, // "monday", "tuesday"...
    val startTime: String, // "08:00:00"
    val enabled: Boolean = true,
    val updatedAt: String = "",
    val deleted: Boolean = false,
)

@Serializable
data class Session(
    val id: String,
    val activityId: String,
    val startedAt: String,
    val minEndsAt: String,
    val endedAt: String? = null,
    val origin: String, // "schedule" or "manual"
    val deviceId: String,
    val rulesSnapshot: Activity,
)

@Serializable
data class AlwaysAllowed(val platform: String, val identifier: String)

val json = Json {
    ignoreUnknownKeys = true
    encodeDefaults = true
}

/** Parses the PC's "2026-10-19T08:00:00+00:00" and our own "2026-10-19T08:00:00Z". Empty means "long ago". */
fun ts(value: String): Instant = if (value.isEmpty()) Instant.EPOCH else OffsetDateTime.parse(value).toInstant()
