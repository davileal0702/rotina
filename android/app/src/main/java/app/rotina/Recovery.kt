package app.rotina

import java.security.MessageDigest

/** Same hash as windows/Rotina.Core/Recovery.cs, so the PC's key works here offline. */
object Recovery {
    /** Upper case, letters and digits only: "abcde-fghjk" and "ABCDE FGHJK" are the same key. */
    fun normalize(key: String) = key.uppercase().filter { it in 'A'..'Z' || it in '0'..'9' }

    fun hash(key: String): String =
        MessageDigest.getInstance("SHA-256").digest(normalize(key).toByteArray()).joinToString("") { "%02X".format(it) }

    fun matches(key: String, hash: String?): Boolean =
        hash != null && MessageDigest.isEqual(hash(key).toByteArray(), hash.toByteArray())
}
