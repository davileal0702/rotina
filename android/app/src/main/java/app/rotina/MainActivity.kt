package app.rotina

import android.Manifest
import android.accessibilityservice.AccessibilityServiceInfo
import android.app.AlarmManager
import android.app.NotificationManager
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.os.PowerManager
import android.provider.Settings
import android.view.accessibility.AccessibilityManager
import androidx.activity.ComponentActivity
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.NavigationBar
import androidx.compose.material3.NavigationBarItem
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.graphics.vector.PathParser
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.StrokeJoin
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.semantics.heading
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.time.Duration
import java.time.Instant
import java.time.ZoneId

class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        Repo.init(this)
        enableEdgeToEdge() // dark status bar icons on the light background
        setContent { RotinaTheme { MainScreen() } }
    }

    override fun onResume() {
        super.onResume()
        Scheduler.sync(this) // in the foreground the session notification is always allowed to start
    }
}

private enum class Tab(val label: String, val path: String) {
    Home("Início", "M3,10 L12,3 L21,10 V21 H15 V14 H9 V21 H3 Z"),
    Start("Iniciar", "M8,5 L19,12 L8,19 Z"),
    Setup("Configuração", "M12,3 L20,6 V12 C20,16.5 16.5,19.5 12,21 C7.5,19.5 4,16.5 4,12 V6 Z"),
}

// Line icons drawn from SVG paths (no emoji, no extra icon library).
private fun icon(path: String) = ImageVector.Builder(defaultWidth = 24.dp, defaultHeight = 24.dp, viewportWidth = 24f, viewportHeight = 24f)
    .addPath(PathParser().parsePathString(path).toNodes(), stroke = SolidColor(Ink), strokeLineWidth = 2f,
        strokeLineCap = StrokeCap.Round, strokeLineJoin = StrokeJoin.Round)
    .build()

@Composable
private fun MainScreen() {
    var tab by remember { mutableIntStateOf(0) }
    // Screens re-read Repo on every recomposition, and `now` ticks every second, so changes show up by themselves.
    val now = rememberNow()
    Scaffold(
        bottomBar = {
            NavigationBar {
                Tab.entries.forEachIndexed { i, t ->
                    NavigationBarItem(selected = tab == i, onClick = { tab = i },
                        icon = { Icon(icon(t.path), contentDescription = null) }, label = { Text(t.label) })
                }
            }
        },
    ) { pad ->
        Column(Modifier.padding(pad).fillMaxSize().padding(horizontal = 20.dp)) {
            when (Tab.entries[tab]) {
                Tab.Home -> HomeScreen(now)
                Tab.Start -> StartScreen(now) { tab = 0 }
                Tab.Setup -> SetupScreen(now)
            }
        }
    }
}

@Composable
private fun Title(text: String) =
    Text(text, fontSize = 26.sp, fontWeight = FontWeight.SemiBold, modifier = Modifier.padding(top = 24.dp, bottom = 16.dp).semantics { heading() })

@Composable
private fun HomeScreen(now: Instant) {
    val context = LocalContext.current
    val cur = Repo.current()
    val state = Engine.stateOf(cur, now)
    var error by remember { mutableStateOf<String?>(null) }
    Column(Modifier.verticalScroll(rememberScrollState())) {
        Title("Início")
        Card(colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surface), modifier = Modifier.fillMaxWidth()) {
            Column(Modifier.padding(20.dp)) {
                Text(if (cur == null) "AGORA" else "EM ANDAMENTO", fontSize = 12.sp, color = Muted)
                Row(verticalAlignment = Alignment.CenterVertically) {
                    if (cur != null) { Dot(cur.rulesSnapshot.color); Spacer(Modifier.width(8.dp)) }
                    Text(cur?.rulesSnapshot?.name ?: "Livre", fontSize = 22.sp, fontWeight = FontWeight.SemiBold)
                }
                when (state) {
                    RuleState.FREE -> Text("Nenhuma atividade em andamento. Tudo está liberado.", color = Muted)
                    RuleState.LOCKED -> {
                        Text(clock(Duration.between(now, ts(cur!!.minEndsAt)).seconds), fontSize = 44.sp, fontWeight = FontWeight.SemiBold, color = Teal)
                        Text("para poder encerrar", color = Muted)
                    }
                    RuleState.COMPLETABLE -> Text("Tempo mínimo cumprido. Você já pode encerrar.", color = Muted)
                }
                if (state == RuleState.COMPLETABLE) {
                    Spacer(Modifier.height(16.dp))
                    Button(onClick = {
                        runCatching { Repo.end() }.onFailure { error = it.message }
                        Scheduler.sync(context)
                    }, modifier = Modifier.fillMaxWidth().heightIn(min = 48.dp)) { Text("Encerrar atividade") }
                }
                error?.let { Text(it, color = MaterialTheme.colorScheme.error) }
            }
        }
        if (Repo.data.development) Text("Modo desenvolvimento: os tempos mínimos contam em segundos.", fontSize = 12.sp, color = Muted, modifier = Modifier.padding(top = 8.dp))

        Text("Hoje", fontSize = 18.sp, fontWeight = FontWeight.SemiBold, modifier = Modifier.padding(top = 28.dp, bottom = 8.dp).semantics { heading() })
        val today = now.atZone(ZoneId.of(Repo.data.zoneId)).dayOfWeek.name.lowercase()
        val acts = Repo.activities()
        val items = Repo.data.schedule.filter { it.enabled && !it.deleted && today in it.daysOfWeek && acts[it.activityId]?.deleted == false }
            .sortedBy { it.startTime }
        if (items.isEmpty()) Text("Nada agendado para hoje.", color = Muted)
        items.forEach { e ->
            val a = acts.getValue(e.activityId)
            Row(Modifier.padding(vertical = 8.dp), verticalAlignment = Alignment.CenterVertically) {
                Text(e.startTime.take(5), fontWeight = FontWeight.SemiBold, modifier = Modifier.width(64.dp))
                Dot(a.color, 10); Spacer(Modifier.width(10.dp)); Text(a.name)
            }
        }
    }
}

@Composable
private fun StartScreen(now: Instant, started: () -> Unit) {
    val context = LocalContext.current
    val locked = Repo.state(now) == RuleState.LOCKED
    val acts = Repo.data.activities.filterNot { it.deleted }.sortedBy { it.name }
    Title("Iniciar atividade")
    if (locked) Text("Disponível quando a atividade atual atingir o tempo mínimo.", color = Muted, modifier = Modifier.padding(bottom = 12.dp))
    if (acts.isEmpty()) Text("Nenhuma atividade ainda. Importe do PC em Configuração.", color = Muted)
    LazyColumn(verticalArrangement = Arrangement.spacedBy(10.dp)) {
        items(acts, key = { it.id }) { a ->
            Card(
                modifier = Modifier.fillMaxWidth().heightIn(min = 64.dp)
                    .clickable(enabled = !locked, onClickLabel = "Iniciar ${a.name}") {
                        Repo.startManual(a.id); Scheduler.sync(context); started()
                    },
                colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surface),
            ) {
                Row(Modifier.padding(16.dp), verticalAlignment = Alignment.CenterVertically) {
                    Dot(a.color); Spacer(Modifier.width(12.dp))
                    Column {
                        Text(a.name, fontWeight = FontWeight.SemiBold, color = if (locked) Muted else Ink)
                        Text("mínimo de ${a.minDurationMinutes} min", fontSize = 13.sp, color = Muted)
                    }
                }
            }
        }
    }
}

@Composable
private fun SetupScreen(now: Instant) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    var url by remember { mutableStateOf(Repo.data.pcBaseUrl) }
    var status by remember { mutableStateOf("") }
    val askNotifications = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { }
    now.let { } // reading `now` re-checks the permission states every second
    Column(Modifier.verticalScroll(rememberScrollState())) {
        Title("Configuração")
        Text("Computador", fontSize = 18.sp, fontWeight = FontWeight.SemiBold, modifier = Modifier.semantics { heading() })
        Text("Por enquanto o celular só copia as atividades e a agenda do PC. O pareamento por QR code chega na sincronização.",
            color = Muted, fontSize = 14.sp, modifier = Modifier.padding(vertical = 6.dp))
        OutlinedTextField(url, { url = it }, label = { Text("Endereço do serviço no PC") }, singleLine = true, modifier = Modifier.fillMaxWidth())
        Spacer(Modifier.height(8.dp))
        Button(onClick = {
            Repo.update { it.copy(pcBaseUrl = url.trim()) }
            status = "Importando..."
            scope.launch {
                status = withContext(Dispatchers.IO) { runCatching { Repo.importFromPc() }.getOrElse { "Não foi possível falar com o PC: ${it.message}" } }
                Scheduler.sync(context)
            }
        }, modifier = Modifier.heightIn(min = 48.dp)) { Text("Importar do PC") }
        if (status.isNotEmpty()) Text(status, color = Muted, modifier = Modifier.padding(top = 6.dp))

        Text("Permissões", fontSize = 18.sp, fontWeight = FontWeight.SemiBold, modifier = Modifier.padding(top = 28.dp, bottom = 4.dp).semantics { heading() })
        if (Build.VERSION.SDK_INT >= 33) {
            Text("Antes de ativar a acessibilidade: Informações do app, menu ⋮, \"Permitir configurações restritas\".",
                color = Muted, fontSize = 14.sp)
            TextButton(onClick = { context.startActivity(Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS, Uri.parse("package:${context.packageName}"))) }) {
                Text("Abrir informações do app")
            }
        }
        Permission("Acessibilidade (bloqueio de apps)", accessibilityOn(context)) {
            context.startActivity(Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS))
        }
        Permission("Notificações", context.getSystemService(NotificationManager::class.java).areNotificationsEnabled()) {
            if (Build.VERSION.SDK_INT >= 33) askNotifications.launch(Manifest.permission.POST_NOTIFICATIONS)
            else context.startActivity(Intent(Settings.ACTION_APP_NOTIFICATION_SETTINGS).putExtra(Settings.EXTRA_APP_PACKAGE, context.packageName))
        }
        Permission("Alarmes no horário exato", Build.VERSION.SDK_INT < 31 || context.getSystemService(AlarmManager::class.java).canScheduleExactAlarms()) {
            if (Build.VERSION.SDK_INT >= 31) context.startActivity(Intent(Settings.ACTION_REQUEST_SCHEDULE_EXACT_ALARM, Uri.parse("package:${context.packageName}")))
        }
        Permission("Sem limite de bateria", context.getSystemService(PowerManager::class.java).isIgnoringBatteryOptimizations(context.packageName)) {
            context.startActivity(Intent(Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS, Uri.parse("package:${context.packageName}")))
        }
        Spacer(Modifier.height(24.dp))
    }
}

@Composable
private fun Permission(label: String, granted: Boolean, fix: () -> Unit) {
    Row(Modifier.fillMaxWidth().heightIn(min = 56.dp), verticalAlignment = Alignment.CenterVertically) {
        Column(Modifier.weight(1f)) {
            Text(label)
            Text(if (granted) "Ativado" else "Desativado", fontSize = 13.sp, color = if (granted) Teal else Accent)
        }
        if (!granted) OutlinedButton(onClick = fix) { Text("Ativar") }
    }
}

private fun accessibilityOn(context: Context): Boolean {
    val me = ComponentName(context, BlockerService::class.java)
    return context.getSystemService(AccessibilityManager::class.java)
        .getEnabledAccessibilityServiceList(AccessibilityServiceInfo.FEEDBACK_ALL_MASK)
        .any { ComponentName.unflattenFromString(it.id) == me }
}
