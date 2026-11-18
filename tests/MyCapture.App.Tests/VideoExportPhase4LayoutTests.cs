using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Recording;
using MyCapture.App.Themes;
using MyCapture.App.Threading;
using MyCapture.Core.Localization;
using MyCapture.Core.Recording;
using MyCapture.Core.Themes;
using MyCapture.Platform.Recording;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class VideoExportPhase4LayoutTests
{
    public static IEnumerable<object[]> ReadingConditions()
    {
        foreach (string language in new[] { "ko-KR", "en-US" })
        foreach (AppTheme theme in new[] { AppTheme.Midnight, AppTheme.Daylight })
        foreach (double scale in new[] { 1d, 1.5d })
        foreach (double width in new[] { 460d, 640d })
        foreach (bool gif in new[] { false, true })
            yield return [language, theme, scale, width, gif];
    }

    [Theory]
    [MemberData(nameof(ReadingConditions))]
    public async Task FormatPreviewAndOutputPrecedeOptionsAtBothWidths(
        string language, AppTheme theme, double scale, double width, bool gif) => await Run(() =>
    {
        using var languageScope = UiText.UseLanguage(language);
        WithDialog(gif, theme, scale, width, dialog =>
        {
            StackPanel settings = Field<StackPanel>(dialog, "_settings");
            FrameworkElement preview = gif ? dialog.SamplePanel : Field<Border>(dialog, "_previewCard");
            TextBlock estimate = Field<TextBlock>(dialog, "_estimate");
            StackPanel options = Field<StackPanel>(dialog, gif ? "_gif" : "_mp4");
            ToggleButton format = Format(dialog, gif ? "GIF" : "MP4");

            Assert.Equal(scale, VisualTreeHelper.GetDpi(dialog).DpiScaleX);
            Assert.Equal(SizeToContent.Height, dialog.SizeToContent);
            AssertBefore(format, preview, settings);
            AssertBefore(preview, estimate, settings);
            AssertBefore(estimate, options, settings);
            Assert.True(options.IsVisible);
            Assert.False(string.IsNullOrWhiteSpace(dialog.EstimateText));

            DependencyObject[] readingOrder = Logical(settings).ToArray();
            Assert.True(Array.IndexOf(readingOrder, format) < Array.IndexOf(readingOrder, preview));
            Assert.True(Array.IndexOf(readingOrder, preview) < Array.IndexOf(readingOrder, estimate));
            Assert.True(Array.IndexOf(readingOrder, estimate) < Array.IndexOf(readingOrder, options));
            if (gif)
            {
                FrameworkElement samplePosition = Field<StackPanel>(dialog, "_samplePositionRow");
                AssertBefore(preview, samplePosition, settings);
                AssertBefore(samplePosition, estimate, settings);
                ComboBox[] choices = [Field<ComboBox>(dialog, "_quality"), Field<ComboBox>(dialog, "_frameRate"),
                    Field<ComboBox>(dialog, "_speed"), Field<ComboBox>(dialog, "_shareTarget")];
                foreach (ComboBox choice in choices) AssertInsideWidth(choice, settings);
                AssertBefore(choices[0], choices[2], settings);
                AssertBefore(choices[1], choices[3], settings);
                Assert.True(Right(choices[0], settings) <= choices[1].TranslatePoint(new Point(), settings).X + 0.5);
                Assert.True(Right(choices[2], settings) <= choices[3].TranslatePoint(new Point(), settings).X + 0.5);
            }
            else
            {
                Assert.False(Field<StackPanel>(dialog, "_gifPreview").IsVisible);
                Assert.False(Field<StackPanel>(dialog, "_gif").IsVisible);
            }

            // The action remains outside the scrolling form and is reachable at a capped height.
            Button export = Field<Button>(dialog, "_export");
            Assert.DoesNotContain(export, readingOrder);
            Assert.True(export.IsVisible && export.ActualWidth > 0 && export.ActualHeight > 0);
            AssertInsideWidth(export, (FrameworkElement)dialog.Content);
        });
    });

    [Theory]
    [InlineData("ko-KR")]
    [InlineData("en-US")]
    public async Task SwitchingFormatSwapsPreviewAndDetailsWithoutLosingSelections(string language) => await Run(() =>
    {
        using var languageScope = UiText.UseLanguage(language);
        WithDialog(gif: false, AppTheme.Midnight, 1, 460, dialog =>
        {
            dialog.TargetReductionPercent = 55;
            Border sourcePreview = Field<Border>(dialog, "_previewCard");
            StackPanel gifPreview = Field<StackPanel>(dialog, "_gifPreview");
            StackPanel mp4Options = Field<StackPanel>(dialog, "_mp4");
            StackPanel gifOptions = Field<StackPanel>(dialog, "_gif");

            Format(dialog, "GIF").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            dialog.SelectedFrameRate = 50;
            dialog.ShareTargetIndex = 0;
            Settle(dialog);
            Assert.False(sourcePreview.IsVisible);
            Assert.False(mp4Options.IsVisible);
            Assert.True(gifPreview.IsVisible && gifOptions.IsVisible);
            Assert.True(Field<StackPanel>(dialog, "_samplePositionRow").IsVisible);

            Format(dialog, "MP4").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Settle(dialog);
            Assert.True(sourcePreview.IsVisible && mp4Options.IsVisible);
            Assert.False(gifPreview.IsVisible);
            Assert.False(gifOptions.IsVisible);
            Assert.Equal(55, dialog.TargetReductionPercent);
            Assert.Equal(-1, dialog.SizePresetIndex);
            Assert.Equal(Visibility.Collapsed, Field<Grid>(dialog, "_adviceRow").Visibility);

            Format(dialog, "GIF").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Settle(dialog);
            Assert.Equal(50, dialog.SelectedFrameRate);
            Assert.Equal(0, dialog.ShareTargetIndex);
            Assert.True(gifPreview.IsVisible && gifOptions.IsVisible);
            Assert.Same(dialog.SamplePanel, gifPreview.Children[0]);
        });
    });

    [Theory]
    [InlineData("ko-KR")]
    [InlineData("en-US")]
    public async Task TabVisitsPreviewPositionBeforeGifOptionsAndThenExport(string language) => await Run(() =>
    {
        using var languageScope = UiText.UseLanguage(language);
        WithDialog(gif: true, AppTheme.Daylight, 1, 460, dialog =>
        {
            // The sample is still pending. Its playback controls are correctly disabled, but
            // the sample-position slider remains available before the four output choices.
            UIElement[] expected = [Field<Slider>(dialog, "_samplePosition"), Field<ComboBox>(dialog, "_quality"),
                Field<ComboBox>(dialog, "_frameRate"), Field<ComboBox>(dialog, "_speed"),
                Field<ComboBox>(dialog, "_shareTarget"), Field<Button>(dialog, "_export"), Field<Button>(dialog, "_close")];
            ToggleButton lastFormat = Format(dialog, "GIF");
            KeyboardFocusCue.NotifyKeyboardInput(dialog);
            Assert.Same(lastFormat, Keyboard.Focus(lastFormat));
            UIElement previous = lastFormat;
            foreach (UIElement next in expected)
            {
                Assert.True(previous.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)));
                Settle(dialog);
                Assert.Same(next, Keyboard.FocusedElement);
                Assert.True(next.IsKeyboardFocused);
                previous = next;
            }
        });
    });

    private static void WithDialog(bool gif, AppTheme theme, double scale, double width, Action<VideoExportDialog> action)
    {
        bool? previousMotion = FluidMotion.AnimationsEnabledOverrideForTest;
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        var recording = new RecordingResult(Path.Combine(AppContext.BaseDirectory, "phase4-export-never-read.mp4"), 40_000, 30, 1200, 1280, 720);
        var dialog = new VideoExportDialog(recording, VideoEditDocument.CreateFor(1280, 720, 40_000), NullLoggerFactory.Instance, gif)
        {
            Width = width, WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false,
        };
        dialog.SourcePreviewRendererForTest = async token =>
        {
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("The controlled source preview never completes.");
        };
        dialog.SampleRendererForTest = async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("The controlled sample never completes.");
        };
        bool closed = false;
        dialog.Closed += (_, _) => closed = true;
        try
        {
            ResourceDictionary resources = SystemHighContrastThemeTests.LoadTheme();
            ThemeService.ApplyResources(theme, resources, systemHighContrast: false);
            dialog.Resources.MergedDictionaries.Add(resources);
            StandardWindowTheme.Apply(dialog);
            FluidMotion.SetWindowEntrance(dialog, false);
            dialog.Show();
            VisualTreeHelper.SetRootDpi(dialog, new DpiScale(scale, scale));
            Settle(dialog);
            action(dialog);
        }
        finally
        {
            dialog.Close();
            var clock = Stopwatch.StartNew();
            while (!closed && clock.Elapsed < TimeSpan.FromSeconds(15))
            {
                var frame = new DispatcherFrame();
                _ = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
                Dispatcher.PushFrame(frame);
            }
            FluidMotion.AnimationsEnabledOverrideForTest = previousMotion;
            Assert.True(closed, "The controlled previews did not drain on close.");
        }
    }

    private static void AssertBefore(FrameworkElement first, FrameworkElement second, FrameworkElement root)
    {
        double bottom = first.TranslatePoint(new Point(0, first.ActualHeight), root).Y;
        double top = second.TranslatePoint(new Point(), root).Y;
        Assert.True(first.IsVisible && second.IsVisible && first.ActualHeight > 0 && second.ActualHeight > 0);
        Assert.True(bottom <= top + 0.5, $"{first.GetType().Name} ends at {bottom:0.#}, after {second.GetType().Name} starts at {top:0.#}.");
    }

    private static void AssertInsideWidth(FrameworkElement element, FrameworkElement root)
    {
        Assert.True(element.ActualWidth > 0);
        Assert.True(element.TranslatePoint(new Point(), root).X >= -0.5);
        Assert.True(Right(element, root) <= root.ActualWidth + 0.5);
    }

    private static double Right(FrameworkElement element, FrameworkElement root) =>
        element.TranslatePoint(new Point(element.ActualWidth, 0), root).X;

    private static T Field<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    private static ToggleButton Format(DependencyObject root, string format) =>
        Logical(root).OfType<ToggleButton>().Single(toggle => AutomationProperties.GetName(toggle) == format);

    private static IEnumerable<DependencyObject> Logical(DependencyObject root)
    {
        foreach (object child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is not DependencyObject element) continue;
            yield return element;
            foreach (DependencyObject descendant in Logical(element)) yield return descendant;
        }
    }

    private static void Settle(Window window)
    {
        for (int pass = 0; pass < 2; pass++)
            window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
    }

    private static Task Run(Action action) => StaThreadTask.RunAsync(() =>
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        action();
        return true;
    });
}
