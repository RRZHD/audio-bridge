package com.audiobridge.ui

import android.bluetooth.BluetoothDevice
import androidx.compose.animation.AnimatedContent
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.animateColorAsState
import androidx.compose.animation.core.RepeatMode
import androidx.compose.animation.core.animateFloat
import androidx.compose.animation.core.infiniteRepeatable
import androidx.compose.animation.core.rememberInfiniteTransition
import androidx.compose.animation.core.tween
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.expandVertically
import androidx.compose.animation.shrinkVertically
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Bluetooth
import androidx.compose.material.icons.filled.Cable
import androidx.compose.material.icons.filled.Check
import androidx.compose.material.icons.filled.GraphicEq
import androidx.compose.material.icons.filled.KeyboardArrowDown
import androidx.compose.material.icons.filled.KeyboardArrowUp
import androidx.compose.material.icons.filled.Mic
import androidx.compose.material.icons.filled.PowerSettingsNew
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material.icons.filled.SystemUpdate
import androidx.compose.material.icons.filled.Wifi
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.scale
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import com.audiobridge.transport.DiscoveredServer
import com.audiobridge.transport.UsbTransport
import com.audiobridge.ui.components.BtnKind
import com.audiobridge.ui.components.HSpace
import com.audiobridge.ui.components.MgrButton
import com.audiobridge.ui.components.MgrCard
import com.audiobridge.ui.components.MgrChip
import com.audiobridge.ui.components.MgrDivider
import com.audiobridge.ui.components.MgrNote
import com.audiobridge.ui.components.MgrPill
import com.audiobridge.ui.components.MgrSwitchRow
import com.audiobridge.ui.components.MgrTextField
import com.audiobridge.ui.components.SectionTitle
import com.audiobridge.ui.components.StatusDot
import com.audiobridge.ui.components.Tone
import com.audiobridge.ui.components.VSpace
import com.audiobridge.ui.theme.LocalPalette
import com.audiobridge.ui.theme.Radius
import kotlinx.coroutines.delay

/** Главный экран: одна большая кнопка «включить/выключить», выбор способа и минимум настроек под рукой. */
@Composable
fun HomeScreen(state: AppState, onOpenSettings: () -> Unit) {
    val p = LocalPalette.current
    BoxWithConstraints(Modifier.fillMaxSize().background(p.bg).statusBarsPadding().navigationBarsPadding()) {
        // Широкий экран (альбомная ориентация, планшет): слева кнопка и статус, справа всё остальное.
        val wide = maxWidth >= 600.dp
        if (wide) {
            Row(Modifier.padding(horizontal = 16.dp), horizontalArrangement = Arrangement.spacedBy(16.dp)) {
                Column(
                    Modifier.weight(0.42f).verticalScroll(rememberScrollState()),
                    verticalArrangement = Arrangement.spacedBy(12.dp),
                ) {
                    Header(state, onOpenSettings)
                    UpdateSlot(state)
                    HeroCard(state, compact = true)
                    VSpace(8.dp)
                }
                Column(
                    Modifier.weight(0.58f).verticalScroll(rememberScrollState()),
                    verticalArrangement = Arrangement.spacedBy(12.dp),
                ) {
                    VSpace(4.dp)
                    ModePills(state)
                    ModeCard(state)
                    QuickToggles(state)
                    LogCard(state.logLines)
                    VSpace(8.dp)
                }
            }
        } else {
            Column(
                Modifier.verticalScroll(rememberScrollState()).padding(horizontal = 16.dp),
                verticalArrangement = Arrangement.spacedBy(14.dp),
            ) {
                Header(state, onOpenSettings)
                UpdateSlot(state)
                HeroCard(state, compact = false)
                ModePills(state)
                ModeCard(state)
                QuickToggles(state)
                LogCard(state.logLines)
                VSpace(12.dp)
            }
        }
    }
}

@Composable
private fun UpdateSlot(state: AppState) {
    val info = state.updateInfo
    AnimatedVisibility(info != null && !state.updateDismissed, enter = fadeIn() + expandVertically(), exit = fadeOut() + shrinkVertically()) {
        if (info != null) UpdateBanner(state, info)
    }
}

@Composable
private fun QuickToggles(state: AppState) {
    MgrCard(padding = androidx.compose.foundation.layout.PaddingValues(horizontal = 16.dp, vertical = 12.dp)) {
        Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
            MgrSwitchRow(
                title = "Микрофон телефона → ПК",
                hint = when {
                    state.micEnabled && state.link == Link.ON -> "Микрофон идёт на ПК (там выбери, куда его воспроизводить)"
                    state.micEnabled -> "Включится после подключения"
                    else -> "Выключен: микрофон не используется"
                },
                checked = state.micEnabled,
                onChange = state::updateMic,

            )
            MgrDivider()
            MgrSwitchRow(
                title = "Подключаться при запуске",
                hint = "Приложение само подключится с последними настройками",
                checked = state.autoConnect,
                onChange = state::updateAutoConnect,
            )
        }
    }
}

@Composable
private fun Header(state: AppState, onOpenSettings: () -> Unit) {
    val p = LocalPalette.current
    Row(Modifier.fillMaxWidth().padding(top = 8.dp), verticalAlignment = Alignment.CenterVertically) {
        Box(
            Modifier.size(36.dp).clip(RoundedCornerShape(Radius.sm)).background(p.accent),
            contentAlignment = Alignment.Center,
        ) {
            Icon(Icons.Filled.GraphicEq, contentDescription = null, tint = Color.White, modifier = Modifier.size(20.dp))
        }
        HSpace(12.dp)
        Column(Modifier.weight(1f)) {
            Text("Audio Bridge", style = MaterialTheme.typography.titleMedium, color = p.text)
            Text("Звук с ПК на телефон", style = MaterialTheme.typography.bodySmall, color = p.muted)
        }
        Box(
            Modifier.size(40.dp).clip(CircleShape).clickable(onClick = onOpenSettings),
            contentAlignment = Alignment.Center,
        ) {
            Icon(Icons.Filled.Settings, contentDescription = "Настройки", tint = p.muted)
        }
    }
}

@Composable
private fun UpdateBanner(state: AppState, info: com.audiobridge.UpdateInfo) {
    val p = LocalPalette.current
    MgrCard(selected = true) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Icon(Icons.Filled.SystemUpdate, contentDescription = null, tint = p.accent)
            HSpace(12.dp)
            Column(Modifier.weight(1f)) {
                Text("Доступна версия ${info.version}", style = MaterialTheme.typography.titleSmall, color = p.text)
                Text("Обновление скачается и установится", style = MaterialTheme.typography.bodySmall, color = p.muted)
            }
            HSpace(8.dp)
            if (state.downloadingUpdate) {
                CircularProgressIndicator(Modifier.size(20.dp), strokeWidth = 2.dp, color = p.accent)
            } else {
                MgrButton("Обновить", onClick = { state.installUpdate(info) }, height = 32.dp)
                HSpace(6.dp)
                Text(
                    "Позже", style = MaterialTheme.typography.labelMedium, color = p.muted,
                    modifier = Modifier.clickable { state.updateDismissed = true }.padding(6.dp),
                )
            }
        }
    }
}

// ---------------------------------------------------------------------------------------- «герой»

@Composable
private fun HeroCard(state: AppState, compact: Boolean) {
    val p = LocalPalette.current
    val link = state.link
    val title = when {
        link == Link.ON -> "Подключено"
        link == Link.CONNECTING && state.status.startsWith("Переподключ") -> "Переподключение…"
        link == Link.CONNECTING -> "Подключение…"
        state.status.startsWith("Нет разрешений") -> "Нужны разрешения"
        else -> "Выключено"
    }
    val subtitle = when (link) {
        Link.ON -> "${modeTitle(state.mode)} · звук ПК идёт на телефон"
        Link.CONNECTING -> state.lastProblem ?: waitingHint(state.mode)
        Link.OFF -> if (state.status.startsWith("Нет разрешений")) "Разреши доступ в системном окне и нажми ещё раз"
        else "Нажми кнопку, чтобы подключить звук с ПК"
    }
    val tone = when (link) { Link.ON -> Tone.Success; Link.CONNECTING -> Tone.Warning; Link.OFF -> Tone.Neutral }

    MgrCard(padding = androidx.compose.foundation.layout.PaddingValues(horizontal = 16.dp, vertical = if (compact) 14.dp else 22.dp)) {
        Column(Modifier.fillMaxWidth(), horizontalAlignment = Alignment.CenterHorizontally) {
            PowerButton(link = link, onClick = state::toggle, compact = compact)
            VSpace(16.dp)
            AnimatedContent(targetState = title, label = "hero-title") { t ->
                Text(t, style = MaterialTheme.typography.titleLarge, color = p.text, textAlign = TextAlign.Center)
            }
            VSpace(4.dp)
            Text(
                subtitle, style = MaterialTheme.typography.bodyMedium, color = p.muted,
                textAlign = TextAlign.Center, modifier = Modifier.padding(horizontal = 12.dp),
            )
            VSpace(12.dp)
            MgrChip(
                text = when (link) { Link.ON -> "В эфире"; Link.CONNECTING -> "Ожидание ПК"; Link.OFF -> "Не подключено" },
                tone = tone,
                leading = { StatusDot(color = when (link) { Link.ON -> p.success; Link.CONNECTING -> p.warning; Link.OFF -> p.muted }, pulsing = link == Link.CONNECTING) },
            )
        }
    }
}

private fun modeTitle(m: Mode) = when (m) {
    Mode.WIFI -> "Wi-Fi"
    Mode.USB -> "USB-кабель"
    Mode.BLE -> "Bluetooth LE"
    Mode.BLUETOOTH -> "Bluetooth"
}

private fun waitingHint(m: Mode) = when (m) {
    Mode.WIFI -> "Подключаюсь к серверу на ПК"
    Mode.USB -> "Жду, пока ПК переключит телефон в режим аксессуара"
    Mode.BLE -> "Жду ПК: запусти сервер и нажми «Старт»"
    Mode.BLUETOOTH -> "Подключаюсь к ПК по Bluetooth"
}

@Composable
private fun PowerButton(link: Link, onClick: () -> Unit, compact: Boolean) {
    val outer = if (compact) 124.dp else 150.dp
    val inner = if (compact) 92.dp else 112.dp
    val p = LocalPalette.current
    val fill by animateColorAsState(
        when (link) { Link.ON -> p.accent; Link.CONNECTING -> p.accentSoft; Link.OFF -> p.bg }, tween(250), label = "pw-fill",
    )
    val iconTint by animateColorAsState(
        when (link) { Link.ON -> Color.White; Link.CONNECTING -> p.accent; Link.OFF -> p.muted }, tween(250), label = "pw-icon",
    )
    val ring by animateColorAsState(
        when (link) { Link.ON -> p.accent.copy(alpha = 0.25f); Link.CONNECTING -> p.accent.copy(alpha = 0.5f); Link.OFF -> p.border }, tween(250), label = "pw-ring",
    )
    val transition = rememberInfiniteTransition(label = "pw-pulse")
    val pulse by transition.animateFloat(
        0f, 1f, infiniteRepeatable(tween(1400), RepeatMode.Restart), label = "pw-pulse-v",
    )
    Box(Modifier.size(outer), contentAlignment = Alignment.Center) {
        // Расходящееся кольцо — пока идёт подключение.
        if (link == Link.CONNECTING) {
            Box(
                Modifier.size(inner).scale(1f + pulse * 0.28f).alpha(1f - pulse)
                    .border(2.dp, p.accent, CircleShape),
            )
        }
        // Мягкое «свечение» — когда звук идёт.
        if (link == Link.ON) {
            Box(Modifier.size(outer - 6.dp).clip(CircleShape).background(p.accent.copy(alpha = 0.08f)))
            Box(Modifier.size(outer - 22.dp).clip(CircleShape).background(p.accent.copy(alpha = 0.12f)))
        }
        Box(
            Modifier
                .size(inner)
                .clip(CircleShape)
                .background(fill)
                .border(2.dp, ring, CircleShape)
                .clickable(onClick = onClick),
            contentAlignment = Alignment.Center,
        ) {
            Icon(Icons.Filled.PowerSettingsNew, contentDescription = if (link == Link.OFF) "Подключить" else "Отключить", tint = iconTint, modifier = Modifier.size(52.dp))
        }
    }
}

// ----------------------------------------------------------------------------------------- режимы

@Composable
private fun ModePills(state: AppState) {
    val enabled = state.link == Link.OFF
    Row(Modifier.horizontalScroll(rememberScrollState()), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
        MgrPill("Wi-Fi", state.mode == Mode.WIFI, { state.selectMode(Mode.WIFI) }, icon = Icons.Filled.Wifi, enabled = enabled)
        MgrPill("USB", state.mode == Mode.USB, { state.selectMode(Mode.USB) }, icon = Icons.Filled.Cable, enabled = enabled)
        MgrPill("Bluetooth LE", state.mode == Mode.BLE, { state.selectMode(Mode.BLE) }, icon = Icons.Filled.Bluetooth, enabled = enabled)
        MgrPill("Bluetooth", state.mode == Mode.BLUETOOTH, { state.selectMode(Mode.BLUETOOTH) }, icon = Icons.Filled.Bluetooth, enabled = enabled)
    }
}

@Composable
private fun ModeCard(state: AppState) {
    MgrCard {
        AnimatedContent(targetState = state.mode, label = "mode-card") { m ->
            when (m) {
                Mode.WIFI -> WifiPanel(state)
                Mode.USB -> UsbPanel()
                Mode.BLE -> BlePanel()
                Mode.BLUETOOTH -> BluetoothPanel(state)
            }
        }
    }
}

@Composable
private fun WifiPanel(state: AppState) {
    val p = LocalPalette.current
    val enabled = state.link == Link.OFF
    var manual by remember { mutableStateOf(false) }
    // На телефонах, подключённых не к Wi-Fi, поиск бесполезен — но запуск дешёвый.
    LaunchedEffect(Unit) { if (state.discovered.isEmpty() && !state.discovering) state.runDiscovery() }

    Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
        Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
            Column(Modifier.weight(1f)) {
                SectionTitle("ПК в сети", "Телефон и ПК должны быть в одной сети")
            }
            if (state.discovering) {
                CircularProgressIndicator(Modifier.size(20.dp), strokeWidth = 2.dp, color = p.accent)
            } else {
                Box(
                    Modifier.size(36.dp).clip(CircleShape).clickable(enabled = enabled) { state.runDiscovery() },
                    contentAlignment = Alignment.Center,
                ) { Icon(Icons.Filled.Refresh, contentDescription = "Найти ещё раз", tint = p.muted) }
            }
        }

        state.discovered.forEach { server ->
            PcTile(server.name, "${server.host}:${server.port}", selected = server.host == state.host, enabled = enabled) { state.pick(server) }
        }
        val known = state.host.isNotBlank() && state.discovered.none { it.host == state.host }
        if (known) PcTile("Сохранённый ПК", "${state.host}:${state.port}", selected = true, enabled = enabled) {}

        if (state.discovered.isEmpty() && !state.discovering && !known) {
            MgrNote("Серверов не видно. Запусти сервер на ПК («Старт») или укажи адрес вручную — это нужно, если ПК в другой подсети.")
        }

        Text(
            if (manual) "Скрыть ручной ввод" else "Указать адрес вручную",
            style = MaterialTheme.typography.labelMedium, color = p.accent,
            modifier = Modifier.clickable { manual = !manual }.padding(vertical = 4.dp),
        )
        AnimatedVisibility(manual) {
            Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                MgrTextField(
                    state.host, { state.host = it }, Modifier.weight(1f), label = "IP-адрес ПК",
                    placeholder = "10.20.20.244", enabled = enabled,
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Uri), mono = true,
                )
                MgrTextField(
                    state.port, { if (it.length <= 5 && it.all(Char::isDigit)) state.port = it }, Modifier.weight(0.5f),
                    label = "Порт", enabled = enabled, keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number), mono = true,
                )
            }
        }
    }
}

@Composable
private fun PcTile(title: String, subtitle: String, selected: Boolean, enabled: Boolean, onClick: () -> Unit) {
    val p = LocalPalette.current
    MgrCard(onClick = if (enabled) onClick else null, selected = selected, padding = androidx.compose.foundation.layout.PaddingValues(12.dp)) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Column(Modifier.weight(1f)) {
                Text(title, style = MaterialTheme.typography.titleSmall, color = p.text)
                Text(subtitle, style = MaterialTheme.typography.bodySmall, color = p.muted, fontFamily = FontFamily.Monospace)
            }
            if (selected) Icon(Icons.Filled.Check, contentDescription = "Выбран", tint = p.accent, modifier = Modifier.size(20.dp))
        }
    }
}

@Composable
private fun UsbPanel() {
    val p = LocalPalette.current
    val context = LocalContext.current
    var attached by remember { mutableStateOf(false) }
    LaunchedEffect(Unit) {
        while (true) {
            attached = UsbTransport.isAccessoryAttached(context)
            delay(1000)
        }
    }
    Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
        SectionTitle("USB-кабель", "Без модема и отладки: сервер на ПК сам переключит телефон")
        Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            StatusDot(if (attached) p.success else p.warning)
            Text(
                if (attached) "ПК подключён по кабелю" else "ПК пока не переключил телефон",
                style = MaterialTheme.typography.bodyMedium, color = p.text,
            )
        }
        if (!attached) MgrNote("Запусти сервер на ПК, включи в нём «USB-кабель» и воткни кабель. Если драйвер UsbDk ещё не стоит, сервер предложит установить.")
        else MgrNote("Звук идёт без сжатия.")
    }
}

@Composable
private fun BlePanel() {
    Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
        Row(horizontalArrangement = Arrangement.spacedBy(8.dp), verticalAlignment = Alignment.CenterVertically) {
            Box(Modifier.weight(1f)) { SectionTitle("Bluetooth LE", "Рекомендуемый Bluetooth-режим") }
            MgrChip("без сопряжения", Tone.Accent)
        }
        MgrNote("Нажми кнопку — телефон начнёт ждать, запущенный сервер на ПК сам найдёт его и подключится. Задержка около 100–150 мс, без провалов.")
    }
}

@Suppress("MissingPermission")
@Composable
private fun BluetoothPanel(state: AppState) {
    val p = LocalPalette.current
    val enabled = state.link == Link.OFF
    LaunchedEffect(Unit) { if (state.pairedDevices.isEmpty()) state.refreshPairedDevices() }
    Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
        Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
            Box(Modifier.weight(1f)) { SectionTitle("Сопряжённый ПК", "Классический Bluetooth: сначала спари устройства в настройках") }
            Box(
                Modifier.size(36.dp).clip(CircleShape).clickable(enabled = enabled) { state.refreshPairedDevices() },
                contentAlignment = Alignment.Center,
            ) { Icon(Icons.Filled.Refresh, contentDescription = "Обновить список", tint = p.muted) }
        }
        if (state.pairedDevices.isEmpty()) MgrNote("Нет сопряжённых устройств.")
        state.pairedDevices.forEach { d: BluetoothDevice ->
            PcTile(d.name ?: d.address, d.address, selected = d.address == state.btAddress, enabled = enabled) { state.btAddress = d.address }
        }
        MgrNote("Режим «Bluetooth LE» устойчивее: рекомендуется он.")
    }
}

// --------------------------------------------------------------------------------------------- лог

@Composable
private fun LogCard(lines: List<String>) {
    val p = LocalPalette.current
    var open by remember { mutableStateOf(false) }
    MgrCard(padding = androidx.compose.foundation.layout.PaddingValues(horizontal = 16.dp, vertical = 12.dp)) {
        Column {
            Row(
                Modifier.fillMaxWidth().clickable { open = !open },
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Text("Подробности", style = MaterialTheme.typography.titleSmall, color = p.text, modifier = Modifier.weight(1f))
                MgrChip(lines.size.toString())
                HSpace(6.dp)
                Icon(if (open) Icons.Filled.KeyboardArrowUp else Icons.Filled.KeyboardArrowDown, contentDescription = null, tint = p.muted)
            }
            AnimatedVisibility(open) {
                Column(Modifier.padding(top = 10.dp).heightIn(max = 220.dp).verticalScroll(rememberScrollState())) {
                    if (lines.isEmpty()) MgrNote("Пока пусто")
                    lines.forEach {
                        Text(
                            it, style = MaterialTheme.typography.bodySmall.copy(fontFamily = FontFamily.Monospace),
                            color = p.muted, modifier = Modifier.padding(vertical = 1.dp),
                        )
                    }
                }
            }
        }
    }
}
