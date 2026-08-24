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

    fun buildConfigPayload(pcAudioBitrateBps: Int): ByteArray =
        ByteBuffer.allocate(4).order(ByteOrder.LITTLE_ENDIAN).putInt(pcAudioBitrateBps).array()

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
