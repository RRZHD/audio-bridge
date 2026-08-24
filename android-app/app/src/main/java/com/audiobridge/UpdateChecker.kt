package com.audiobridge

import org.json.JSONObject
import java.net.HttpURLConnection
import java.net.URL

data class UpdateInfo(val version: String, val apkUrl: String, val releaseUrl: String)

/** Checks GitHub Releases for a newer version — this app isn't on Play Store, so this is the
 *  update mechanism. Never throws; a failed/offline check just means no update is offered. */
object UpdateChecker {
    private const val REPO = "RRZHD/audio-bridge"
    private const val API_URL = "https://api.github.com/repos/$REPO/releases/latest"

    /** Blocking network call — run off the main thread. */
    fun checkForUpdate(currentVersion: String): UpdateInfo? {
        return try {
            val json = fetchJson(API_URL) ?: return null
            val tag = json.optString("tag_name", "").removePrefix("v")
            if (tag.isEmpty() || !isNewer(tag, currentVersion)) return null

            val assets = json.optJSONArray("assets") ?: return null
            var apkUrl: String? = null
            for (i in 0 until assets.length()) {
                val asset = assets.getJSONObject(i)
                if (asset.optString("name").endsWith(".apk")) {
                    apkUrl = asset.optString("browser_download_url")
                    break
                }
            }
            val apk = apkUrl ?: return null
            UpdateInfo(
                version = tag,
                apkUrl = apk,
                releaseUrl = json.optString("html_url", "https://github.com/$REPO/releases/latest"),
            )
        } catch (e: Exception) {
            null
        }
    }

    private fun fetchJson(urlStr: String): JSONObject? {
        val conn = URL(urlStr).openConnection() as HttpURLConnection
        return try {
            conn.setRequestProperty("Accept", "application/vnd.github+json")
            conn.setRequestProperty("User-Agent", "AudioBridge-Android-App") // GitHub API 403s without one
            conn.connectTimeout = 5000
            conn.readTimeout = 5000
            if (conn.responseCode != 200) return null
            val text = conn.inputStream.bufferedReader().use { it.readText() }
            JSONObject(text)
        } finally {
            conn.disconnect()
        }
    }

    /** Compares dotted numeric versions ("1.2.0" vs "1.10.0") component by component — plain
     *  string comparison would get "1.10.0" wrong (sorts before "1.2.0"). */
    private fun isNewer(remote: String, local: String): Boolean {
        val r = remote.split(".").map { it.toIntOrNull() ?: 0 }
        val l = local.split(".").map { it.toIntOrNull() ?: 0 }
        for (i in 0 until maxOf(r.size, l.size)) {
            val rv = r.getOrElse(i) { 0 }
            val lv = l.getOrElse(i) { 0 }
            if (rv != lv) return rv > lv
        }
        return false
    }
}
