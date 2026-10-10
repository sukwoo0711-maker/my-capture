using System.Windows.Input;
using MyCapture.App.Capture;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using Xunit;
using static MyCapture.App.Tests.WindowDetectOverlayTests;

namespace MyCapture.App.Tests;

/// <summary>The overlay's C key: copies the frozen pixel under the pointer and stays open.</summary>
public sealed class ColorPickOverlayTests
{
    [Theory]
    [InlineData(ColorFormat.Hex, "#604020")]
    [InlineData(ColorFormat.Rgb, "rgb(96, 64, 32)")]
    [InlineData(ColorFormat.Hsl, "hsl(30, 50%, 25%)")]
    public void CKeyCopiesTheFrozenPixelInTheConfiguredFormatAndKeepsTheOverlayOpen(ColorFormat format, string expected) =>
        StaTestHost.Run(() =>
        {
            using var language = UiText.UseLanguage("ko-KR");
            var copied = new List<string>();
            var view = new CaptureOverlayView(Frame(), showMagnifier: false)
            {
                ReadCursor = () => new PointD(120, 80),
                CopyColorText = text => { copied.Add(text); return Task.FromResult(true); },
                ReadColorFormat = () => format,
            };
            int cancelled = 0;
            int confirmed = 0;
            view.CancelRequested += (_, _) => cancelled++;
            view.SelectionConfirmed += (_, _) => confirmed++;
            try
            {
                view.InitializePointer();
                KeyEventArgs key = KeyArgs(Key.C);
                view.RaiseEvent(key);

                Assert.True(key.Handled);
                Assert.Equal(new[] { expected }, copied);
                Assert.Equal(UiText.Format("Tools_ColorCopied", expected), view.ToastMessageForTest);
                Assert.Equal(0, cancelled);
                Assert.Equal(0, confirmed);
                Assert.Contains(UiText.Get("ColorPick_OverlayHint"), view.OverlayHintText);
            }
            finally { view.ReleaseResources(); }
        });

    [Fact]
    public void ClipboardFailureShowsFailureFeedbackAndReturnsNothing() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("en-US");
        var view = new CaptureOverlayView(Frame(), showMagnifier: false)
        {
            ReadCursor = () => new PointD(5, 5),
            CopyColorText = _ => Task.FromResult(false),
        };
        try
        {
            view.InitializePointer();
            Task<string?> copy = view.CopyColorAtPointerAsync();
            Assert.True(copy.IsCompletedSuccessfully);
            Assert.Null(copy.Result);
            Assert.Equal(UiText.Get("ColorPick_CopyFailed"), view.ToastMessageForTest);
        }
        finally { view.ReleaseResources(); }
    });

    [Fact]
    public void ColorCopyIsUnboundWithoutAnOwnerAndAfterTheOverlayEnds() => StaTestHost.Run(() =>
    {
        var unbound = new CaptureOverlayView(Frame(), showMagnifier: false) { ReadCursor = () => new PointD(5, 5) };
        int copies = 0;
        var ended = new CaptureOverlayView(Frame(), showMagnifier: false)
        {
            ReadCursor = () => new PointD(5, 5),
            CopyColorText = _ => { copies++; return Task.FromResult(true); },
        };
        try
        {
            unbound.InitializePointer();
            KeyEventArgs key = KeyArgs(Key.C);
            unbound.RaiseEvent(key);
            Assert.False(key.Handled);
            Assert.Null(unbound.ToastMessageForTest);

            ended.InitializePointer();
            ended.RaiseEvent(KeyArgs(Key.Escape));
            Assert.Null(ended.CopyColorAtPointerAsync().Result);
            Assert.Equal(0, copies);
        }
        finally
        {
            unbound.ReleaseResources();
            ended.ReleaseResources();
        }
    });
}
