package com.audiobridge

import android.app.PendingIntent
import android.content.Intent
import android.os.Build
import android.service.quicksettings.Tile
import android.service.quicksettings.TileService

/**
 * Плитка в шторке быстрых настроек: включить/выключить Audio Bridge, не открывая приложение.
 * Включение запускает приложение с последними настройками (экран подключит сам — так работают разрешения
 * и фоновый запуск сервиса), выключение гасит сервис прямо из шторки.
 */
class QuickTile : TileService() {

    override fun onStartListening() {
        StatusBus.changed = { refresh() }
        refresh()
    }

    override fun onStopListening() {
        StatusBus.changed = null
    }

    override fun onClick() {
        if (AudioService.active) {
            startService(Intent(this, AudioService::class.java).apply { action = AudioService.ACTION_STOP })
            return
        }
        val launch = Intent(this, MainActivity::class.java).apply {
            addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_SINGLE_TOP)
            putExtra(MainActivity.EXTRA_CONNECT, true)
        }
        if (Build.VERSION.SDK_INT >= 34) {
            val pending = PendingIntent.getActivity(this, 0, launch, PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE)
            startActivityAndCollapse(pending)
        } else {
            @Suppress("DEPRECATION")
            startActivityAndCollapse(launch)
        }
    }

    private fun refresh() {
        val tile = qsTile ?: return
        val status = AudioService.lastStatus
        tile.state = if (AudioService.active) Tile.STATE_ACTIVE else Tile.STATE_INACTIVE
        tile.label = "Audio Bridge"
        if (Build.VERSION.SDK_INT >= 29) {
            tile.subtitle = when {
                !AudioService.active -> "Выключен"
                status.startsWith("Подключено") -> "Подключён"
                else -> "Подключение…"
            }
        }
        tile.updateTile()
    }
}
