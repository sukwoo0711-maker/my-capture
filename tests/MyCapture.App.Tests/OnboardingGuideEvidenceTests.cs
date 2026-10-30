using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using MyCapture.Core.Themes;
using MyCapture.Platform.Display;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// Draws every step of the first-use guide with the real theme resources, in both languages, three
/// themes, 96 and 144 DPI, and text sizes 1.0 and 1.5, and checks that nothing is clipped.
/// </summary>
/// <remarks>
/// Set <c>MYCAPTURE_ONBOARD_GUIDE_EVIDENCE</c> to a directory to keep the PNGs and the layout numbers.
/// Without it the scenes are still built and checked.
/// </remarks>
public sealed class OnboardingGuideEvidenceTests
{
    private const string EvidenceVariable = "MYCAPTURE_ONBOARD_GUIDE_EVIDENCE";

    private static readonly (AppTheme Theme, string Name)[] Themes =
    [
        (AppTheme.Midnight, "midnight"), (AppTheme.Glass, "dark"), (AppTheme.GlassLight, "light"), (AppTheme.HighContrast, "contrast"),
    ];

    [Fact]
    public Task EveryStepRendersInBothLanguagesThemesDpisAndTextSizesWithoutClipping() =>
        SettingsThemePreviewTests.RunIsolated(nameof(EveryStepRendersInBothLanguagesThemesDpisAndTextSizesWithoutClipping),
            RenderEverything, typeof(OnboardingGuideEvidenceTests));

    private static void RenderEverything()
    {
        string? requested = Environment.GetEnvironmentVariable(EvidenceVariable);
        string? root = string.IsNullOrWhiteSpace(requested) ? null : Directory.CreateDirectory(requested).FullName;
        var log = new StringBuilder();
        Failures.Clear();
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{name}.xaml"),
            });
        bool? motion = FluidMotion.AnimationsEnabledOverrideForTest;
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        try
        {
            foreach ((string tag, string culture) in new[] { ("ko", "ko-KR"), ("en", "en-US") })
            {
                using var language = UiText.UseLanguage(culture);
                UiText.Configure(tag);
                foreach ((AppTheme theme, string themeName) in Themes)
                {
                    ThemeService.Apply(theme);
                    // 100% display, ordinary text: the set the design is reviewed from.
                    Scene(root, log, "96dpi", tag, themeName, dpi: 96, textScale: 1, workArea: new RectD(0, 0, 1920, 1040));
                    if (themeName == "contrast") continue;
                    // 150% display on a 1080p laptop (a 1280 x 693 DIP work area), ordinary text and 1.5 times larger text.
                    Scene(root, log, "144dpi", tag, themeName, dpi: 144, textScale: 1, workArea: new RectD(0, 0, 1920, 1040));
                    Scene(root, log, "text150", tag, themeName, dpi: 144, textScale: 1.5, workArea: new RectD(0, 0, 1920, 1040));
                    Scene(root, log, "text150-96dpi", tag, themeName, dpi: 96, textScale: 1.5, workArea: new RectD(0, 0, 1920, 1040));
                }
            }
        }
        finally
        {
            FluidMotion.AnimationsEnabledOverrideForTest = motion;
            application.Shutdown();
        }

        if (root is not null) File.WriteAllText(Path.Combine(root, "layout.txt"), log.ToString(), new UTF8Encoding(false));
        Assert.True(Failures.Count == 0, string.Join(Environment.NewLine, Failures));
    }

    private static readonly List<string> Failures = [];

    private static void Check(bool condition, string message)
    {
        if (!condition) Failures.Add(message);
    }

    private static void Scene(string? root, StringBuilder log, string set, string language, string themeName, uint dpi, double textScale, RectD workArea)
    {
        double scale = dpi / 96.0;
        var monitor = new MonitorInfo("\\\\.\\DISPLAY1", new RectD(0, 0, workArea.Width, workArea.Height + 40), workArea, dpi, IsPrimary: true);
        // Cover every current lesson, including the steps that wait for an action in another window.
        (PracticeStep? Step, bool NoLibraryKey)[] scenes =
        [
            (null, false),
            .. PracticeSession.Steps.Select(step => ((PracticeStep?)step, false)),
            (PracticeStep.Results, false), (PracticeStep.C1, true),
        ];
        foreach ((PracticeStep? step, bool noLibraryKey) in scenes)
        {
            var clipboard = new OnboardingGuideTests.FakeClipboard();
            HotkeySettings? keys = noLibraryKey ? NewInstallDefaults.Create().Hotkeys : null;
            if (noLibraryKey) Assert.Equal(Hotkey.None, keys!.OpenLibrary);
            WelcomeWindow guide = OnboardingGuideTests.Create(clipboard,
                new WelcomeOptions { ReducedMotion = true, TextScale = textScale, MonitorProvider = () => monitor,
                    ShowCoreShortcutsConsent = step is null }, keys);
            guide.WindowStartupLocation = WindowStartupLocation.Manual;
            guide.Left = -10000; guide.Top = -10000; guide.ShowActivated = false; guide.ShowInTaskbar = false;
            guide.LimitToWorkArea(monitor);
            try
            {
                if (step is { } lesson) PracticeJourneyDriver.AdvanceTo(guide.Session, lesson);
                guide.Show();
                Drain(guide);
                string suffix = noLibraryKey ? "-no-key" : string.Empty;
                string name = step?.ToString() ?? "consent";
                string label = $"{set} {language} {themeName} {name}{suffix}";
                if (noLibraryKey)
                {
                    Check(!guide.KeyShown, label + ": an unassigned library shortcut must not be shown");
                    Check(guide.TryNowButton.IsVisible, label + ": the library button must remain visible without a key");
                    Check(guide.InstructionText == UiText.Get("Journey.C1.Body"), label + ": use the library-button instruction");
                }
                AssertFits(guide, label, log);

                if (root is null) continue;
                string directory = Directory.CreateDirectory(Path.Combine(root, set)).FullName;
                SaveBitmap(Render(guide, scale), Path.Combine(directory, $"{name.ToLowerInvariant()}{suffix}-{language}-{themeName}.png"));
            }
            finally { guide.Close(); }
        }
    }

    /// <summary>Everything the person must see or press lies inside the window, in both directions.</summary>
    private static void AssertFits(WelcomeWindow guide, string label, StringBuilder log)
    {
        var content = (FrameworkElement)guide.Content;
        ScrollViewer scroll = guide.Scroller;
        double overflow = scroll.ExtentHeight - scroll.ViewportHeight;
        log.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"{label}: window {guide.ActualWidth:0}x{guide.ActualHeight:0}, content {content.ActualWidth:0}x{content.ActualHeight:0}, extent {scroll.ExtentHeight:0}, viewport {scroll.ViewportHeight:0}, hidden below {Math.Max(0, overflow):0}"));

        // Nothing is wider than the column it sits in: no clipped words or buttons on the right.
        foreach (FrameworkElement element in VisualDescendants((DependencyObject)scroll.Content).OfType<FrameworkElement>().Prepend((FrameworkElement)scroll.Content))
        {
            if (!element.IsVisible || element.ActualWidth <= 0) continue;
            // The scroll bar belongs to the viewport's edge, not to the content column.
            if (element.GetType().Namespace == "System.Windows.Controls.Primitives" && element is not System.Windows.Controls.Primitives.ToggleButton) continue;
            if (!scroll.IsAncestorOf(element)) continue;
            Rect bounds = element.TransformToAncestor(scroll).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
            Check(bounds.Left >= -1 && bounds.Right <= scroll.ViewportWidth + 1,
                $"{label}: {element.GetType().Name} spans {bounds.Left:0}..{bounds.Right:0} in a {scroll.ViewportWidth:0} wide column");
        }

        // When the lesson requires drawing/resizing in another window, the guide has no
        // duplicate primary action. Its exit remains available and every shown action must fit.
        bool finish = guide.CurrentStep == WelcomeStep.Finish;
        bool waitingElsewhere = guide.CurrentStep != WelcomeStep.Consent
            && (guide.Session.Step is >= PracticeStep.A4 and <= PracticeStep.A6
                || guide.Session.Step is >= PracticeStep.B2 and <= PracticeStep.B5);
        Check(guide.TryNowButton.IsVisible == !waitingElsewhere, label + ": primary action visibility");
        Check(guide.TryNowButton.IsTabStop == !waitingElsewhere, label + ": primary tab navigation");
        Check(guide.SkipButton.Visibility == (finish ? Visibility.Collapsed : Visibility.Visible), label + ": Skip visibility");
        Check(guide.SkipButton.IsTabStop == !finish, label + ": Skip tab navigation");
        if (finish) Check(!guide.SkipButton.IsVisible, label + ": Finish must not show Skip");
        else Check(guide.SkipButton.IsVisible, label + ": practice must show Skip");
        // The footer stays outside the scrolling content; every available action must fit.
        Button[] buttons = finish ? [guide.TryNowButton] : waitingElsewhere ? [guide.SkipButton] : [guide.TryNowButton, guide.SkipButton];
        foreach (Button button in buttons)
        {
            Check(button.IsVisible, label);
            Rect bounds = button.TransformToAncestor(content).TransformBounds(new Rect(0, 0, button.ActualWidth, button.ActualHeight));
            Check(bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= content.ActualWidth + 1 && bounds.Bottom <= content.ActualHeight + 1,
                $"{label}: {System.Windows.Automation.AutomationProperties.GetName(button)} at {bounds}");
            // The label fits its button: the shared command face keeps the name in the automation name.
            Check(button.ActualWidth >= 80 && !string.IsNullOrEmpty(System.Windows.Automation.AutomationProperties.GetName(button)), label);
        }

        // The 15-step recap and the picture/clip lessons can scroll in the fixed window.
        // Check actual reachability of every label and control, not merely the scroll extent.
        double initialOffset = scroll.VerticalOffset;
        FrameworkElement[] readable = VisualDescendants((DependencyObject)scroll.Content).OfType<FrameworkElement>()
            .Where(element => element.IsVisible && element.ActualHeight > 0
                && element is TextBlock or Button or CheckBox).ToArray();
        foreach (FrameworkElement element in readable)
        {
            Rect before = element.TransformToAncestor(scroll).TransformBounds(new Rect(element.RenderSize));
            scroll.ScrollToVerticalOffset(scroll.VerticalOffset + before.Top);
            Drain(guide);
            Rect bounds = element.TransformToAncestor(scroll).TransformBounds(new Rect(element.RenderSize));
            Check(bounds.Top >= -1 && bounds.Bottom <= scroll.ViewportHeight + 1,
                $"{label}: {element.GetType().Name} cannot be fully read after scrolling: {bounds}, viewport {scroll.ViewportHeight:0}");
        }
        scroll.ScrollToVerticalOffset(initialOffset);
        Drain(guide);
    }

    private static IEnumerable<DependencyObject> VisualDescendants(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < count; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (DependencyObject descendant in VisualDescendants(child)) yield return descendant;
        }
    }

    private static void Drain(Window window) =>
        window.Dispatcher.Invoke(() => window.UpdateLayout(), DispatcherPriority.ApplicationIdle);

    /// <summary>The window's content at <paramref name="scale"/> times 96 DPI, over the opaque base colour the desktop would show.</summary>
    private static RenderTargetBitmap Render(Window window, double scale)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * scale), (int)Math.Ceiling(window.ActualHeight * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(window);
        bitmap.Freeze();
        return bitmap;
    }

    private static void SaveBitmap(BitmapSource image, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using FileStream stream = File.Create(path);
        encoder.Save(stream);
    }
}
