package com.audiobridge

import android.media.AudioAttributes
import android.media.AudioFormat
import android.media.AudioTrack
import kotlin.math.max
import kotlin.math.min

/** The little of an audio output that [PlayoutBuffer] needs — lets the buffering logic be
 *  unit-tested on the JVM against a simulated clock instead of a real device. */
interface PlayoutSink {
    /** Blocking write of interleaved float samples. Returns samples written, or a negative error. */
    fun write(samples: FloatArray, count: Int): Int

    /** Frames the hardware has consumed so far (monotonic, 64-bit). */
    fun playedFrames(): Long

    fun play()
    fun release()

    /** Device-specific state for the stats line (play state, underruns, ...). */
    fun diagnostics(): String = ""
}

/**
 * Jitter buffer in front of the audio output.
 *
 * Before this existed, every received chunk was written to AudioTrack the instant it arrived and
 * play() started on an empty buffer — so the buffer hovered around 0 and any chunk arriving a few
 * ms late meant an underrun (a click/dropout). Network jitter is normal on Wi-Fi, adb and
 * especially Bluetooth, so playback has to keep a cushion queued:
 *
 *  - **prime:** start with [targetMs] of silence queued ahead of the first chunk;
 *  - **underrun:** if the hardware drained everything, queue silence back up to the target (one
 *    small gap instead of a stream of them) and raise the target a step, so a link with more jitter
 *    settles on a deeper buffer by itself;
 *  - **overflow:** if the buffer has grown far past the target (sender running ahead), drop the
 *    incoming chunk instead of letting latency creep up forever.
 *
 * Buffered amount is measured from AudioTrack's real playback position, not estimated.
 */
class PlayoutBuffer(
    private val sink: PlayoutSink,
    private val sampleRate: Int,
    private val channels: Int,
    initialTargetMs: Int,
    private val onEvent: (String) -> Unit = {},
    private val clock: () -> Long = System::currentTimeMillis,
) {
    companion object {
        const val DEFAULT_TARGET_MS = 120
        const val MIN_TARGET_MS = 30
        const val MAX_TARGET_MS = 600
        const val OVERFLOW_SLACK_MS = 80
        /** If the hardware has not consumed a single frame this long after play(), it is not playing this stream. */
        const val STALL_AFTER_MS = 2500L
        private const val GROW_STEP_MS = 40
        private const val UNDERRUN_THRESHOLD_MS = 4
    }

    var targetMs: Int = initialTargetMs.coerceIn(MIN_TARGET_MS, MAX_TARGET_MS)
        private set
    var underruns = 0
        private set
    var dropped = 0
        private set
    var framesWritten = 0L
        private set

    private var started = false
    private var playStartedAtMs = 0L
    private var trimming = false
    private val silenceFrames = framesFor(50)
    private val silence = FloatArray(silenceFrames * channels)

    private fun framesFor(ms: Int): Int = (sampleRate.toLong() * ms / 1000).toInt()

    private fun bufferedFrames(): Long = max(0L, framesWritten - sink.playedFrames())

    fun bufferedMs(): Int = (bufferedFrames() * 1000 / sampleRate).toInt()

    /** @param sampleCount interleaved samples in [samples]. @return false if the sink failed. */
    fun write(samples: FloatArray, sampleCount: Int): Boolean {
        if (!started) {
            writeSilence(framesFor(targetMs))
            if (!put(samples, sampleCount)) return false
            sink.play()
            playStartedAtMs = clock()
            started = true
            return true
        }

        val buffered = bufferedFrames()
        if (buffered <= framesFor(UNDERRUN_THRESHOLD_MS)) {
            underruns++
            targetMs = min(MAX_TARGET_MS, targetMs + GROW_STEP_MS)
            onEvent("Недогрузка буфера (#$underruns) — цель теперь $targetMs мс")
            writeSilence(max(0L, framesFor(targetMs) - buffered).toInt())
        } else {
            // Overflow: the sender is ahead of playback (the hardware also "wakes up" ~150 ms after
            // play(), during which chunks keep arriving). Once the queue passes target + slack, skip
            // incoming chunks until it is back near the target — not merely back under the
            // threshold, or the stream would sit at the threshold (~200 ms) forever.
            if (buffered > framesFor(targetMs + OVERFLOW_SLACK_MS)) trimming = true
            if (trimming) {
                if (buffered > framesFor(targetMs) + sampleCount / channels) {
                    dropped++
                    return true
                }
                trimming = false
            }
        }
        return put(samples, sampleCount)
    }

    /**
     * True when play() was called a while ago yet the hardware has consumed nothing. That is not
     * jitter — the output simply isn't playing this stream (observed: a 192 kHz float track on the
     * Xperia 1 IV's wired-headset route reports "playing" with the head stuck at 0 forever).
     */
    fun isStalled(): Boolean =
        started && clock() - playStartedAtMs > STALL_AFTER_MS && sink.playedFrames() == 0L

    fun statsLine(): String =
        "буфер ${bufferedMs()} мс (цель $targetMs), недогрузок $underruns, сброшено $dropped; ${sink.diagnostics()}"

    fun release() = sink.release()

    private fun writeSilence(frames: Int) {
        var remaining = frames
        while (remaining > 0) {
            val n = min(remaining, silenceFrames)
            if (!put(silence, n * channels)) return
            remaining -= n
        }
    }

    private fun put(samples: FloatArray, sampleCount: Int): Boolean {
        val written = sink.write(samples, sampleCount)
        if (written < 0) return false
        framesWritten += written / channels
        return true
    }
}

/** [PlayoutSink] backed by a real AudioTrack. */
class AudioTrackSink(private val track: AudioTrack) : PlayoutSink {
    private var lastRaw = 0L
    private var wraps = 0L

    override fun write(samples: FloatArray, count: Int): Int =
        track.write(samples, 0, count, AudioTrack.WRITE_BLOCKING)

    // playbackHeadPosition is a 32-bit counter that wraps; widen it to a monotonic 64-bit value.
    override fun playedFrames(): Long {
        val raw = track.playbackHeadPosition.toLong() and 0xFFFFFFFFL
        if (raw < lastRaw && lastRaw - raw > 0x80000000L) wraps++
        lastRaw = raw
        return (wraps shl 32) + raw
    }

    override fun play() = track.play()

    override fun diagnostics(): String {
        val state = when (track.playState) {
            AudioTrack.PLAYSTATE_PLAYING -> "playing"
            AudioTrack.PLAYSTATE_PAUSED -> "paused"
            AudioTrack.PLAYSTATE_STOPPED -> "stopped"
            else -> "?"
        }
        return "track=$state head=${track.playbackHeadPosition} underruns=${track.underrunCount} " +
            "cap=${track.bufferSizeInFrames}fr ${track.sampleRate}Hz"
    }

    override fun release() {
        try { track.stop() } catch (_: Exception) { /* already stopped */ }
        try { track.release() } catch (_: Exception) { /* already released */ }
    }
}

/** Creates the output track with room for the deepest jitter buffer plus overflow headroom. */
fun createPlaybackTrack(sampleRate: Int, channels: Int): AudioTrack {
    val channelMask = if (channels >= 2) AudioFormat.CHANNEL_OUT_STEREO else AudioFormat.CHANNEL_OUT_MONO
    val format = AudioFormat.Builder()
        .setEncoding(AudioFormat.ENCODING_PCM_FLOAT)
        .setSampleRate(sampleRate)
        .setChannelMask(channelMask)
        .build()
    val attrs = AudioAttributes.Builder()
        .setUsage(AudioAttributes.USAGE_MEDIA)
        .setContentType(AudioAttributes.CONTENT_TYPE_MUSIC)
        .build()
    val minBuf = AudioTrack.getMinBufferSize(sampleRate, channelMask, AudioFormat.ENCODING_PCM_FLOAT)
    val capacityMs = PlayoutBuffer.MAX_TARGET_MS + PlayoutBuffer.OVERFLOW_SLACK_MS + 100
    val bytes = (sampleRate.toLong() * capacityMs / 1000 * channels * 4).toInt()
    return AudioTrack.Builder()
        .setAudioAttributes(attrs)
        .setAudioFormat(format)
        .setBufferSizeInBytes(max(minBuf, bytes))
        .setTransferMode(AudioTrack.MODE_STREAM)
        // A deep, deliberate buffer is the point here; the "low latency" fast-track path (tiny
        // fixed buffers, and not available for non-native rates such as 192 kHz anyway) would
        // work against it.
        .setPerformanceMode(AudioTrack.PERFORMANCE_MODE_LOW_LATENCY)
        .build()
}
