package app.rotina

import java.security.MessageDigest
import java.time.DayOfWeek
import java.time.Duration
import java.time.Instant
import java.time.LocalDate
import java.time.LocalTime
import java.time.ZoneId
import java.time.ZonedDateTime
import java.util.UUID

enum class RuleState { FREE, LOCKED, COMPLETABLE }

class RuleException(message: String) : Exception(message)

data class PendingStart(val entry: ScheduleEntry, val activity: Activity, val at: Instant)

data class TickResult(val ended: Session?, val started: Session?)

/**
 * Port of windows/Rotina.Core/Engine.cs. Pure: the caller passes state and clock.
 * Any rule change must happen in both files, or PC and phone will disagree.
 */
class Engine(private val zone: ZoneId, private val minUnit: Duration, private val deviceId: String) {

    fun minDuration(a: Activity): Duration = minUnit.multipliedBy(a.minDurationMinutes.toLong())

    fun startManual(current: Session?, activity: Activity, now: Instant): TickResult {
        if (activity.deleted) throw RuleException("Atividade excluída.")
        if (stateOf(current, now) == RuleState.LOCKED)
            throw RuleException("A atividade atual ainda não atingiu o tempo mínimo.")
        val ended = current?.takeIf { it.endedAt == null }?.copy(endedAt = now.toString())
        return TickResult(ended, newSession(UUID.randomUUID().toString(), activity, now, "manual"))
    }

    /** The scheduled start that should happen now, or that is queued behind a locked session. */
    fun pending(
        current: Session?, entries: List<ScheduleEntry>, activities: Map<String, Activity>,
        sessionExists: (String) -> Boolean, now: Instant,
    ): PendingStart? {
        val running = current?.takeIf { it.endedAt == null }
        var best: PendingStart? = null
        for (e in entries) {
            val a = activities[e.activityId]
            if (!e.enabled || e.deleted || a == null || a.deleted) continue
            // Occurrences before the entry was last edited are ignored, so saving a past time never starts it.
            val at = latestOccurrence(e, now) ?: continue
            if (at <= ts(e.updatedAt)) continue
            // Free: a missed start only counts while its minimum would still be running.
            // Running: only starts that arrived after the current session began.
            if (running == null) { if (at + minDuration(a) <= now) continue }
            else if (at <= ts(running.startedAt)) continue
            if (sessionExists(scheduledSessionId(e, at))) continue
            // Latest wins; ties break by entry id so PC and phone pick the same one.
            if (best == null || at > best.at || (at == best.at && e.id < best.entry.id)) best = PendingStart(e, a, at)
        }
        return best
    }

    fun tick(
        current: Session?, entries: List<ScheduleEntry>, activities: Map<String, Activity>,
        sessionExists: (String) -> Boolean, now: Instant,
    ): TickResult {
        val p = pending(current, entries, activities, sessionExists, now) ?: return TickResult(null, null)
        val id = scheduledSessionId(p.entry, p.at)
        val running = current?.takeIf { it.endedAt == null }
            ?: return TickResult(null, newSession(id, p.activity, p.at, "schedule"))
        val minEnds = ts(running.minEndsAt)
        if (now < minEnds) return TickResult(null, null) // queued
        // Replaces a completable session at the scheduled time, or starts a queued one right when the minimum ended.
        val startAt = if (p.at > minEnds) p.at else minEnds
        return TickResult(running.copy(endedAt = startAt.toString()), newSession(id, p.activity, startAt, "schedule"))
    }

    fun latestOccurrence(e: ScheduleEntry, now: Instant): Instant? {
        val today = now.atZone(zone).toLocalDate()
        for (back in 0L..7L) {
            val date = today.minusDays(back)
            if (date.dayOfWeek !in days(e)) continue
            val at = toInstant(date, LocalTime.parse(e.startTime))
            if (at <= now) return at
        }
        return null
    }

    /** First occurrence strictly after now; used to set the next alarm. */
    fun nextOccurrence(e: ScheduleEntry, now: Instant): Instant? {
        val today = now.atZone(zone).toLocalDate()
        for (ahead in 0L..7L) {
            val date = today.plusDays(ahead)
            if (date.dayOfWeek !in days(e)) continue
            val at = toInstant(date, LocalTime.parse(e.startTime))
            if (at > now) return at
        }
        return null
    }

    /** Same id on every device: first 16 bytes of SHA-256("schedule:{entryId}:{yyyy-MM-dd}") as a UUID. */
    fun scheduledSessionId(e: ScheduleEntry, occurrence: Instant): String {
        val date = occurrence.atZone(zone).toLocalDate()
        val hash = MessageDigest.getInstance("SHA-256").digest("schedule:${e.id}:$date".toByteArray())
        val hex = hash.take(16).joinToString("") { "%02x".format(it) }
        return "${hex.substring(0, 8)}-${hex.substring(8, 12)}-${hex.substring(12, 16)}-${hex.substring(16, 20)}-${hex.substring(20)}"
    }

    private fun days(e: ScheduleEntry) = e.daysOfWeek.map { DayOfWeek.valueOf(it.uppercase()) }.toSet()

    // A start inside a DST gap moves forward by the gap, like the PC.
    private fun toInstant(date: LocalDate, time: LocalTime) = ZonedDateTime.of(date, time, zone).toInstant()

    private fun newSession(id: String, a: Activity, at: Instant, origin: String) = Session(
        id = id, activityId = a.id, startedAt = at.toString(), minEndsAt = (at + minDuration(a)).toString(),
        origin = origin, deviceId = deviceId, rulesSnapshot = a,
    )

    companion object {
        fun stateOf(current: Session?, now: Instant): RuleState = when {
            current == null || current.endedAt != null -> RuleState.FREE
            now < ts(current.minEndsAt) -> RuleState.LOCKED
            else -> RuleState.COMPLETABLE
        }

        /** Recovery key: ends the running session now, even before its minimum. */
        fun recover(current: Session, now: Instant): Session =
            if (current.endedAt == null) current.copy(endedAt = now.toString(), recovered = true)
            else throw RuleException("Não há atividade em andamento.")

        fun end(current: Session, now: Instant): Session = when (stateOf(current, now)) {
            RuleState.COMPLETABLE -> current.copy(endedAt = now.toString())
            RuleState.LOCKED -> throw RuleException("Ainda não atingiu o tempo mínimo.")
            RuleState.FREE -> throw RuleException("Sessão já encerrada.")
        }
    }
}

/** Which foreground app gets the block screen during an activity. */
object Blocking {
    fun shouldBlock(pkg: String, allowed: Set<String>, exempt: Set<String>): Boolean = pkg !in allowed && pkg !in exempt
}
