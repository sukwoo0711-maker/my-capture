using System.Globalization;
using System.IO;
using MyCapture.App.Diagnostics;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class RecordingAudioSelfTestOptionsTests
{
    [Fact]
    public void DefaultsRequireOnlyTheExplicitSystemAudioDiagnostic()
    {
        string root = OwnedTestDirectory.Create("mc-audio-options-");
        string output = Path.Combine(root, "output");
        RecordingAudioSelfTestOptions options = Parse(output);
        Assert.Equal(Path.GetFullPath(output), options.OutputDirectory);
        Assert.Equal(10, options.Seconds);
        Assert.True(options.SystemAudio);
        Assert.False(options.Microphone);
        Assert.False(options.Tone);
        Assert.Null(options.PauseAtSeconds);
        Assert.Equal(0, options.PauseSeconds);
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void MicrophoneAndToneRequireTheirOwnFlagsAndPauseAddsWallTime()
    {
        string root = OwnedTestDirectory.Create("mc-audio-options-explicit-");
        RecordingAudioSelfTestOptions options = Parse(Path.Combine(root, "output"),
            "--seconds", "1800", "--microphone", "--tone", "--pause-at", "3.5", "--pause-for", "3600");
        Assert.True(options.SystemAudio);
        Assert.True(options.Microphone);
        Assert.True(options.Tone);
        Assert.Equal(1800, options.Seconds);
        Assert.Equal(3.5, options.PauseAtSeconds);
        Assert.Equal(3600, options.PauseSeconds);
    }

    [Fact]
    public void NoAudioTurnsBothSourcesOffWithoutEnablingTone()
    {
        string root = OwnedTestDirectory.Create("mc-audio-options-silent-");
        RecordingAudioSelfTestOptions options = Parse(root, "--no-audio");
        Assert.False(options.SystemAudio);
        Assert.False(options.Microphone);
        Assert.False(options.Tone);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("86400")]
    [InlineData("1.25")]
    public void ActiveDurationAcceptsFiniteInclusiveBounds(string seconds)
    {
        string root = OwnedTestDirectory.Create("mc-audio-options-bounds-");
        RecordingAudioSelfTestOptions options = Parse(root, "--seconds", seconds);
        Assert.Equal(double.Parse(seconds, CultureInfo.InvariantCulture), options.Seconds);
    }

    public static IEnumerable<object[]> InvalidOptions()
    {
        yield return [new[] { "--seconds" }];
        foreach (string value in new[] { "0", "0.99", "86400.01", "-1", "NaN", "Infinity", "-Infinity", "1e999", "1,5", " 10", "10 " })
            yield return [new[] { "--seconds", value }];
        foreach (string flag in new[] { "--microphone", "--tone", "--no-audio" })
            yield return [new[] { flag, flag }];
        yield return [new[] { "--seconds", "10", "--SECONDS", "20" }];
        yield return [new[] { "--tone", "true" }];
        yield return [new[] { "--microphone", "false" }];
        yield return [new[] { "--seconds=10" }];
        yield return [new[] { "--unknown" }];
        yield return [new[] { "extra-output" }];
        yield return [new[] { "--pause-at", "2" }];
        yield return [new[] { "--pause-for", "2" }];
        yield return [new[] { "--pause-at", "0", "--pause-for", "1" }];
        yield return [new[] { "--pause-at", "10", "--pause-for", "1" }];
        yield return [new[] { "--pause-at", "11", "--pause-for", "1" }];
        yield return [new[] { "--pause-at", "1", "--pause-for", "0" }];
        yield return [new[] { "--pause-at", "1", "--pause-for", "3600.01" }];
        yield return [new[] { "--pause-at", "NaN", "--pause-for", "1" }];
        yield return [new[] { "--pause-at", "1", "--pause-for", "Infinity" }];
        yield return [new[] { "--no-audio", "--tone" }];
        yield return [new[] { "--tone", "--no-audio" }];
        yield return [new[] { "--no-audio", "--microphone" }];
        yield return [new[] { "--microphone", "--no-audio" }];
        yield return [new[] { "--selftest-resident" }];
        yield return [new[] { RecordingAudioSelfTestOptions.CommandLineSwitch }];
    }

    [Theory]
    [MemberData(nameof(InvalidOptions))]
    public void RejectsMissingConflictingDuplicateAndAmbiguousOptions(string[] flags)
    {
        string root = OwnedTestDirectory.Create("mc-audio-options-invalid-");
        string output = Path.Combine(root, "must-not-be-created");
        Assert.Throws<ArgumentException>(() => Parse(output, flags));
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void RequiresSwitchAndOutputInTheirDeclaredPositions()
    {
        string root = OwnedTestDirectory.Create("mc-audio-options-position-");
        Assert.Throws<ArgumentException>(() => RecordingAudioSelfTestOptions.Parse([]));
        Assert.Throws<ArgumentException>(() => RecordingAudioSelfTestOptions.Parse([RecordingAudioSelfTestOptions.CommandLineSwitch]));
        Assert.Throws<ArgumentException>(() => RecordingAudioSelfTestOptions.Parse([root]));
        Assert.Throws<ArgumentException>(() => RecordingAudioSelfTestOptions.Parse([root, RecordingAudioSelfTestOptions.CommandLineSwitch]));
        Assert.Throws<ArgumentException>(() => RecordingAudioSelfTestOptions.Parse([RecordingAudioSelfTestOptions.CommandLineSwitch, "--tone"]));
    }

    [Fact]
    public void RootIsolationRejectsNormalizedAncestorsAndDescendants()
    {
        string root = OwnedTestDirectory.Create("mc-audio-options-isolation-");
        string profile = Path.Combine(root, "profile");
        string child = Path.Combine(profile, "capture");
        Assert.Throws<ArgumentException>(() => RecordingAudioSelfTestOptions.ValidateIsolatedRoot(profile, profile));
        Assert.Throws<ArgumentException>(() => RecordingAudioSelfTestOptions.ValidateIsolatedRoot(root, profile));
        Assert.Throws<ArgumentException>(() => RecordingAudioSelfTestOptions.ValidateIsolatedRoot(child, profile));
        Assert.Throws<ArgumentException>(() => RecordingAudioSelfTestOptions.ValidateIsolatedRoot(profile.ToUpperInvariant(), profile));
        Assert.Throws<ArgumentException>(() => RecordingAudioSelfTestOptions.ValidateIsolatedRoot(Path.Combine(child, ".."), profile));
        Assert.Throws<ArgumentException>(() => RecordingAudioSelfTestOptions.ValidateIsolatedRoot(profile + Path.DirectorySeparatorChar, profile));
        Assert.False(Directory.Exists(profile));
    }

    [Fact]
    public void SiblingRootsAndNamesWithSharedPrefixesRemainIsolated()
    {
        string root = OwnedTestDirectory.Create("mc-audio-options-sibling-");
        string profile = Path.Combine(root, "profile");
        string sibling = Path.Combine(root, "profile-diagnostic");
        Assert.Equal(sibling, RecordingAudioSelfTestOptions.ValidateIsolatedRoot(sibling, profile));
        string normalized = RecordingAudioSelfTestOptions.ValidateIsolatedRoot(Path.Combine(sibling, "nested", ".."), profile);
        Assert.Equal(sibling, normalized);
        Assert.False(Directory.Exists(sibling));
        Assert.False(Directory.Exists(profile));
    }

    private static RecordingAudioSelfTestOptions Parse(string output, params string[] options) =>
        RecordingAudioSelfTestOptions.Parse([RecordingAudioSelfTestOptions.CommandLineSwitch, output, .. options]);
}
