package app.rotina

import android.content.Intent
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.BackHandler
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.Button
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.semantics.LiveRegionMode
import androidx.compose.ui.semantics.liveRegion
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import java.time.Duration

/** Full screen shown over an app that is not allowed in the running activity. */
class BlockActivity : ComponentActivity() {
    private var appLabel = ""

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        Repo.init(this)
        enableEdgeToEdge() // dark status bar icons on the light background
        readLabel(intent)
        setContent { RotinaTheme { BlockScreen(appLabel, ::goHome) } }
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        readLabel(intent)
        setContent { RotinaTheme { BlockScreen(appLabel, ::goHome) } }
    }

    private fun readLabel(intent: Intent) {
        val pkg = intent.getStringExtra(EXTRA_PACKAGE).orEmpty()
        appLabel = runCatching { packageManager.getApplicationLabel(packageManager.getApplicationInfo(pkg, 0)).toString() }
            .getOrDefault(pkg)
    }

    private fun goHome() {
        startActivity(Intent(Intent.ACTION_MAIN).addCategory(Intent.CATEGORY_HOME).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
        finish()
    }

    companion object { const val EXTRA_PACKAGE = "pkg" }
}

@androidx.compose.runtime.Composable
private fun BlockScreen(appLabel: String, goHome: () -> Unit) {
    BackHandler(onBack = goHome) // back would return to the blocked app
    val now = rememberNow()
    val cur = Repo.current()
    val state = Engine.stateOf(cur, now)
    if (state == RuleState.FREE) { goHome(); return }
    val left = Duration.between(now, ts(cur!!.minEndsAt)).seconds
    Surface(Modifier.fillMaxSize(), color = MaterialTheme.colorScheme.background) {
        Column(
            Modifier.fillMaxSize().padding(28.dp),
            verticalArrangement = Arrangement.Center,
            horizontalAlignment = Alignment.CenterHorizontally,
        ) {
            Dot(cur.rulesSnapshot.color, 20)
            Spacer(Modifier.height(16.dp))
            Text(
                "$appLabel não está liberado",
                fontSize = 24.sp, fontWeight = FontWeight.SemiBold, textAlign = TextAlign.Center,
                modifier = Modifier.semantics { liveRegion = LiveRegionMode.Polite },
            )
            Text("em ${cur.rulesSnapshot.name}", color = Muted, fontSize = 17.sp, textAlign = TextAlign.Center)
            Spacer(Modifier.height(24.dp))
            if (state == RuleState.LOCKED) {
                Text(clock(left), fontSize = 48.sp, fontWeight = FontWeight.SemiBold, color = Teal)
                Text("para poder encerrar", color = Muted)
            } else {
                Text("O tempo mínimo já passou. Você pode encerrar a atividade no Rotina.", color = Muted, textAlign = TextAlign.Center)
            }
            Spacer(Modifier.height(36.dp))
            Button(onClick = goHome, modifier = Modifier.fillMaxWidth().height(52.dp)) { Text("Ir para a tela inicial", fontSize = 16.sp) }
        }
    }
}
