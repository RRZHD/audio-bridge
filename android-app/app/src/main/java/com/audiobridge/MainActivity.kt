package com.audiobridge

import android.Manifest
import android.bluetooth.BluetoothAdapter
import android.bluetooth.BluetoothDevice
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import android.os.Bundle
import android.widget.ArrayAdapter
import android.widget.Button
import android.widget.EditText
import android.widget.RadioGroup
import android.widget.Spinner
import android.widget.TextView
import androidx.appcompat.app.AppCompatActivity
import androidx.core.content.ContextCompat

class MainActivity : AppCompatActivity() {

    private lateinit var modeGroup: RadioGroup
    private lateinit var wifiFields: android.view.View
    private lateinit var bluetoothFields: android.view.View
    private lateinit var hostInput: EditText
    private lateinit var portInput: EditText
    private lateinit var bluetoothDeviceSpinner: Spinner
    private lateinit var connectButton: Button
    private lateinit var statusText: TextView
    private lateinit var logText: TextView

    private var connected = false
    private var pairedDevices: List<BluetoothDevice> = emptyList()

    private val requiredPermissions: Array<String>
        get() {
            val perms = mutableListOf(Manifest.permission.RECORD_AUDIO)
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
                perms.add(Manifest.permission.BLUETOOTH_CONNECT)
            }
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
                perms.add(Manifest.permission.POST_NOTIFICATIONS)
            }
            return perms.toTypedArray()
        }

    private val permissionLauncher = registerForActivityResult(
        androidx.activity.result.contract.ActivityResultContracts.RequestMultiplePermissions()
    ) { results ->
        if (results.values.all { it }) {
            doConnect()
        } else {
            statusText.text = "Нет разрешений (микрофон/Bluetooth/уведомления)"
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_main)

        modeGroup = findViewById(R.id.modeGroup)
        wifiFields = findViewById(R.id.wifiFields)
        bluetoothFields = findViewById(R.id.bluetoothFields)
        hostInput = findViewById(R.id.hostInput)
        portInput = findViewById(R.id.portInput)
        bluetoothDeviceSpinner = findViewById(R.id.bluetoothDeviceSpinner)
        connectButton = findViewById(R.id.connectButton)
        statusText = findViewById(R.id.statusText)
        logText = findViewById(R.id.logText)

        modeGroup.setOnCheckedChangeListener { _, checkedId ->
            wifiFields.visibility = if (checkedId == R.id.modeWifi) android.view.View.VISIBLE else android.view.View.GONE
            bluetoothFields.visibility = if (checkedId == R.id.modeBluetooth) android.view.View.VISIBLE else android.view.View.GONE
            if (checkedId == R.id.modeBluetooth) loadPairedDevices()
        }

        connectButton.setOnClickListener {
            if (connected) doDisconnect() else requestPermissionsAndConnect()
        }

        StatusBus.statusListener = { text ->
            statusText.text = text
            connected = text.startsWith("Подключено")
            connectButton.text = if (connected) "Отключиться" else "Подключиться"
        }
        StatusBus.logListener = { line ->
            logText.text = ((logText.text.toString().lines().takeLast(20) + line).joinToString("\n"))
        }
    }

    private fun requestPermissionsAndConnect() {
        val missing = requiredPermissions.filter {
            ContextCompat.checkSelfPermission(this, it) != PackageManager.PERMISSION_GRANTED
        }
        if (missing.isEmpty()) doConnect() else permissionLauncher.launch(missing.toTypedArray())
    }

    @android.annotation.SuppressLint("MissingPermission")
    private fun loadPairedDevices() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S &&
            ContextCompat.checkSelfPermission(this, Manifest.permission.BLUETOOTH_CONNECT) != PackageManager.PERMISSION_GRANTED
        ) {
            permissionLauncher.launch(arrayOf(Manifest.permission.BLUETOOTH_CONNECT))
            return
        }
        val adapter = BluetoothAdapter.getDefaultAdapter()
        pairedDevices = adapter?.bondedDevices?.toList() ?: emptyList()
        val names = pairedDevices.map { "${it.name} (${it.address})" }
        bluetoothDeviceSpinner.adapter = ArrayAdapter(this, android.R.layout.simple_spinner_dropdown_item, names)
    }

    private fun doConnect() {
        val mode = when (modeGroup.checkedRadioButtonId) {
            R.id.modeWifi -> "WIFI"
            R.id.modeUsb -> "USB"
            R.id.modeBluetooth -> "BLUETOOTH"
            else -> "WIFI"
        }
        val port = portInput.text.toString().toIntOrNull() ?: 57120

        val intent = Intent(this, AudioService::class.java).apply {
            action = AudioService.ACTION_START
            putExtra(AudioService.EXTRA_MODE, mode)
            putExtra(AudioService.EXTRA_PORT, port)
            if (mode == "WIFI") putExtra(AudioService.EXTRA_HOST, hostInput.text.toString())
            if (mode == "BLUETOOTH") {
                val index = bluetoothDeviceSpinner.selectedItemPosition
                val device = pairedDevices.getOrNull(index)
                if (device == null) {
                    statusText.text = "Выберите сопряжённое Bluetooth-устройство"
                    return
                }
                putExtra(AudioService.EXTRA_BT_ADDRESS, device.address)
            }
        }
        ContextCompat.startForegroundService(this, intent)
    }

    private fun doDisconnect() {
        val intent = Intent(this, AudioService::class.java).apply { action = AudioService.ACTION_STOP }
        startService(intent)
    }
}
