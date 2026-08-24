package com.audiobridge

import android.content.Context

/** Small SharedPreferences wrapper — persists the last-used connection settings and, per the
 *  product decision, the Opus bitrates (these live in the phone app's UI, not hardcoded on the PC —
 *  the PC-audio bitrate is pushed to the server via a CONFIG frame at connect time; the mic bitrate
 *  only matters locally since the phone is the mic encoder). */
class Settings(context: Context) {
    private val prefs = context.applicationContext.getSharedPreferences("audiobridge_settings", Context.MODE_PRIVATE)

    var mode: String
        get() = prefs.getString("mode", "WIFI") ?: "WIFI"
        set(value) = prefs.edit().putString("mode", value).apply()

    var host: String
        get() = prefs.getString("host", "") ?: ""
        set(value) = prefs.edit().putString("host", value).apply()

    var port: Int
        get() = prefs.getInt("port", 57120)
        set(value) = prefs.edit().putInt("port", value).apply()

    var btAddress: String
        get() = prefs.getString("bt_address", "") ?: ""
        set(value) = prefs.edit().putString("bt_address", value).apply()

    var pcBitrateBps: Int
        get() = prefs.getInt("pc_bitrate", AudioService.DEFAULT_PC_BITRATE)
        set(value) = prefs.edit().putInt("pc_bitrate", value).apply()

    var micBitrateBps: Int
        get() = prefs.getInt("mic_bitrate", AudioService.DEFAULT_MIC_BITRATE)
        set(value) = prefs.edit().putInt("mic_bitrate", value).apply()
}
