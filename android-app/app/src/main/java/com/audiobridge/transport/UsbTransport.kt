package com.audiobridge.transport

import android.app.PendingIntent
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.hardware.usb.UsbAccessory
import android.hardware.usb.UsbManager
import android.os.Build
import android.os.ParcelFileDescriptor
import java.io.FileInputStream
import java.io.FileOutputStream
import java.io.IOException
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit

/**
 * USB over **Android Open Accessory** — no adb, no USB debugging, no USB tethering.
 *
 * The PC server acts as the accessory host: it finds this phone on the cable, switches it into
 * accessory mode and opens the accessory's two bulk endpoints. Android then lists an accessory
 * (manufacturer/model/version from `accessory_filter.xml`) which this class opens as a plain
 * duplex file descriptor. The wire protocol above it is the same as for Wi-Fi/Bluetooth.
 *
 * Nothing here dials out: the connection exists only while the PC has put the phone into accessory
 * mode, so [connect] waits for the accessory to show up (the service retries every couple of seconds).
 */
class UsbTransport(private val context: Context) : Transport {
    override val kind = TransportKind.USB

    private val manager = context.applicationContext.getSystemService(Context.USB_SERVICE) as UsbManager

    override fun connect(): OpenConnection {
        val accessory = waitForAccessory()
            ?: throw IOException(
                "ПК не переключил телефон в режим аксессуара. Запусти сервер на ПК с включённой галочкой USB " +
                    "и установленным драйвером UsbDk, подключи кабель."
            )

        if (!manager.hasPermission(accessory) && !requestPermission(accessory)) {
            throw IOException("Нет разрешения на доступ к USB-аксессуару (ответь «ОК» в системном окне)")
        }

        val fd: ParcelFileDescriptor = manager.openAccessory(accessory)
            ?: throw IOException("Не удалось открыть USB-аксессуар")
        val input = FileInputStream(fd.fileDescriptor)
        val output = FileOutputStream(fd.fileDescriptor)
        return OpenConnection(input, output, java.io.Closeable {
            try { input.close() } catch (_: IOException) {}
            try { output.close() } catch (_: IOException) {}
            try { fd.close() } catch (_: IOException) {}
        })
    }

    private fun waitForAccessory(): UsbAccessory? {
        val deadline = System.currentTimeMillis() + WAIT_ACCESSORY_MS
        while (true) {
            manager.accessoryList?.firstOrNull { it.manufacturer == MANUFACTURER && it.model == MODEL }?.let { return it }
            if (System.currentTimeMillis() >= deadline) return null
            Thread.sleep(250)
        }
    }

    private fun requestPermission(accessory: UsbAccessory): Boolean {
        val action = "${context.packageName}.USB_PERMISSION"
        val latch = CountDownLatch(1)
        var granted = false
        val receiver = object : BroadcastReceiver() {
            override fun onReceive(c: Context, intent: Intent) {
                granted = intent.getBooleanExtra(UsbManager.EXTRA_PERMISSION_GRANTED, false)
                latch.countDown()
            }
        }
        val filter = IntentFilter(action)
        if (Build.VERSION.SDK_INT >= 33) {
            context.registerReceiver(receiver, filter, Context.RECEIVER_NOT_EXPORTED)
        } else {
            @Suppress("UnspecifiedRegisterReceiverFlag")
            context.registerReceiver(receiver, filter)
        }
        try {
            // Must be mutable: the system fills in the accessory and the grant result.
            val flags = PendingIntent.FLAG_UPDATE_CURRENT or
                if (Build.VERSION.SDK_INT >= 31) PendingIntent.FLAG_MUTABLE else 0
            val pending = PendingIntent.getBroadcast(context, 0, Intent(action).setPackage(context.packageName), flags)
            manager.requestPermission(accessory, pending)
            latch.await(PERMISSION_WAIT_MS, TimeUnit.MILLISECONDS)
        } finally {
            try { context.unregisterReceiver(receiver) } catch (_: Exception) {}
        }
        return granted
    }

    companion object {
        /** Must match the identification the PC sends (AoaHost.cs) and res/xml/accessory_filter.xml. */
        const val MANUFACTURER = "AudioBridge"
        const val MODEL = "AudioBridge"

        private const val WAIT_ACCESSORY_MS = 8_000L
        private const val PERMISSION_WAIT_MS = 30_000L

        /** True if an Audio Bridge accessory is attached right now. */
        fun isAccessoryAttached(context: Context): Boolean {
            val m = context.applicationContext.getSystemService(Context.USB_SERVICE) as UsbManager
            return m.accessoryList?.any { it.manufacturer == MANUFACTURER && it.model == MODEL } == true
        }
    }
}
