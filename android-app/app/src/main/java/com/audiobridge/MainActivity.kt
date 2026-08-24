package com.audiobridge

import android.Manifest
import android.bluetooth.BluetoothAdapter
import android.bluetooth.BluetoothDevice
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.animation.AnimatedContent
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Bluetooth
import androidx.compose.material.icons.filled.Cable
import androidx.compose.material.icons.filled.PowerSettingsNew
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.Search
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material.icons.filled.Wifi
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import androidx.core.content.ContextCompat
import com.audiobridge.transport.DiscoveredServer
import com.audiobridge.transport.Discovery
import com.audiobridge.ui.theme.AudioBridgeTheme
import com.audiobridge.ui.theme.StatusAmber
import com.audiobridge.ui.theme.StatusGreen
import com.audiobridge.ui.theme.StatusRed
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

class MainActivity : ComponentActivity() {

    private lateinit var settings: Settings

    private val requiredPermissions: Array<String>
        get() {
            val perms = mutableListOf(Manifest.permission.RECORD_AUDIO)
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) perms.add(Manifest.permission.BLUETOOTH_CONNECT)
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) perms.add(Manifest.permission.POST_NOTIFICATIONS)
            return perms.toTypedArray()
        }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        settings = Settings(this)

        setContent {
            AudioBridgeTheme {
                Surface(color = MaterialTheme.colorScheme.background) {
                    AppRoot(settings = settings, requiredPermissions = requiredPermissions)
                }
            }
        }
    }
}

private enum class Mode { WIFI, USB, BLUETOOTH }

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun AppRoot(settings: Settings, requiredPermissions: Array<String>) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()

    var mode by remember { mutableStateOf(runCatching { Mode.valueOf(settings.mode) }.getOrDefault(Mode.WIFI)) }
    var host by remember { mutableStateOf(settings.host) }
    var port by remember { mutableStateOf(settings.port.toString()) }
    var btAddress by remember { mutableStateOf(settings.btAddress) }
    var pcBitrate by remember { mutableFloatStateOf(settings.pcBitrateBps / 1000f) }
    var micBitrate by remember { mutableFloatStateOf(settings.micBitrateBps / 1000f) }

    var status by remember { mutableStateOf("Отключено") }
    var logLines by remember { mutableStateOf(listOf<String>()) }
    val connected = status.startsWith("Подключено")

    var discovered by remember { mutableStateOf(listOf<DiscoveredServer>()) }
    var discovering by remember { mutableStateOf(false) }

    var pairedDevices by remember { mutableStateOf(listOf<BluetoothDevice>()) }
    var showSettingsSheet by remember { mutableStateOf(false) }

    DisposableEffect(Unit) {
        StatusBus.statusListener = { status = it }
        StatusBus.logListener = { line -> logLines = (logLines + line).takeLast(30) }
        onDispose {
            StatusBus.statusListener = null
            StatusBus.logListener = null
        }
    }

    val permissionLauncher = rememberLauncherForActivityResult(ActivityResultContracts.RequestMultiplePermissions()) { results ->
        if (results.values.all { it }) {
            startBridge(context, mode, host, port.toIntOrNull() ?: 57120, btAddress, (pcBitrate * 1000).toInt(), (micBitrate * 1000).toInt())
        } else {
            status = "Нет разрешений (микрофон/Bluetooth/уведомления)"
        }
    }

    fun connect() {
        settings.mode = mode.name
        settings.host = host
        settings.port = port.toIntOrNull() ?: 57120
        settings.btAddress = btAddress
        val missing = requiredPermissions.filter {
            ContextCompat.checkSelfPermission(context, it) != PackageManager.PERMISSION_GRANTED
        }
        if (missing.isEmpty()) {
            startBridge(context, mode, host, port.toIntOrNull() ?: 57120, btAddress, (pcBitrate * 1000).toInt(), (micBitrate * 1000).toInt())
        } else {
            permissionLauncher.launch(missing.toTypedArray())
        }
    }

    fun disconnect() {
        context.startService(Intent(context, AudioService::class.java).apply { action = AudioService.ACTION_STOP })
    }

    fun refreshPairedDevices() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S &&
            ContextCompat.checkSelfPermission(context, Manifest.permission.BLUETOOTH_CONNECT) != PackageManager.PERMISSION_GRANTED
        ) {
            permissionLauncher.launch(arrayOf(Manifest.permission.BLUETOOTH_CONNECT))
            return
        }
        @Suppress("MissingPermission")
        pairedDevices = BluetoothAdapter.getDefaultAdapter()?.bondedDevices?.toList() ?: emptyList()
    }

    fun runDiscovery() {
        discovering = true
        scope.launch {
            val found = withContext(Dispatchers.IO) { Discovery.discover(context) }
            discovered = found
            discovering = false
        }
    }

    LaunchedEffect(mode) {
        if (mode == Mode.BLUETOOTH && pairedDevices.isEmpty()) refreshPairedDevices()
        if (mode == Mode.WIFI && discovered.isEmpty() && !discovering) runDiscovery()
    }

    Scaffold(
        topBar = {
            CenterAlignedTopAppBar(
                title = { Text("Audio Bridge") },
                actions = {
                    IconButton(onClick = { showSettingsSheet = true }) {
                        Icon(Icons.Filled.Settings, contentDescription = "Настройки битрейта")
                    }
                },
            )
        },
    ) { padding ->
        Column(
            modifier = Modifier
                .padding(padding)
                .padding(horizontal = 16.dp)
                .verticalScroll(rememberScrollState()),
            verticalArrangement = Arrangement.spacedBy(16.dp),
        ) {
            Spacer(Modifier.height(4.dp))
            StatusCard(status = status, connected = connected)

            ModeSelector(mode = mode, enabled = !connected, onSelect = { mode = it })

            AnimatedContent(targetState = mode, label = "mode-fields") { m ->
                when (m) {
                    Mode.WIFI -> WifiSection(
                        host = host, onHostChange = { host = it },
                        port = port, onPortChange = { port = it },
                        discovered = discovered, discovering = discovering,
                        enabled = !connected,
                        onRefresh = ::runDiscovery,
                        onPick = { host = it.host; port = it.port.toString() },
                    )
                    Mode.USB -> UsbSection(port = port, onPortChange = { port = it }, enabled = !connected)
                    Mode.BLUETOOTH -> BluetoothSection(
                        devices = pairedDevices,
                        selectedAddress = btAddress,
                        onSelect = { btAddress = it },
                        onRefresh = ::refreshPairedDevices,
                        enabled = !connected,
                    )
                }
            }

            Button(
                onClick = { if (connected) disconnect() else connect() },
                modifier = Modifier.fillMaxWidth().height(52.dp),
                shape = RoundedCornerShape(14.dp),
                colors = if (connected) ButtonDefaults.buttonColors(containerColor = StatusRed) else ButtonDefaults.buttonColors(),
            ) {
                Icon(Icons.Filled.PowerSettingsNew, contentDescription = null)
                Spacer(Modifier.width(8.dp))
                Text(if (connected) "Отключиться" else "Подключиться", style = MaterialTheme.typography.titleMedium)
            }

            LogCard(logLines)
            Spacer(Modifier.height(8.dp))
        }
    }

    if (showSettingsSheet) {
        BitrateSettingsSheet(
            pcBitrate = pcBitrate,
            onPcBitrateChange = { pcBitrate = it; settings.pcBitrateBps = (it * 1000).toInt() },
            micBitrate = micBitrate,
            onMicBitrateChange = { micBitrate = it; settings.micBitrateBps = (it * 1000).toInt() },
            onDismiss = { showSettingsSheet = false },
        )
    }
}

private fun startBridge(
    context: android.content.Context,
    mode: Mode,
    host: String,
    port: Int,
    btAddress: String,
    pcBitrateBps: Int,
    micBitrateBps: Int,
) {
    val intent = Intent(context, AudioService::class.java).apply {
        action = AudioService.ACTION_START
        putExtra(AudioService.EXTRA_MODE, mode.name)
        putExtra(AudioService.EXTRA_PORT, port)
        putExtra(AudioService.EXTRA_PC_BITRATE, pcBitrateBps)
        putExtra(AudioService.EXTRA_MIC_BITRATE, micBitrateBps)
        if (mode == Mode.WIFI) putExtra(AudioService.EXTRA_HOST, host)
        if (mode == Mode.BLUETOOTH) putExtra(AudioService.EXTRA_BT_ADDRESS, btAddress)
    }
    ContextCompat.startForegroundService(context, intent)
}

@Composable
private fun StatusCard(status: String, connected: Boolean) {
    val dotColor = when {
        connected -> StatusGreen
        status.startsWith("Переподключение") || status.startsWith("Подключение") -> StatusAmber
        status.startsWith("Нет разрешений") || status.startsWith("Ошибка") -> StatusRed
        else -> MaterialTheme.colorScheme.outline
    }
    ElevatedCard(shape = RoundedCornerShape(18.dp), modifier = Modifier.fillMaxWidth()) {
        Row(
            modifier = Modifier.padding(18.dp).fillMaxWidth(),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Box(
                modifier = Modifier
                    .size(12.dp)
                    .clip(CircleShape)
                    .background(dotColor),
            )
            Spacer(Modifier.width(12.dp))
            Text(status, style = MaterialTheme.typography.titleMedium)
        }
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun ModeSelector(mode: Mode, enabled: Boolean, onSelect: (Mode) -> Unit) {
    val options = listOf(
        Triple(Mode.WIFI, "Wi-Fi", Icons.Filled.Wifi),
        Triple(Mode.USB, "USB", Icons.Filled.Cable),
        Triple(Mode.BLUETOOTH, "Bluetooth", Icons.Filled.Bluetooth),
    )
    SingleChoiceSegmentedButtonRow(modifier = Modifier.fillMaxWidth()) {
        options.forEachIndexed { index, (value, label, icon) ->
            SegmentedButton(
                selected = mode == value,
                onClick = { if (enabled) onSelect(value) },
                shape = SegmentedButtonDefaults.itemShape(index = index, count = options.size),
                enabled = enabled,
                icon = { Icon(icon, contentDescription = null, modifier = Modifier.size(18.dp)) },
            ) {
                Text(label)
            }
        }
    }
}

@Composable
private fun WifiSection(
    host: String, onHostChange: (String) -> Unit,
    port: String, onPortChange: (String) -> Unit,
    discovered: List<DiscoveredServer>, discovering: Boolean,
    enabled: Boolean,
    onRefresh: () -> Unit,
    onPick: (DiscoveredServer) -> Unit,
) {
    Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
        Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.SpaceBetween, modifier = Modifier.fillMaxWidth()) {
            Text("Серверы в сети", style = MaterialTheme.typography.titleMedium)
            TextButton(onClick = onRefresh, enabled = enabled && !discovering) {
                if (discovering) {
                    CircularProgressIndicator(modifier = Modifier.size(16.dp), strokeWidth = 2.dp)
                } else {
                    Icon(Icons.Filled.Search, contentDescription = null, modifier = Modifier.size(18.dp))
                }
                Spacer(Modifier.width(6.dp))
                Text(if (discovering) "Поиск..." else "Найти")
            }
        }

        if (discovered.isEmpty() && !discovering) {
            Text(
                "Ничего не найдено — убедись, что сервер запущен на ПК и телефон в той же Wi-Fi сети, или впиши IP вручную ниже.",
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }

        discovered.forEach { server ->
            val isSelected = server.host == host
            OutlinedCard(
                onClick = { if (enabled) onPick(server) },
                shape = RoundedCornerShape(14.dp),
                modifier = Modifier.fillMaxWidth(),
                colors = if (isSelected) CardDefaults.outlinedCardColors(containerColor = MaterialTheme.colorScheme.surfaceVariant) else CardDefaults.outlinedCardColors(),
            ) {
                Column(Modifier.padding(14.dp)) {
                    Text(server.name, style = MaterialTheme.typography.titleMedium)
                    Text("${server.host}:${server.port}", style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
                }
            }
        }

        OutlinedTextField(
            value = host, onValueChange = onHostChange,
            label = { Text("IP адрес ПК (вручную)") },
            placeholder = { Text("192.168.137.1") },
            enabled = enabled,
            singleLine = true,
            modifier = Modifier.fillMaxWidth(),
        )
        PortField(port, onPortChange, enabled)
    }
}

@Composable
private fun UsbSection(port: String, onPortChange: (String) -> Unit, enabled: Boolean) {
    Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
        Text(
            "Подключи кабель, включи USB-отладку и выполни в приложении на ПК \"adb reverse (USB)\". " +
                "Порт должен совпадать с тем, что указан на ПК.",
            style = MaterialTheme.typography.bodyMedium,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
        PortField(port, onPortChange, enabled)
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun BluetoothSection(
    devices: List<BluetoothDevice>,
    selectedAddress: String,
    onSelect: (String) -> Unit,
    onRefresh: () -> Unit,
    enabled: Boolean,
) {
    var expanded by remember { mutableStateOf(false) }
    @Suppress("MissingPermission")
    val selectedName = devices.firstOrNull { it.address == selectedAddress }?.let { "${it.name} (${it.address})" } ?: "Выбери устройство"

    Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
        Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.SpaceBetween, modifier = Modifier.fillMaxWidth()) {
            Text("Сопряжённое устройство (ПК)", style = MaterialTheme.typography.titleMedium)
            IconButton(onClick = onRefresh, enabled = enabled) {
                Icon(Icons.Filled.Refresh, contentDescription = "Обновить список")
            }
        }
        ExposedDropdownMenuBox(expanded = expanded, onExpandedChange = { if (enabled) expanded = it }) {
            OutlinedTextField(
                value = selectedName,
                onValueChange = {},
                readOnly = true,
                enabled = enabled,
                modifier = Modifier.fillMaxWidth().menuAnchor(),
                trailingIcon = { ExposedDropdownMenuDefaults.TrailingIcon(expanded = expanded) },
            )
            ExposedDropdownMenu(expanded = expanded, onDismissRequest = { expanded = false }) {
                if (devices.isEmpty()) {
                    DropdownMenuItem(text = { Text("Нет сопряжённых устройств") }, onClick = {}, enabled = false)
                }
                @Suppress("MissingPermission")
                devices.forEach { device ->
                    DropdownMenuItem(
                        text = { Text("${device.name} (${device.address})") },
                        onClick = { onSelect(device.address); expanded = false },
                    )
                }
            }
        }
        Text(
            "Сначала спарь ПК и телефон через настройки Bluetooth — это не A2DP-колонка, а отдельный канал приложения.",
            style = MaterialTheme.typography.bodyMedium,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
    }
}

@Composable
private fun PortField(port: String, onPortChange: (String) -> Unit, enabled: Boolean) {
    OutlinedTextField(
        value = port, onValueChange = { if (it.length <= 5 && it.all(Char::isDigit)) onPortChange(it) },
        label = { Text("Порт") },
        enabled = enabled,
        singleLine = true,
        keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
        modifier = Modifier.fillMaxWidth(0.5f),
    )
}

@Composable
private fun LogCard(lines: List<String>) {
    ElevatedCard(shape = RoundedCornerShape(14.dp), modifier = Modifier.fillMaxWidth()) {
        Column(Modifier.padding(14.dp).heightIn(max = 160.dp).verticalScroll(rememberScrollState())) {
            if (lines.isEmpty()) {
                Text("Лог пуст", style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
            lines.forEach {
                Text(it, style = MaterialTheme.typography.bodyMedium.copy(fontFamily = FontFamily.Monospace))
            }
        }
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun BitrateSettingsSheet(
    pcBitrate: Float, onPcBitrateChange: (Float) -> Unit,
    micBitrate: Float, onMicBitrateChange: (Float) -> Unit,
    onDismiss: () -> Unit,
) {
    ModalBottomSheet(onDismissRequest = onDismiss) {
        Column(Modifier.padding(horizontal = 20.dp).padding(bottom = 32.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
            Text("Качество звука (Bluetooth)", style = MaterialTheme.typography.titleLarge)
            Text(
                "Действует только для режима Bluetooth — Wi-Fi и USB всегда идут без сжатия.",
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
            Spacer(Modifier.height(12.dp))

            Text("Звук ПК → телефон: ${pcBitrate.toInt()} kbps", style = MaterialTheme.typography.titleMedium)
            Slider(value = pcBitrate, onValueChange = onPcBitrateChange, valueRange = 64f..320f, steps = 15)

            Spacer(Modifier.height(12.dp))

            Text("Микрофон → ПК: ${micBitrate.toInt()} kbps", style = MaterialTheme.typography.titleMedium)
            Slider(value = micBitrate, onValueChange = onMicBitrateChange, valueRange = 12f..64f, steps = 12)
        }
    }
}
