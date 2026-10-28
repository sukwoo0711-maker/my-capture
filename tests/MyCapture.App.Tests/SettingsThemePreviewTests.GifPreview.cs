using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Diagnostics;
using MyCapture.App.Recording;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Recording;
using MyCapture.Core.Themes;
using MyCapture.Platform.Recording;
using Xunit;

namespace MyCapture.App.Tests;

public sealed partial class SettingsThemePreviewTests
{
    [Fact]
    public Task GifPreview_ExportDialogPlaysTheSampleAndRecommendsInEveryTheme() =>
        RunIsolated(nameof(GifPreview_ExportDialogPlaysTheSampleAndRecommendsInEveryTheme), VerifyGifPreviewDialog);

    private static void VerifyGifPreviewDialog()
    {
        // As inside the app's dispatcher, awaited sample and original renders resume on the UI thread.
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{name}.xaml"),
            });
        string root = OwnedTestDirectory.Create("mc-gif-preview-dialog-");
        bool? motion = FluidMotion.AnimationsEnabledOverrideForTest;
        Func<bool>? previousReader = ThemeService.SystemLightThemeReaderForTest;
        ThemeService.SystemLightThemeReaderForTest = static () => false;
        try
        {
            RecordingResult recording = GifPreviewFixtureClips.Encode(Path.Combine(root, "clip.mp4"),
                GifPreviewFixtureClips.Kind.DocumentScroll, 1280, 720, 15, 4_000);
            var document = VideoEditDocument.CreateFor(recording.Width, recording.Height, recording.DurationMs);
            document.TextOverlays.Add(new TimedTextOverlay { Text = "GIF 미리보기", StartMs = 500, EndMs = 3_500 });
            foreach (AppTheme theme in Enum.GetValues<AppTheme>())
            {
                // High contrast doubles as the reduced-motion run: the sample must start paused.
                bool animate = theme != AppTheme.HighContrast;
                VerifyGifPreviewTheme(recording, document, theme, animate, "ko-KR");
            }

            VerifyGifPreviewTheme(recording, document, AppTheme.Glass, animate: true, "en-US");
            ThemeService.SystemLightThemeReaderForTest = static () => true;
            VerifyGifPreviewTheme(recording, document, AppTheme.System, animate: true, "en-US");

            RecordingResult longer = GifPreviewFixtureClips.Encode(Path.Combine(root, "long.mp4"),
                GifPreviewFixtureClips.Kind.Mixed, 1280, 720, 15, 12_000);
            VerifyGifPreviewLongRange(longer);
            Console.WriteLine("GIF preview layout checks completed before cleanup.");
        }
        catch (Exception error)
        {
            // Preserve the assertion/decoder evidence if the host also rejects the
            // recoverable fixture cleanup in finally (for example in a restricted sandbox).
            Console.WriteLine("GIF preview body failed before cleanup: " + error);
            throw;
        }
        finally
        {
            FluidMotion.AnimationsEnabledOverrideForTest = motion;
            ThemeService.SystemLightThemeReaderForTest = previousReader;
            application.Shutdown();
            OwnedTestDirectory.Delete(root);
        }
    }

    private static void VerifyGifPreviewTheme(RecordingResult recording, VideoEditDocument document, AppTheme theme, bool animate, string language)
    {
        using var scope = UiText.UseLanguage(language);
        bool korean = language.StartsWith("ko", StringComparison.Ordinal);
        ThemeService.Apply(theme);
        FluidMotion.AnimationsEnabledOverrideForTest = animate;
        IReadOnlyDictionary<string, ThemeColor> colors = ThemeCatalog.ColorsFor(ThemeService.ResolveTheme(theme));
        var dialog = new VideoExportDialog(recording, document, NullLoggerFactory.Instance, gif: true)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000, Top = -10000,
            ShowActivated = false, ShowInTaskbar = false,
        };
        FluidMotion.SetWindowEntrance(dialog, false);
        try
        {
            dialog.Show();
            PumpUntil(dialog, () => dialog.SamplePanel.HasSample, "the preview sample");
            GifPreviewPanel panel = dialog.SamplePanel;
            Assert.True(panel.FrameCount > 30, $"{theme}: {panel.FrameCount} frames");
            Assert.Equal(panel.FrameCount, dialog.DisplayedFrameCount);
            Assert.Contains($"{panel.FrameCount}{(korean ? "프레임" : " frames")}", dialog.EstimateText, StringComparison.Ordinal);
            Assert.True(dialog.HasMeasuredResult, $"{theme}: a whole-range sample must be the exact export at once");
            Assert.True(dialog.CanExport && !dialog.IsProgressVisible, $"{theme}: 내보내기 is available before any encode");
            Assert.Contains(korean ? "측정" : "measured", dialog.EstimateText, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(dialog.AdviceText));
            Assert.Equal(animate, panel.IsPlaying);
            Assert.Equal(!animate, panel.InfoText.Contains(korean ? "애니메이션 효과가 꺼져" : "animation effects are off", StringComparison.Ordinal));
            Button play = Descendants(panel).OfType<Button>().Single();
            Assert.Equal(UiText.Get(animate ? "GifPreview_Pause" : "GifPreview_Play"), AutomationProperties.GetName(play));
            Assert.True(play.IsEnabled && play.Focusable && play.IsTabStop);
            Slider position = Descendants(panel).OfType<Slider>().Single();
            Assert.Equal(UiText.Get("GifPreview_Position"), AutomationProperties.GetName(position));
            Assert.Equal(panel.FrameCount - 1, position.Maximum);
            AssertBrush(panel.Background, colors["Surface.Sunken"]);
            AssertBrush(Assert.IsType<TextBlock>(Descendants(panel).OfType<TextBlock>().First()).Foreground, colors["Text.Primary"]);
            ComboBox quality = Descendants(dialog).OfType<ComboBox>().Single(combo => AutomationProperties.GetName(combo) == UiText.Get("MediaExport_GifPreset"));
            Assert.Equal(korean ? "GIF 크기" : "GIF size", AutomationProperties.GetName(quality));
            Assert.Contains(UiText.Get("GifPreview_Recommended"), string.Join("|", quality.Items.Cast<object>()), StringComparison.Ordinal);
            Assert.DoesNotContain("fps", string.Join("|", quality.Items.Cast<object>()), StringComparison.OrdinalIgnoreCase);
            AssertGifSizeClosedCaption(dialog, quality);
            ComboBox frameRate = GifDialogLayoutTests.Combo(dialog, korean ? "프레임 속도" : "Frame rate");
            Assert.Equal(korean ? "자동 · 10fps" : "Auto · 10 fps", frameRate.SelectedItem.ToString());

            if (!animate)
            {
                play.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.True(panel.IsPlaying);
                Assert.Equal(UiText.Get("GifPreview_Pause"), AutomationProperties.GetName(play));
            }

            // Comparison is a still view of the same moment from the source recording.
            ToggleButton compare = Descendants(panel).OfType<ToggleButton>().Single(toggle => AutomationProperties.GetName(toggle) == UiText.Get("GifPreview_Compare"));
            position.Value = 12;
            compare.IsChecked = true;
            Assert.False(panel.IsPlaying);
            PumpUntil(dialog, () => panel.OriginalFrame is not null, "the original comparison frame");
            Assert.Equal(12, panel.FrameIndex);
            Assert.Equal((recording.Width, recording.Height), (panel.OriginalFrame!.PixelWidth, panel.OriginalFrame.PixelHeight));
            ToggleButton actual = Descendants(panel).OfType<ToggleButton>().Single(toggle => AutomationProperties.GetName(toggle) == UiText.Get("GifPreview_ActualSize"));
            actual.IsChecked = true;
            Drain(dialog);
            Image gif = Descendants(panel).OfType<Image>().Single(image => AutomationProperties.GetName(image) == UiText.Get("GifPreview_GifFrame"));
            Assert.Equal(960 / VisualTreeHelper.GetDpi(panel).DpiScaleX, gif.Width, 3);
            SaveGifPreviewEvidence(dialog, $"gif-preview-{theme}-{language}-compare.png");
            actual.IsChecked = false;
            compare.IsChecked = false;
            position.Value = 20;
            Drain(dialog);
            SaveGifPreviewEvidence(dialog, $"gif-preview-{theme}-{language}.png");
            if ((theme == AppTheme.Daylight && korean) || (theme == AppTheme.Glass && !korean))
            {
                VerifyGifPreviewLayoutEvidence(dialog, theme, language);
                VerifyGifPreviewCapEvidence(dialog, theme, language);
            }

            // A different preset invalidates the measured result and renders a new sample.
            dialog.QualityIndex = 3;
            Assert.False(dialog.HasMeasuredResult);
            Assert.False(panel.HasSample);
            PumpUntil(dialog, () => panel.HasSample, "the 480 px sample");
            Assert.True(dialog.HasMeasuredResult);
            Assert.Equal(korean ? "자동 · 5fps" : "Auto · 5 fps", frameRate.SelectedItem.ToString());
            AssertGifSizeClosedCaption(dialog, quality);
        }
        finally
        {
            dialog.Close();
            Drain(dialog);
        }
    }

    private static void AssertGifSizeClosedCaption(VideoExportDialog dialog, ComboBox quality)
    {
        quality.ApplyTemplate();
        Drain(dialog);
        string fullCaption = quality.SelectedItem.ToString()!;
        string compactCaption = string.Join(" · ", fullCaption.Split(" · ", StringSplitOptions.None).Skip(1))
            .Replace(" px", "px", StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(compactCaption));
        string[] visible = Descendants(quality).OfType<TextBlock>().Where(text => text.IsVisible).Select(text => text.Text).ToArray();
        Assert.Contains(compactCaption, visible);
        Assert.DoesNotContain(fullCaption, visible);
        Assert.DoesNotContain(visible, text => text.Contains("fps", StringComparison.OrdinalIgnoreCase));
    }

    private static void VerifyGifPreviewLayoutEvidence(VideoExportDialog dialog, AppTheme theme, string language)
    {
        double defaultWidth = dialog.Width, defaultHeight = dialog.Height;
        Assert.InRange(defaultHeight, dialog.MinHeight, 860);
        string[] fontKeys = ["FontSize.Body", "FontSize.Control", "FontSize.Caption", "CommandButton.FontSize"];
        double[] fontSizes = fontKeys.Select(key => (double)dialog.FindResource(key)).ToArray();
        ComboBox[] options =
        [
            GifDialogLayoutTests.Combo(dialog, UiText.Get("MediaExport_GifPreset")),
            GifDialogLayoutTests.Combo(dialog, UiText.Get("GifExport_FrameRate")),
            GifDialogLayoutTests.Combo(dialog, UiText.Get("MediaExport_Speed")),
            GifDialogLayoutTests.Combo(dialog, UiText.Get("GifPreview_Target")),
        ];
        ScrollViewer form = Descendants(dialog).OfType<ScrollViewer>().First();
        Grid stage = Descendants(dialog.SamplePanel).OfType<Grid>().Single(grid => grid.Height == GifPreviewPanel.ViewportHeight);
        try
        {
            int originalTarget = dialog.ShareTargetIndex;
            for (int index = 0; index < GifSharePolicy.Targets.Count; index++)
            {
                dialog.ShareTargetIndex = index;
                AssertGifTargetClosedCaption(dialog, options[3]);
            }
            dialog.ShareTargetIndex = originalTarget;
            foreach (double textScale in new[] { 1d, 1.5d })
            {
                for (int index = 0; index < fontKeys.Length; index++)
                    dialog.Resources[fontKeys[index]] = fontSizes[index] * textScale;
                foreach (bool minimum in new[] { false, true })
                {
                    dialog.Width = minimum ? dialog.MinWidth : defaultWidth;
                    dialog.Height = minimum ? dialog.MinHeight : defaultHeight;
                    form.ScrollToTop();
                    Drain(dialog);
                    string variant = $"{(minimum ? "minimum" : "default")}-text{(int)(textScale * 100)}";
                    string context = $"{theme}/{language}/{variant}";
                    Assert.True(form.ScrollableWidth <= 0.5, context + ": settings must fit horizontally");
                    Assert.Equal(GifPreviewPanel.ViewportHeight, stage.ActualHeight, 3);
                    Assert.Equal(options[0].ActualHeight, options[1].ActualHeight, 3);
                    Assert.Equal(options[0].ActualHeight, options[2].ActualHeight, 3);
                    Assert.Equal(options[0].ActualHeight, options[3].ActualHeight, 3);
                    foreach (ComboBox option in options)
                    {
                        foreach (TextBlock selectedText in Descendants(option).OfType<TextBlock>().Where(text => text.IsVisible))
                            AssertGifTextFits(selectedText, context + ": " + AutomationProperties.GetName(option));
                        StackPanel cell = Assert.IsType<StackPanel>(LogicalTreeHelper.GetParent(option));
                        TextBlock label = Assert.IsType<TextBlock>(cell.Children[0]);
                        Point labelOrigin = label.TranslatePoint(new Point(), cell);
                        Point comboOrigin = option.TranslatePoint(new Point(), cell);
                        Assert.True(labelOrigin.X >= -0.5 && labelOrigin.X + label.ActualWidth <= cell.ActualWidth + 0.5,
                            context + ": " + label.Text + " label is outside its cell");
                        Assert.True(labelOrigin.Y + label.ActualHeight <= comboOrigin.Y + 0.5,
                            context + ": " + label.Text + " overlaps its combo");
                    }
                    Point sizeOrigin = options[0].TranslatePoint(new Point(), dialog);
                    Point rateOrigin = options[1].TranslatePoint(new Point(), dialog);
                    Point speedOrigin = options[2].TranslatePoint(new Point(), dialog);
                    Point targetOrigin = options[3].TranslatePoint(new Point(), dialog);
                    Assert.True(sizeOrigin.X + options[0].ActualWidth <= rateOrigin.X + 0.5, context);
                    Assert.True(speedOrigin.X + options[2].ActualWidth <= targetOrigin.X + 0.5, context);
                    Assert.True(sizeOrigin.Y + options[0].ActualHeight < speedOrigin.Y, context);
                    Assert.True(rateOrigin.Y + options[1].ActualHeight < targetOrigin.Y, context);
                    AssertGifTargetClosedCaption(dialog, options[3]);
                    AssertGifPreviewControlsFit(dialog.SamplePanel, context);
                    ToggleButton comparison = Descendants(dialog.SamplePanel).OfType<ToggleButton>()
                        .Single(toggle => AutomationProperties.GetName(toggle) == UiText.Get("GifPreview_Compare"));
                    // Enlarged English controls can legitimately need a second row at
                    // the default width too. Normal text must return to one row when widened.
                    if (minimum || textScale == 1) Assert.Equal(minimum ? 1 : 0, Grid.GetRow(comparison));
                    SaveGifPreviewEvidence(dialog, $"gif-preview-{theme}-{language}-{variant}.png");
                    if (minimum)
                    {
                        form.ScrollToEnd();
                        Drain(dialog);
                        SaveGifPreviewEvidence(dialog, $"gif-preview-{theme}-{language}-{variant}-end.png");
                    }
                }
            }
        }
        finally
        {
            for (int index = 0; index < fontKeys.Length; index++) dialog.Resources[fontKeys[index]] = fontSizes[index];
            dialog.Width = defaultWidth;
            dialog.Height = defaultHeight;
            form.ScrollToTop();
            Drain(dialog);
        }

        AssertGifPreviewControlsFit(dialog.SamplePanel, "restored default size");
        Assert.Equal(0, Grid.GetRow(Descendants(dialog.SamplePanel).OfType<ToggleButton>()
            .Single(toggle => AutomationProperties.GetName(toggle) == UiText.Get("GifPreview_Compare"))));

        // Match the lead's earlier 980 DIP capture as well as the actual default-size evidence.
        if (theme == AppTheme.Daylight && language == "ko-KR")
        {
            try
            {
                dialog.Height = 980;
                Drain(dialog);
                SaveGifPreviewEvidence(dialog, $"gif-preview-{theme}-{language}-comparison980.png");
            }
            finally { dialog.Height = defaultHeight; Drain(dialog); }
        }
    }

    private static void AssertGifTargetClosedCaption(VideoExportDialog dialog, ComboBox target)
    {
        target.ApplyTemplate();
        Drain(dialog);
        string fullCaption = target.SelectedItem.ToString()!;
        string compactCaption = fullCaption.Split(" · ", StringSplitOptions.None)[0];
        string[] visible = Descendants(target).OfType<TextBlock>().Where(text => text.IsVisible).Select(text => text.Text).ToArray();
        Assert.Contains(compactCaption, visible);
        if (fullCaption != compactCaption) Assert.DoesNotContain(fullCaption, visible);
        Assert.Equal(fullCaption, target.ToolTip);
        Assert.Equal(fullCaption, AutomationProperties.GetHelpText(target));
        if (dialog.ShareTargetIndex == 2)
        {
            Assert.Contains("X", fullCaption, StringComparison.Ordinal);
            Assert.Contains("350", fullCaption, StringComparison.Ordinal);
        }
    }

    private static void AssertGifTextFits(TextBlock text, string context)
    {
        if (string.IsNullOrEmpty(text.Text)) return;
        var natural = new FormattedText(text.Text, CultureInfo.CurrentUICulture, text.FlowDirection,
            new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch), text.FontSize,
            Brushes.Black, VisualTreeHelper.GetDpi(text).PixelsPerDip);
        Assert.True(natural.WidthIncludingTrailingWhitespace <= text.ActualWidth + 1,
            $"{context}: '{text.Text}' needs {natural.WidthIncludingTrailingWhitespace:0.0} DIP but has {text.ActualWidth:0.0}");
    }

    private static void AssertGifPreviewControlsFit(GifPreviewPanel panel, string context)
    {
        FrameworkElement root = Assert.IsAssignableFrom<FrameworkElement>(panel.Child);
        Slider slider = Descendants(panel).OfType<Slider>().Single();
        Button play = Descendants(panel).OfType<Button>().Single();
        ToggleButton compare = Descendants(panel).OfType<ToggleButton>().Single(toggle => AutomationProperties.GetName(toggle) == UiText.Get("GifPreview_Compare"));
        ToggleButton actual = Descendants(panel).OfType<ToggleButton>().Single(toggle => AutomationProperties.GetName(toggle) == UiText.Get("GifPreview_ActualSize"));
        TextBlock frame = Descendants(panel).OfType<TextBlock>().Single(text => text.MinWidth == 64);
        FrameworkElement[] controls = [play, slider, frame, compare, actual];
        Assert.True(slider.ActualWidth >= 64 - 0.5, context + ": the frame slider needs at least 64 DIP");
        Rect[] bounds = controls.Select(control => new Rect(control.TranslatePoint(new Point(), root), control.RenderSize)).ToArray();
        for (int index = 0; index < controls.Length; index++)
        {
            Rect bound = bounds[index];
            Assert.True(bound.Left >= -0.5 && bound.Top >= -0.5
                && bound.Right <= root.ActualWidth + 0.5 && bound.Bottom <= root.ActualHeight + 0.5,
                context + ": preview control is outside its content: " + controls[index].GetType().Name);
            foreach (TextBlock text in Descendants(controls[index]).OfType<TextBlock>().Where(text => text.IsVisible))
                AssertGifTextFits(text, context + ": preview control");
            for (int other = index + 1; other < controls.Length; other++)
            {
                Rect overlap = Rect.Intersect(bound, bounds[other]);
                Assert.True(overlap.IsEmpty || overlap.Width <= 0.5 || overlap.Height <= 0.5,
                    context + ": preview controls overlap");
            }
        }
        AssertGifTextFits(frame, context + ": frame counter");
    }

    private static void VerifyGifPreviewCapEvidence(VideoExportDialog dialog, AppTheme theme, string language)
    {
        int previousRate = dialog.SelectedFrameRate;
        string[] fontKeys = ["FontSize.Body", "FontSize.Control", "FontSize.Caption", "CommandButton.FontSize"];
        double[] fontSizes = fontKeys.Select(key => (double)dialog.FindResource(key)).ToArray();
        try
        {
            dialog.SelectedFrameRate = 30;
            PumpUntil(dialog, () => dialog.SamplePanel.HasSample && dialog.SampleCompletion.IsCompleted, "the source-capped 15 fps sample");
            Assert.Equal(15, dialog.EffectiveFrameRate);
            string expected = language == "ko-KR" ? "원본이 15fps라 15fps로 만듭니다." : "Made at 15 fps because the source is 15 fps.";
            foreach (double scale in new[] { 1d, 1.5d })
            {
                for (int index = 0; index < fontKeys.Length; index++) dialog.Resources[fontKeys[index]] = fontSizes[index] * scale;
                Drain(dialog);
                TextBlock notice = Descendants(dialog).OfType<TextBlock>().Single(text => text.Text == expected);
                Assert.True(notice.IsVisible);
                AssertGifTextFits(notice, $"{language}/source-cap/text{scale}");
                AssertGifPreviewControlsFit(dialog.SamplePanel, $"{language}/source-cap/text{scale}");
                SaveGifPreviewEvidence(dialog, $"gif-preview-{theme}-{language}-source-cap-text{(int)(scale * 100)}.png");
            }
        }
        finally
        {
            for (int index = 0; index < fontKeys.Length; index++) dialog.Resources[fontKeys[index]] = fontSizes[index];
            dialog.SelectedFrameRate = previousRate;
            Drain(dialog);
        }
    }

    private static void VerifyGifPreviewLongRange(RecordingResult recording)
    {
        using var scope = UiText.UseLanguage("ko-KR");
        ThemeService.Apply(AppTheme.Glass);
        FluidMotion.AnimationsEnabledOverrideForTest = true;
        var document = VideoEditDocument.CreateFor(recording.Width, recording.Height, recording.DurationMs);
        var dialog = new VideoExportDialog(recording, document, NullLoggerFactory.Instance, gif: true)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000, Top = -10000, Height = 1000,
            ShowActivated = false, ShowInTaskbar = false,
        };
        FluidMotion.SetWindowEntrance(dialog, false);
        try
        {
            dialog.Show();
            PumpUntil(dialog, () => dialog.SamplePanel.HasSample && dialog.SampleCompletion.IsCompleted, "the sample and its estimate segments");
            GifPreviewPanel panel = dialog.SamplePanel;
            // 12 s is longer than one sample: a centred 3 s window, an estimate range, no exact result yet.
            Assert.False(dialog.HasMeasuredResult);
            Assert.True(dialog.CanExport);
            Assert.StartsWith("원본 4.5~7.5초", panel.InfoText, StringComparison.Ordinal);
            Assert.Contains("예상 크기", dialog.EstimateText, StringComparison.Ordinal);
            Assert.Contains("~", dialog.EstimateText, StringComparison.Ordinal);
            Slider position = Descendants(dialog).OfType<Slider>().Single(slider => AutomationProperties.GetName(slider) == UiText.Get("GifPreview_SamplePosition"));
            Assert.True(position.IsVisible);
            SaveGifPreviewEvidence(dialog, "gif-preview-long-Glass-ko-KR.png");

            // A 5 MB limit cannot hold 12 s at 960/10: the advice names another preset or MP4.
            dialog.ShareTargetIndex = 0;
            Drain(dialog);
            GifRecommendation recommendation = Assert.IsType<GifRecommendation>(dialog.Recommendation);
            Assert.NotEqual(GifRecommendationKind.CurrentFits, recommendation.Kind);
            Button apply = Descendants(dialog).OfType<Button>().Single(button =>
                AutomationProperties.GetName(button) == UiText.Get(recommendation.Kind == GifRecommendationKind.UseMp4 ? "GifPreview_SwitchToMp4" : "GifPreview_ApplyAdvice"));
            Assert.True(apply.IsVisible);
            SaveGifPreviewEvidence(dialog, "gif-preview-long-Glass-ko-KR-5mb.png");
            apply.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            if (recommendation.Kind == GifRecommendationKind.UseMp4)
            {
                Assert.False(dialog.IsGif);
            }
            else
            {
                Assert.Equal(GifSharePolicy.Presets.ToList().IndexOf(recommendation.Quality), dialog.QualityIndex);
                PumpUntil(dialog, () => dialog.SamplePanel.HasSample && dialog.SampleCompletion.IsCompleted, "the recommended preset sample");
                Assert.True(dialog.Recommendation is { Kind: GifRecommendationKind.CurrentFits } or { Kind: GifRecommendationKind.SwitchPreset },
                    dialog.AdviceText);
            }

            // Moving the sample re-renders the preview without touching the settings.
            if (dialog.IsGif)
            {
                position.Value = 0;
                PumpUntil(dialog, () => dialog.SamplePanel.HasSample && dialog.SampleCompletion.IsCompleted, "the moved sample");
                Assert.StartsWith("원본 0.0~3.0초", panel.InfoText, StringComparison.Ordinal);
            }
        }
        finally
        {
            dialog.Close();
            Drain(dialog);
        }
    }

    private static void PumpUntil(Window window, Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for {what}.");
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.Background, window.Dispatcher) { Interval = TimeSpan.FromMilliseconds(25) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start();
            Dispatcher.PushFrame(frame);
        }

        Drain(window);
    }

    private static void SaveGifPreviewEvidence(Window window, string name)
    {
        string? requested = Environment.GetEnvironmentVariable("MYCAPTURE_GIF_PREVIEW_EVIDENCE");
        if (string.IsNullOrWhiteSpace(requested)) return;
        string directory = DiagnosticOutputPaths.Create(requested);
        Drain(window);
        double scale = VisualTreeHelper.GetDpi(window).DpiScaleX;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * scale), (int)Math.Ceiling(window.ActualHeight * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        var backdrop = new DrawingVisual();
        using (DrawingContext context = backdrop.RenderOpen())
        {
            // Glass themes leave the window transparent for Mica; give the evidence an opaque base.
            context.DrawRectangle((Brush)window.FindResource("Surface.Base"), null, new Rect(0, 0, window.ActualWidth, window.ActualHeight));
        }

        bitmap.Render(backdrop);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(DiagnosticOutputPaths.Child(directory, name));
        encoder.Save(stream);
    }
}
