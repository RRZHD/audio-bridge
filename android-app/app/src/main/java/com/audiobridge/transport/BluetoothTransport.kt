package com.audiobridge.transport

import android.annotation.SuppressLint
import android.bluetooth.BluetoothAdapter
import java.util.UUID

/** Custom RFCOMM service — matches BluetoothServerListener.ServiceUuid on the PC side. Requires the
 *  devices to already be paired in Android/Windows Bluetooth settings. */
class BluetoothTransport(private val deviceAddress: String) : Transport {
    override val kind = TransportKind.BLUETOOTH

    companion object {
        val SERVICE_UUID: UUID = UUID.fromString("7c9f1a2e-4b3d-4c7a-9e1f-2a6b8d4c5e3f")
    }

    @SuppressLint("MissingPermission") // caller is responsible for having requested BLUETOOTH_CONNECT
    override fun connect(): OpenConnection {
        val adapter = BluetoothAdapter.getDefaultAdapter()
            ?: error("Bluetooth adapter not available")
        // Deliberately no adapter.cancelDiscovery() here — it requires BLUETOOTH_SCAN (API 31+),
        // a permission this app otherwise has no use for since it only ever connects to already-
        // paired devices (see MainActivity.refreshPairedDevices, which just reads bondedDevices).
        // Not calling it just means a connect attempt during an active scan may be slightly less
        // reliable, which is an acceptable trade for not requesting an extra permission.
        val device = adapter.getRemoteDevice(deviceAddress)
        val socket = device.createRfcommSocketToServiceRecord(SERVICE_UUID)
        socket.connect()
        return OpenConnection(socket.inputStream, socket.outputStream, socket)
    }
}
