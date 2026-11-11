package com.audiobridge

import java.io.EOFException
import java.io.InputStream
import java.io.OutputStream
import java.nio.ByteBuffer
import java.nio.ByteOrder

/** Mirrors pc-server/AudioBridge.Server/Core/FrameIO.cs — see PROTOCOL.md. */
object FrameType {
    const val AUDIO_PC: Byte = 0x01
    const val AUDIO_MIC: Byte = 0x02
    const val FORMAT: Byte = 0x03
    const val CONFIG: Byte = 0x04
    const val VOLUME: Byte = 0x05 // u8 level 0..100, u8 flags (bit0 = muted) — both directions
    const val MEDIA: Byte = 0x06 // u8 command: 1 = play/pause, 2 = next, 3 = previous (phone -> PC)
    const val PING: Byte = 0xFF.toByte()
}

object PcAudioCodecKind {
    const val PCM_FLOAT32: Byte = 0x00
    const val OPUS: Byte = 0x01
}

data class Frame(val type: Byte, val payload: ByteArray)

data class PcFormat(val sampleRate: Int, val channels: Int, val codec: Byte)

/** Frame I/O: byte type, uint32 length (LE), payload. Writes are synchronized per-stream by the caller. */
object FrameIO {

    fun writeFrame(out: OutputStream, type: Byte, payload: ByteArray) {
        val header = ByteBuffer.allocate(5).order(ByteOrder.LITTLE_ENDIAN)
        header.put(type)
        header.putInt(payload.size)
        out.write(header.array())
        if (payload.isNotEmpty()) out.write(payload)
        out.flush()
    }

    fun readFrame(input: InputStream): Frame {
        val header = readExact(input, 5)
        val buf = ByteBuffer.wrap(header).order(ByteOrder.LITTLE_ENDIAN)
        val type = buf.get()
        val length = buf.int
        require(length in 0..10 * 1024 * 1024) { "Invalid frame length $length" }
        val payload = if (length == 0) ByteArray(0) else readExact(input, length)
        return Frame(type, payload)
    }

    /**
     * CONFIG (phone -> PC, once right after connecting):
     * `uint32 pcAudioBitrateBps, uint32 maxRawSampleRate, byte nameLen, utf8 name` — the PC reads
     * as much of it as is present, so older/newer peers interoperate. See PROTOCOL.md.
     */
    fun buildConfigPayload(pcAudioBitrateBps: Int, maxRawSampleRate: Int, deviceName: String, wifiQuality: Int = 0): ByteArray {
        val name = deviceName.toByteArray(Charsets.UTF_8).let { if (it.size > 255) it.copyOf(255) else it }
        return ByteBuffer.allocate(4 + 4 + 1 + name.size + 1).order(ByteOrder.LITTLE_ENDIAN)
            .putInt(pcAudioBitrateBps)
            .putInt(maxRawSampleRate)
            .put(name.size.toByte())
            .put(name)
            .put((wifiQuality and 3).toByte()) // v3 flags: Wi-Fi quality, 0 = auto, 1 = lossless, 2 = compressed
            .array()
    }

    fun parseFormat(payload: ByteArray): PcFormat {
        val buf = ByteBuffer.wrap(payload).order(ByteOrder.LITTLE_ENDIAN)
        val sampleRate = buf.int
        val channels = buf.get().toInt()
        val codec = buf.get()
        return PcFormat(sampleRate, channels, codec)
    }

    private fun readExact(input: InputStream, count: Int): ByteArray {
        val buf = ByteArray(count)
        var offset = 0
        while (offset < count) {
            val read = input.read(buf, offset, count - offset)
            if (read == -1) throw EOFException("Stream closed by remote end")
            offset += read
        }
        return buf
    }
}
