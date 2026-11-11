package com.audiobridge

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import kotlin.math.ceil
import kotlin.math.max

/**
 * Drives [PlayoutBuffer] with arrival-time patterns measured on the real pipeline, against a
 * simulated output that drains in real time and records how long it starved.
 */
class PlayoutBufferTest {

    private val rate = 48_000
    private val channels = 2
    private val chunkMs = 20
    private val chunkSamples = rate * chunkMs / 1000 * channels
    private val chunk = FloatArray(chunkSamples) { 0.1f }

    /** Output that consumes frames in real (simulated) time once play() was called. */
    private class FakeSink(private val rate: Int, private val channels: Int) : PlayoutSink {
        var written = 0L
        private var played = 0.0
        private var playing = false
        private var last = 0.0
        var starvedMs = 0.0
        var starveEvents = 0
        private var starving = false
        var failWrites = false
        var maxBufferedMs = 0.0

        /** Real hardware takes a moment to start consuming after play(). */
        var startDelayMs = 0.0
        private var playFrom = 0.0

        fun advanceTo(t: Double) {
            if (playing) {
                val want = max(0.0, t - max(last, playFrom)) * rate / 1000.0
                val avail = written - played
                if (want <= avail) {
                    played += want
                    starving = false
                } else {
                    played = written.toDouble()
                    starvedMs += (want - avail) * 1000.0 / rate
                    if (!starving) starveEvents++
                    starving = true
                }
            }
            last = t
        }

        fun bufferedMs() = (written - played) * 1000.0 / rate

        override fun write(samples: FloatArray, count: Int): Int {
            if (failWrites) return -1
            written += count / channels
            maxBufferedMs = max(maxBufferedMs, bufferedMs())
            return count
        }

        override fun playedFrames(): Long = played.toLong()
        override fun play() { playing = true; playFrom = last + startDelayMs }
        override fun release() {}
    }

    private class Run(val buffer: PlayoutBuffer, val sink: FakeSink)

    private fun simulate(arrivals: List<Double>, targetMs: Int = 100): Run {
        val sink = FakeSink(rate, channels)
        val buffer = PlayoutBuffer(sink, rate, channels, targetMs)
        for (t in arrivals) {
            sink.advanceTo(t)
            assertTrue(buffer.write(chunk, chunkSamples))
        }
        return Run(buffer, sink)
    }

    private fun steady(seconds: Int) = (0 until seconds * 50).map { it * 20.0 }

    @Test
    fun steadyStreamNeverUnderruns() {
        val r = simulate(steady(60))
        assertEquals(0, r.buffer.underruns)
        assertEquals(0, r.buffer.dropped)
        assertEquals(0, r.sink.starveEvents)
    }

    @Test
    fun coarseTimerJitterIsAbsorbed() {
        // What the old PC pipeline produced: chunks released on a ~15.6 ms timer tick, so arrivals
        // are the ideal 20 ms schedule rounded UP to multiples of 15.6 ms (intervals 15.6/31.2).
        val arrivals = steady(60).map { ceil(it / 15.6) * 15.6 }
        val r = simulate(arrivals)
        assertEquals(0, r.buffer.underruns)
        assertEquals(0, r.sink.starveEvents)
    }

    @Test
    fun oldPollingCaptureBurstsAreAbsorbed() {
        // Old NAudio loopback polling: ~60 ms of audio delivered at once, i.e. 3 chunks per burst.
        val arrivals = (0 until 60 * 50).map { (it / 3) * 60.0 }
        val r = simulate(arrivals)
        assertEquals(0, r.buffer.underruns)
        assertEquals(0, r.buffer.dropped)
        assertEquals(0, r.sink.starveEvents)
    }

    @Test
    fun withoutACushionTheSameJitterWouldUnderrun() {
        // Control for the tests above: the pre-fix behaviour was effectively "play immediately,
        // nothing queued". Model that with the minimum target and a jitter bigger than it.
        // Every 7th chunk is held up by an extra 45 ms; chunks behind it can't overtake it (it's a
        // queue), so arrival times stay monotonic.
        var previous = 0.0
        val arrivals = steady(30).mapIndexed { i, t ->
            val delayed = ceil(t / 15.6) * 15.6 + if (i > 0 && i % 7 == 0) 45.0 else 0.0
            max(previous, delayed).also { previous = it }
        }
        val tiny = simulate(arrivals, targetMs = PlayoutBuffer.MIN_TARGET_MS)
        assertTrue("expected underruns with a 30 ms cushion", tiny.buffer.underruns > 0)
        val deep = simulate(arrivals, targetMs = 120)
        assertEquals(0, deep.buffer.underruns)
    }

    @Test
    fun targetEasesBackAfterTheLinkRecovers() {
        // A Bluetooth hiccup grows the target; once the link has been quiet for a while the target
        // must come back to the configured value instead of keeping the latency forever.
        var now = 0L
        val sink = FakeSink(rate, channels)
        val buffer = PlayoutBuffer(sink, rate, channels, 100, clock = { now })
        fun feed(t: Double) {
            now = t.toLong()
            sink.advanceTo(t)
            assertTrue(buffer.write(chunk, chunkSamples))
        }
        steady(5).forEach(::feed)
        // 1.2 s with nothing arriving, then the stream resumes.
        val resume = 5_000.0 + 1_200.0
        (0 until 50 * 90).forEach { feed(resume + it * 20.0) }
        assertTrue("the stall must have caused an underrun", buffer.underruns > 0)
        assertEquals("target should have decayed to the configured value", 100, buffer.targetMs)
    }

    @Test
    fun stallCausesOneGapThenRecovers() {
        val before = steady(5)
        // 400 ms stall; the backlog (20 chunks) then arrives at once, after which delivery is steady.
        val resume = 5000.0 + 400.0
        val backlog = List(20) { resume }
        val after = (1..1500).map { resume + it * 20.0 }
        val r = simulate(before + backlog + after)
        assertEquals(1, r.buffer.underruns)
        assertTrue("target should have grown after the underrun", r.buffer.targetMs > 100)
        // ...and the burst must not leave the stream stuck far behind real time.
        assertTrue(r.sink.bufferedMs() <= r.buffer.targetMs + PlayoutBuffer.OVERFLOW_SLACK_MS + chunkMs)
        assertEquals("no further underruns after recovering", 1, r.buffer.underruns)
    }

    @Test
    fun senderRunningAheadHasLatencyCapped() {
        // Sender clock 5% fast: without a cap the queue would grow without bound.
        val arrivals = (0 until 3000).map { it * 19.0 }
        val r = simulate(arrivals)
        assertEquals(0, r.buffer.underruns)
        assertTrue("some chunks must have been dropped", r.buffer.dropped > 0)
        assertTrue(
            "buffered ${r.sink.maxBufferedMs} ms exceeded the cap",
            r.sink.maxBufferedMs <= r.buffer.targetMs + PlayoutBuffer.OVERFLOW_SLACK_MS + 2 * chunkMs,
        )
    }

    @Test
    fun slowSenderTriggersRePrimeAndDeeperTarget() {
        val arrivals = (0 until 1500).map { it * 21.0 } // 5% slower than real time
        val r = simulate(arrivals)
        assertTrue(r.buffer.underruns >= 1)
        assertTrue(r.buffer.targetMs > 100)
    }

    @Test
    fun sinkFailureIsReported() {
        val sink = FakeSink(rate, channels)
        val buffer = PlayoutBuffer(sink, rate, channels, 100)
        assertTrue(buffer.write(chunk, chunkSamples))
        sink.failWrites = true
        sink.advanceTo(20.0)
        assertFalse(buffer.write(chunk, chunkSamples))
    }

    @Test
    fun settlesNearTheTargetAfterASlowHardwareStart() {
        // Measured on the phone: the output starts consuming ~150 ms after play(), so the queue
        // balloons by that much at the start. It must be trimmed back to the target (not parked at
        // the overflow threshold, which was ~200 ms of needless latency).
        val sink = FakeSink(rate, channels).also { it.startDelayMs = 150.0 }
        val buffer = PlayoutBuffer(sink, rate, channels, 120)
        for (t in steady(10)) {
            sink.advanceTo(t)
            buffer.write(chunk, chunkSamples)
        }
        assertEquals(0, buffer.underruns)
        assertTrue("dropped something to shed the start-up backlog", buffer.dropped > 0)
        assertTrue("settled at ${sink.bufferedMs()} ms", sink.bufferedMs() <= 120 + 2 * chunkMs)
    }

    @Test
    fun anOutputThatNeverConsumesIsDetectedAsStalled() {
        // Seen on the Xperia 1 IV: a 192 kHz float track reports "playing" but its head position
        // stays at 0 forever. That is not jitter and no amount of buffering fixes it.
        class DeadSink : PlayoutSink {
            override fun write(samples: FloatArray, count: Int) = count
            override fun playedFrames() = 0L
            override fun play() {}
            override fun release() {}
        }
        var now = 0L
        val buffer = PlayoutBuffer(DeadSink(), rate, channels, 100, clock = { now })
        buffer.write(chunk, chunkSamples)
        now = 1_000
        assertFalse("not stalled yet — give the hardware time to start", buffer.isStalled())
        now = PlayoutBuffer.STALL_AFTER_MS + 100
        assertTrue(buffer.isStalled())
    }

    @Test
    fun aHealthyOutputIsNeverReportedStalled() {
        var now = 0L
        val sink = FakeSink(rate, channels)
        val buffer = PlayoutBuffer(sink, rate, channels, 100, clock = { now })
        for (i in 0 until 400) {
            now = i * 20L
            sink.advanceTo(now.toDouble())
            buffer.write(chunk, chunkSamples)
            assertFalse(buffer.isStalled())
        }
    }

    @Test
    fun primingQueuesTheTargetAheadOfTheFirstChunk() {
        val sink = FakeSink(rate, channels)
        val buffer = PlayoutBuffer(sink, rate, channels, 120)
        sink.advanceTo(0.0)
        buffer.write(chunk, chunkSamples)
        // 120 ms of silence + the 20 ms chunk
        assertEquals(140.0, sink.bufferedMs(), 1.0)
    }
}
