package com.audiobridge.transport

import android.annotation.SuppressLint
import android.bluetooth.BluetoothDevice
import android.bluetooth.BluetoothGatt
import android.bluetooth.BluetoothGattCharacteristic
import android.bluetooth.BluetoothGattDescriptor
import android.bluetooth.BluetoothGattServer
import android.bluetooth.BluetoothGattServerCallback
import android.bluetooth.BluetoothGattService
import android.bluetooth.BluetoothManager
import android.bluetooth.le.AdvertiseCallback
import android.bluetooth.le.AdvertiseData
import android.bluetooth.le.AdvertiseSettings
import android.content.Context
import android.os.Build
import android.os.ParcelUuid
import java.io.Closeable
import java.io.IOException
import java.io.InputStream
import java.io.OutputStream
import java.util.UUID
import java.util.concurrent.CountDownLatch
import java.util.concurrent.LinkedBlockingQueue
import java.util.concurrent.Semaphore
import java.util.concurrent.TimeUnit

/**
 * Bluetooth **LE** transport: the phone is the GATT server, the PC the client.
 *
 * Why not classic RFCOMM: on this phone the classic link stalls for 100-900 ms at a time (the radio
 * is shared with BLE scans of other apps), which no jitter buffer can hide without seconds of
 * latency. An LE connection is scheduled in fixed connection events that the controller serves
 * before scanning — it is the link type hearing aids and wireless headsets' control channels rely on.
 *
 * Two characteristics carry the same byte stream as every other transport:
 *  - RX (PC -> phone): WRITE_NO_RESPONSE — audio;
 *  - TX (phone -> PC): NOTIFY — microphone, volume, headset button, pings.
 * The phone advertises the service UUID; the PC scans for it and connects, no pairing needed.
 */
class BleTransport(private val context: Context) : Transport {
    override val kind = TransportKind.BLE

    @SuppressLint("MissingPermission") // BLUETOOTH_CONNECT / BLUETOOTH_ADVERTISE are requested by the UI before starting
    override fun connect(): OpenConnection {
        val manager = context.getSystemService(Context.BLUETOOTH_SERVICE) as BluetoothManager
        val adapter = manager.adapter ?: throw IOException("Bluetooth недоступен на этом устройстве")
        if (!adapter.isEnabled) throw IOException("Bluetooth выключен")
        val advertiser = adapter.bluetoothLeAdvertiser
            ?: throw IOException("Это устройство не умеет Bluetooth LE-рекламу")

        val link = BleLink()
        val server = manager.openGattServer(context, link.callback)
            ?: throw IOException("Не удалось открыть GATT-сервер")
        link.server = server

        val advertiseCallback = object : AdvertiseCallback() {
            override fun onStartFailure(errorCode: Int) { link.fail("реклама не запустилась (код $errorCode)") }
        }
        try {
            val service = BluetoothGattService(SERVICE_UUID, BluetoothGattService.SERVICE_TYPE_PRIMARY)
            val rx = BluetoothGattCharacteristic(
                RX_UUID,
                BluetoothGattCharacteristic.PROPERTY_WRITE_NO_RESPONSE or BluetoothGattCharacteristic.PROPERTY_WRITE,
                BluetoothGattCharacteristic.PERMISSION_WRITE,
            )
            val tx = BluetoothGattCharacteristic(
                TX_UUID,
                BluetoothGattCharacteristic.PROPERTY_NOTIFY,
                BluetoothGattCharacteristic.PERMISSION_READ,
            )
            tx.addDescriptor(
                BluetoothGattDescriptor(
                    CCCD_UUID,
                    BluetoothGattDescriptor.PERMISSION_READ or BluetoothGattDescriptor.PERMISSION_WRITE,
                )
            )
            service.addCharacteristic(rx)
            service.addCharacteristic(tx)
            link.tx = tx
            if (!server.addService(service)) throw IOException("GATT-сервис не добавлен")
            if (!link.serviceAdded.await(5, TimeUnit.SECONDS)) throw IOException("GATT-сервис не добавлен (таймаут)")

            val settings = AdvertiseSettings.Builder()
                .setAdvertiseMode(AdvertiseSettings.ADVERTISE_MODE_LOW_LATENCY)
                .setTxPowerLevel(AdvertiseSettings.ADVERTISE_TX_POWER_HIGH)
                .setConnectable(true)
                .setTimeout(0)
                .build()
            val data = AdvertiseData.Builder()
                .setIncludeDeviceName(false)
                .addServiceUuid(ParcelUuid(SERVICE_UUID))
                .build()
            advertiser.startAdvertising(settings, data, advertiseCallback)

            // Wait for the PC to connect and switch the TX notifications on.
            val deadline = System.currentTimeMillis() + WAIT_FOR_PC_MS
            while (!link.ready.await(250, TimeUnit.MILLISECONDS)) {
                link.failure?.let { throw IOException("Bluetooth LE: $it") }
                if (Thread.currentThread().isInterrupted) throw InterruptedException()
                if (System.currentTimeMillis() > deadline) {
                    throw IOException("ПК не подключился по Bluetooth LE: запусти сервер на ПК (Старт) и подожди пару секунд")
                }
            }
            try { advertiser.stopAdvertising(advertiseCallback) } catch (_: Exception) {}
        } catch (e: Throwable) {
            try { advertiser.stopAdvertising(advertiseCallback) } catch (_: Exception) {}
            link.close()
            throw e
        }
        return OpenConnection(link.input, link.output, link)
    }

    /** One LE session: the GATT server callback plus the stream views over it. */
    private class BleLink : Closeable {
        @Volatile var server: BluetoothGattServer? = null
        @Volatile var tx: BluetoothGattCharacteristic? = null
        @Volatile var device: BluetoothDevice? = null
        @Volatile var failure: String? = null
        @Volatile private var closed = false
        @Volatile private var mtu = 23

        val serviceAdded = CountDownLatch(1)
        val ready = CountDownLatch(1)
        private val incoming = LinkedBlockingQueue<ByteArray>()
        // One notification in flight at a time: the next may only be sent after onNotificationSent.
        private val notifySlot = Semaphore(1)

        fun fail(reason: String) { failure = reason }

        val callback = object : BluetoothGattServerCallback() {
            override fun onServiceAdded(status: Int, service: BluetoothGattService?) {
                if (status == BluetoothGatt.GATT_SUCCESS) serviceAdded.countDown() else fail("сервис не добавлен ($status)")
            }

            override fun onConnectionStateChange(dev: BluetoothDevice, status: Int, newState: Int) {
                if (newState == android.bluetooth.BluetoothProfile.STATE_CONNECTED) {
                    if (device == null) device = dev
                } else if (dev.address == device?.address) {
                    markClosed()
                }
            }

            override fun onMtuChanged(dev: BluetoothDevice, newMtu: Int) { mtu = newMtu }

            @SuppressLint("MissingPermission")
            override fun onCharacteristicWriteRequest(
                dev: BluetoothDevice, requestId: Int, characteristic: BluetoothGattCharacteristic,
                preparedWrite: Boolean, responseNeeded: Boolean, offset: Int, value: ByteArray?,
            ) {
                if (characteristic.uuid == RX_UUID && value != null) incoming.offer(value)
                if (responseNeeded) server?.sendResponse(dev, requestId, BluetoothGatt.GATT_SUCCESS, offset, value)
            }

            @SuppressLint("MissingPermission")
            override fun onDescriptorWriteRequest(
                dev: BluetoothDevice, requestId: Int, descriptor: BluetoothGattDescriptor,
                preparedWrite: Boolean, responseNeeded: Boolean, offset: Int, value: ByteArray?,
            ) {
                if (descriptor.uuid == CCCD_UUID && value != null &&
                    value.contentEquals(BluetoothGattDescriptor.ENABLE_NOTIFICATION_VALUE)
                ) {
                    device = dev
                    ready.countDown()
                }
                if (responseNeeded) server?.sendResponse(dev, requestId, BluetoothGatt.GATT_SUCCESS, offset, value)
            }

            // Windows reads characteristic/descriptor values while it discovers the service. A GATT server
            // that never answers a read makes that discovery time out ("service not found").
            @SuppressLint("MissingPermission")
            override fun onCharacteristicReadRequest(
                dev: BluetoothDevice, requestId: Int, offset: Int, characteristic: BluetoothGattCharacteristic,
            ) {
                server?.sendResponse(dev, requestId, BluetoothGatt.GATT_SUCCESS, offset, ByteArray(0))
            }

            @SuppressLint("MissingPermission")
            override fun onDescriptorReadRequest(
                dev: BluetoothDevice, requestId: Int, offset: Int, descriptor: BluetoothGattDescriptor,
            ) {
                val value = if (descriptor.uuid == CCCD_UUID && ready.count == 0L)
                    BluetoothGattDescriptor.ENABLE_NOTIFICATION_VALUE else BluetoothGattDescriptor.DISABLE_NOTIFICATION_VALUE
                server?.sendResponse(dev, requestId, BluetoothGatt.GATT_SUCCESS, offset, value)
            }

            override fun onNotificationSent(dev: BluetoothDevice, status: Int) { notifySlot.release() }
        }

        val input = object : InputStream() {
            private var current: ByteArray? = null
            private var pos = 0

            private fun fill(): Boolean {
                while (current == null || pos >= current!!.size) {
                    val next = incoming.poll(500, TimeUnit.MILLISECONDS)
                    if (next === EOF) { incoming.offer(EOF); return false }
                    if (next == null) {
                        if (closed) return false
                        continue
                    }
                    current = next
                    pos = 0
                }
                return true
            }

            override fun read(): Int {
                if (!fill()) return -1
                return current!![pos++].toInt() and 0xFF
            }

            override fun read(b: ByteArray, off: Int, len: Int): Int {
                if (len == 0) return 0
                if (!fill()) return -1
                val n = minOf(len, current!!.size - pos)
                System.arraycopy(current!!, pos, b, off, n)
                pos += n
                return n
            }
        }

        val output = object : OutputStream() {
            override fun write(b: Int) = write(byteArrayOf(b.toByte()), 0, 1)

            @SuppressLint("MissingPermission")
            override fun write(b: ByteArray, off: Int, len: Int) {
                var sent = 0
                // ATT payload = MTU - 3; stay inside what the PC negotiated.
                val chunk = (mtu - 3).coerceIn(20, 244)
                while (sent < len) {
                    if (closed) throw IOException("LE-соединение закрыто")
                    val n = minOf(chunk, len - sent)
                    if (!notifySlot.tryAcquire(2, TimeUnit.SECONDS)) throw IOException("LE: ПК не принимает данные")
                    val ch = tx ?: throw IOException("LE: нет характеристики")
                    val dev = device ?: throw IOException("LE: нет подключённого ПК")
                    val part = b.copyOfRange(off + sent, off + sent + n)
                    val ok = if (Build.VERSION.SDK_INT >= 33) {
                        server?.notifyCharacteristicChanged(dev, ch, false, part) == BluetoothGatt.GATT_SUCCESS
                    } else {
                        @Suppress("DEPRECATION")
                        ch.value = part
                        @Suppress("DEPRECATION")
                        server?.notifyCharacteristicChanged(dev, ch, false) == true
                    }
                    if (!ok) {
                        notifySlot.release()
                        throw IOException("LE: не удалось отправить уведомление")
                    }
                    sent += n
                }
            }
        }

        private fun markClosed() {
            closed = true
            incoming.offer(EOF)
            notifySlot.release()
        }

        @SuppressLint("MissingPermission")
        override fun close() {
            markClosed()
            try { device?.let { server?.cancelConnection(it) } } catch (_: Exception) {}
            try { server?.close() } catch (_: Exception) {}
            server = null
        }
    }

    companion object {
        // Must match pc-server/.../Core/BleServerListener.cs
        val SERVICE_UUID: UUID = UUID.fromString("7c9f1a2e-4b3d-4c7a-9e1f-2a6b8d4c5e40")
        val RX_UUID: UUID = UUID.fromString("7c9f1a2e-4b3d-4c7a-9e1f-2a6b8d4c5e41")
        val TX_UUID: UUID = UUID.fromString("7c9f1a2e-4b3d-4c7a-9e1f-2a6b8d4c5e42")
        val CCCD_UUID: UUID = UUID.fromString("00002902-0000-1000-8000-00805f9b34fb")

        private const val WAIT_FOR_PC_MS = 45_000L
        private val EOF = ByteArray(0)
    }
}
