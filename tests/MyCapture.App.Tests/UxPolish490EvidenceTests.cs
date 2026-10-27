using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Capture;
using MyCapture.App.Diagnostics;
using MyCapture.App.Ocr;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Ocr;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// Renders the quick access card and the OCR translation tab so a change can be compared with the
/// build before it. Set <c>MYCAPTURE_UXPOLISH_EVIDENCE</c> to a directory to keep the PNGs and
/// <c>MYCAPTURE_UXPOLISH_PHASE</c> to label them (for example <c>before</c> or <c>after</c>); without
/// the directory the tests do nothing. They call only members the earlier build also has.
/// </summary>
public sealed class UxPolish490EvidenceTests
{
    private static string? Folder => Environment.GetEnvironmentVariable("MYCAPTURE_UXPOLISH_EVIDENCE") is { Length: > 0 } folder ? folder : null;

    private static string Phase => Environment.GetEnvironmentVariable("MYCAPTURE_UXPOLISH_PHASE") is { Length: > 0 } phase ? phase : "after";

    public static IEnumerable<object[]> CardCases()
    {
        foreach (string language in new[] { "ko", "en" })
        foreach (double dpi in new[] { 1.0, 1.5 })
        foreach (double textScale in new[] { 1.0, 1.5 })
            yield return [language, dpi, textScale];
    }

    [Theory]
    [MemberData(nameof(CardCases))]
    public void QuickAccessCardRenders(string language, double dpi, double textScale) => StaTestHost.Run(() =>
    {
        if (Folder is not { } requested) return;
        string folder = DiagnosticOutputPaths.Create(requested);
        using var culture = UiText.UseLanguage(language);
        using var motion = new ShortcutMotionTestScope();
        BitmapSource bitmap = QuickAccessLabelFixture.Sample();
        var card = new QuickAccessWindow(bitmap, () => Task.FromResult(true), () => { }, () => Task.CompletedTask,
            () => Task.FromResult(true), () => { }, () => Task.FromResult("unused.png"), firstCaptureTip: null,
            copiedToClipboard: true);
        try
        {
            Border host = QuickAccessDetachedFixture.Layout(card, dpi);
            QuickAccessLabelFixture.ApplyTextScale(host, textScale);
            QuickAccessLabelFixture.RealIcons(card, host);
            QuickAccessLabelFixture.Relayout(host, card.Width, dpi);
            QuickAccessLabelFixture.SavePng(host, dpi,
                DiagnosticOutputPaths.Child(folder, $"quick-card-{language}-dpi{dpi * 96:0}-text{textScale:0.0}-{Phase}.png"));
            // The same card while Alt shows each action's key.
            foreach (Button button in card.ActionButtonsForTest)
                button.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            ShortcutPresentation.SetAltRevealed(host, true);
            host.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() => { }));
            QuickAccessLabelFixture.Relayout(host, card.Width, dpi);
            QuickAccessLabelFixture.SavePng(host, dpi,
                DiagnosticOutputPaths.Child(folder, $"quick-card-{language}-dpi{dpi * 96:0}-text{textScale:0.0}-alt-{Phase}.png"));
        }
        finally { card.Close(); }
    });

    [Theory]
    [InlineData("ko")]
    [InlineData("en")]
    public void OcrTranslationTabRenders(string language) => Ocr410Fixture.RunSta(() =>
    {
        if (Folder is not { } requested) return;
        string folder = DiagnosticOutputPaths.Create(requested);
        using var culture = UiText.UseLanguage(language);
        var translator = new OcrTranslationWindowTests.FakeTranslator();
        (BitmapSource image, OcrResult result) = TranslationFixture.RegisterTableWithImage();
        var window = new OcrResultWindow { ShowInTaskbar = false, Width = 1040, Height = 720, Translator = translator };
        try
        {
            Ocr410Fixture.LoadTheme(window);
            window.ShowResult(result, "fixture", image);
            window.SelectExtractionMode(OcrExtractionMode.Translation);
            OcrTranslationWindowTests.PumpUntil(() => window.TranslationWork.IsCompleted);
            window.UpdateLayout();
            Ocr410Fixture.Settle();
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight),
                96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);
            Ocr410Fixture.Save(bitmap, DiagnosticOutputPaths.Child(folder, $"ocr-translation-{language}-{Phase}.png"));
        }
        finally { window.CloseForExit(); }
    });
}
