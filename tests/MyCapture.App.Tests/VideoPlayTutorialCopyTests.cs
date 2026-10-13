using MyCapture.Core.Localization;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// The video-play tutorial clip plays a recording in the video editor. Opening a recording from
/// the library starts the floating player instead, so the steps name the library's own
/// "Edit video" command and the editor where Space plays.
/// </summary>
public sealed class VideoPlayTutorialCopyTests
{
    [Theory]
    [InlineData("ko-KR")]
    [InlineData("en-US")]
    public void StepsOpenTheRecordingInTheVideoEditorThroughItsLibraryCommand(string language)
    {
        using IDisposable scope = UiText.UseLanguage(language);
        FeatureTutorial entry = TutorialCatalog.Get("video-play");
        string editVideo = UiText.Get("Text_35383149904D");

        Assert.Equal(3, entry.Steps.Count);
        Assert.Contains(editVideo, entry.Steps[0], StringComparison.Ordinal);
        Assert.Contains("Space", entry.Steps[1], StringComparison.Ordinal);
        Assert.Contains(language == "ko-KR" ? "동영상 편집기" : "video editor", entry.Steps[1], StringComparison.Ordinal);
        Assert.All(entry.Steps, step => Assert.DoesNotContain("!", step, StringComparison.Ordinal));
    }
}
