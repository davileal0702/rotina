package app.rotina

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.unit.dp
import kotlinx.coroutines.delay
import java.time.Instant

// Same palette as the Windows app (ui-ux-pro-max design system).
val Teal = Color(0xFF0F766E)
val TealSoft = Color(0xFFCCFBF1)
val Ink = Color(0xFF134E4A)
val Muted = Color(0xFF475569)
val Accent = Color(0xFFC2410C)

@Composable
fun RotinaTheme(content: @Composable () -> Unit) = MaterialTheme(
    colorScheme = lightColorScheme(
        primary = Teal, onPrimary = Color.White, primaryContainer = TealSoft, onPrimaryContainer = Ink,
        // Navigation bar and its selected pill use these; Material's defaults are lilac.
        secondaryContainer = TealSoft, onSecondaryContainer = Ink, surfaceContainer = Color(0xFFE8F3F1),
        background = Color(0xFFF4FBFA), surface = Color.White, onSurface = Ink, onSurfaceVariant = Muted,
        error = Color(0xFFB91C1C),
    ),
    content = content,
)

/** Current time, ticking every second, so countdowns and state follow the clock. */
@Composable
fun rememberNow(): Instant {
    var now by remember { mutableStateOf(Repo.now()) }
    LaunchedEffect(Unit) { while (true) { delay(1000); now = Repo.now() } }
    return now
}

fun clock(seconds: Long): String {
    val s = seconds.coerceAtLeast(0)
    return if (s >= 3600) "%d:%02d:%02d".format(s / 3600, s / 60 % 60, s % 60) else "%02d:%02d".format(s / 60, s % 60)
}

fun color(hex: String): Color = runCatching { Color(android.graphics.Color.parseColor(hex)) }.getOrDefault(Teal)

@Composable
fun Dot(hex: String, size: Int = 12) = Box(Modifier.size(size.dp).background(color(hex), CircleShape))
