package app.rotina

import kotlinx.serialization.Serializable

/** A moment when the wall clock was trusted, with the phone's boot number and its uptime clock at that moment. */
@Serializable
data class ClockAnchor(val boot: Int, val wall: Long, val elapsed: Long)

/**
 * The time to use for the rules: the wall clock, unless it jumped more than 2 minutes ahead of where the uptime
 * clock (which the user cannot change) says it should be since the anchor. Moving the clock forward to make the
 * minimum pass early therefore has no effect. Moving it back only makes the wait longer, so it is left alone.
 */
fun trustedMillis(wall: Long, elapsed: Long, boot: Int, anchor: ClockAnchor?): Long {
    if (anchor == null || anchor.boot != boot) return wall
    val expected = anchor.wall + (elapsed - anchor.elapsed)
    return if (wall - expected > 120_000) expected else wall
}
