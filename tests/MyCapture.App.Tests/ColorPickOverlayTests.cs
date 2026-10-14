using System.Windows.Input;
using MyCapture.App.Capture;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using Xunit;
using static MyCapture.App.Tests.WindowDetectOverlayTests;

namespace MyCapture.App.Tests;

/// <summary>The overlay's C key: copies the frozen pixel and ends only after success.</summary>
public sealed class ColorPickOverlayTests
{
    [Theory]
    [InlineData(ColorFormat.Hex, "#604020")]
    [InlineData(ColorFormat.Rgb, "rgb(96, 64, 32)")]
    [InlineData(ColorFormat.Hsl, "hsl(30, 50%, 25%)")]
    public void CKeyCopiesTheFrozenPixelInTheConfiguredFormatAndEndsTheOverlay(ColorFormat format, string expected) =>
        StaTestHost.Run(() =>
        {
            using var language = UiText.UseLanguage("ko-KR");
            var copied = new List<string>();
            var view = new CaptureOverlayView(Frame(), showMagnifier: false)
            {
                ReadCursor = () => new PointD(120, 80),
                CopyColorText = text => { copied.Add(text); return Task.FromResult(true); },
                ReadColorFormat = () => format,
                ShowModeBar = true,
            };
            int cancelled = 0;
            int confirmed = 0;
            ColorCopyFeedback? feedback = null;
            view.ColorCopied = result => { Assert.Equal(1, cancelled); feedback = result; };
            view.CancelRequested += (_, _) => cancelled++;
            view.SelectionConfirmed += (_, _) => confirmed++;
            try
            {
                view.InitializePointer();
                KeyEventArgs key = KeyArgs(Key.C);
                view.RaiseEvent(key);

                Assert.True(key.Handled);
                Assert.Equal(new[] { expected }, copied);
                Assert.NotNull(feedback);
                Assert.Equal(UiText.Format("Tools_ColorCopied", expected), feedback.Message);
                Assert.Equal(System.Windows.Media.Color.FromRgb(96, 64, 32), feedback.ChipColor);
                Assert.Equal("#604020", feedback.HexValue);
                Assert.Equal(1, cancelled);
                Assert.Equal(0, confirmed);
                Assert.Null(view.CopyColorAtPointerAsync().Result);
                Assert.Single(copied);
                // C is a shortcut, not a mode switch: the bar still shows image capture, and its
                // colour button names C as its accelerator key and in its tooltip (no badge on the bar).
                Assert.Equal(CaptureSelectorMode.Capture, view.Mode);
                Assert.Equal(System.Windows.Visibility.Visible, view.ModeBarForTest!.ColorButton.Visibility);
                Assert.Equal("C", System.Windows.Automation.AutomationProperties.GetAcceleratorKey(view.ModeBarForTest.ColorButton));
                Assert.EndsWith("(C)", (string)view.ModeBarForTest.ColorButton.ToolTip, StringComparison.Ordinal);
            }
            finally { view.ReleaseResources(); }
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClipboardFailureShowsFailureFeedbackAndReturnsNothing(bool throws) => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("en-US");
        var view = new CaptureOverlayView(Frame(), showMagnifier: false)
        {
            ReadCursor = () => new PointD(5, 5),
            CopyColorText = _ => throws ? Task.FromException<bool>(new InvalidOperationException("clipboard unavailable")) : Task.FromResult(false),
        };
        int cancelled = 0;
        view.CancelRequested += (_, _) => cancelled++;
        view.ColorCopied = _ => Assert.Fail("A failed copy must not report success.");
        try
        {
            view.InitializePointer();
            Task<string?> copy = view.CopyColorAtPointerAsync();
            Assert.True(copy.IsCompletedSuccessfully);
            Assert.Null(copy.Result);
            Assert.Equal(UiText.Get("ColorPick_CopyFailed"), view.ToastMessageForTest);
            Assert.Equal(0, cancelled);
            view.RaiseEvent(KeyArgs(Key.Escape));
            Assert.Equal(1, cancelled);
        }
        finally { view.ReleaseResources(); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PendingCopyIgnoresRepeatedKeysAndLateFeedbackAfterCancellation(bool release) => StaTestHost.Run(() =>
    {
        var pending = new TaskCompletionSource<bool>();
        int calls = 0;
        int cancelled = 0;
        var view = new CaptureOverlayView(Frame(), showMagnifier: false)
        {
            CopyColorText = _ => { calls++; return pending.Task; },
            ColorCopied = _ => Assert.Fail("A closed selector must not show late feedback."),
        };
        view.CancelRequested += (_, _) => cancelled++;
        try
        {
            Task<string?> copy = view.CopyColorAtPointerAsync();
            Assert.False(copy.IsCompleted);
            Assert.Null(view.CopyColorAtPointerAsync().Result);
            Assert.Equal(1, calls);
            view.RaiseEvent(KeyArgs(Key.Escape));
            if (release) view.ReleaseResources();
            pending.SetResult(true);
            Pump();
            Assert.True(copy.IsCompletedSuccessfully);
            Assert.Equal(1, cancelled);
            Assert.Null(view.ToastMessageForTest);
        }
        finally { view.ReleaseResources(); }
    });

    [Theory]
    [InlineData("ko-KR", "C: 색상 복사 후 캡처 종료")]
    [InlineData("en-US", "C: Copy color and end capture")]
    public void HintExplainsCopyAndEndCapture(string language, string expected) => StaTestHost.Run(() =>
    {
        using var scope = UiText.UseLanguage(language);
        var view = new CaptureOverlayView(Frame(), showMagnifier: true)
        {
            CopyColorText = _ => Task.FromResult(true),
        };
        try
        {
            Assert.Equal(expected, view.ColorCopyHint);
            view.ShowShortcutHints = false;
            Assert.Null(view.ColorCopyHint);
            view.ShowShortcutHints = true;
            view.CopyColorText = null;
            Assert.Null(view.ColorCopyHint);
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
