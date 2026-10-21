package app.rotina

import org.junit.Assert.assertEquals
import org.junit.Test

class ClockTest {
    private val anchor = ClockAnchor(boot = 7, wall = 1_000_000L, elapsed = 50_000L)

    @Test fun `normal time passes through`() =
        assertEquals(1_060_000L, trustedMillis(wall = 1_060_000L, elapsed = 110_000L, boot = 7, anchor = anchor))

    @Test fun `clock pushed forward is ignored`() =
        // 1 minute of uptime passed, but the wall clock says 3 hours: trust the uptime.
        assertEquals(1_060_000L, trustedMillis(wall = 1_000_000L + 3 * 3_600_000L, elapsed = 110_000L, boot = 7, anchor = anchor))

    @Test fun `small corrections and moves back are accepted`() {
        assertEquals(1_120_000L, trustedMillis(wall = 1_120_000L, elapsed = 110_000L, boot = 7, anchor = anchor))
        assertEquals(900_000L, trustedMillis(wall = 900_000L, elapsed = 110_000L, boot = 7, anchor = anchor))
    }

    @Test fun `after a reboot the wall clock is used`() =
        assertEquals(5_000_000L, trustedMillis(wall = 5_000_000L, elapsed = 10_000L, boot = 8, anchor = anchor))
}
