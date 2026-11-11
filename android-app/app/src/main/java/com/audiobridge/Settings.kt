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

    /**
     * Highest sample rate the PC may send on the lossless Wi-Fi/USB path. Default 48 kHz: the
     * Xperia 1 IV's audio stack accepted a 192 kHz float track but never played a single frame of
     * it, and Android mixes to the output's native rate (48 kHz) anyway. Lowered automatically if
     * a higher rate is chosen and the device turns out not to play it.
     */
    var maxRawSampleRate: Int
        get() = prefs.getInt("max_raw_rate", DEFAULT_MAX_RAW_RATE)
        set(value) = prefs.edit().putInt("max_raw_rate", value).apply()

    /** Wi-Fi stream quality: 0 = auto (lossless, drops to Opus when the link cannot keep up), 1 = lossless only, 2 = compressed. */
    var wifiQuality: Int
        get() = prefs.getInt("wifi_quality", 0)
        set(value) = prefs.edit().putInt("wifi_quality", value).apply()

    /** Send this phone's microphone to the PC. Off by default (privacy; also avoids surprise playback). */
    var micEnabled: Boolean
        get() = prefs.getBoolean("mic_enabled", false)
        set(value) = prefs.edit().putBoolean("mic_enabled", value).apply()

    /** Playback jitter buffer (ms) — the latency-vs-robustness knob for choppy links. */
    var bufferMs: Int
        get() = prefs.getInt("buffer_ms", AudioService.DEFAULT_BUFFER_MS)
        set(value) = prefs.edit().putInt("buffer_ms", value).apply()
}

const val DEFAULT_MAX_RAW_RATE = 48_000
