package app.rotina

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import java.time.Instant

class RecoveryTest {
    @Test fun `hash matches the PC`() {
        // Same vector as Rotina.Tests (C#).
        assertEquals("DA4E7EA9782C8CB109255854F713C0F231925C812491D35BC39200CFA5C39D0D", Recovery.hash("abcde-fghjk lmnpq-rstuv"))
        assertTrue(Recovery.matches("ABCDE-FGHJK-LMNPQ-RSTUV", Recovery.hash("abcdefghjklmnpqrstuv")))
        assertFalse(Recovery.matches("ABCDE", null))
    }

    @Test fun `recover ends before the minimum`() {
        val a = Activity(id = "a", name = "Estudo", minDurationMinutes = 60)
        val s = Session("s", "a", "2026-10-19T08:00:00Z", "2026-10-19T09:00:00Z", origin = "manual", deviceId = "p", rulesSnapshot = a)
        val now = Instant.parse("2026-10-19T08:05:00Z")
        val r = Engine.recover(s, now)
        assertTrue(r.recovered)
        assertEquals(RuleState.FREE, Engine.stateOf(r, now))
    }
}
