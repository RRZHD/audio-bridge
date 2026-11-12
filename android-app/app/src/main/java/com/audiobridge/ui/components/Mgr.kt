package com.audiobridge.ui.components

import androidx.compose.animation.animateColorAsState
import androidx.compose.animation.core.RepeatMode
import androidx.compose.animation.core.animateFloat
import androidx.compose.animation.core.infiniteRepeatable
import androidx.compose.animation.core.rememberInfiniteTransition
import androidx.compose.animation.core.tween
import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.interaction.collectIsFocusedAsState
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Slider
import androidx.compose.material3.SliderDefaults
import androidx.compose.material3.Switch
import androidx.compose.material3.SwitchDefaults
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.scale
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.material3.Icon
import com.audiobridge.ui.theme.Control
import com.audiobridge.ui.theme.LocalPalette
import com.audiobridge.ui.theme.Radius

/** Карточка консоли: поверхность, рамка 1 dp, скругление 12. */
@Composable
fun MgrCard(
    modifier: Modifier = Modifier,
    onClick: (() -> Unit)? = null,
    selected: Boolean = false,
    padding: PaddingValues = PaddingValues(16.dp),
    content: @Composable () -> Unit,
) {
    val p = LocalPalette.current
    val shape = RoundedCornerShape(Radius.md)
    val borderColor by animateColorAsState(if (selected) p.accent else p.border, tween(150), label = "card-border")
    val bg by animateColorAsState(if (selected) p.accentSoft else p.surface, tween(150), label = "card-bg")
    Box(
        modifier
            .clip(shape)
            .background(bg)
            .border(BorderStroke(1.dp, borderColor), shape)
            .then(if (onClick != null) Modifier.clickable(onClick = onClick) else Modifier)
            .padding(padding),
    ) { content() }
}

/** Заголовок блока: 16 sp полужирный; под ним — пояснение приглушённым 12 sp. */
@Composable
fun SectionTitle(title: String, hint: String? = null, modifier: Modifier = Modifier) {
    val p = LocalPalette.current
    Column(modifier.fillMaxWidth(), verticalArrangement = Arrangement.spacedBy(2.dp)) {
        Text(title, style = MaterialTheme.typography.titleMedium, color = p.text)
        if (hint != null) Text(hint, style = MaterialTheme.typography.bodySmall, color = p.muted)
    }
}

/** Пилюля-фильтр (mgr-pill): капсула 32 dp, выбранная — подсветка и рамка акцентом. */
@Composable
fun MgrPill(
    label: String,
    selected: Boolean,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    icon: ImageVector? = null,
    enabled: Boolean = true,
) {
    val p = LocalPalette.current
    val shape = RoundedCornerShape(Radius.sm * 2.5f)
    val border by animateColorAsState(if (selected) p.accent else p.border, tween(150), label = "pill-border")
    val bg by animateColorAsState(if (selected) p.accentSoft else p.surface, tween(150), label = "pill-bg")
    val fg by animateColorAsState(if (selected) p.accent else p.text, tween(150), label = "pill-fg")
    Row(
        modifier
            .alpha(if (enabled) 1f else 0.5f)
            .height(Control.md)
            .clip(shape)
            .background(bg)
            .border(1.dp, border, shape)
            .clickable(enabled = enabled, onClick = onClick)
            .padding(horizontal = 14.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(6.dp, Alignment.CenterHorizontally),
    ) {
        if (icon != null) Icon(icon, contentDescription = null, tint = fg, modifier = Modifier.size(18.dp))
        Text(label, style = MaterialTheme.typography.labelMedium, color = fg, maxLines = 1)
    }
}

enum class Tone { Neutral, Success, Warning, Danger, Info, Accent }

/** Плашка состояния (mgr-chip): капсула с мягкой подложкой цвета тона. */
@Composable
fun MgrChip(text: String, tone: Tone = Tone.Neutral, modifier: Modifier = Modifier, leading: (@Composable () -> Unit)? = null) {
    val p = LocalPalette.current
    val (bg, fg) = when (tone) {
        Tone.Neutral -> p.chipBg to p.muted
        Tone.Success -> p.soft(p.success) to p.ink(p.success)
        Tone.Warning -> p.soft(p.warning) to p.ink(p.warning)
        Tone.Danger -> p.soft(p.danger) to p.ink(p.danger)
        Tone.Info -> p.soft(p.info) to p.ink(p.info)
        Tone.Accent -> p.accentSoft to p.accent
    }
    Row(
        modifier.clip(RoundedCornerShape(999.dp)).background(bg).padding(horizontal = 9.dp, vertical = 3.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(5.dp),
    ) {
        leading?.invoke()
        Text(text, style = MaterialTheme.typography.bodySmall, color = fg, maxLines = 1)
    }
}

/** Точка состояния; в тоне «идёт» мягко пульсирует. */
@Composable
fun StatusDot(color: Color, pulsing: Boolean = false, size: Dp = 8.dp) {
    val transition = rememberInfiniteTransition(label = "dot")
    val scale by transition.animateFloat(
        initialValue = 1f, targetValue = if (pulsing) 1.5f else 1f,
        animationSpec = infiniteRepeatable(tween(700), RepeatMode.Reverse), label = "dot-scale",
    )
    Box(Modifier.size(size).scale(scale).clip(CircleShape).background(color))
}

enum class BtnKind { Primary, Ghost, Danger }

/** Кнопка консоли: основная — заливка акцентом, второстепенная — рамка, опасная — цвет «ошибка». Высоты 32/36/44. */
@Composable
fun MgrButton(
    text: String,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    kind: BtnKind = BtnKind.Primary,
    height: Dp = Control.lg,
    icon: ImageVector? = null,
    enabled: Boolean = true,
) {
    val p = LocalPalette.current
    val shape = RoundedCornerShape(Radius.sm)
    val (bg, fg, borderColor) = when (kind) {
        BtnKind.Primary -> Triple(p.accent, Color.White, p.accent)
        BtnKind.Danger -> Triple(p.danger, Color.White, p.danger)
        BtnKind.Ghost -> Triple(p.surface, p.text, p.border)
    }
    val animBg by animateColorAsState(bg, tween(150), label = "btn-bg")
    Row(
        modifier
            .alpha(if (enabled) 1f else 0.5f)
            .height(height)
            .clip(shape)
            .background(animBg)
            .border(1.dp, borderColor, shape)
            .clickable(enabled = enabled, onClick = onClick)
            .padding(horizontal = 18.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(8.dp, Alignment.CenterHorizontally),
    ) {
        if (icon != null) Icon(icon, contentDescription = null, tint = fg, modifier = Modifier.size(18.dp))
        Text(text, style = MaterialTheme.typography.labelLarge, color = fg, maxLines = 1)
    }
}

/** Поле ввода консоли: подпись над полем, высота 44, рамка, при фокусе — акцент и мягкое кольцо. */
@Composable
fun MgrTextField(
    value: String,
    onValueChange: (String) -> Unit,
    modifier: Modifier = Modifier,
    label: String? = null,
    placeholder: String? = null,
    hint: String? = null,
    enabled: Boolean = true,
    keyboardOptions: KeyboardOptions = KeyboardOptions.Default,
    mono: Boolean = false,
) {
    val p = LocalPalette.current
    val interaction = remember { MutableInteractionSource() }
    val focused by interaction.collectIsFocusedAsState()
    val shape = RoundedCornerShape(Radius.sm)
    val borderColor by animateColorAsState(if (focused) p.accent else p.border, tween(150), label = "field-border")
    val textStyle = TextStyle(
        color = p.text,
        fontSize = 14.sp,
        fontFamily = if (mono) FontFamily.Monospace else FontFamily.SansSerif,
    )
    Column(modifier, verticalArrangement = Arrangement.spacedBy(4.dp)) {
        if (label != null) Text(label, style = MaterialTheme.typography.labelMedium, color = p.muted)
        Box(
            Modifier
                .fillMaxWidth()
                .alpha(if (enabled) 1f else 0.5f)
                .then(if (focused) Modifier.border(3.dp, p.accent.copy(alpha = 0.18f), RoundedCornerShape(Radius.sm + 2.dp)) else Modifier)
                .padding(if (focused) 0.dp else 0.dp),
        ) {
            BasicTextField(
                value = value,
                onValueChange = onValueChange,
                enabled = enabled,
                singleLine = true,
                textStyle = textStyle,
                keyboardOptions = keyboardOptions,
                cursorBrush = SolidColor(p.accent),
                interactionSource = interaction,
                modifier = Modifier.fillMaxWidth(),
                decorationBox = { inner ->
                    Box(
                        Modifier
                            .fillMaxWidth()
                            .height(Control.lg)
                            .clip(shape)
                            .background(p.surface)
                            .border(1.dp, borderColor, shape)
                            .padding(horizontal = 12.dp),
                        contentAlignment = Alignment.CenterStart,
                    ) {
                        if (value.isEmpty() && placeholder != null) {
                            Text(placeholder, style = textStyle.copy(color = p.muted))
                        }
                        inner()
                    }
                },
            )
        }
        if (hint != null) Text(hint, style = MaterialTheme.typography.bodySmall, color = p.muted)
    }
}

/** Строка с переключателем: заголовок, пояснение и switch консоли (трек — акцент, бегунок — поверхность). */
@Composable
fun MgrSwitchRow(
    title: String,
    hint: String?,
    checked: Boolean,
    onChange: (Boolean) -> Unit,
    modifier: Modifier = Modifier,
    enabled: Boolean = true,
) {
    val p = LocalPalette.current
    Row(
        modifier.fillMaxWidth().clickable(enabled = enabled) { onChange(!checked) }.padding(vertical = 2.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Column(Modifier.weight(1f).padding(end = 12.dp), verticalArrangement = Arrangement.spacedBy(2.dp)) {
            Text(title, style = MaterialTheme.typography.bodyMedium, color = p.text)
            if (hint != null) Text(hint, style = MaterialTheme.typography.bodySmall, color = p.muted)
        }
        Switch(
            checked = checked,
            onCheckedChange = onChange,
            enabled = enabled,
            colors = SwitchDefaults.colors(
                checkedThumbColor = p.surface,
                checkedTrackColor = p.accent,
                checkedBorderColor = Color.Transparent,
                uncheckedThumbColor = p.surface,
                uncheckedTrackColor = p.border,
                uncheckedBorderColor = Color.Transparent,
                disabledCheckedTrackColor = p.accent.copy(alpha = 0.5f),
                disabledUncheckedTrackColor = p.border.copy(alpha = 0.5f),
            ),
        )
    }
}

/** Слайдер с подписью и текущим значением справа. */
@Composable
fun MgrSliderRow(
    title: String,
    valueText: String,
    value: Float,
    onChange: (Float) -> Unit,
    range: ClosedFloatingPointRange<Float>,
    steps: Int = 0,
    hint: String? = null,
) {
    val p = LocalPalette.current
    Column(Modifier.fillMaxWidth()) {
        Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween, verticalAlignment = Alignment.CenterVertically) {
            Text(title, style = MaterialTheme.typography.bodyMedium, color = p.text)
            Text(valueText, style = MaterialTheme.typography.labelMedium, color = p.accent, fontWeight = FontWeight.SemiBold)
        }
        Slider(
            value = value, onValueChange = onChange, valueRange = range, steps = steps,
            colors = SliderDefaults.colors(
                thumbColor = p.accent, activeTrackColor = p.accent, inactiveTrackColor = p.border,
                activeTickColor = Color.Transparent, inactiveTickColor = Color.Transparent,
            ),
        )
        if (hint != null) Text(hint, style = MaterialTheme.typography.bodySmall, color = p.muted)
    }
}

/** Тонкая линия между строками карточки. */
@Composable
fun MgrDivider(modifier: Modifier = Modifier) {
    val p = LocalPalette.current
    Box(modifier.fillMaxWidth().height(1.dp).background(p.line))
}

@Composable
fun VSpace(h: Dp) = Spacer(Modifier.height(h))

@Composable
fun HSpace(w: Dp) = Spacer(Modifier.width(w))

/** Подсказка/ограничение: приглушённый абзац. */
@Composable
fun MgrNote(text: String, modifier: Modifier = Modifier) {
    Text(text, modifier, style = MaterialTheme.typography.bodySmall, color = LocalPalette.current.muted)
}

@Composable
fun heightMin(content: @Composable () -> Unit) = Box(Modifier.heightIn(min = Control.lg)) { content() }
