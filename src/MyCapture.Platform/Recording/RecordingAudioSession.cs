using System.Diagnostics;
using MyCapture.Core.Recording;

namespace MyCapture.Platform.Recording;

/// <summary>
/// Session-only capture and bounded source rings. The video/writer owner never calls a device.
/// Commands invalidate callbacks synchronously; native cancellation and release run on each
/// source's MTA. A stuck driver can outlive the bounded join and remains visible in diagnostics.
/// </summary>
public sealed class RecordingAudioSession : IDisposable
{
    public const int CaptureLookbehindMilliseconds = 40;
    public const int MaximumBufferedSourceFrames = RecordingAudioFormat.SampleRate / 5;
    public const int SourceJoinBudgetMilliseconds = 100;
    private readonly object _gate = new();
    private readonly RecordingAudioTimeline _timeline;
    private readonly RecordingAudioSourceFactory _factory;
    private readonly TimeProvider _time;
    private readonly Source _system, _microphone;
    private readonly long _epoch, _frequency;
    private bool _stopping;
    private long _stopTimestamp;
    private long _outputFrames, _silentFrames;
    private double _joinWait;

    private sealed class Source(bool enabled, RecordingAudioSourceKind kind)
    {
        public readonly RecordingAudioSourceStateMachine State = new(enabled);
        public readonly RecordingAudioPacketGate Gate = new();
        public readonly RecordingAudioSourceKind Kind = kind;
        public readonly float[] Ring = new float[MaximumBufferedSourceFrames * 2];
        public readonly long[] Positions = Enumerable.Repeat(-1L, MaximumBufferedSourceFrames).ToArray();
        public Thread? Thread;
        public CancellationTokenSource? Cancellation;
        public Pipeline? Pipeline;
        public long Opened, Active, Packets, Silent, Rejected, Overflow, TimestampErrors, Failures;
        public string? LastFailure;
        public double LastDriftPpm;
        public int PeakPendingCorrectedFrames;
    }

    // Lives on one worker's stack. Commands only invalidate the generation; they never
    // release apartment-bound resamplers underneath a callback that is still returning.
    private sealed class Pipeline : IDisposable
    {
        public MediaFoundationRecordingResampler? Resampler;
        public RecordingAudioDriftEstimator? Drift;
        public readonly RecordingAudioDriftCorrector Corrector = new();
        public long Cursor, ExpectedInputStart, ExpectedDevicePosition;
        public long Generation;
        // A command seals the already accepted input's tail at this generation's boundary.
        // New callbacks remain rejected. Later resume/unmute commands may never widen it.
        public long DrainThroughSample = -1;
        public readonly List<float> PendingCorrected = [];
        public void Dispose() => Resampler?.Dispose();
    }

    public RecordingAudioSession(long epochTimestamp, long timestampFrequency, bool systemEnabled,
        bool microphoneEnabled, RecordingAudioSourceFactory? sourceFactory = null, TimeProvider? timeProvider = null,
        bool initiallyPaused = false)
    {
        if (!systemEnabled && !microphoneEnabled) throw new ArgumentException("An audio session requires a selected source.");
        _timeline = new(epochTimestamp, timestampFrequency);
        _epoch = epochTimestamp; _frequency = timestampFrequency;
        _time = timeProvider ?? TimeProvider.System;
        _factory = sourceFactory ?? (static kind => new WasapiCaptureSource(kind));
        _system = new(systemEnabled, RecordingAudioSourceKind.System);
        _microphone = new(microphoneEnabled, RecordingAudioSourceKind.Microphone);
        if (initiallyPaused)
        {
            _timeline.TryPause(epochTimestamp);
            _system.State.SetPaused(true); _microphone.State.SetPaused(true);
        }
        StartWorker(_system);
        StartWorker(_microphone);
    }

    public long OutputFrames { get { lock (_gate) return _outputFrames; } }

    public void SetSources(bool systemEnabled, bool microphoneEnabled)
    {
        lock (_gate)
        {
            if (_stopping) return;
            SetEnabled(_system, systemEnabled);
            SetEnabled(_microphone, microphoneEnabled);
            Monitor.PulseAll(_gate);
        }
    }

    public void Pause(long timestamp)
    {
        lock (_gate)
        {
            if (_stopping || !_timeline.TryPause(timestamp)) return;
            long boundary = RecordingAudioFormat.SampleAt100ns(_timeline.Active100ns(timestamp));
            ChangePause(_system, true, boundary);
            ChangePause(_microphone, true, boundary);
            Monitor.PulseAll(_gate);
        }
    }

    public void Resume(long timestamp)
    {
        lock (_gate)
        {
            if (_stopping || !_timeline.TryResume(timestamp)) return;
            long boundary = RecordingAudioFormat.SampleAt100ns(_timeline.Active100ns(timestamp));
            ChangePause(_system, false, boundary);
            ChangePause(_microphone, false, boundary);
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>Closes acceptance immediately and never waits for a native call.</summary>
    public void RequestStop(long timestamp)
    {
        lock (_gate)
        {
            if (_stopping) return;
            _stopping = true;
            _stopTimestamp = timestamp;
            _timeline.End(timestamp);
            long boundary = RecordingAudioFormat.SampleAt100ns(_timeline.Active100ns(timestamp));
            foreach (Source source in new[] { _system, _microphone })
            {
                source.State.End();
                Invalidate(source, boundary);
            }
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>
    /// Returns a block without advancing consumption. The owner commits only after a successful
    /// writer submission. Unavailable/late/no-packet spans are silence, never repeated PCM.
    /// </summary>
    public RecordingAudioBlock RenderNext(long endSample, int maximumFrames = 480)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(endSample);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumFrames);
        lock (_gate)
        {
            int frames = checked((int)Math.Min(Math.Max(0, endSample - _outputFrames), maximumFrames));
            float[] system = new float[frames * 2], microphone = new float[frames * 2];
            Read(_system, _outputFrames, system);
            Read(_microphone, _outputFrames, microphone);
            short[] pcm = new short[frames * 2];
            RecordingAudioMixer.Mix(system, microphone, pcm);
            return new(_outputFrames, pcm);
        }
    }

    public void Commit(in RecordingAudioBlock block)
    {
        lock (_gate)
        {
            if (block.StartSample != _outputFrames) throw new InvalidOperationException("Audio commits must be contiguous.");
            for (int i = 0; i < block.Samples.Length; i += 2)
                if (block.Samples.Span[i] == 0 && block.Samples.Span[i + 1] == 0) _silentFrames++;
            _outputFrames = block.EndSample;
            _system.Gate.ConsumeThrough(_outputFrames);
            _microphone.Gate.ConsumeThrough(_outputFrames);
        }
    }

    public void JoinSources()
    {
        long started = Stopwatch.GetTimestamp();
        foreach (Source source in new[] { _system, _microphone })
        {
            int remaining = Math.Max(0, SourceJoinBudgetMilliseconds - (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            source.Thread?.Join(remaining);
        }
        lock (_gate) _joinWait += Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    public RecordingAudioDiagnostics Snapshot()
    {
        lock (_gate)
        {
            return new(AudioEncoderState.Available, Snapshot(_system), Snapshot(_microphone),
                _outputFrames, _silentFrames, _outputFrames == 0 ? -1 : 0,
                RecordingAudioFormat.SampleTime100ns(_outputFrames), -1, -1, 0, 0, 0, 0,
                _joinWait, (_system.Thread?.IsAlive == true ? 1 : 0) + (_microphone.Thread?.IsAlive == true ? 1 : 0), 0, 0)
            {
                EpochTimestamp = _epoch, TimestampFrequency = _frequency,
                StopElapsed100ns = _timeline.Elapsed100ns(_stopping ? _stopTimestamp : _time.GetTimestamp()),
                ActiveSampleBudget = RecordingAudioFormat.SampleAt100ns(_timeline.Active100ns(_time.GetTimestamp())),
                PaddingFrames = Math.Max(0, _outputFrames - RecordingAudioFormat.SampleAt100ns(_timeline.Active100ns(_time.GetTimestamp()))),
                TrimmedFrames = _stopping ? Math.Max(0, RecordingAudioFormat.SampleAt100ns(_timeline.Active100ns(_time.GetTimestamp())) - _outputFrames) : 0,
                PauseIntervals = _timeline.PauseIntervals.Select(p => new RecordingAudioPauseInterval(p.Start100ns, p.End100ns)).ToArray(),
            };
        }
    }

    public static RecordingAudioDiagnostics InactiveSnapshot(AudioEncoderState state,
        bool systemEnabled = false, bool microphoneEnabled = false)
    {
        RecordingAudioSourceDiagnostics Source(bool enabled) => new(new(enabled,
            enabled && state == AudioEncoderState.Unavailable ? AudioSourceState.Unavailable : AudioSourceState.Off, 0),
            0, 0, 0, 0, 0, 0, 0, 0, 0, null);
        return new(state, Source(systemEnabled), Source(microphoneEnabled), 0, 0, -1, 0, -1, -1, 0, 0, 0, 0, 0, 0, 0, 0);
    }

    private static RecordingAudioSourceDiagnostics Snapshot(Source source) => new(source.State.Status,
        source.Opened, source.Active, source.Packets, source.Silent, source.Rejected, source.Overflow,
        source.TimestampErrors, source.Failures, source.LastDriftPpm, source.LastFailure)
    { PeakPendingCorrectedFrames = source.PeakPendingCorrectedFrames };

    private void SetEnabled(Source source, bool enabled)
    {
        if (!source.State.SetEnabled(enabled)) return;
        Invalidate(source, RecordingAudioFormat.SampleAt100ns(_timeline.Active100ns(_time.GetTimestamp())));
        StartWorker(source);
    }

    private void ChangePause(Source source, bool paused, long boundary)
    {
        source.State.SetPaused(paused);
        Invalidate(source, boundary);
    }

    private static void Invalidate(Source source, long boundary, bool preserveAcceptedTail = true)
    {
        if (source.Pipeline is { } pipeline)
        {
            pipeline.DrainThroughSample = preserveAcceptedTail
                && (pipeline.Generation == source.Gate.Generation || pipeline.DrainThroughSample >= 0)
                ? pipeline.DrainThroughSample < 0 ? boundary : Math.Min(pipeline.DrainThroughSample, boundary)
                : -1;
        }
        source.Cancellation?.Cancel();
        source.Gate.BeginGeneration(source.State.Status.Generation);
        for (int i = 0; i < source.Positions.Length; i++)
            if (source.Positions[i] >= boundary) source.Positions[i] = -1;
    }

    private void StartWorker(Source source)
    {
        if (!source.State.Status.Enabled || source.Thread is not null) return;
        if (source.Gate.Generation == 0) source.Gate.BeginGeneration(source.State.Status.Generation);
        source.Thread = new Thread(() => SourceLoop(source))
        { IsBackground = true, Name = $"MyCapture.Audio.{source.Kind}", Priority = ThreadPriority.Normal };
        source.Thread.SetApartmentState(ApartmentState.MTA);
        source.Thread.Start();
    }

    private void SourceLoop(Source source)
    {
        int retryMs = 0;
        while (true)
        {
            long generation;
            CancellationTokenSource cancellation;
            Pipeline pipeline;
            lock (_gate)
            {
                while (!_stopping && (!source.State.Status.Enabled || source.State.Status.State == AudioSourceState.Paused))
                    Monitor.Wait(_gate);
                if (_stopping) return;
                if (retryMs > 0)
                {
                    Monitor.Wait(_gate, retryMs);
                    if (_stopping) return;
                    if (!source.State.Status.Enabled || source.State.Status.State == AudioSourceState.Paused) continue;
                }
                if (source.State.Status.State == AudioSourceState.Unavailable)
                {
                    source.State.BeginRestart();
                    Invalidate(source, source.Gate.ConsumedThroughSample, preserveAcceptedTail: false);
                }
                generation = source.State.Status.Generation;
                source.Cancellation = cancellation = new CancellationTokenSource();
                source.Pipeline = pipeline = new Pipeline { Generation = generation };
            }
            IRecordingAudioPacketSource? capture = null;
            try
            {
                cancellation.Token.ThrowIfCancellationRequested();
                capture = _factory(source.Kind);
                lock (_gate) source.Active++;
                cancellation.Token.ThrowIfCancellationRequested();
                capture.Run(packet => Receive(source, pipeline, generation, packet), state =>
                {
                    lock (_gate)
                    {
                        if (state == RecordingAudioSourceEvent.Opened)
                        {
                            source.Opened++;
                            source.State.TrySetCapturing(generation);
                        }
                    }
                }, cancellation.Token);
                if (!cancellation.IsCancellationRequested) throw new InvalidOperationException("Audio source ended unexpectedly.");
                retryMs = 0;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { retryMs = 0; }
            catch (Exception ex)
            {
                lock (_gate)
                {
                    // Run may fail and then spend time releasing a native device before this
                    // catch executes. A concurrent Pause/Stop can already have advanced the
                    // public generation and granted a command tail permit. Failure revokes
                    // that permit unconditionally, independently of current session state.
                    pipeline.DrainThroughSample = -1;
                    source.Failures++;
                    source.LastFailure = $"{ex.GetType().Name}: 0x{ex.HResult:X8}";
                    if (ReferenceEquals(source.Pipeline, pipeline))
                    {
                        for (int i = 0; i < source.Positions.Length; i++)
                            if (source.Positions[i] >= source.Gate.ConsumedThroughSample) source.Positions[i] = -1;
                    }
                    if (generation == source.State.Status.Generation && !_stopping)
                    {
                        // An unavailable callback must never revive its old gate or converter.
                        source.State.BeginRestart();
                        Invalidate(source, source.Gate.ConsumedThroughSample, preserveAcceptedTail: false);
                        source.State.TrySetUnavailable(source.State.Status.Generation);
                    }
                }
                retryMs = retryMs == 0 ? 500 : Math.Min(2_000, retryMs * 2);
            }
            finally
            {
                try { capture?.Dispose(); }
                catch (Exception ex) { lock (_gate) source.LastFailure = $"Dispose: 0x{ex.HResult:X8}"; }
                try { DrainAcceptedTail(source, pipeline); }
                catch (Exception ex)
                {
                    lock (_gate) { source.Failures++; source.LastFailure = $"DSP drain: 0x{ex.HResult:X8}"; }
                }
                try { pipeline.Dispose(); }
                catch (Exception ex) { lock (_gate) source.LastFailure = $"DSP dispose: 0x{ex.HResult:X8}"; }
                lock (_gate)
                {
                    if (capture is not null) source.Active--;
                    if (ReferenceEquals(source.Pipeline, pipeline)) source.Pipeline = null;
                    if (ReferenceEquals(source.Cancellation, cancellation)) source.Cancellation = null;
                }
                cancellation.Dispose();
            }
        }
    }

    private void Receive(Source source, Pipeline pipeline, long generation, RecordingAudioPacket packet)
    {
        IReadOnlyList<RecordingAudioSlice> slices;
        lock (_gate)
        {
            if (_stopping || generation != source.State.Status.Generation
                || source.State.Status.State is AudioSourceState.Off or AudioSourceState.Paused or AudioSourceState.Unavailable)
            { source.Rejected++; return; }
            if ((packet.Flags & RecordingAudioPacketFlags.TimestampError) != 0 || packet.Qpc100ns < 0
                || packet.DevicePosition < 0)
            { source.TimestampErrors++; throw new InvalidOperationException("Invalid device timestamp."); }
            if ((packet.Flags & RecordingAudioPacketFlags.Discontinuity) != 0 && pipeline.Drift is not null)
                throw new InvalidOperationException("Device clock discontinuity.");
            if (packet.SampleRate is < 8_000 or > 192_000 || packet.Stereo.Length % 2 != 0
                || packet.Stereo.Length > packet.SampleRate * 2)
                throw new InvalidOperationException("Invalid device PCM format or packet size.");
            source.Packets++;
            if ((packet.Flags & RecordingAudioPacketFlags.Silent) != 0) source.Silent++;
            if (pipeline.Drift is not null && pipeline.Drift.NominalSampleRate != packet.SampleRate)
                throw new InvalidOperationException("Device format changed.");
            pipeline.Drift ??= new(packet.SampleRate);
            if (!pipeline.Drift.Observe(packet.DevicePosition, packet.Qpc100ns))
            { source.TimestampErrors++; throw new InvalidOperationException("Device clock moved discontinuously."); }
            source.LastDriftPpm = pipeline.Drift.PartsPerMillion;
            if (Math.Abs(source.LastDriftPpm) > RecordingAudioDriftCorrector.MaximumCorrectionPpm)
            { source.TimestampErrors++; throw new InvalidOperationException("Device clock drift exceeds the correction range."); }
            long packetStart = _timeline.ElapsedFromPacketQpc100ns(packet.Qpc100ns);
            slices = _timeline.SplitPacket(packetStart, packet.Stereo.Length / 2, packet.SampleRate);
        }
        // Native DSP work is deliberately outside the command lock: even a stalled MF call
        // cannot delay microphone cancellation, pause or the session's bounded stop join.
        foreach (RecordingAudioSlice slice in slices)
        {
            if (slice.InputFrameCount == 0)
            {
                lock (_gate)
                {
                    if (_stopping || generation != source.State.Status.Generation) { source.Rejected++; return; }
                    Put(source, slice.OutputStartSample, new float[checked((int)slice.OutputFrameCount * 2)]);
                }
                continue;
            }
            long firstDeviceFrame = checked(packet.DevicePosition + slice.InputOffsetFrames);
            if (pipeline.Resampler is null)
            {
                pipeline.Resampler = new(packet.SampleRate);
                pipeline.Cursor = slice.OutputStartSample;
                pipeline.ExpectedInputStart = slice.OutputStartSample;
                pipeline.ExpectedDevicePosition = firstDeviceFrame;
            }
            long outputGap = slice.OutputStartSample - pipeline.ExpectedInputStart;
            long nativeGap = firstDeviceFrame - pipeline.ExpectedDevicePosition;
            // An advancing, internally consistent QPC/device-position pair can contain a
            // dropped packet without DISCONTINUITY. Preserve that interval through both
            // continuous filters as zero PCM; appending the next packet directly compresses
            // time and makes every later signal early by the accumulated gaps.
            if (Math.Abs(outputGap) > RecordingAudioFormat.SampleRate / 25 || nativeGap < 0
                || nativeGap > packet.SampleRate / 25
                || Math.Abs(outputGap - nativeGap * (double)RecordingAudioFormat.SampleRate
                    / (packet.SampleRate * pipeline.Drift!.RateRatio)) > 2)
                throw new InvalidOperationException("Device timestamp jumped.");
            if (nativeGap > 0 && !ConvertAndPut(new float[checked((int)nativeGap * 2)], slice.OutputStartSample))
                return;
            pipeline.ExpectedInputStart = slice.OutputEndSample;
            pipeline.ExpectedDevicePosition = checked(firstDeviceFrame + slice.InputFrameCount);
            ReadOnlySpan<float> input = packet.Stereo.Span.Slice(slice.InputOffsetFrames * 2, slice.InputFrameCount * 2);
            if (!ConvertAndPut((packet.Flags & RecordingAudioPacketFlags.Silent) != 0 ? new float[input.Length] : input,
                slice.OutputEndSample)) return;
        }

        bool ConvertAndPut(ReadOnlySpan<float> input, long outputEnd)
        {
            float[] nominal = pipeline.Resampler!.Process(input);
            return PublishConverted(source, pipeline, generation, nominal, outputEnd);
        }
    }

    private void DrainAcceptedTail(Source source, Pipeline pipeline)
    {
        long boundary;
        lock (_gate)
        {
            if (pipeline.Resampler is null || pipeline.DrainThroughSample < 0) return;
            boundary = Math.Min(pipeline.DrainThroughSample, pipeline.ExpectedInputStart);
        }
        // Executed only by the source owner, after Run exits and before its DSP is released.
        // MF's bounded output loop may still encounter a stalled native call; the writer's
        // 100 ms join remains the bound and reports a live owner instead of waiting for it.
        float[] nominal = pipeline.Resampler.Process([], drain: true);
        _ = PublishConverted(source, pipeline, pipeline.Generation, nominal, boundary, finalDrain: true);
    }

    private bool PublishConverted(Source source, Pipeline pipeline, long generation,
        ReadOnlySpan<float> nominal, long outputEnd, bool finalDrain = false)
    {
        float[] corrected = new float[RecordingAudioDriftCorrector.MaximumOutputFrames(nominal.Length / 2) * 2];
        int count = pipeline.Corrector.Process(nominal, corrected, pipeline.Drift!.RateRatio);
        for (int index = 0; index < count * 2; index++) pipeline.PendingCorrected.Add(corrected[index]);
        lock (_gate)
        {
            bool sealedGeneration = _stopping || generation != source.State.Status.Generation;
            if (sealedGeneration)
            {
                // This is output from input accepted before a command, never a new callback.
                // Its fixed generation boundary survives rapid pause/resume or mute/unmute.
                if (!ReferenceEquals(source.Pipeline, pipeline) || pipeline.DrainThroughSample < 0)
                { pipeline.PendingCorrected.Clear(); source.Rejected++; return false; }
                outputEnd = Math.Min(outputEnd, pipeline.DrainThroughSample);
            }
            int allowed = checked((int)Math.Min(pipeline.PendingCorrected.Count / 2, Math.Max(0, outputEnd - pipeline.Cursor)));
            if (allowed > 0)
            {
                Put(source, pipeline.Cursor,
                    System.Runtime.InteropServices.CollectionsMarshal.AsSpan(pipeline.PendingCorrected)[..(allowed * 2)]);
                pipeline.PendingCorrected.RemoveRange(0, allowed * 2);
                pipeline.Cursor += allowed;
            }
            if (sealedGeneration && (finalDrain || pipeline.Cursor >= pipeline.DrainThroughSample))
                pipeline.PendingCorrected.Clear();
            else
            {
                // An estimator's gradual response to a rate change can produce a few samples
                // beyond this packet's cumulative QPC budget. Keep their original positions
                // until the next budget arrives; dropping them creates a growing hole at every
                // packet seam. Only a fixed pause/mute/stop boundary discards the future tail.
                int pendingFrames = pipeline.PendingCorrected.Count / 2;
                if (pendingFrames > MaximumBufferedSourceFrames)
                    throw new InvalidOperationException("Corrected audio exceeded its bounded pending budget.");
                source.PeakPendingCorrectedFrames = Math.Max(source.PeakPendingCorrectedFrames, pendingFrames);
            }
        }
        return true;
    }

    private static void Put(Source source, long start, ReadOnlySpan<float> stereo)
    {
        long consumed = source.Gate.ConsumedThroughSample;
        if (start + stereo.Length / 2 <= consumed) { source.Rejected++; return; }
        for (int i = 0; i < stereo.Length / 2; i++)
        {
            long position = start + i;
            if (position < consumed) continue;
            if (position - consumed >= MaximumBufferedSourceFrames) { source.Overflow++; continue; }
            int index = (int)(position % MaximumBufferedSourceFrames);
            source.Positions[index] = position;
            source.Ring[index * 2] = stereo[i * 2];
            source.Ring[index * 2 + 1] = stereo[i * 2 + 1];
        }
    }

    private static void Read(Source source, long start, Span<float> stereo)
    {
        for (int i = 0; i < stereo.Length / 2; i++)
        {
            long position = start + i;
            int index = (int)(position % MaximumBufferedSourceFrames);
            if (source.Positions[index] != position) continue;
            stereo[i * 2] = source.Ring[index * 2];
            stereo[i * 2 + 1] = source.Ring[index * 2 + 1];
        }
    }

    public void Dispose() { RequestStop(_time.GetTimestamp()); JoinSources(); }
}
