package app.rotina

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.time.Duration
import java.time.Instant
import java.time.ZoneOffset

class EngineTest {
    private val mon8 = Instant.parse("2026-10-19T08:00:00Z") // a Monday
    private val engine = Engine(ZoneOffset.UTC, Duration.ofMinutes(1), "phone")
    private val study = Activity(id = "a-study", name = "Estudo", minDurationMinutes = 30, updatedAt = "2026-10-18T08:00:00Z")
    private val gym = Activity(id = "a-gym", name = "Treino", minDurationMinutes = 60, updatedAt = "2026-10-18T08:00:00Z")
    private val acts = mapOf(study.id to study, gym.id to gym)

    private fun entry(a: Activity, time: String, id: String = "e-$time") =
        ScheduleEntry(id = id, activityId = a.id, daysOfWeek = listOf("monday"), startTime = time, updatedAt = "2026-10-18T08:00:00Z")

    private fun tick(cur: Session?, now: Instant, vararg e: ScheduleEntry) = engine.tick(cur, e.toList(), acts, { false }, now)
    private fun manual(a: Activity, at: Instant) = engine.startManual(null, a, at).started!!

    @Test fun `scheduled session id matches the PC`() {
        // Same vector as Rotina.Tests (C#): both devices must create the same session id.
        val e = entry(study, "08:00:00", id = "11111111-1111-1111-1111-111111111111")
        assertEquals("13f2ffe2-e261-482c-2e69-254023c1016b", engine.scheduledSessionId(e, mon8))
    }

    @Test fun `states follow the minimum`() {
        val s = manual(study, mon8)
        assertEquals(RuleState.LOCKED, Engine.stateOf(s, mon8.plusSeconds(29 * 60)))
        assertEquals(RuleState.COMPLETABLE, Engine.stateOf(s, mon8.plusSeconds(30 * 60)))
        assertEquals(RuleState.FREE, Engine.stateOf(null, mon8))
    }

    @Test(expected = RuleException::class) fun `end is refused before the minimum`() {
        Engine.end(manual(study, mon8), mon8.plusSeconds(60))
    }

    @Test fun `scheduled start happens at its time`() {
        val e = entry(study, "08:00:00")
        assertNull(tick(null, mon8.minusSeconds(1), e).started)
        val s = tick(null, mon8.plusSeconds(5), e).started!!
        assertEquals(mon8, ts(s.startedAt))
        assertEquals("schedule", s.origin)
    }

    @Test fun `missed start only counts while its minimum still runs`() {
        val e = entry(study, "08:00:00")
        assertTrue(tick(null, mon8.plusSeconds(29 * 60), e).started != null)
        assertNull(tick(null, mon8.plusSeconds(30 * 60), e).started)
    }

    @Test fun `scheduled start queues behind a locked session`() {
        val cur = manual(gym, mon8.minusSeconds(30 * 60)) // locked until 08:30
        val e = entry(study, "08:00:00")
        assertEquals(TickResult(null, null), tick(cur, mon8.plusSeconds(600), e))
        val r = tick(cur, mon8.plusSeconds(30 * 60), e)
        assertEquals(mon8.plusSeconds(30 * 60), ts(r.ended!!.endedAt!!))
        assertEquals(mon8.plusSeconds(30 * 60), ts(r.started!!.startedAt))
    }

    @Test fun `next occurrence is strictly after now`() {
        val e = entry(study, "08:00:00")
        assertEquals(mon8, engine.nextOccurrence(e, mon8.minusSeconds(1)))
        assertEquals(mon8.plusSeconds(7 * 86400), engine.nextOccurrence(e, mon8))
    }

    @Test fun `blocking spares allowed and exempt apps`() {
        assertTrue(Blocking.shouldBlock("com.instagram.android", setOf("com.spotify.music"), setOf("app.rotina")))
        assertFalse(Blocking.shouldBlock("com.spotify.music", setOf("com.spotify.music"), setOf("app.rotina")))
        assertFalse(Blocking.shouldBlock("app.rotina", emptySet(), setOf("app.rotina")))
    }
}
