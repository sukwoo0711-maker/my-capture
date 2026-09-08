using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Recording;
using MyCapture.App.Settings;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Platform.Recording;

namespace MyCapture.App.Diagnostics;

/// <summary>Isolated production-theme language/layout probe. No live settings, registration or capture writes.</summary>
internal static class LocalizationSelfTest
{
    internal const string CommandLineSwitch = "--selftest-localization";

    internal static int Run(string outputDirectory)
    {
        outputDirectory = DiagnosticOutputPaths.Create(outputDirectory);
        var report = new StringBuilder();
        try
        {
            string systemLanguage = UiText.Culture.Name;
            UiText.Configure("en-US");
            Check(UiText.Get("Settings.Language") == "Display language", "English startup resources");
            UiText.Configure(null);
            Check(UiText.Culture.Name == systemLanguage, "System selection restores original user display language");
            UiText.Configure("en-US");
            var settings = new AppSettings { General = { Language = "en-US" } };
            var window = new SettingsWindow(() => settings, next =>
            {
                settings = next;
                return new SettingsApplyResult(true, true, true, true, [UiText.Get("Settings.LanguageRestart")]);
            }, NullLogger.Instance);
            try
            {
                Show(window, 940, 700);
                var selector = (ComboBox)window.FindName("LanguageSelector");
                Check(Equals(selector.SelectedValue, "en-US"), "Persisted English selected");
                Check(AutomationProperties.GetName(selector) == "Display language", "English language selector accessibility");
                selector.SelectedValue = "ko-KR";
                Check(((SettingsDraft)window.DataContext).Language == "ko-KR", "Language selection edits existing draft field");
                Check(settings.General.Language == "en-US", "Unapplied language keeps current settings");
                selector.SelectedValue = "en-US";
                Render(window, "settings-english-940.png");
                window.Width = 780;
                window.Height = 560;
                window.UpdateLayout();
                Render(window, "settings-english-780.png");
                foreach (TabItem tab in Descendants(window).OfType<TabItem>().ToArray())
                {
                    tab.IsSelected = true;
                    window.UpdateLayout();
                    foreach (TextBlock text in Descendants(tab).OfType<TextBlock>().Where(t => t.IsVisible))
                        Check(!System.Text.RegularExpressions.Regex.IsMatch(text.Text, "[가-힣]"), "English settings label: " + text.Text);
                }
            }
            finally { window.CloseForExit(); }

            var timing = new TimedTextOverlayDialog(3000, 0);
            try
            {
                Show(timing, 520, 420);
                foreach (Control field in Descendants(timing).OfType<Control>().Where(c => c is TextBox or ComboBox))
                {
                    Point bottom = field.TranslatePoint(new Point(field.ActualWidth, field.ActualHeight), timing);
                    Check(bottom.X <= timing.ActualWidth && bottom.Y <= timing.ActualHeight, "Timed text field fits: " + AutomationProperties.GetName(field));
                }
                Render(timing, "timed-text-english.png");
            }
            finally { timing.Close(); }

            foreach (int width in new[] { 760, 920 })
            {
                var recording = new RecordingResult(DiagnosticOutputPaths.Child(outputDirectory, "layout-probe.mp4"), 2000, 30, 60, 320, 240);
                var video = new VideoEditorWindow(recording, AppPaths.CreateForRoot(DiagnosticOutputPaths.Child(outputDirectory, "isolated")), NullLoggerFactory.Instance);
                try
                {
                    Show(video, width, width == 760 ? 555 : 680);
                    Grid layout = (Grid)video.Content;
                    Border status = layout.Children.OfType<Border>().Single(b => Grid.GetRow(b) == 3);
                    var client = (FrameworkElement)VisualTreeHelper.GetParent(layout);
                    var statusText = (TextBlock)status.Child;
                    Check(statusText.ActualHeight >= statusText.DesiredSize.Height - .5, $"Video {width}: complete status glyph height");
                    Check(statusText.TranslatePoint(new Point(0, statusText.ActualHeight), client).Y <= client.ActualHeight + .5,
                        $"Video {width}: status inside native client");
                    var connector = Descendants(layout).OfType<TimelineRenderSurface>().Single(surface => !surface.IsHitTestVisible);
                    var hint = new FormattedText("The selected interval above fills the timeline below.", CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight, new Typeface(video.FontFamily, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
                        10.5, Brushes.White, VisualTreeHelper.GetDpi(video).PixelsPerDip);
                    Check(connector.ActualHeight >= hint.Height, $"Video {width}: complete connector hint glyph height");
                    foreach (Button button in Descendants(layout).OfType<Button>().Where(b => b.IsVisible))
                    {
                        Point bottom = button.TranslatePoint(new Point(button.ActualWidth, button.ActualHeight), layout);
                        Check(bottom.X <= layout.ActualWidth + .5 && bottom.Y <= layout.ActualHeight + .5, $"Video {width}: {AutomationProperties.GetName(button)} fits");
                    }
                    Render(video, $"video-english-themed-{width}.png");
                }
                finally { video.Close(); }
            }
            report.AppendLine("RESULT: PASS");
            return 0;
        }
        catch (Exception ex)
        {
            report.AppendLine("RESULT: FAIL");
            report.AppendLine(ex.ToString());
            return 2;
        }
        finally { File.WriteAllText(DiagnosticOutputPaths.Child(outputDirectory, "localization-selftest-report.txt"), report.ToString()); }

        void Check(bool condition, string description)
        {
            report.AppendLine($"{(condition ? "PASS" : "FAIL")}: {description}");
            if (!condition) throw new InvalidOperationException(description);
        }
        void Render(Window window, string name)
        {
            FrameworkElement content = window;
            var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(content);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(DiagnosticOutputPaths.Child(outputDirectory, name));
            encoder.Save(stream);
        }
    }

    private static void Show(Window window, int width, int height)
    {
        window.Width = width;
        window.Height = height;
        window.Left = -10000;
        window.Top = -10000;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.ShowInTaskbar = false;
        window.ShowActivated = false;
        window.Show();
        window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
        window.UpdateLayout();
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (DependencyObject descendant in Descendants(child)) yield return descendant;
        }
    }
}
