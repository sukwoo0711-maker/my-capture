using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.App.Diagnostics;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Platform;
using MyCapture.Core.Themes;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class SupportErrorReportTests
{
    [Theory]
    [InlineData(1040, 540, 1000)]
    [InlineData(600, 540, 560)]
    [InlineData(500, 460, 460)]
    public void HeightLimitsReserveSpaceAndShrinkTheMinimum(double workArea, double minimum, double maximum)
    {
        Assert.Equal((minimum, maximum), SupportWindow.HeightLimits(workArea));
    }

    [Fact]
    public Task PreviewHasNoActionsAndKeepsEditedSummaryThroughCopyAndDpiRefresh() =>
        Isolated(nameof(PreviewHasNoActionsAndKeepsEditedSummaryThroughCopyAndDpiRefresh), () =>
        {
            Application app = CreateApplication();
            try
            {
                foreach (string languageName in new[] { "ko-KR", "en" })
                {
                    using var language = UiText.UseLanguage(languageName);
                    var actions = new FakeActions();
                    ErrorReport report = Report();
                    var window = new SupportWindow(actions, _ => Snapshot(), report);
                    try
                    {
                        Show(window);
                        Assert.Empty(actions.Opened);
                        Assert.Null(actions.Copied);
                        Assert.Equal(0, actions.ChooseCalls);
                        Assert.Equal(0, actions.SaveCalls);
                        Assert.Null(actions.ShownPath);
                        Assert.Equal(AppIdentity.FormatWindowTitle(UiText.Get("Support_ReportError")), window.Title);
                        var content = (StackPanel)((ScrollViewer)window.Content).Content;
                        Assert.Equal(UiText.Get("Support_ReportError"), content.Children.OfType<TextBlock>().First().Text);
                        Assert.True(window.DiagnosticsBox.IsReadOnly);
                        TextBox summary = Assert.IsType<TextBox>(window.ErrorSummaryBox);
                        Assert.Equal(report.FormatSummary(), summary.Text);
                        Assert.False(summary.IsReadOnly);
                        Assert.True(summary.AcceptsReturn);
                        Assert.Equal(TextWrapping.Wrap, summary.TextWrapping);
                        Assert.Equal(20000, summary.MaxLength);
                        Assert.Equal(Visibility.Collapsed, window.StoreReviewButton.Visibility);
                        Assert.Equal(Visibility.Visible, window.BundleButton.Visibility);
                        window.MessageBox.Text = "  Reproduction steps  ";
                        summary.Text = "  Reviewed and edited summary  ";
                        window.CopyDiagnostics();
                        Assert.Equal(window.Submission().Body, actions.Copied);
                        Assert.Contains("Reproduction steps", actions.Copied);
                        Assert.Contains("Reviewed and edited summary", actions.Copied);
                        string edited = summary.Text;
                        typeof(SupportWindow).GetMethod("OnDpiChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
                            .Invoke(window, [new DpiScale(1, 1), new DpiScale(2, 2)]);
                        Assert.Equal(edited, summary.Text);
                        Assert.Contains(edited.Trim(), window.Submission().Body);
                        window.OpenStoreReview();
                        Assert.Empty(actions.Opened);
                    }
                    finally { window.Close(); }
                }
            }
            finally { app.Shutdown(); }
        });

    [Fact]
    public Task BundleSavingRunsOffDispatcherAndPreservesPreviewOnFailure() =>
        Isolated(nameof(BundleSavingRunsOffDispatcherAndPreservesPreviewOnFailure), () =>
        {
            Application app = CreateApplication();
            SynchronizationContext? previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            try
            {
                using var entered = new ManualResetEventSlim();
                using var release = new ManualResetEventSlim();
                int uiThread = Environment.CurrentManagedThreadId;
                var actions = new FakeActions
                {
                    ChosenPath = "fixture.zip",
                    Save = () => { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(10))); },
                };
                var window = new SupportWindow(actions, _ => Snapshot(), Report());
                try
                {
                    window.ErrorSummaryBox!.Text = "Reviewed summary";
                    Task save = window.SaveBundleAsync();
                    Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
                    Assert.False(window.BundleButton.IsEnabled);
                    Assert.NotEqual(uiThread, actions.SaveThread);
                    Assert.Null(actions.ShownPath);
                    Assert.Equal("Reviewed summary", actions.Submission!.ErrorSummary);
                    Assert.Equal("fixture.zip", actions.SavedPath);
                    Assert.Equal(1, actions.ChooseCalls);
                    Assert.Equal(1, actions.SaveCalls);
                    Assert.True(window.SaveBundleAsync().IsCompletedSuccessfully);
                    Assert.Equal(1, actions.ChooseCalls);
                    release.Set();
                    Complete(save);
                    Assert.True(window.BundleButton.IsEnabled);
                    Assert.Equal("fixture.zip", actions.ShownPath);
                    Assert.Equal(uiThread, actions.ShowThread);
                    Assert.Equal(UiText.Get("Support_BundleSaved"), window.StatusText);

                    actions.ShowSucceeds = false;
                    Complete(window.SaveBundleAsync());
                    Assert.Equal(UiText.Get("Support_BundleSavedFolderFailed"), window.StatusText);
                    actions.Save = () => throw new IOException("Synthetic save failure");
                    actions.ShownPath = null;
                    Complete(window.SaveBundleAsync());
                    Assert.Equal(UiText.Get("Support_BundleFailed"), window.StatusText);
                    Assert.Null(actions.ShownPath);
                    Assert.Equal("Reviewed summary", window.ErrorSummaryBox.Text);
                    Assert.True(window.BundleButton.IsEnabled);
                    actions.ChosenPath = null;
                    int saves = actions.SaveCalls;
                    Complete(window.SaveBundleAsync());
                    Assert.Equal(saves, actions.SaveCalls);
                    Assert.True(window.BundleButton.IsEnabled);
                }
                finally { release.Set(); window.Close(); }
            }
            finally { SynchronizationContext.SetSynchronizationContext(previous); app.Shutdown(); }
        });

    [Fact]
    public Task GitHubUsesEditedSummaryAndReportsRuneSafeTrimming() =>
        Isolated(nameof(GitHubUsesEditedSummaryAndReportsRuneSafeTrimming), () =>
        {
            Application app = CreateApplication();
            try
            {
                using var language = UiText.UseLanguage("ko-KR");
                var actions = new FakeActions();
                var window = new SupportWindow(actions, _ => Snapshot(), Report());
                try
                {
                    window.ErrorSummaryBox!.Text = "Edited error summary";
                    window.OpenGitHub();
                    string first = Assert.Single(actions.Opened);
                    Assert.Contains(Uri.EscapeDataString("Edited error summary"), first);
                    Assert.Contains(Uri.EscapeDataString(UiText.Format("Support_ErrorIssueTitle", "System.InvalidOperationException")), first);
                    Assert.Equal(UiText.Get("Support_GitHubOpened"), window.StatusText);
                    window.MessageBox.Text = string.Concat(Enumerable.Repeat("가😀 &+#?", 4000));
                    window.OpenGitHub();
                    string url = actions.Opened[^1];
                    Assert.True(url.Length <= SupportReport.MaximumIssueUrlLength);
                    Assert.Equal(UiText.Get("Support_GitHubOpenedTrimmed"), window.StatusText);
                    Assert.Equal(url, SupportReport.IssueUrl(window.Submission(), out bool trimmed));
                    Assert.True(trimmed);
                    string encoded = url[(url.IndexOf("&body=", StringComparison.Ordinal) + 6)..];
                    string decoded = Uri.UnescapeDataString(encoded);
                    Assert.DoesNotContain('\uFFFD', decoded);
                    Assert.Equal(encoded, Uri.EscapeDataString(decoded));
                }
                finally { window.Close(); }
                var normal = new SupportWindow(actions, _ => Snapshot());
                try
                {
                    Assert.Null(normal.ErrorSummaryBox);
                    Assert.Equal(SupportReport.Body(normal.DiagnosticsBox.Text, normal.MessageBox.Text), normal.Submission().Body);
                    Assert.Equal(Visibility.Visible, normal.StoreReviewButton.Visibility);
                    // Settings keeps the bundle beside its support button; plain support adds no second one.
                    Assert.Equal(Visibility.Collapsed, normal.BundleButton.Visibility);
                    Assert.True(normal.SaveBundleAsync().IsCompletedSuccessfully);
                    Assert.Equal(0, actions.ChooseCalls);
                }
                finally { normal.Close(); }
            }
            finally { app.Shutdown(); }
        });

    [Fact]
    public Task ShownWindowFitsSmallDesktopAndLastButtonRemainsReachable() =>
        SettingsThemePreviewTests.RunIsolated(nameof(ShownWindowFitsSmallDesktopAndLastButtonRemainsReachable), () =>
        {
            Application app = CreateApplication();
            try
            {
                var window = new SupportWindow(new FakeActions(), _ => Snapshot(), Report());
                // The fixture's native-size hook needs a finite initial request; SizeToContent still owns the height.
                window.Height = window.MinHeight;
                using var track = new WindowTrackSizeScope(window, new Size(0, 0));
                try
                {
                    Show(window);
                    Assert.Equal(SizeToContent.Height, window.SizeToContent);
                    Assert.InRange(window.ActualHeight, window.MinHeight, window.MaxHeight);
                    DpiScale dpi = VisualTreeHelper.GetDpi(window);
                    Assert.True(window.ActualWidth * dpi.DpiScaleX <= WindowTrackSizeScope.SmallDesktopMaximum.Width + 1);
                    Assert.True(window.ActualHeight * dpi.DpiScaleY <= WindowTrackSizeScope.SmallDesktopMaximum.Height + 1);
                    Assert.True(track.MessageCount > 0);
                    var scroll = Assert.IsType<ScrollViewer>(window.Content);
                    Assert.True(scroll.ScrollableHeight > 0);
                    scroll.ScrollToEnd();
                    window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                    Assert.Equal(scroll.ScrollableHeight, scroll.VerticalOffset, precision: 2);
                    Rect last = window.CloseButton.TransformToAncestor(scroll)
                        .TransformBounds(new Rect(new Point(), window.CloseButton.RenderSize));
                    Assert.True(last.Top >= 0 && last.Bottom <= scroll.ActualHeight + 1);
                    Assert.True(window.CloseButton.IsVisible && window.CloseButton.IsEnabled);
                }
                finally { window.Close(); }
            }
            finally { app.Shutdown(); }
        }, typeof(SupportErrorReportTests), new Dictionary<string, string>
        { [WindowTrackSizeScope.SmallDesktopEnvironmentVariable] = "1" });

    [Fact]
    public Task RenderErrorReportEvidence()
    {
        if (Environment.GetEnvironmentVariable("MYCAPTURE_ERROR_REPORT_EVIDENCE") is not { Length: > 0 })
            return Task.CompletedTask;
        return Isolated(nameof(RenderErrorReportEvidence), () =>
        {
            string root = DiagnosticOutputPaths.Create(Environment.GetEnvironmentVariable("MYCAPTURE_ERROR_REPORT_EVIDENCE")!);
            Application app = CreateApplication();
            bool? oldMotion = FluidMotion.AnimationsEnabledOverrideForTest;
            FluidMotion.AnimationsEnabledOverrideForTest = false;
            try
            {
                int count = 0;
                foreach (string languageName in new[] { "ko-KR", "en" })
                foreach (AppTheme theme in new[] { AppTheme.Daylight, AppTheme.Midnight })
                {
                    using var language = UiText.UseLanguage(languageName);
                    ThemeService.Apply(theme);
                    foreach (string surface in new[] { "support", "error", "exit", "unhandled" })
                    {
                        Window window = surface switch
                        {
                            "support" => new SupportWindow(new FakeActions(), _ => Snapshot()),
                            "error" => new SupportWindow(new FakeActions(), _ => Snapshot(), Report()),
                            "exit" => ThemedDialog.CreateUnexpectedExitNotice(() => throw new InvalidOperationException("No actions in render")),
                            _ => ThemedDialog.CreateUnhandled(ThrownException()),
                        };
                        try
                        {
                            Render(window, DiagnosticOutputPaths.Child(root, $"{surface}-{languageName}-{theme}.png"),
                                surface is "support" or "error" ? 680 : 560);
                            count++;
                        }
                        finally { window.Close(); }
                    }
                }
                Assert.Equal(16, count);
            }
            finally { FluidMotion.AnimationsEnabledOverrideForTest = oldMotion; app.Shutdown(); }
        });
    }

    private static Task Isolated(string name, Action action) =>
        SettingsThemePreviewTests.RunIsolated(name, action, typeof(SupportErrorReportTests));

    private static Application CreateApplication()
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            { Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{name}.xaml") });
        ThemeService.Apply(AppTheme.Daylight);
        return application;
    }

    private static SupportDiagnosticSnapshot Snapshot() => new("4.4.0.0", "4.4.0+abc123", true, "10.0.26100", 1,
        UiText.Culture.Name, ThemeService.Current.ToString());
    private static ErrorReport Report() => ErrorReport.ForException(ThrownException(), new RecentErrorLog(), null,
        new DateTimeOffset(2026, 11, 6, 17, 25, 0, TimeSpan.Zero));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Exception ThrownException()
    {
        try { throw new InvalidOperationException("Synthetic exception message"); }
        catch (InvalidOperationException exception) { return exception; }
    }

    private static void Show(Window window)
    {
        FluidMotion.SetWindowEntrance(window, false);
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -10000; window.Top = -10000;
        window.ShowActivated = false; window.ShowInTaskbar = false;
        window.Show();
        window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
    }

    private static void Complete(Task task)
    {
        var elapsed = Stopwatch.StartNew();
        while (!task.IsCompleted)
        {
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(10), "The asynchronous bundle operation did not complete.");
            Ocr410Fixture.Pump();
        }
        task.GetAwaiter().GetResult();
    }

    private static void Render(Window window, string path, double width)
    {
        window.Width = width;
        Show(window);
        var content = (FrameworkElement)window.Content;
        Ocr410Fixture.Pump();
        // Render the real offscreen HWND's settled content so Loaded button faces and DPI
        // have exactly the same layout as the displayed product, without a screen capture.
        window.UpdateLayout();
        Ocr410Fixture.Pump();
        window.UpdateLayout();
        int pixelWidth = Math.Max(1, (int)Math.Ceiling(content.ActualWidth));
        int pixelHeight = Math.Max(1, (int)Math.Ceiling(content.ActualHeight));
        var bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight, 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (DrawingContext drawing = background.RenderOpen())
            drawing.DrawRectangle(window.Background, null, new Rect(0, 0, pixelWidth, pixelHeight));
        bitmap.Render(background);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream output = File.Create(path);
        encoder.Save(output);
    }

    private sealed class FakeActions : ISupportActions
    {
        internal readonly List<string> Opened = [];
        internal string? Copied, ChosenPath, SavedPath, ShownPath;
        internal SupportSubmission? Submission;
        internal int ChooseCalls, SaveCalls, SaveThread, ShowThread;
        internal bool ShowSucceeds = true;
        internal Action? Save;
        public void CopyText(string text) => Copied = text;
        public bool TryOpenUri(string uri) { Opened.Add(uri); return true; }
        public string? ChooseBundlePath(Window owner) { ChooseCalls++; return ChosenPath; }
        public void SaveBundle(string path, SupportSubmission submission)
        {
            SaveCalls++; SaveThread = Environment.CurrentManagedThreadId;
            SavedPath = path; Submission = submission; Save?.Invoke();
        }
        public bool TryShowInFolder(string path)
        {
            ShowThread = Environment.CurrentManagedThreadId; ShownPath = path; return ShowSucceeds;
        }
    }
}
