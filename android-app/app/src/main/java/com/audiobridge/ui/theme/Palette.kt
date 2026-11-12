package com.audiobridge.ui.theme

import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.compositeOver

/**
 * Палитры приложения — светлая и тёмная; значения те же, что у консоли менеджера
 * (local/assets/ai-chat/manager/scss: 00-base, 92-dark, 93-themes, _tokens). Один набор переменных
 * на всё: акцент, фон страницы, поверхность карточек, текст, приглушённый текст, рамка.
 */
data class Palette(
    val id: String,
    val title: String,
    val dark: Boolean,
    val accent: Color,
    val accentHover: Color,
    val bg: Color,
    val surface: Color,
    val text: Color,
    val muted: Color,
    val border: Color,
    /** «Приподнятая» подложка тёмных тем (чужое сообщение, метки); для светлых — лёгкий оттенок текста. */
    val raise: Color,
) {
    /** Подсветка акцентом: активная карточка, выбранная пилюля (8% акцента на поверхности, 18% в тёмных). */
    val accentSoft: Color get() = accent.copy(alpha = if (dark) 0.18f else 0.08f).compositeOver(surface)

    // Цвета состояний консоли (_tokens.scss), светлая и тёмная редакции.
    val success: Color get() = if (dark) Color(0xFF4CC283) else Color(0xFF2F9E5B)
    val warning: Color get() = if (dark) Color(0xFFE0A526) else Color(0xFFD9930D)
    val danger: Color get() = if (dark) Color(0xFFEF5B55) else Color(0xFFD64545)
    val info: Color get() = if (dark) Color(0xFF6FB1FF) else Color(0xFF1F7AE0)

    private val softMix get() = if (dark) 0.22f else 0.14f
    fun soft(tone: Color): Color = tone.copy(alpha = softMix).compositeOver(surface)
    fun ink(tone: Color): Color = tone.copy(alpha = if (dark) 0.85f else 0.85f).compositeOver(text)

    /** Тонкий разделитель: 8% текста. */
    val line: Color get() = text.copy(alpha = 0.08f)
    /** Фон «капсулы-плашки» без тона: 8% текста. */
    val chipBg: Color get() = text.copy(alpha = 0.08f)
}

private fun c(hex: Long) = Color(0xFF000000 or hex)

object Palettes {
    val Light = Palette(
        "light", "Светлая", false,
        accent = c(0xDC2F2F), accentHover = c(0xC72929),
        bg = c(0xF2F3F5), surface = c(0xFFFFFF), text = c(0x222222), muted = c(0x8A8A8A), border = c(0xE4E4E7),
        raise = c(0xF2F3F5),
    )
    val Dark = Palette(
        "dark", "Тёмная", true,
        accent = c(0xDC2F2F), accentHover = c(0xC72929),
        bg = c(0x17171B), surface = c(0x1F1F25), text = c(0xE9E9EE), muted = c(0x9A9AA6), border = c(0x33333D),
        raise = c(0x2B2B33),
    )
    /** Идентификатор «как в системе»: светлая или тёмная по настройке телефона. */
    const val AUTO = "auto"

    val all = listOf(Light, Dark)

    fun byId(id: String, systemDark: Boolean): Palette =
        if (id == AUTO) (if (systemDark) Dark else Light) else all.firstOrNull { it.id == id } ?: (if (systemDark) Dark else Light)
}
