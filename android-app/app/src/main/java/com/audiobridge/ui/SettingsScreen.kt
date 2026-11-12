package com.audiobridge.ui

import androidx.activity.compose.BackHandler
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.animateColorAsState
import androidx.compose.animation.core.tween
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.ArrowBack
import androidx.compose.material.icons.filled.KeyboardArrowDown
import androidx.compose.material.icons.filled.KeyboardArrowUp
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.unit.dp
import com.audiobridge.BuildConfig
import com.audiobridge.ui.components.HSpace
import com.audiobridge.ui.components.MgrCard
import com.audiobridge.ui.components.MgrChip
import com.audiobridge.ui.components.MgrDivider
import com.audiobridge.ui.components.MgrNote
import com.audiobridge.ui.components.MgrPill
import com.audiobridge.ui.components.MgrSliderRow
import com.audiobridge.ui.components.SectionTitle
import com.audiobridge.ui.components.Tone
import com.audiobridge.ui.components.VSpace
import com.audiobridge.ui.theme.LocalPalette
import com.audiobridge.ui.theme.Palette
import com.audiobridge.ui.theme.Palettes
import com.audiobridge.ui.theme.Radius

/**
 * Настройки: сверху — то, что нужно почти всем (оформление, профиль звука), «Дополнительно» свёрнуто.
 * Профиль выставляет буфер, битрейт и качество Wi-Fi одним нажатием; ручная правка ниже переводит его в «Свой».
 */
@Composable
fun SettingsScreen(state: AppState, onBack: () -> Unit) {
    val p = LocalPalette.current
    BackHandler(onBack = onBack)
    var advanced by remember { mutableStateOf(false) }
    val idle = state.link == Link.OFF

    Column(
        Modifier.fillMaxSize().background(p.bg).statusBarsPadding().navigationBarsPadding(),
    ) {
        // Шапка: назад и заголовок.
        Row(Modifier.fillMaxWidth().padding(horizontal = 8.dp, vertical = 8.dp), verticalAlignment = Alignment.CenterVertically) {
            Box(Modifier.size(44.dp).clip(CircleShape).clickable(onClick = onBack), contentAlignment = Alignment.Center) {
                Icon(Icons.Filled.ArrowBack, contentDescription = "Назад", tint = p.text)
            }
            Text("Настройки", style = MaterialTheme.typography.titleLarge, color = p.text, modifier = Modifier.padding(start = 4.dp))
        }

        Column(
            Modifier.verticalScroll(rememberScrollState()).padding(horizontal = 16.dp),
            verticalArrangement = Arrangement.spacedBy(14.dp),
        ) {
            if (!idle) {
                MgrCard(selected = true) {
                    Text(
                        "Часть настроек применится при следующем подключении.",
                        style = MaterialTheme.typography.bodySmall, color = p.accent,
                    )
                }
            }

            // ---- звук ----------------------------------------------------------------------------
            MgrCard {
                Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
                    SectionTitle("Звук", "Один выбор вместо десятка ползунков")
                    Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                        state.profiles.forEach { pr ->
                            MgrPill(pr.title, state.profile == pr.id, { state.applyProfile(pr.id) })
                        }
                    }
                    val current = state.profiles.firstOrNull { it.id == state.profile }
                    MgrNote(current?.hint ?: "Свои значения: настроены вручную в «Дополнительно».")
                }
            }

            // ---- оформление ---------------------------------------------------------------------
            MgrCard {
                Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
                    SectionTitle("Оформление", "Светлая, тёмная или как в системе")
                    ThemeGrid(state)
                }
            }

            // ---- Wi-Fi ----------------------------------------------------------------------------
            MgrCard {
                Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
                    SectionTitle("Качество по Wi-Fi")
                    Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                        listOf(0 to "Авто", 1 to "Без сжатия", 2 to "Сжатый").forEach { (v, label) ->
                            MgrPill(label, state.wifiQuality == v, { state.updateWifiQuality(v) })
                        }
                    }
                    MgrNote(
                        when (state.wifiQuality) {
                            1 -> "Всегда без сжатия: максимальное качество, но на слабой сети (2,4 ГГц, через роутер) может рваться."
                            2 -> "Всегда сжато (Opus 192 кбит/с): нагрузка на сеть в ~15 раз меньше, звук не рвётся."
                            else -> "Звук идёт без сжатия, а если сеть не успевает, сервер сам переключается на сжатие — без переподключения."
                        },
                    )
                }
            }

            // ---- дополнительно -------------------------------------------------------------------
            MgrCard {
                Column {
                    Row(
                        Modifier.fillMaxWidth().clickable { advanced = !advanced },
                        verticalAlignment = Alignment.CenterVertically,
                    ) {
                        Box(Modifier.weight(1f)) { SectionTitle("Дополнительно", "Буфер, битрейт, частота — для тонкой настройки") }
                        Icon(if (advanced) Icons.Filled.KeyboardArrowUp else Icons.Filled.KeyboardArrowDown, contentDescription = null, tint = p.muted)
                    }
                    AnimatedVisibility(advanced) {
                        Column(Modifier.padding(top = 12.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
                            MgrSliderRow(
                                "Буфер воспроизведения", "${state.bufferMs.toInt()} мс", state.bufferMs, state::updateBuffer,
                                AppState.MIN_BUFFER..400f, steps = 36,
                                hint = "Запас против рывков сети: больше — устойчивее, но выше задержка.",
                            )
                            MgrDivider()
                            MgrSliderRow(
                                "Битрейт сжатого звука ПК → телефон", "${state.pcBitrate.toInt()} кбит/с", state.pcBitrate, state::updatePcBitrate,
                                64f..320f, steps = 15,
                                hint = "Для Bluetooth и сжатого Wi-Fi. USB всегда без сжатия.",
                            )
                            MgrDivider()
                            MgrSliderRow(
                                "Битрейт микрофона → ПК", "${state.micBitrate.toInt()} кбит/с", state.micBitrate, state::updateMicBitrate,
                                12f..64f, steps = 12,
                            )
                            MgrDivider()
                            Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                                Text("Частота потока без сжатия", style = MaterialTheme.typography.bodyMedium, color = p.text)
                                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                                    listOf(48_000 to "48 кГц", 96_000 to "96 кГц", 192_000 to "192 кГц").forEach { (v, label) ->
                                        MgrPill(label, state.maxRate == v, { state.updateMaxRate(v) })
                                    }
                                }
                                MgrNote("48 кГц — то, во что Android всё равно сводит звук; выше — только если устройство это играет.")
                            }
                        }
                    }
                }
            }

            // ---- о приложении --------------------------------------------------------------------
            MgrCard {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Column(Modifier.weight(1f)) {
                        Text("Audio Bridge", style = MaterialTheme.typography.titleSmall, color = p.text)
                        Text("Версия ${BuildConfig.VERSION_NAME}", style = MaterialTheme.typography.bodySmall, color = p.muted)
                    }
                    MgrChip("RRZHD", Tone.Neutral)
                }
            }
            VSpace(16.dp)
        }
    }
}

/** Плитки тем, как «Оформление» в консоли: миниатюра (фон, окно, точка акцента, пузырь) и подпись. */
@Composable
private fun ThemeGrid(state: AppState) {
    val tiles: List<Pair<String, Palette>> = listOf("auto" to Palettes.Light) + Palettes.all.map { it.id to it }
    Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
        tiles.chunked(3).forEach { row ->
            Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                row.forEach { (id, pal) ->
                    Box(Modifier.weight(1f)) {
                        ThemeTile(
                            title = if (id == "auto") "Как в системе" else pal.title,
                            palette = pal,
                            selected = state.themeId == id,
                            split = id == "auto",
                        ) { state.updateTheme(id) }
                    }
                }
                repeat(3 - row.size) { Box(Modifier.weight(1f)) {} }
            }
        }
    }
}

@Composable
private fun ThemeTile(title: String, palette: Palette, selected: Boolean, split: Boolean, onClick: () -> Unit) {
    val p = LocalPalette.current
    val ring by animateColorAsState(if (selected) p.accent else Color.Transparent, tween(150), label = "tile-ring")
    Column(Modifier.clickable(onClick = onClick), verticalArrangement = Arrangement.spacedBy(6.dp)) {
        Box(
            Modifier
                .fillMaxWidth()
                .height(54.dp)
                .clip(RoundedCornerShape(Radius.md))
                .background(palette.bg)
                .border(if (selected) 2.dp else 1.dp, if (selected) ring else p.border, RoundedCornerShape(Radius.md)),
        ) {
            if (split) {
                // «Как в системе»: светлая половина слева, тёмная справа.
                Box(Modifier.fillMaxWidth(0.5f).height(54.dp).background(Palettes.Light.bg))
                Box(Modifier.align(Alignment.CenterEnd).fillMaxWidth(0.5f).height(54.dp).background(Palettes.Dark.bg))
            }
            Box(
                Modifier.align(Alignment.BottomCenter).padding(start = 24.dp, top = 10.dp, end = 10.dp)
                    .fillMaxWidth().height(36.dp)
                    .clip(RoundedCornerShape(topStart = Radius.sm, topEnd = Radius.sm))
                    .background(if (split) Palettes.Light.surface else palette.surface),
            )
            Box(Modifier.align(Alignment.BottomStart).padding(8.dp).size(10.dp).clip(CircleShape).background(palette.accent))
            Box(
                Modifier.align(Alignment.TopEnd).padding(top = 20.dp, end = 18.dp)
                    .size(width = 30.dp, height = 10.dp).clip(RoundedCornerShape(Radius.sm)).background(palette.accent),
            )
        }
        Text(title, style = MaterialTheme.typography.bodySmall, color = if (selected) p.accent else p.text, maxLines = 1)
    }
}
