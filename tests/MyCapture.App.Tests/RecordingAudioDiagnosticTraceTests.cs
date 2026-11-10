using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Diagnostics;
using MyCapture.Core.Recording;
using MyCapture.Platform.Recording;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class RecordingAudioDiagnosticTraceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SyntheticFileVerificationChecksTrackCountAndSampleExactPauseBudget(bool audioEnabled)
    {
        string directory = OwnedTestDirectory.Create("mc-audio-diagnostic-");
        string path = System.IO.Path.Combine(directory, "synthetic.mp4");
        using var trace = new RecordingAudioDiagnosticTrace(directory);
        WriteFile(path, trace, audioEnabled);
        RecordingResult result = Result(path, audioEnabled);
        var verified = RecordingAudioSelfTest.VerifyFile(path, audioEnabled, result, trace);
        Assert.Empty(verified.Failures);
        Assert.True(verified.PauseSampleBudgetVerified);
        Assert.Equal(audioEnabled ? 2 : 1, verified.Streams);
        Assert.Equal(audioEnabled ? 1 : 0, verified.AudioStreams);
        Assert.Equal(10, trace.VideoFrames);
        Assert.Equal(audioEnabled ? 48_000 : 0, trace.AudioFrames);
        Assert.True(trace.AudioIsContiguous);
        Assert.True(trace.PresentationTimesOrdered);
        Assert.Equal(audioEnabled, trace.AllAudioIsSilence);
        Assert.True(System.IO.File.Exists(System.IO.Path.Combine(directory, "stream-timestamps.csv")));
    }

    [Fact]
    public void VerificationRejectsWrongPauseBudgetAndMissingTrack()
    {
        string directory = OwnedTestDirectory.Create("mc-audio-diagnostic-budget-");
        string path = System.IO.Path.Combine(directory, "synthetic.mp4");
        using var trace = new RecordingAudioDiagnosticTrace(directory);
        WriteFile(path, trace, true);
        RecordingResult result = Result(path, true);
        result = result with { AudioDiagnostics = result.AudioDiagnostics! with { ActiveSampleBudget = 47_999 } };
        var verification = RecordingAudioSelfTest.VerifyFile(path, true, result, trace);
        Assert.False(verification.PauseSampleBudgetVerified);
        Assert.Contains(verification.Failures, text => text.Contains("output PCM", StringComparison.Ordinal));

        // Self-consistent mixer counters are not enough: the video submissions independently
        // require 48,000 frames. A fictitious trimmed tail must still fail validation.
        RecordingResult shortened = Result(path, true);
        shortened = shortened with { AudioDiagnostics = shortened.AudioDiagnostics! with
            { OutputFrames = 47_000, TrimmedFrames = 1_000 } };
        Assert.False(RecordingAudioSelfTest.VerifyFile(path, true, shortened, trace).PauseSampleBudgetVerified);

        string silentDirectory = OwnedTestDirectory.Create("mc-audio-diagnostic-missing-");
        string silentPath = System.IO.Path.Combine(silentDirectory, "silent.mp4");
        using var silentTrace = new RecordingAudioDiagnosticTrace(silentDirectory);
        WriteFile(silentPath, silentTrace, false);
        var missing = RecordingAudioSelfTest.VerifyFile(silentPath, true, Result(silentPath, false), silentTrace);
        Assert.Contains(missing.Failures, text => text.Contains("stream count", StringComparison.Ordinal));
        Assert.Contains(missing.Failures, text => text.Contains("encoder setup", StringComparison.Ordinal));
    }

    [Fact]
    public void VerificationRejectsSelfConsistentShortAudioAgainstIndependentVideoEnd()
    {
        string directory = OwnedTestDirectory.Create("mc-audio-diagnostic-short-");
        string path = System.IO.Path.Combine(directory, "synthetic.mp4");
        using var trace = new RecordingAudioDiagnosticTrace(directory);
        WriteFile(path, trace, true, 43_200);
        RecordingResult result = Result(path, true);
        result = result with { AudioDiagnostics = result.AudioDiagnostics! with
            { OutputFrames = 43_200, SilentFrames = 43_200, TrimmedFrames = 4_800 } };
        Assert.Equal(result.AudioDiagnostics.OutputFrames, trace.AudioFrames);
        Assert.True(trace.AudioIsContiguous);
        var verification = RecordingAudioSelfTest.VerifyFile(path, true, result, trace);
        Assert.False(verification.PauseSampleBudgetVerified);
        Assert.Contains(verification.Failures, text => text.Contains("output PCM", StringComparison.Ordinal));
    }

    [Fact]
    public void DiagnosticPreferencesRequireTheExplicitMicrophoneFlag()
    {
        string directory = OwnedTestDirectory.Create("mc-audio-diagnostic-options-");
        RecordingAudioSelfTestOptions options = RecordingAudioSelfTestOptions.Parse([RecordingAudioSelfTestOptions.CommandLineSwitch, directory]);
        RecordingSettings settings = RecordingAudioSelfTest.SettingsFor(options);
        Assert.True(settings.RecordSystemAudio);
        Assert.False(settings.RecordMicrophone);
        Assert.False(settings.IncludeCursor);
        Assert.True(RecordingAudioSelfTest.SettingsFor(options with { Microphone = true }).RecordMicrophone);
        RecordingSettings silent = RecordingAudioSelfTest.SettingsFor(options with { SystemAudio = false });
        Assert.False(silent.RecordSystemAudio);
        Assert.False(silent.RecordMicrophone);
        Assert.False(new RecordingSettings().RecordSystemAudio);
        Assert.False(new RecordingSettings().RecordMicrophone);
    }

    private static void WriteFile(string path, RecordingAudioDiagnosticTrace trace, bool audio, int audioFrames = 48_000)
    {
        using IVideoEncoder sink = trace.Bind(new MediaFoundationVideoEncoder(
            new VideoEncoderOptions(path, 64, 64, 10, 128_000)
                { Audio = audio ? new AudioEncoderOptions() : null, AllowHardwareEncoder = false }, NullLogger.Instance));
        for (int index = 0; index < 10; index++)
        {
            sink.WriteFrame(new EncoderFrame(new byte[64 * 64 * 4], 64, 64, 256, index * 100));
            int frames = Math.Min(4800, audioFrames - index * 4800);
            if (audio && frames > 0) ((IAudioVideoEncoder)sink).WriteAudio(new RecordingAudioBlock(index * 4800, new short[frames * 2]));
        }
        sink.Complete();
    }

    private static RecordingResult Result(string path, bool audio)
    {
        var source = new RecordingAudioSourceDiagnostics(new(false, AudioSourceState.Off, 1), 0, 0, 0, 0, 0, 0, 0, 0, 0, null);
        var diagnostics = new RecordingAudioDiagnostics(audio ? AudioEncoderState.Available : AudioEncoderState.Disabled,
            source, source, audio ? 48_000 : 0, audio ? 48_000 : 0, 0, 10_000_000, 0, 9_000_000,
            0, 0, 0, 0, 0, 0, 0, 0)
        {
            EpochTimestamp = 100_000, TimestampFrequency = 10_000_000,
            ActiveSampleBudget = audio ? 48_000 : 0, StopElapsed100ns = 15_000_000,
            PauseIntervals = [new(4_000_000, 9_000_000)],
        };
        return new(path, 1000, 10, 10, 64, 64) { ExpectedAudio = audio, AudioDiagnostics = diagnostics, PauseCount = 1, PausedMs = 500 };
    }
}
