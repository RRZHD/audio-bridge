package com.audiobridge

import android.content.Intent
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.animation.AnimatedContent
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.slideInHorizontally
import androidx.compose.animation.slideOutHorizontally
import androidx.compose.animation.togetherWith
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.material3.Surface
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import com.audiobridge.ui.AppState
import com.audiobridge.ui.HomeScreen
import com.audiobridge.ui.Link
import com.audiobridge.ui.Mode
import com.audiobridge.ui.SettingsScreen
import com.audiobridge.ui.theme.AudioBridgeTheme
import com.audiobridge.ui.theme.LocalPalette

class MainActivity : ComponentActivity() {

    private lateinit var settings: Settings

    /** Растёт при каждом подключении ПК как USB-аксессуара и при запуске из плитки: экран реагирует и подключается. */
    private val connectRequests = mutableIntStateOf(0)

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        settings = Settings(this)
        noteLaunchIntent(intent)

        setContent {
            // Тема читается из настроек на каждый кадр: смена в «Настройках» применяется сразу.
            var themeId by remember { mutableStateOf(settings.themeId) }
            AudioBridgeTheme(themeId = themeId) {
                Surface(modifier = androidx.compose.ui.Modifier.fillMaxSize(), color = LocalPalette.current.bg) {
                    AppRoot(settings = settings, connectRequests = connectRequests.intValue, onThemeChanged = { themeId = it })
                }
            }
        }
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        setIntent(intent)
        noteLaunchIntent(intent)
    }

    // PC switched the phone into accessory mode and Android launched us (USB), or the Quick Settings
    // tile asked to start: plug-and-play, connect without a tap.
    private fun noteLaunchIntent(intent: Intent?) {
        when {
            intent?.action == android.hardware.usb.UsbManager.ACTION_USB_ACCESSORY_ATTACHED -> {
                settings.mode = Mode.USB.name
                connectRequests.intValue++
            }
            intent?.getBooleanExtra(EXTRA_CONNECT, false) == true -> {
                intent.removeExtra(EXTRA_CONNECT)
                connectRequests.intValue++
            }
        }
    }

    companion object {
        /** Запуск из плитки быстрых настроек: открыть приложение и сразу подключить. */
        const val EXTRA_CONNECT = "com.audiobridge.extra.CONNECT"
    }
}

private enum class Screen { HOME, SETTINGS }

@Composable
private fun AppRoot(settings: Settings, connectRequests: Int, onThemeChanged: (String) -> Unit) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    var screen by remember { mutableStateOf(Screen.HOME) }

    // The state needs the permission launcher, the launcher needs the state: tie them with a holder.
    var holder by remember { mutableStateOf<AppState?>(null) }
    val permissionLauncher = rememberLauncherForActivityResult(ActivityResultContracts.RequestMultiplePermissions()) { results ->
        holder?.onPermissionsResult(results.values.all { it })
    }
    val state = remember {
        AppState(context, settings, scope, askPermissions = { permissionLauncher.launch(it) }).also { holder = it }
    }

    DisposableEffect(Unit) {
        StatusBus.statusListener = { state.status = it }
        StatusBus.logListener = { line -> state.logLines = (state.logLines + line).takeLast(60) }
        onDispose {
            StatusBus.statusListener = null
            StatusBus.logListener = null
        }
    }

    // The service may already be running (the app was closed and reopened): take its status.
    LaunchedEffect(Unit) {
        if (AudioService.active && state.status == "Отключено") state.status = AudioService.lastStatus
        state.checkUpdate()
        if (state.mode == Mode.WIFI) state.runDiscovery()
    }

    // Auto-connect on launch (once) with the last mode, if the user switched it on.
    LaunchedEffect(Unit) {
        if (state.autoConnect && state.link == Link.OFF && !AudioService.active) state.connect()
    }

    // USB attach / Quick Settings tile: switch mode if needed and connect (only if idle).
    LaunchedEffect(connectRequests) {
        if (connectRequests > 0) {
            state.mode = runCatching { Mode.valueOf(settings.mode) }.getOrDefault(state.mode)
            if (state.link == Link.OFF) state.connect()
        }
    }

    AnimatedContent(
        targetState = screen,
        transitionSpec = {
            if (targetState == Screen.SETTINGS) {
                (slideInHorizontally { it / 4 } + fadeIn()) togetherWith (fadeOut())
            } else {
                fadeIn() togetherWith (slideOutHorizontally { it / 4 } + fadeOut())
            }
        },
        label = "screen",
    ) { s ->
        when (s) {
            Screen.HOME -> HomeScreen(state, onOpenSettings = { screen = Screen.SETTINGS })
            Screen.SETTINGS -> SettingsScreen(state, onBack = { screen = Screen.HOME })
        }
    }

    // Theme chosen in settings: tell the activity (it owns the composition-wide theme).
    LaunchedEffect(state.themeId) { onThemeChanged(state.themeId) }
}
