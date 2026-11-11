package com.audiobridge

import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import kotlin.math.PI
import kotlin.math.abs
import kotlin.math.sin

class CodecsTest {

    private fun stereoTone(frames: Int, rate: Int, startFrame: Int = 0): FloatArray {
        val out = FloatArray(frames * 2)
        for (i in 0 until frames) {
            val v = (0.5 * sin(2 * PI * 440.0 * (startFrame + i) / rate)).toFloat()
            out[i * 2] = v
            out[i * 2 + 1] = v
        }
        return out
    }

    @Test
    fun rawDecodeReturnsInterleavedSampleCount() {
        // Regression: raw returned the interleaved count while the caller multiplied by channels
        // again, writing 2x the real data (second half stale zeros) to the AudioTrack.
        val codec = RawFloat32PcCodec()
        val frames = 960
        val input = stereoTone(frames, 48_000)
        val payload = codec.encode(input, frames)
        val out = FloatArray(frames * 2 * 2)
        val n = codec.decode(payload, out)
        assertEquals(frames * 2, n)
        assertArrayEquals(input, out.copyOf(n), 0f)
    }

    @Test
    fun opusDecodeReturnsInterleavedSampleCount() {
        val codec = OpusPcCodec(48_000, 2, 320_000)
        val frames = 960
        val payload = codec.encode(stereoTone(frames, 48_000), frames)
        val out = FloatArray(frames * 2 * 2)
        val n = codec.decode(payload, out)
        assertEquals("every codec must report interleaved samples", frames * 2, n)
    }

    @Test
    fun opusStreamRoundTripsAContinuousTone() {
        val enc = OpusPcCodec(48_000, 2, 320_000)
        val dec = OpusPcCodec(48_000, 2, 320_000)
        val frames = 960
        val out = FloatArray(frames * 2 * 2)
        val decoded = ArrayList<Float>()
        for (chunk in 0 until 50) {
            val n = dec.decode(enc.encode(stereoTone(frames, 48_000, chunk * frames), frames), out)
            assertEquals(frames * 2, n)
            for (i in 0 until n step 2) decoded.add(out[i])
        }
        // After the codec's startup, the decoded left channel must be a clean 440 Hz tone: no
        // chunk-boundary discontinuities (second difference stays within the sine's own bound).
        val x = decoded.drop(frames * 5).toFloatArray()
        val w = 2 * PI * 440.0 / 48_000
        val bound = x.maxOf { abs(it) } * w * w
        var glitches = 0
        for (i in 2 until x.size) {
            if (abs(x[i] - 2 * x[i - 1] + x[i - 2]) > 8 * bound + 2e-4) glitches++
        }
        assertTrue("found $glitches discontinuities", glitches == 0)
    }
}
