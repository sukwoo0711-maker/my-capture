using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Core.Primitives;
using MyCapture.Core.Recording;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Recording;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>Every source is fake. The only native audio work is synthetic-PCM MF conversion.</summary>
public sealed class RecordingAudioCaptureTests
{
    [Theory]
    [InlineData("no endpoint", unchecked((int)0x80070490))]
    [InlineData("unplug", unchecked((int)0x88890004))]
    [InlineData("default changed", unchecked((int)0x88890010))]
    [InlineData("exclusive use", unchecked((int)0x8889000A))]
    [InlineData("format changed", unchecked((int)0x88890008))]
    [InlineData("service missing", unchecked((int)0x88890010))]
    [InlineData("access denied", unchecked((int)0x80070005))]
    [InlineData("timestamp error", unchecked((int)0x80004005))]
    public void SourceFailureBecomesSilenceAndOtherSourceContinues(string failure, int hresult)
    {
        var clock = new ManualClock();
        var sources = new FakeSources();
        using var session = new RecordingAudioSession(clock.GetTimestamp(), clock.TimestampFrequency,
            true, true, sources.Create, clock);
        FakeSource system = sources.Get(RecordingAudioSourceKind.System);
        FakeSource microphone = sources.Get(RecordingAudioSourceKind.Microphone);
        system.Fail(new COMException(failure, hresult));
        Wait(() => session.Snapshot().System.Status.State == AudioSourceState.Unavailable);
        microphone.Send(Packet(clock, 0, .4f));
        microphone.Send(Packet(clock, 480, .4f));
        microphone.Send(Packet(clock, 960, .4f));
        clock.Advance(30);
        RecordingAudioBlock mixed = session.RenderNext(960, 960);
        Assert.Contains(mixed.Samples.ToArray(), value => value > 1_000);
        Assert.All(mixed.Samples.ToArray(), value => Assert.InRange(value, (short)0, (short)8_000));
        session.Commit(mixed);
        Assert.Equal(1, session.Snapshot().System.Failures);
        Assert.True(session.Snapshot().System.Status.Enabled);
        Assert.Equal(AudioSourceState.Capturing, session.Snapshot().Microphone.Status.State);
        Stop(session, clock);
    }

    [Fact]
    public void MicrophoneMutePauseResumeAndStopInvalidateOldCallbacksAndReleaseObjects()
    {
        var clock = new ManualClock(); var sources = new FakeSources();
        using var session = new RecordingAudioSession(clock.GetTimestamp(), clock.TimestampFrequency,
            true, false, sources.Create, clock);
        _ = sources.Get(RecordingAudioSourceKind.System);
        Assert.Equal(0, sources.Count(RecordingAudioSourceKind.Microphone));
        session.SetSources(true, true);
        FakeSource firstMic = sources.Get(RecordingAudioSourceKind.Microphone);
        firstMic.Send(Packet(clock, 0, .5f));
        long firstGeneration = session.Snapshot().Microphone.Status.Generation;
        clock.Advance(5);
        session.SetSources(true, false);
        Wait(() => session.Snapshot().Microphone.ActiveObjects == 0);
        firstMic.ReplayOld(Packet(clock, 480, 1));
        Assert.True(session.Snapshot().Microphone.RejectedPackets > 0);
        session.SetSources(true, true);
        _ = sources.Get(RecordingAudioSourceKind.Microphone, 2);
        Assert.True(session.Snapshot().Microphone.Status.Generation > firstGeneration);
        session.Pause(clock.GetTimestamp());
        Wait(() => session.Snapshot().Microphone.ActiveObjects == 0 && session.Snapshot().System.ActiveObjects == 0);
        clock.Advance(1_000);
        session.Resume(clock.GetTimestamp());
        _ = sources.Get(RecordingAudioSourceKind.Microphone, 3);
        clock.Advance(5);
        Stop(session, clock);
        Assert.Single(session.Snapshot().PauseIntervals);
        Assert.Equal(480, session.Snapshot().ActiveSampleBudget);
        Assert.Equal(10_000_000, session.Snapshot().PauseIntervals[0].End100ns - session.Snapshot().PauseIntervals[0].Start100ns);
    }

    [Fact]
    public void StopWhilePausedAddsNoCaptureAndClosesAtThePauseBoundary()
    {
        var clock = new ManualClock(); var sources = new FakeSources();
        using var session = new RecordingAudioSession(clock.GetTimestamp(), clock.TimestampFrequency,
            false, true, sources.Create, clock);
        _ = sources.Get(RecordingAudioSourceKind.Microphone);
        clock.Advance(7.125);
        session.Pause(clock.GetTimestamp());
        Wait(() => session.Snapshot().Microphone.ActiveObjects == 0);
        int opened = sources.Count(RecordingAudioSourceKind.Microphone);
        clock.Advance(60_000);
        Stop(session, clock);
        Assert.Equal(opened, sources.Count(RecordingAudioSourceKind.Microphone));
        Assert.Equal(342, session.Snapshot().ActiveSampleBudget);
        Assert.Equal(60_000 * 10_000L, session.Snapshot().PauseIntervals[0].End100ns - session.Snapshot().PauseIntervals[0].Start100ns);
    }

    [Fact]
    public void QueueIsBoundedAndCommittedSilenceRejectsLatePcm()
    {
        var clock = new ManualClock(); var sources = new FakeSources();
        using var session = new RecordingAudioSession(clock.GetTimestamp(), clock.TimestampFrequency,
            true, false, sources.Create, clock);
        FakeSource source = sources.Get(RecordingAudioSourceKind.System);
        RecordingAudioBlock silence = session.RenderNext(480);
        Assert.All(silence.Samples.ToArray(), value => Assert.Equal(0, value));
        session.Commit(silence);
        source.Send(Packet(clock, 0, .7f));
        source.Send(Packet(clock, 480, .7f, frames: 24_000));
        Assert.True(session.Snapshot().System.OverflowFrames > 0);
        RecordingAudioBlock next = session.RenderNext(960);
        Assert.Equal(480, next.StartSample);
        Assert.Equal(480, session.OutputFrames);
        session.Commit(next);
        Assert.Equal(960, session.OutputFrames);
        Stop(session, clock);
    }

    [Fact]
    public void TimestampErrorClosesGenerationAndLateCallbackCannotReviveIt()
    {
        var clock = new ManualClock(); var sources = new FakeSources();
        using var session = new RecordingAudioSession(clock.GetTimestamp(), clock.TimestampFrequency,
            true, false, sources.Create, clock);
        FakeSource source = sources.Get(RecordingAudioSourceKind.System);
        long old = session.Snapshot().System.Status.Generation;
        source.Send(Packet(clock, 0, 1) with { Flags = RecordingAudioPacketFlags.TimestampError });
        Wait(() => session.Snapshot().System.Status.State == AudioSourceState.Unavailable);
        source.ReplayOld(Packet(clock, 480, 1));
        Assert.True(session.Snapshot().System.Status.Generation > old);
        Assert.Equal(1, session.Snapshot().System.TimestampErrors);
        Assert.True(session.Snapshot().System.RejectedPackets > 0);
        Assert.All(session.RenderNext(480).Samples.ToArray(), value => Assert.Equal(0, value));
        Stop(session, clock);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public void MissingPacketsWithoutDiscontinuityKeepEachSignalAtItsQpcPosition(int gapCount)
    {
        var clock = new ManualClock(); var sources = new FakeSources();
        using var session = new RecordingAudioSession(clock.GetTimestamp(), clock.TimestampFrequency,
            true, false, sources.Create, clock);
        FakeSource source = sources.Get(RecordingAudioSourceKind.System);
        source.Send(Packet(clock, 0, .5f));
        for (int gap = 1; gap <= gapCount; gap++)
            source.Send(Packet(clock, gap * 960L, gap % 2 == 1 ? -.5f : .5f));
        // Extra zero input releases any bounded DSP lookahead from the final signal packet.
        long finalSignalEnd = gapCount * 960L + 480;
        source.Send(Packet(clock, finalSignalEnd, 0));
        clock.Advance((finalSignalEnd + 480) / 48d);
        RecordingAudioBlock block = session.RenderNext(finalSignalEnd + 480, checked((int)finalSignalEnd + 480));
        short[] pcm = block.Samples.ToArray();
        for (int gap = 1; gap <= gapCount; gap++)
        {
            int missingCenter = gap * 960 - 240;
            int signalCenter = gap * 960 + 240;
            // Far from the finite FIR seams, each skipped 10 ms is silent and the following
            // packet retains its own sign/amplitude at the original device QPC position.
            Assert.InRange(Math.Abs((int)pcm[missingCenter * 2]), 0, 4);
            Assert.InRange(pcm[signalCenter * 2] * (gap % 2 == 1 ? -1 : 1), 8_100, 8_300);
        }
        session.Commit(block);
        Assert.Equal(finalSignalEnd + 480, session.OutputFrames);
        Assert.Equal(0, session.Snapshot().System.Failures);
        Assert.Equal(0, session.Snapshot().System.TimestampErrors);
        Stop(session, clock);
        Assert.Equal(finalSignalEnd + 480, session.Snapshot().ActiveSampleBudget);
    }

    [Theory]
    [InlineData(44_100, -500, 137)]
    [InlineData(44_100, 500, 441)]
    [InlineData(48_000, -500, 173)]
    [InlineData(48_000, 500, 480)]
    [InlineData(96_000, -500, 293)]
    [InlineData(96_000, 500, 960)]
    public void NativePacketsDriftAndFractionalPauseKeepTheIntegratedWaveformOnActiveTime(int rate, int ppm, int packetSize)
    {
        const long firstSpan100ns = 1_873_210; // fractional native/output frame boundary
        const long pause100ns = 1_234_567;
        const long secondSpan100ns = 2_631_790;
        double actualRate = rate * (1 + ppm / 1_000_000d);
        var clock = new ManualClock(); var sources = new FakeSources();
        using var session = new RecordingAudioSession(clock.GetTimestamp(), clock.TimestampFrequency,
            true, false, sources.Create, clock);
        var rendered = new List<short>();
        FakeSource first = sources.Get(RecordingAudioSourceKind.System);
        Feed(first, rawStart100ns: 0, activeStart100ns: 0, firstSpan100ns);
        session.Pause(clock.GetTimestamp());
        Wait(() => session.Snapshot().System.ActiveObjects == 0);
        long beforeResumeGeneration = session.Snapshot().System.Status.Generation;
        // A stale source cannot inject this full-scale marker into the resumed timeline.
        first.ReplayOld(new(Enumerable.Repeat(1f, 960).ToArray(), rate, 0,
            clock.Epoch + firstSpan100ns, RecordingAudioPacketFlags.None));
        clock.SetElapsed100ns(firstSpan100ns + pause100ns);
        session.Resume(clock.GetTimestamp());
        FakeSource second = sources.Get(RecordingAudioSourceKind.System, 2);
        Assert.True(session.Snapshot().System.Status.Generation > beforeResumeGeneration);
        Feed(second, firstSpan100ns + pause100ns, firstSpan100ns, secondSpan100ns);
        session.RequestStop(clock.GetTimestamp()); session.JoinSources();
        long totalFrames = RecordingAudioFormat.SampleAt100ns(firstSpan100ns + secondSpan100ns);
        Drain(totalFrames);

        Assert.Equal(totalFrames * 2, rendered.Count);
        Assert.Equal(totalFrames, session.Snapshot().ActiveSampleBudget);
        Assert.Equal(firstSpan100ns + pause100ns + secondSpan100ns, session.Snapshot().StopElapsed100ns);
        Assert.Equal(pause100ns, session.Snapshot().PauseIntervals.Single().End100ns
            - session.Snapshot().PauseIntervals.Single().Start100ns);
        Assert.Equal(0, session.Snapshot().System.Failures);
        Assert.Equal(0, session.Snapshot().System.OverflowFrames);
        Assert.True(session.Snapshot().System.RejectedPackets > 0);
        Assert.InRange(session.Snapshot().System.DriftPpm, ppm - 2, ppm + 2);
        Assert.Equal(0, session.Snapshot().System.ActiveObjects);
        Assert.Equal(0, session.Snapshot().SourceThreadsStillRunning);

        double squared = 0; int compared = 0;
        long seam = RecordingAudioFormat.SampleAt100ns(firstSpan100ns);
        for (int frame = 96; frame < totalFrames - 96; frame++)
        {
            // Only FIR start/restart edges are excluded. Every interior sample is compared
            // at its original active-time index; duration-only or periodic-tone checks cannot
            // detect the packet segmentation and pause-position regressions this catches.
            if (Math.Abs(frame - seam) < 96) continue;
            double expected = Signal(frame / 48_000d) * .5;
            double actual = rendered[frame * 2] / 32768d;
            squared += Math.Pow(actual - expected, 2); compared++;
            Assert.InRange(actual, -.27, .27);
        }
        Assert.InRange(Math.Sqrt(squared / compared), 0, .025);

        void Feed(FakeSource source, long rawStart100ns, long activeStart100ns, long duration100ns)
        {
            long position = 0;
            // Supply bounded lookahead beyond the command boundary. Pause/Stop must truncate
            // that already-converted PCM, while retaining every sample before the boundary.
            long inputLimit = (long)Math.Ceiling((duration100ns + 200_000) * actualRate / 10_000_000);
            int packetNumber = 0;
            while (position < inputLimit)
            {
                int size = (packetNumber++ % 3) switch { 0 => packetSize, 1 => packetSize + 7, _ => packetSize * 2 - 3 };
                int frames = (int)Math.Min(size, inputLimit - position);
                float[] stereo = new float[frames * 2];
                for (int frame = 0; frame < frames; frame++)
                    stereo[frame * 2] = stereo[frame * 2 + 1] = (float)Signal(activeStart100ns / 10_000_000d
                        + (position + frame) / actualRate);
                long qpc = clock.Epoch + rawStart100ns + (long)Math.Round(position * 10_000_000 / actualRate);
                source.Send(new(stereo, rate, position, qpc));
                position += frames;
                long elapsed = rawStart100ns + Math.Min(duration100ns, (long)Math.Round(position * 10_000_000 / actualRate));
                clock.SetElapsed100ns(elapsed);
                long active = activeStart100ns + elapsed - rawStart100ns;
                Drain(RecordingAudioFormat.SampleAt100ns(Math.Max(0, active - 400_000)));
            }
        }

        void Drain(long end)
        {
            while (session.OutputFrames < end)
            {
                RecordingAudioBlock block = session.RenderNext(end);
                rendered.AddRange(block.Samples.ToArray()); session.Commit(block);
            }
        }

        static double Signal(double seconds) => .3 * Math.Sin(2 * Math.PI * 713 * seconds)
            + .2 * Math.Sin(2 * Math.PI * 1_291 * seconds);
    }

    [Theory]
    [InlineData(44_100)]
    [InlineData(48_000)]
    [InlineData(96_000)]
    public void ImmediateStopWithoutFuturePacketsRetainsTheAcceptedResamplerTail(int rate)
    {
        int nativeFrames = rate / 10;
        float[] native = new float[nativeFrames * 2];
        // A nonzero final 5 ms makes dropping buffered FIR output observable at the end.
        Array.Fill(native, .5f, (nativeFrames - rate / 200) * 2, rate / 200 * 2);
        List<float> expected = [];
        using (var reference = new MediaFoundationRecordingResampler(rate))
        {
            expected.AddRange(reference.Process(native));
            expected.AddRange(reference.Process([], drain: true));
        }
        Assert.InRange(Math.Abs(expected.Count / 2 - 4_800), 0, 1);
        Assert.True(expected.TakeLast(192).Max(Math.Abs) > .1f);
        var clock = new ManualClock(); var sources = new FakeSources();
        using var session = new RecordingAudioSession(clock.GetTimestamp(), clock.TimestampFrequency,
            true, false, sources.Create, clock);
        FakeSource source = sources.Get(RecordingAudioSourceKind.System);
        source.Send(new(native, rate, 0, clock.Epoch));
        clock.Advance(100);
        Stop(session, clock);
        RecordingAudioBlock block = session.RenderNext(4_800, 4_800);
        int comparedEnd = Math.Min(expected.Count, block.Samples.Length);
        for (int index = comparedEnd - 192; index < comparedEnd; index++)
        {
            short referenceSample = RecordingAudioMixer.ToPcm16(expected[index] * RecordingAudioMixer.SourceGain);
            Assert.InRange(Math.Abs((int)block.Samples.Span[index] - referenceSample), 0, 2);
        }
        session.Commit(block);
        Assert.Equal(4_800, session.Snapshot().OutputFrames);
        Assert.Equal(4_800, session.Snapshot().ActiveSampleBudget);
        Assert.Equal(0, session.Snapshot().PaddingFrames);
    }

    [Theory]
    [InlineData(44_100, true)]
    [InlineData(48_000, true)]
    [InlineData(96_000, true)]
    [InlineData(44_100, false)]
    [InlineData(48_000, false)]
    [InlineData(96_000, false)]
    public void RapidResumeOrUnmutePreservesOnlyTheOldGenerationsFixedTailBoundary(int rate, bool pause)
    {
        const long boundary100ns = 995_000; // 99.5 ms, inside the accepted final packet
        long boundarySample = RecordingAudioFormat.SampleAt100ns(boundary100ns);
        float[] input = Enumerable.Repeat(.5f, rate / 10 * 2).ToArray();
        List<float> expected = [];
        using (var reference = new MediaFoundationRecordingResampler(rate))
        {
            expected.AddRange(reference.Process(input));
            expected.AddRange(reference.Process([], drain: true));
        }
        var clock = new ManualClock(); var sources = new FakeSources();
        using var session = new RecordingAudioSession(clock.GetTimestamp(), clock.TimestampFrequency,
            false, true, sources.Create, clock);
        FakeSource first = sources.Get(RecordingAudioSourceKind.Microphone);
        using var exitEntered = new ManualResetEventSlim(); using var releaseExit = new ManualResetEventSlim();
        first.ExitEntered = exitEntered; first.ReleaseExit = releaseExit;
        try
        {
            first.Send(new(input, rate, 0, clock.Epoch));
            clock.SetElapsed100ns(boundary100ns);
            if (pause) session.Pause(clock.GetTimestamp()); else session.SetSources(false, false);
            Assert.True(exitEntered.Wait(TimeSpan.FromSeconds(5)));
            clock.Advance(50);
            if (pause) session.Resume(clock.GetTimestamp()); else session.SetSources(false, true);
            first.ReplayOld(new(Enumerable.Repeat(1f, 960).ToArray(), rate, rate,
                clock.Epoch + 10_000_000));
            releaseExit.Set();
            _ = sources.Get(RecordingAudioSourceKind.Microphone, 2);
            // The new source has supplied nothing. Draining the old source must restore its
            // pre-command FIR tail without resurrecting its post-command samples, even though
            // resume/unmute advanced the current generation before the old owner could drain.
            RecordingAudioBlock block = session.RenderNext(boundarySample + 24, checked((int)boundarySample + 24));
            for (int frame = checked((int)boundarySample) - 96; frame < boundarySample; frame++)
            {
                short target = RecordingAudioMixer.ToPcm16(expected[frame * 2] * RecordingAudioMixer.SourceGain);
                Assert.InRange(Math.Abs((int)block.Samples.Span[frame * 2] - target), 0, 2);
            }
            for (int frame = (int)boundarySample; frame < boundarySample + 24; frame++)
                Assert.Equal(0, block.Samples.Span[frame * 2]);
            Assert.True(session.Snapshot().Microphone.RejectedPackets > 0);
        }
        finally { releaseExit.Set(); Stop(session, clock); }
    }

    [Fact]
    public void FailedSourceCannotDrainInvalidatedPcmBackIntoTheRing()
    {
        const int rate = 44_100;
        var clock = new ManualClock(); var sources = new FakeSources();
        using var session = new RecordingAudioSession(clock.GetTimestamp(), clock.TimestampFrequency,
            true, false, sources.Create, clock);
        FakeSource source = sources.Get(RecordingAudioSourceKind.System);
        source.Send(new(Enumerable.Repeat(.5f, rate / 10 * 2).ToArray(), rate, 0, clock.Epoch));
        source.Fail(new COMException("device invalidated", unchecked((int)0x88890004)));
        Wait(() => session.Snapshot().System.Status.State == AudioSourceState.Unavailable
            && session.Snapshot().System.ActiveObjects == 0);
        Assert.All(session.RenderNext(4_800, 4_800).Samples.ToArray(), sample => Assert.Equal(0, sample));
        Stop(session, clock);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void FailureBeforeDelayedReleaseCannotAcquireATailPermitFromPauseOrStop(bool pause, bool packetFailure)
    {
        const int rate = 44_100;
        var clock = new ManualClock(); var sources = new FakeSources();
        using var session = new RecordingAudioSession(clock.GetTimestamp(), clock.TimestampFrequency,
            true, false, sources.Create, clock);
        FakeSource source = sources.Get(RecordingAudioSourceKind.System);
        using var exitEntered = new ManualResetEventSlim(); using var releaseExit = new ManualResetEventSlim();
        source.ExitEntered = exitEntered; source.ReleaseExit = releaseExit;
        try
        {
            source.Send(new(Enumerable.Repeat(.5f, rate / 10 * 2).ToArray(), rate, 0, clock.Epoch));
            if (packetFailure)
                source.Send(new(new float[882], rate, 4_410, clock.Epoch + 1_000_000,
                    RecordingAudioPacketFlags.TimestampError));
            else source.Fail(new COMException("device release delayed after failure", unchecked((int)0x88890004)));
            Assert.True(exitEntered.Wait(TimeSpan.FromSeconds(5)));
            clock.Advance(100);
            if (pause) session.Pause(clock.GetTimestamp()); else session.RequestStop(clock.GetTimestamp());
            releaseExit.Set();
            Wait(() => session.Snapshot().System.Failures == 1 && session.Snapshot().System.ActiveObjects == 0);
            Assert.Equal(pause ? AudioSourceState.Paused : AudioSourceState.Off, session.Snapshot().System.Status.State);
            Assert.All(session.RenderNext(4_800, 4_800).Samples.ToArray(), value => Assert.Equal(0, value));
        }
        finally { releaseExit.Set(); Stop(session, clock); }
    }

    [Theory]
    [InlineData(44_100)]
    [InlineData(48_000)]
    [InlineData(96_000)]
    public void RateStepDoesNotDiscardCorrectedPcmAtEachPacketBudget(int rate)
    {
        var clock = new ManualClock(); var sources = new FakeSources();
        using var session = new RecordingAudioSession(clock.GetTimestamp(), clock.TimestampFrequency,
            true, false, sources.Create, clock);
        FakeSource source = sources.Get(RecordingAudioSourceKind.System);
        int nativeFrames = rate / 100;
        float[] constant = Enumerable.Repeat(.5f, nativeFrames * 2).ToArray();
        var rendered = new List<short>();
        long position = 0; double elapsed100ns = 0;
        for (int packet = 0; packet < 800; packet++)
        {
            // A realistic device-rate step inside one uninterrupted capture generation.
            // Packet QPC remains continuous; the two-second slope estimator adapts gradually.
            double ratio = packet < 300 ? .9995 : 1.0005;
            source.Send(new(constant, rate, position, clock.Epoch + (long)Math.Round(elapsed100ns)));
            position += nativeFrames;
            elapsed100ns += nativeFrames * 10_000_000d / (rate * ratio);
            clock.SetElapsed100ns((long)Math.Round(elapsed100ns));
            Drain(RecordingAudioFormat.SampleAt100ns(Math.Max(0, (long)Math.Round(elapsed100ns) - 400_000)));
        }
        Stop(session, clock);
        long total = session.Snapshot().ActiveSampleBudget;
        Drain(total);
        Assert.Equal(total * 2, rendered.Count);
        Assert.Equal(0, session.Snapshot().System.Failures);
        Assert.Equal(0, session.Snapshot().System.OverflowFrames);
        Assert.InRange(session.Snapshot().System.DriftPpm, 498, 502);
        Assert.InRange(session.Snapshot().System.PeakPendingCorrectedFrames, 1,
            RecordingAudioSession.MaximumBufferedSourceFrames);
        // Every interior sample must retain the continuous DC signal. This deliberately
        // checks every packet seam, including the whole estimator adaptation window.
        for (int sample = 960 * 2; sample < rendered.Count - 960 * 2; sample++)
            Assert.InRange(rendered[sample], (short)8_100, (short)8_300);

        void Drain(long end)
        {
            while (session.OutputFrames < end)
            {
                RecordingAudioBlock block = session.RenderNext(end);
                rendered.AddRange(block.Samples.ToArray()); session.Commit(block);
            }
        }
    }

    [Fact]
    public void HungOpenDoesNotBlockStopAndRemainingObjectIsReportedUntilReleased()
    {
        var clock = new ManualClock();
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        using var session = new RecordingAudioSession(clock.GetTimestamp(), clock.TimestampFrequency,
            false, true, _ => new HungSource(entered, release), clock);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        try
        {
            session.RequestStop(clock.GetTimestamp());
            var watch = Stopwatch.StartNew(); session.JoinSources(); watch.Stop();
            Assert.True(watch.ElapsedMilliseconds < 1_000);
            Assert.Equal(1, session.Snapshot().SourceThreadsStillRunning);
            Assert.Equal(1, session.Snapshot().Microphone.ActiveObjects);
            Assert.InRange(session.Snapshot().SourceJoinWaitMs, 0, 1_000);
        }
        finally { release.Set(); }
        Wait(() => session.Snapshot().SourceThreadsStillRunning == 0);
        Assert.Equal(0, session.Snapshot().Microphone.ActiveObjects);
    }

    [Theory]
    [InlineData(false, AudioEncoderState.Available)]
    [InlineData(true, AudioEncoderState.Unavailable)]
    public void SilentStartAndAacFallbackNeverCreateSources(bool requested, AudioEncoderState available)
    {
        var sink = new FakeEncoder(available); VideoEncoderOptions? capturedOptions = null;
        int sourceObjects = 0;
        using var recorder = NewRecorder(sink, options => capturedOptions = options, _ =>
        { Interlocked.Increment(ref sourceObjects); throw new InvalidOperationException("No source should be created."); });
        recorder.Start(new RectD(0, 0, 32, 24), "unused.mp4", new RecordingSettings { RecordSystemAudio = requested });
        Wait(() => recorder.IsReady && sink.VideoCount > 0);
        RecordingResult result = recorder.Stop();
        Assert.Equal(0, sourceObjects);
        Assert.Equal(requested, capturedOptions!.Audio is not null);
        Assert.False(result.ExpectedAudio);
        Assert.Equal(0, sink.AudioFrames);
        Assert.Equal(0, result.AudioDiagnostics!.System.OpenedObjects);
        Assert.Equal(0, result.AudioDiagnostics.Microphone.OpenedObjects);
        Assert.False(recorder.SetAudioSources(true, true));
    }

    [Fact]
    public void RecorderFakeAudioHasOrderedStreamsAndExactVideoTailAndPauseAccounting()
    {
        var sink = new FakeEncoder(AudioEncoderState.Available); var sources = new FakeSources();
        using var recorder = NewRecorder(sink, _ => { }, sources.Create);
        recorder.Start(new RectD(0, 0, 32, 24), "unused.mp4", new RecordingSettings { RecordSystemAudio = true });
        Wait(() => recorder.IsReady && sink.VideoCount >= 3);
        Assert.True(recorder.Pause());
        Assert.True(recorder.WaitUntilCaptureParked(TimeSpan.FromSeconds(5)));
        Thread.Sleep(20);
        Assert.True(recorder.Resume());
        Wait(() => sink.VideoCount >= 5);
        RecordingResult result = recorder.Stop();
        RecordingAudioDiagnostics audio = result.AudioDiagnostics!;
        Assert.True(result.ExpectedAudio);
        Assert.Equal(sink.AudioFrames, audio.OutputFrames);
        Assert.Equal(audio.ActiveSampleBudget + audio.PaddingFrames - audio.TrimmedFrames, audio.OutputFrames);
        long rawActive = audio.StopElapsed100ns - audio.PauseIntervals.Sum(p => p.End100ns - p.Start100ns);
        Assert.Equal(RecordingAudioFormat.SampleAt100ns(rawActive), audio.ActiveSampleBudget);
        Assert.Equal(RecordingAudioFormat.SampleAt100ns(audio.LastVideoTimestamp100ns + 10_000_000 / 30), audio.OutputFrames);
        Assert.True(audio.AllSilence);
        Assert.Equal(0, audio.SourceThreadsStillRunning);
        Assert.Equal(0, audio.System.ActiveObjects);
        Assert.True(audio.PeakQueueAudioFrames <= 4_800);
        Assert.True(sink.Completed);
    }

    private static RegionRecorder NewRecorder(FakeEncoder sink, Action<VideoEncoderOptions> options,
        RecordingAudioSourceFactory factory)
    {
        var grabber = new RegionFrameGrabber(new ScreenCaptureEngine(NullLogger<ScreenCaptureEngine>.Instance),
            false, false, null, null, (pixels, _, _, _) => Array.Fill(pixels, (byte)30));
        return new(grabber, value => { options(value); return sink; }, NullLogger.Instance, audioSourceFactory: factory);
    }

    private static RecordingAudioPacket Packet(ManualClock clock, long position, float value, int frames = 480) =>
        new(Enumerable.Repeat(value, frames * 2).ToArray(), 48_000, position,
            clock.Epoch + RecordingAudioFormat.SampleTime100ns(position));
    private static void Stop(RecordingAudioSession session, ManualClock clock)
    {
        session.RequestStop(clock.GetTimestamp()); session.JoinSources();
        Assert.Equal(0, session.Snapshot().SourceThreadsStillRunning);
        Assert.Equal(0, session.Snapshot().System.ActiveObjects);
        Assert.Equal(0, session.Snapshot().Microphone.ActiveObjects);
    }
    private static void Wait(Func<bool> condition) => Assert.True(SpinWait.SpinUntil(condition, TimeSpan.FromSeconds(5)));

    private sealed class ManualClock : TimeProvider
    {
        public long Epoch { get; } = 10_000_000;
        private long _elapsed;
        public override long TimestampFrequency => 10_000_000;
        public override long GetTimestamp() => Epoch + Interlocked.Read(ref _elapsed);
        public void Advance(double milliseconds) => Interlocked.Add(ref _elapsed, (long)Math.Round(milliseconds * 10_000));
        public void SetElapsed100ns(long value) => Interlocked.Exchange(ref _elapsed, value);
    }

    private sealed class FakeSources
    {
        private readonly ConcurrentDictionary<RecordingAudioSourceKind, ConcurrentQueue<FakeSource>> _sources = new();
        public IRecordingAudioPacketSource Create(RecordingAudioSourceKind kind)
        {
            var source = new FakeSource(); _sources.GetOrAdd(kind, _ => new()).Enqueue(source); return source;
        }
        public int Count(RecordingAudioSourceKind kind) => _sources.TryGetValue(kind, out var values) ? values.Count : 0;
        public FakeSource Get(RecordingAudioSourceKind kind, int number = 1)
        { Wait(() => Count(kind) >= number); FakeSource source = _sources[kind].ToArray()[number - 1]; source.WaitStarted(); return source; }
    }

    private sealed class FakeSource : IRecordingAudioPacketSource
    {
        private readonly BlockingCollection<Action> _commands = new();
        private readonly ManualResetEventSlim _started = new();
        private Action<RecordingAudioPacket>? _callback;
        public ManualResetEventSlim? ExitEntered;
        public ManualResetEventSlim? ReleaseExit;
        public void Run(Action<RecordingAudioPacket> packet, Action<RecordingAudioSourceEvent> state, CancellationToken cancellationToken)
        {
            _callback = packet; state(RecordingAudioSourceEvent.Opened); _started.Set();
            try { foreach (Action command in _commands.GetConsumingEnumerable(cancellationToken)) command(); }
            finally
            {
                ExitEntered?.Set(); ReleaseExit?.Wait();
                state(RecordingAudioSourceEvent.Closed);
            }
        }
        public void WaitStarted() => Assert.True(_started.Wait(TimeSpan.FromSeconds(5)));
        public void Send(RecordingAudioPacket packet)
        {
            using var complete = new ManualResetEventSlim();
            _commands.Add(() => { try { _callback!(packet); } finally { complete.Set(); } });
            Assert.True(complete.Wait(TimeSpan.FromSeconds(5)));
        }
        public void Fail(Exception error) => _commands.Add(() => throw error);
        public void ReplayOld(RecordingAudioPacket packet) => _callback!(packet);
        public void Dispose() { _commands.Dispose(); _started.Dispose(); }
    }

    private sealed class HungSource(ManualResetEventSlim entered, ManualResetEventSlim release) : IRecordingAudioPacketSource
    {
        public void Run(Action<RecordingAudioPacket> packet, Action<RecordingAudioSourceEvent> state, CancellationToken cancellationToken)
        { entered.Set(); release.Wait(); }
        public void Dispose() { }
    }

    private sealed class FakeEncoder(AudioEncoderState audioState) : IAudioVideoEncoder
    {
        public AudioEncoderState AudioState => audioState;
        public string? AudioUnavailableReason => null;
        public int Width => 32; public int Height => 24;
        private long _lastTime = -1, _audioFrames; private int _videoCount;
        public int VideoCount => Volatile.Read(ref _videoCount);
        public long AudioFrames => Interlocked.Read(ref _audioFrames);
        public bool Completed { get; private set; }
        public void WriteFrame(in EncoderFrame frame) { Observe((long)(frame.TimestampMs * 10_000)); Interlocked.Increment(ref _videoCount); }
        public void WriteAudio(in RecordingAudioBlock block)
        { Observe(block.Timestamp100ns); Assert.Equal(AudioFrames, block.StartSample); Interlocked.Add(ref _audioFrames, block.FrameCount); }
        private void Observe(long time) { Assert.True(time >= _lastTime); _lastTime = time; }
        public void Complete() => Completed = true;
        public void Dispose() { }
    }
}
