package com.audiobridge.ui.theme

import android.app.Activity
import android.os.Build
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.dynamicDarkColorScheme
import androidx.compose.material3.dynamicLightColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.toArgb
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalView
import androidx.core.view.WindowCompat

val AccentPurple = Color(0xFF7C5CFF)
val AccentPurpleDark = Color(0xFF6247D9)
val AppBackgroundDark = Color(0xFF16161F)
val AppSurfaceDark = Color(0xFF1E1E2E)
val AppBackgroundLight = Color(0xFFF7F6FB)
val AppSurfaceLight = Color(0xFFFFFFFF)
val StatusGreen = Color(0xFF34C77B)
val StatusAmber = Color(0xFFE0A93B)
val StatusRed = Color(0xFFE0545C)

private val DarkColors = darkColorScheme(
    primary = AccentPurple,
    onPrimary = Color.White,
    secondary = AccentPurpleDark,
    background = AppBackgroundDark,
    surface = AppSurfaceDark,
    surfaceVariant = Color(0xFF29293D),
    onBackground = Color(0xFFEAEAF4),
    onSurface = Color(0xFFEAEAF4),
)

private val LightColors = lightColorScheme(
    primary = AccentPurple,
    onPrimary = Color.White,
    secondary = AccentPurpleDark,
    background = AppBackgroundLight,
    surface = AppSurfaceLight,
    surfaceVariant = Color(0xFFEDEAFB),
    onBackground = Color(0xFF1B1B23),
    onSurface = Color(0xFF1B1B23),
)

@Composable
fun AudioBridgeTheme(
    darkTheme: Boolean = isSystemInDarkTheme(),
    dynamicColor: Boolean = true,
    content: @Composable () -> Unit,
) {
    val context = LocalContext.current
    val colorScheme = when {
        dynamicColor && Build.VERSION.SDK_INT >= Build.VERSION_CODES.S ->
            if (darkTheme) dynamicDarkColorScheme(context) else dynamicLightColorScheme(context)
        darkTheme -> DarkColors
        else -> LightColors
    }

    val view = LocalView.current
    if (!view.isInEditMode) {
        androidx.compose.runtime.SideEffect {
            val window = (view.context as Activity).window
            window.statusBarColor = colorScheme.background.toArgb()
            WindowCompat.getInsetsController(window, view).isAppearanceLightStatusBars = !darkTheme
        }
    }

    MaterialTheme(
        colorScheme = colorScheme,
        typography = Typography,
        content = content,
    )
}
