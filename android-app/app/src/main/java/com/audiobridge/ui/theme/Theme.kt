package com.audiobridge.ui.theme

import android.app.Activity
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Shapes
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.SideEffect
import androidx.compose.runtime.staticCompositionLocalOf
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.toArgb
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.unit.dp
import androidx.core.view.WindowCompat

/** Палитра текущей темы: из неё берут цвета компоненты, которых нет в Material (мягкие плашки, состояния). */
val LocalPalette = staticCompositionLocalOf { Palettes.Light }

// Состояния для мест, где палитры под рукой нет (старые ссылки).
val StatusGreen = Color(0xFF2F9E5B)
val StatusAmber = Color(0xFFD9930D)
val StatusRed = Color(0xFFD64545)

/** Скругления консоли: xs 4 (полоски), sm 8 (кнопки и поля), обычное 12 (карточки), lg 16 (листы и «герой»). */
object Radius {
    val xs = 4.dp
    val sm = 8.dp
    val md = 12.dp
    val lg = 16.dp
}

/** Высоты кнопок и полей консоли: 32 / 36 / 44. */
object Control {
    val sm = 32.dp
    val md = 36.dp
    val lg = 44.dp
}

private fun Palette.toScheme() = (if (dark) darkColorScheme() else lightColorScheme()).copy(
    primary = accent,
    onPrimary = Color.White,
    primaryContainer = accentSoft,
    onPrimaryContainer = accent,
    secondary = accent,
    onSecondary = Color.White,
    secondaryContainer = accentSoft,
    onSecondaryContainer = accent,
    tertiary = info,
    background = bg,
    onBackground = text,
    surface = surface,
    onSurface = text,
    surfaceVariant = raise,
    onSurfaceVariant = muted,
    surfaceContainer = surface,
    surfaceContainerLow = surface,
    surfaceContainerHigh = surface,
    surfaceContainerHighest = raise,
    surfaceTint = Color.Transparent,
    outline = border,
    outlineVariant = border,
    error = danger,
    onError = Color.White,
    errorContainer = soft(danger),
    onErrorContainer = ink(danger),
    inverseSurface = text,
    inverseOnSurface = surface,
    scrim = Color.Black,
)

@Composable
fun AudioBridgeTheme(
    themeId: String = Palettes.AUTO,
    content: @Composable () -> Unit,
) {
    val palette = Palettes.byId(themeId, isSystemInDarkTheme())

    val view = LocalView.current
    if (!view.isInEditMode) {
        SideEffect {
            val window = (view.context as Activity).window
            window.statusBarColor = palette.bg.toArgb()
            window.navigationBarColor = palette.bg.toArgb()
            val controller = WindowCompat.getInsetsController(window, view)
            controller.isAppearanceLightStatusBars = !palette.dark
            controller.isAppearanceLightNavigationBars = !palette.dark
        }
    }

    CompositionLocalProvider(LocalPalette provides palette) {
        MaterialTheme(
            colorScheme = palette.toScheme(),
            typography = Typography,
            shapes = Shapes(
                extraSmall = RoundedCornerShape(Radius.xs),
                small = RoundedCornerShape(Radius.sm),
                medium = RoundedCornerShape(Radius.md),
                large = RoundedCornerShape(Radius.lg),
                extraLarge = RoundedCornerShape(Radius.lg),
            ),
            content = content,
        )
    }
}
