using System.IO;
using MyCapture.App.Pinning;
using MyCapture.App.Preview;
using MyCapture.App.Threading;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class PreviewLaunchOptionsTests
{
    private const string Roaming = @"C:\SyntheticUser\AppData\Roaming";
    private const string Local = @"C:\SyntheticUser\AppData\Local";
    private const string Profile = @"C:\PreviewTests\pin-review\profile";

    [Fact]
    public void NormalLaunchArgumentsDoNotSelectPreview()
    {
        string[][] cases = [[], ["--background"], ["--settings"], ["--selftest-shell", @"C:\SyntheticOutput"]];
        foreach (string[] args in cases) Assert.Null(PreviewLaunchOptions.Parse(args, Roaming, Local));
    }

    [Fact]
    public void ProfileIdentityIsCanonicalAndSeparateFromOtherProfiles()
    {
        var first = Parse(Profile);
        var equivalent = Parse(@"c:\previewtests\pin-review\other\..\profile\");
        var different = Parse(@"C:\PreviewTests\other-review\profile");
        Assert.Equal(first.MutexName, equivalent.MutexName);
        Assert.Equal(first.ActivationName, equivalent.ActivationName);
        Assert.NotEqual(first.MutexName, different.MutexName);
        Assert.NotEqual(first.ActivationName, different.ActivationName);
        Assert.Contains("MyCapture.PinPreview", first.MutexName);
        Assert.Contains("MyCapture.PinPreview", first.ActivationName);
        Assert.Equal(Path.Combine(Profile, "settings.json"), first.Paths.SettingsFile);
        Assert.Equal(Path.Combine(Profile, "pins"), first.Paths.PinSessionRoot);
        Assert.Equal(Path.Combine(Profile, "translation-models"), first.Paths.TranslationModelsRoot);
        Assert.StartsWith(Profile, first.Paths.QuickSaveRoot);
    }

    [Theory]
    [InlineData("relative-profile")]
    [InlineData("C:\\")]
    [InlineData("\\\\server\\preview")]
    [InlineData("C:\\SyntheticUser\\AppData\\Roaming\\MyCapture")]
    [InlineData("C:\\SyntheticUser\\AppData\\Roaming\\MyCapture\\preview")]
    [InlineData("C:\\SyntheticUser\\AppData\\Local\\MyCapture\\translation-models")]
    [InlineData("C:\\SyntheticUser\\AppData\\Local\\Packages\\MyCapture_Test\\LocalState")]
    [InlineData("C:\\SyntheticUser\\AppData\\Roaming\\MyCapture.")]
    [InlineData("C:\\SyntheticUser\\AppData")]
    public void RejectsPathsThatAreNotIsolatedLocalProfiles(string root) =>
        Assert.Throws<ArgumentException>(() => Parse(root));

    [Fact]
    public void PreviewRequiresItsOwnCompleteCommand()
    {
        Assert.Throws<ArgumentException>(() => PreviewLaunchOptions.Parse(["--preview-pin"], Roaming, Local));
        Assert.Throws<ArgumentException>(() => PreviewLaunchOptions.Parse(["--preview-pin", Profile, "--settings"], Roaming, Local));
    }

    [Fact]
    public void PreviewDefaultsAndStartupAdapterCannotEnableWindowsStartup()
    {
        var settings = PreviewLaunchOptions.CreateSettings();
        Assert.False(settings.General.LaunchAtLogin);
        Assert.False(settings.Pin.RestoreOpenPinsOnLaunch);
        var startup = new PreviewStartupRegistration();
        Assert.False(startup.IsEnabled());
        Assert.False(startup.ReconcileOnStartup(true));
        Assert.False(startup.Apply(true).Succeeded);
        Assert.True(startup.Apply(false).Succeeded);
        Assert.False(startup.IsEnabled());
    }

    [Fact]
    public async Task CopyBufferRetainsExactSourceWithoutAWindowsClipboardWriter()
    {
        var buffer = new PreviewCopyBuffer();
        string? observed = null;
        buffer.Changed += text => observed = text;
        const string source = "# Release\r\n\r\n**Ready**\r\n";
        Assert.True(await buffer.CopyTextAsync(source));
        Assert.Equal(source, buffer.Text);
        Assert.Equal(source, observed);
    }

    [Fact]
    public async Task EditableSourceUsesRealRenderersAndRetainsAllOriginalText() => await StaThreadTask.RunAsync(() =>
    {
        const string source = "# Custom input\r\n\r\n**Save your work before closing the window.**";
        var markdown = PinPreviewSession.CreateContent(source, markdown: true);
        var english = PinPreviewSession.CreateContent(source, markdown: false);
        Assert.True(markdown.IsMarkdown);
        Assert.False(english.IsMarkdown);
        Assert.Equal(source, markdown.OriginalText);
        Assert.Equal(source, english.OriginalText);
        Assert.False(markdown.Provenance.HasSource);
        Assert.False(english.Provenance.HasSource);
        Assert.True(markdown.Image.IsFrozen);
        Assert.True(english.Image.IsFrozen);
        return true;
    });

    [Fact]
    public async Task DefaultEnglishSampleCanTranslateImmediately() => await StaThreadTask.RunAsync(() =>
    {
        var content = PinPreviewSession.CreateContent(PinPreviewSession.EnglishSampleSource, markdown: false);
        Assert.False(content.IsMarkdown);
        Assert.True(PinTranslationService.CanTranslate(content));
        Assert.Equal(PinPreviewSession.EnglishSampleSource, content.OriginalText);
        Assert.Contains("5000 ms to 30000 ms", content.OriginalText);
        return true;
    });

    private static PreviewLaunchOptions Parse(string root) =>
        PreviewLaunchOptions.Parse(["--preview-pin", root], Roaming, Local)!;
}
