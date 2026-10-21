package app.rotina

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class SyncTest {
    @Test fun `merge replaces by id and keeps local order`() {
        val local = listOf("a" to 1, "b" to 1)
        val merged = mergeById(local, listOf("b" to 2, "c" to 2)) { it.first }
        assertEquals(listOf("a" to 1, "b" to 2, "c" to 2), merged)
    }

    @Test fun `pairing code gives the PC address and token`() {
        assertEquals("http://192.168.0.10:47800/api/v1/" to "abc123",
            parsePairing(" rotina://pair?h=192.168.0.10&p=47800&t=abc123 "))
        assertNull(parsePairing("https://example.com"))
        assertNull(parsePairing("rotina://pair?h=192.168.0.10&p=47800"))
        assertNull(parsePairing("rotina://pair?h=192.168.0.10&p=999999&t=x"))
    }
}
