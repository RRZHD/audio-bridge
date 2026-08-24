package com.audiobridge

import org.concentus.OpusApplication
import org.concentus.OpusDecoder
import org.concentus.OpusEncoder
import kotlin.math.max
import kotlin.math.min

/** Mirrors pc-server/AudioBridge.Server/Core/Codecs.cs — kept symmetric so both sides agree on the
 *  wire format for a given transport without needing runtime codec negotiation. */

interface PcAudioCodec {
    /** Encode interleaved float PCM (-1..1) into wire bytes. */
    fun encode(interleaved: FloatArray, sampleCountPerChannel: Int): ByteArray

    /** Decode wire bytes into interleaved float PCM, returns sample count per channel. */
    fun decode(payload: ByteArray, outInterleaved: FloatArray): Int
}

interface MicAudioCodec {
    fun encode(pcm: ShortArray, sampleCount: Int): ByteArray
    fun decode(payload: ByteArray, outPcm: ShortArray): Int
}

/** Lossless passthrough — Wi-Fi and USB. */
class RawFloat32PcCodec : PcAudioCodec {
    override fun encode(interleaved: FloatArray, sampleCountPerChannel: Int): ByteArray {
        val bb = java.nio.ByteBuffer.allocate(interleaved.size * 4).order(java.nio.ByteOrder.LITTLE_ENDIAN)
        for (v in interleaved) bb.putFloat(v)
        return bb.array()
    }

    override fun decode(payload: ByteArray, outInterleaved: FloatArray): Int {
        val bb = java.nio.ByteBuffer.wrap(payload).order(java.nio.ByteOrder.LITTLE_ENDIAN)
        val count = payload.size / 4
        for (i in 0 until count) outInterleaved[i] = bb.float
        return count
    }
}

class RawPcm16MicCodec : MicAudioCodec {
    override fun encode(pcm: ShortArray, sampleCount: Int): ByteArray {
        val bb = java.nio.ByteBuffer.allocate(sampleCount * 2).order(java.nio.ByteOrder.LITTLE_ENDIAN)
        for (i in 0 until sampleCount) bb.putShort(pcm[i])
        return bb.array()
    }

    override fun decode(payload: ByteArray, outPcm: ShortArray): Int {
        val bb = java.nio.ByteBuffer.wrap(payload).order(java.nio.ByteOrder.LITTLE_ENDIAN)
        val count = payload.size / 2
        for (i in 0 until count) outPcm[i] = bb.short
        return count
    }
}

/** Bluetooth RFCOMM — fixed 48kHz/stereo, high bitrate (see PROTOCOL.md for the quality rationale). */
class OpusPcCodec(sampleRate: Int, private val channels: Int, bitrateBps: Int) : PcAudioCodec {
    private val encoder = OpusEncoder(sampleRate, channels, OpusApplication.OPUS_APPLICATION_AUDIO).apply {
        setBitrate(bitrateBps)
    }
    private val decoder = OpusDecoder(sampleRate, channels)
    private val scratch = ByteArray(8000)
    private val pcmScratch = ShortArray(sampleRate / 10 * channels)

    override fun encode(interleaved: FloatArray, sampleCountPerChannel: Int): ByteArray {
        val total = sampleCountPerChannel * channels
        for (i in 0 until total) {
            val v = min(1f, max(-1f, interleaved[i]))
            pcmScratch[i] = (v * Short.MAX_VALUE).toInt().toShort()
        }
        val len = encoder.encode(pcmScratch, 0, sampleCountPerChannel, scratch, 0, scratch.size)
        return scratch.copyOf(len)
    }

    override fun decode(payload: ByteArray, outInterleaved: FloatArray): Int {
        val frameSize = outInterleaved.size / channels
        val decoded = decoder.decode(payload, 0, payload.size, pcmScratch, 0, frameSize, false)
        val total = decoded * channels
        for (i in 0 until total) outInterleaved[i] = pcmScratch[i] / Short.MAX_VALUE.toFloat()
        return decoded
    }
}

class OpusMicCodec(sampleRate: Int, bitrateBps: Int) : MicAudioCodec {
    private val encoder = OpusEncoder(sampleRate, 1, OpusApplication.OPUS_APPLICATION_VOIP).apply {
        setBitrate(bitrateBps)
    }
    private val decoder = OpusDecoder(sampleRate, 1)
    private val scratch = ByteArray(4000)

    override fun encode(pcm: ShortArray, sampleCount: Int): ByteArray {
        val len = encoder.encode(pcm, 0, sampleCount, scratch, 0, scratch.size)
        return scratch.copyOf(len)
    }

    override fun decode(payload: ByteArray, outPcm: ShortArray): Int {
        return decoder.decode(payload, 0, payload.size, outPcm, 0, outPcm.size, false)
    }
}
