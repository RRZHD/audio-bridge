package com.audiobridge.ui

import android.Manifest
import android.bluetooth.BluetoothAdapter
import android.bluetooth.BluetoothDevice
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.core.content.ContextCompat
import com.audiobridge.AudioService
import com.audiobridge.BuildConfig
import com.audiobridge.PlayoutBuffer
import com.audiobridge.Settings
import com.audiobridge.UpdateChecker
import com.audiobridge.UpdateInfo
import com.audiobridge.UpdateInstaller
import com.audiobridge.transport.DiscoveredServer
import com.audiobridge.transport.Discovery
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

enum class Mode { WIFI, USB, BLE, BLUETOOTH }

/** Что показывает главная кнопка. */
enum class Link { OFF, CONNECTING, ON }

/**
 * Состояние приложения: настройки, режим, статус, найденные ПК. Экраны только рисуют его и зовут
 * connect()/disconnect() — вся логика запуска сервиса и разрешений здесь, в одном месте.
 */
class AppState(
    private val context: Context,
    val settings: Settings,
    private val scope: CoroutineScope,
    private val askPermissions: (Array<String>) -> Unit,
) {
    var mode by mutableStateOf(runCatching { Mode.valueOf(settings.mode) }.getOrDefault(Mode.BLE))
    var host by mutableStateOf(settings.host)
    var port by mutableStateOf(settings.port.toString())
    var btAddress by mutableStateOf(settings.btAddress)

    var themeId by mutableStateOf(settings.themeId)
    var micEnabled by mutableStateOf(settings.micEnabled)
    var autoConnect by mutableStateOf(settings.autoConnect)
    var profile by mutableIntStateOf(settings.profile)
    var wifiQuality by mutableIntStateOf(settings.wifiQuality)
    var maxRate by mutableIntStateOf(settings.maxRawSampleRate)
    var pcBitrate by mutableFloatStateOf(settings.pcBitrateBps / 1000f)
    var micBitrate by mutableFloatStateOf(settings.micBitrateBps / 1000f)
    var bufferMs by mutableFloatStateOf(settings.bufferMs.toFloat())

    var status by mutableStateOf("Отключено")
    var logLines by mutableStateOf(listOf<String>())

    var discovered by mutableStateOf(listOf<DiscoveredServer>())
    var discovering by mutableStateOf(false)
    var pairedDevices by mutableStateOf(listOf<BluetoothDevice>())

    var updateInfo by mutableStateOf<UpdateInfo?>(null)
    var updateDismissed by mutableStateOf(false)
    var downloadingUpdate by mutableStateOf(false)

    val link: Link
        get() = when {
            status.startsWith("Подключено") -> Link.ON
            status.startsWith("Подключ") || status.startsWith("Переподключ") -> Link.CONNECTING
            else -> Link.OFF
        }

    /** Последняя строка лога с ошибкой — подсказка, почему соединение не поднимается. */
    val lastProblem: String?
        get() = logLines.lastOrNull { it.startsWith("Ошибка") || it.contains("не подключился") || it.contains("не отвечает") }
            ?.removePrefix("Ошибка соединения: ")?.removePrefix("java.io.IOException: ")

    // ---- профили -------------------------------------------------------------------------------

    data class Profile(val id: Int, val title: String, val hint: String, val bufferMs: Int, val wifiQuality: Int, val pcBitrateK: Int)

    val profiles = listOf(
        Profile(0, "Баланс", "Рекомендуется: без сжатия, пока сеть тянет, и мягкий запас против рывков", 120, 0, 192),
        Profile(1, "Малая задержка", "Минимальный запас: быстрее реакция, но чувствительнее к плохой сети", 60, 1, 160),
        Profile(2, "Стабильность", "Большой запас и сжатие: звук не рвётся даже на слабом Wi-Fi, задержка выше", 300, 2, 128),
    )

    fun applyProfile(id: Int) {
        val p = profiles.firstOrNull { it.id == id } ?: return
        profile = id
        settings.profile = id
        bufferMs = p.bufferMs.toFloat(); settings.bufferMs = p.bufferMs
        wifiQuality = p.wifiQuality; settings.wifiQuality = p.wifiQuality
        pcBitrate = p.pcBitrateK.toFloat(); settings.pcBitrateBps = p.pcBitrateK * 1000
    }

    /** Ручная правка параметра переводит профиль в «свой». */
    private fun custom() { profile = 3; settings.profile = 3 }

    fun updateBuffer(v: Float) { bufferMs = v; settings.bufferMs = v.toInt(); custom() }
    fun updatePcBitrate(v: Float) { pcBitrate = v; settings.pcBitrateBps = (v * 1000).toInt(); custom() }
    fun updateMicBitrate(v: Float) { micBitrate = v; settings.micBitrateBps = (v * 1000).toInt() }
    fun updateWifiQuality(v: Int) { wifiQuality = v; settings.wifiQuality = v; custom() }
    fun updateMaxRate(v: Int) { maxRate = v; settings.maxRawSampleRate = v }
    fun updateTheme(id: String) { themeId = id; settings.themeId = id }
    fun updateMic(on: Boolean) {
        micEnabled = on
        settings.micEnabled = on
        // A running session picks the switch up at once (no reconnect needed).
        if (link != Link.OFF) {
            context.startService(Intent(context, AudioService::class.java).apply {
                action = AudioService.ACTION_MIC
                putExtra(AudioService.EXTRA_MIC_ON, on)
            })
        }
    }
    fun updateAutoConnect(on: Boolean) { autoConnect = on; settings.autoConnect = on }

    fun selectMode(m: Mode) {
        mode = m
        settings.mode = m.name
        if (m == Mode.WIFI && discovered.isEmpty() && !discovering) runDiscovery()
        if (m == Mode.BLUETOOTH && pairedDevices.isEmpty()) refreshPairedDevices()
    }

    // ---- подключение ---------------------------------------------------------------------------

    private val requiredPermissions: Array<String>
        get() {
            val perms = mutableListOf(Manifest.permission.RECORD_AUDIO)
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
                perms.add(Manifest.permission.BLUETOOTH_CONNECT)
                perms.add(Manifest.permission.BLUETOOTH_ADVERTISE)
            }
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) perms.add(Manifest.permission.POST_NOTIFICATIONS)
            return perms.toTypedArray()
        }

    fun connect() {
        settings.mode = mode.name
        settings.host = host
        settings.port = port.toIntOrNull() ?: 45121
        settings.btAddress = btAddress
        val missing = requiredPermissions.filter {
            ContextCompat.checkSelfPermission(context, it) != PackageManager.PERMISSION_GRANTED
        }
        if (missing.isEmpty()) start() else askPermissions(missing.toTypedArray())
    }

    fun onPermissionsResult(allGranted: Boolean) {
        if (!allGranted) {
            status = "Нет разрешений (микрофон/Bluetooth/уведомления)"
            return
        }
        // Starting the foreground service synchronously in the permission callback can race with
        // ActivityManagerService's permission cache right after a fresh grant; a beat on the main
        // looper lets it settle.
        android.os.Handler(android.os.Looper.getMainLooper()).postDelayed({ start() }, 300)
    }

    private fun start() {
        val intent = Intent(context, AudioService::class.java).apply {
            action = AudioService.ACTION_START
            putExtra(AudioService.EXTRA_MODE, mode.name)
            putExtra(AudioService.EXTRA_PORT, port.toIntOrNull() ?: 45121)
            putExtra(AudioService.EXTRA_PC_BITRATE, (pcBitrate * 1000).toInt())
            putExtra(AudioService.EXTRA_MIC_BITRATE, (micBitrate * 1000).toInt())
            putExtra(AudioService.EXTRA_BUFFER_MS, bufferMs.toInt())
            if (mode == Mode.WIFI) putExtra(AudioService.EXTRA_HOST, host)
            if (mode == Mode.BLUETOOTH) putExtra(AudioService.EXTRA_BT_ADDRESS, btAddress)
        }
        ContextCompat.startForegroundService(context, intent)
    }

    fun disconnect() {
        context.startService(Intent(context, AudioService::class.java).apply { action = AudioService.ACTION_STOP })
    }

    fun toggle() { if (link == Link.OFF) connect() else disconnect() }

    // ---- поиск ---------------------------------------------------------------------------------

    fun refreshPairedDevices() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S &&
            ContextCompat.checkSelfPermission(context, Manifest.permission.BLUETOOTH_CONNECT) != PackageManager.PERMISSION_GRANTED
        ) {
            askPermissions(arrayOf(Manifest.permission.BLUETOOTH_CONNECT))
            return
        }
        @Suppress("MissingPermission")
        pairedDevices = BluetoothAdapter.getDefaultAdapter()?.bondedDevices?.toList() ?: emptyList()
    }

    fun runDiscovery() {
        if (discovering) return
        discovering = true
        scope.launch {
            val found = withContext(Dispatchers.IO) { Discovery.discover(context) }
            discovered = found
            discovering = false
            // One server in the network and nothing chosen yet: just take it.
            if (found.size == 1 && (host.isBlank() || found.none { it.host == host })) pick(found[0])
        }
    }

    fun pick(server: DiscoveredServer) {
        host = server.host
        port = server.port.toString()
    }

    // ---- обновление ----------------------------------------------------------------------------

    fun checkUpdate() {
        scope.launch {
            val info = withContext(Dispatchers.IO) { UpdateChecker.checkForUpdate(BuildConfig.VERSION_NAME) }
            if (info != null) updateInfo = info
        }
    }

    fun installUpdate(info: UpdateInfo) {
        if (UpdateInstaller.canInstallPackages(context)) {
            downloadingUpdate = true
            UpdateInstaller.downloadAndInstall(context, info.apkUrl, info.version)
        } else {
            context.startActivity(UpdateInstaller.installPermissionSettingsIntent(context))
        }
    }

    companion object {
        const val MIN_BUFFER = PlayoutBuffer.MIN_TARGET_MS.toFloat()
    }
}
