using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Diagnostics;
using MyCapture.App.Settings;
using MyCapture.App.Store;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Platform;
using MyCapture.Core.Settings;
using MyCapture.Core.Themes;
using Xunit;

namespace MyCapture.App.Tests;

public sealed partial class SettingsThemePreviewTests
{
    [Fact]
    public Task UpdateTab_OpensTheMicrosoftStoreListing_InEveryBuild() =>
        RunIsolated(nameof(UpdateTab_OpensTheMicrosoftStoreListing_InEveryBuild), VerifyStoreUpdateTab);

    [Fact]
    public Task StoreUpdate_RejectsPreviousProgressAndResetsFailedAttempts() =>
        RunIsolated(nameof(StoreUpdate_RejectsPreviousProgressAndResetsFailedAttempts), VerifyStoreUpdateOperationBoundaries);

    [Fact]
    public Task EngagementNotice_IsInlineDismissibleAndOnlyChosenReviewRequests() =>
        RunIsolated(nameof(EngagementNotice_IsInlineDismissibleAndOnlyChosenReviewRequests), VerifyEngagementNotice);

    private static void VerifyEngagementNotice()
    {
        Application application = CreateThemedApplication();
        try
        {
            foreach (string locale in new[] { "ko-KR", "en-US" })
            {
                using var language = UiText.UseLanguage(locale);
                SettingsWindow window = CreateOffscreenSettingsWindow(new AppSettings());
                try
                {
                    UIElement original = (UIElement)window.Content;
                    var notice = new EngagementNotice();
                    // The shell's overlay: the card floats over the content and never moves it.
                    EngagementNoticeHost.Attach(window, notice, bottomInset: 76);
                    int requests = 0; notice.ReviewChosen = () => requests++;
                    window.Show();
                    var apply = Assert.IsType<Button>(window.FindName("CancelButton"));
                    foreach ((double width, double height, string theme) in new[] { (940d, 700d, AppThemeNames.Glass), (780d, 560d, AppThemeNames.HighContrast) })
                    {
                        ThemeService.ApplyFromSettings(theme); window.Width = width; window.Height = height;
                        notice.Dismiss(); Drain(window);
                        Point before = original.TranslatePoint(new Point(), window);
                        notice.ShowInvitation(); Drain(window);
                        Assert.Equal(before, original.TranslatePoint(new Point(), window));
                        Assert.Equal(0, requests);
                        AssertFits(window, notice);
                        foreach (Button button in Descendants(notice).OfType<Button>()) AssertFits(window, button);
                        // It sits above the footer, so Close stays reachable.
                        Rect card = notice.TransformToAncestor(window).TransformBounds(new Rect(notice.RenderSize));
                        Rect applyBounds = apply.TransformToAncestor(window).TransformBounds(new Rect(apply.RenderSize));
                        Assert.True(card.Bottom <= applyBounds.Top, $"{card} covers Close {applyBounds}");
                        SaveStoreUpdateEvidence(window, $"rating-invitation-{locale}-{theme}-{width}x{height}.png");
                    }
                    Button close = Descendants(notice).OfType<Button>().Single(button => (string)button.Content == UiText.Get("Dialog.Close"));
                    close.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    Assert.Equal(Visibility.Collapsed, notice.Visibility);
                    Assert.Equal(0, requests);
                    notice.ShowInvitation();
                    Button review = Descendants(notice).OfType<Button>().Single(button => (string)button.Content == UiText.Get("StoreRating_InviteAction"));
                    review.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    Assert.Equal(1, requests);
                    Assert.Equal(Visibility.Collapsed, notice.Visibility);
                    notice.ShowUpdateCompleted("4.1.2.0"); Drain(window);
                    Assert.Equal(Visibility.Collapsed, review.Visibility);
                    Assert.Single(Descendants(notice).OfType<TextBlock>(), text => text.Text == UiText.Format("StoreUpdate_CompletedVersion", "4.1.2"));
                    SaveStoreUpdateEvidence(window, $"update-completed-{locale}-high-contrast-780x560.png");
                }
                finally { window.CloseForExit(); }
            }
        }
        finally { application.Shutdown(); }
    }

    private static void VerifyStoreUpdateTab()
    {
        Application application = CreateThemedApplication();
        try
        {
            foreach (bool packaged in new[] { true, false })
            {
                // The isolated child process owns this override, so no other test sees it.
                AppPackage.IsPackagedOverride = packaged;
                foreach (string locale in new[] { "ko-KR", "en-US" })
                {
                    using var language = UiText.UseLanguage(locale);
                    VerifyStoreUpdateTabFor(packaged, locale);
                }
            }
        }
        finally
        {
            application.Shutdown();
            AppPackage.IsPackagedOverride = null;
        }
    }

    private static void VerifyStoreUpdateTabFor(bool packaged, string locale)
    {
        string build = packaged ? "store" : "unpackaged";
        SettingsWindow window = CreateOffscreenSettingsWindow(new AppSettings());
        try
        {
            window.Show();
            var tabs = Assert.IsType<TabControl>(window.FindName("CategoryTabs"));
            tabs.SelectedItem = window.FindName("UpdateTab");
            Drain(window);

            // Legacy GitHub updater controls remain absent; progress belongs to the Store adapter.
            Assert.Null(window.FindName("UpdateCancel"));
            Assert.Null(window.FindName("UpdateDownloadProgress"));
            Assert.Null(window.FindName("UpdateStatus"));

            var description = Assert.IsType<TextBlock>(window.FindName("UpdateDescription"));
            var action = Assert.IsType<Button>(window.FindName("UpdateAction"));
            var rating = Assert.IsType<Button>(window.FindName("StoreRatingButton"));
            string expectedDescription = packaged
                ? UiText.Get("StoreUpdate_ObservedDescription")
                : UiText.Get("StoreUpdate_UnpackagedDescription") + Environment.NewLine + Environment.NewLine
                    + UiText.Get("StoreUpdate_UnpackagedNote");
            string expectedLabel = UiText.Get(packaged ? "StoreUpdate_Check" : "StoreUpdate_OpenStore");
            string expectedHelp = UiText.Get(packaged ? "StoreUpdate_CheckHelp" : "StoreUpdate_OpenStoreHelp");
            Assert.Equal(expectedDescription, description.Text);
            Assert.Equal(expectedLabel, action.Content);
            Assert.Equal(expectedLabel, AutomationProperties.GetName(action));
            Assert.Equal(expectedHelp, AutomationProperties.GetHelpText(action));
            Assert.True(action.IsVisible && action.IsEnabled && action.Focusable);
            Assert.Equal(packaged, rating.IsVisible);
            // Checking for updates heads the update section; rating sits with reporting a problem in
            // the feedback section. Each row wraps when narrow.
            Assert.IsType<WrapPanel>(VisualTreeHelper.GetParent(action));
            Assert.IsType<WrapPanel>(VisualTreeHelper.GetParent(rating));
            Assert.Same(VisualTreeHelper.GetParent(rating), VisualTreeHelper.GetParent((Button)window.FindName("SupportReport")));
            AssertUpdateTabFits(window, description, action, rating);
            SaveStoreUpdateEvidence(window, $"update-tab-{build}-{locale}-glass-940x700.png");

            var fake = new ObservableStoreUpdates();
            window.StoreUpdates = fake;
            var checkedAt = new DateTimeOffset(2026, 10, 13, 14, 2, 0, TimeSpan.Zero);
            window.StoreCheckClock = () => checkedAt;
            if (packaged)
            {
                // Checking Store availability does not open a page or install anything; the
                // result carries the time of the check.
                Assert.True(window.RunStoreUpdateActionAsync().IsCompletedSuccessfully);
                Assert.Equal(1, fake.Checks);
                Assert.Equal(0, fake.Installs);
                Assert.Equal(SettingsWindow.UpdateCheckSummary(StoreUpdatePhase.Current, checkedAt),
                    ((TextBlock)window.FindName("UpdateObservedStatus")).Text);
                Assert.EndsWith(" · " + UiText.Get("About_UpToDate"), ((TextBlock)window.FindName("UpdateObservedStatus")).Text,
                    StringComparison.Ordinal);
                // The listing button repeats Check and Rate; it waits for a failed check.
                Assert.Equal(Visibility.Collapsed, ((Button)window.FindName("StorePageAction")).Visibility);
            }
            // The distinct listing action keeps the Store/browser fallback.
            Button linkAction = packaged ? (Button)window.FindName("StorePageAction") : action;
            var opened = new List<string>();
            window.StoreLink = new StoreUpdateLink(opened.Add);
            linkAction.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Drain(window);
            Assert.Equal(new[] { StoreUpdateLink.StoreAppUri }, opened);
            var status = Assert.IsType<TextBlock>(window.FindName("SettingsStatus"));
            Assert.True(string.IsNullOrEmpty(status.Text));
            Assert.False(status.IsVisible);

            // With neither the Store app nor a browser available, the status line gives the address.
            opened.Clear();
            window.StoreLink = new StoreUpdateLink(uri =>
            {
                opened.Add(uri);
                throw new Win32Exception(2);
            });
            linkAction.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Drain(window);
            Assert.Equal(new[] { StoreUpdateLink.StoreAppUri, StoreUpdateLink.WebUri }, opened);
            Assert.Equal(UiText.Format("StoreUpdate_OpenFailed", StoreUpdateLink.WebUri), status.Text);
            Assert.Contains(StoreUpdateLink.WebUri, status.Text, StringComparison.Ordinal);
            Assert.True(status.IsVisible);
            AssertStatusFits(window, status);
            SaveStoreUpdateEvidence(window, $"update-tab-{build}-{locale}-glass-open-failed.png");

            // The smallest window and the high-contrast palette keep every part readable.
            window.Width = window.MinWidth;
            window.Height = window.MinHeight;
            ThemeService.ApplyFromSettings(AppThemeNames.HighContrast);
            Drain(window);
            Assert.Equal(AppTheme.HighContrast, ThemeService.Current);
            AssertUpdateTabFits(window, description, action, rating);
            AssertStatusFits(window, status);
            SaveStoreUpdateEvidence(window, $"update-tab-{build}-{locale}-high-contrast-780x560.png");
            if (packaged) VerifyObservedStoreDeployment(window, fake, locale);
            VerifySupportPreview(window, build, locale);

        }
        finally
        {
            window.CloseForExit();
        }
    }

    private sealed class ObservableStoreUpdates : IStorePackageUpdateService
    {
        public int Checks, Installs;
        public bool Available;
        public IProgress<StoreUpdateSnapshot>? Progress;
        public TaskCompletionSource<StoreUpdatePhase>? Completion;
        public TaskCompletionSource<bool>? CheckCompletion;
        public Task<bool> CheckAsync()
        {
            Checks++;
            return CheckCompletion?.Task ?? Task.FromResult(Available);
        }
        public Task<StoreUpdatePhase> InstallAsync(IProgress<StoreUpdateSnapshot> progress)
        {
            Installs++; Progress = progress;
            Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return Completion.Task;
        }
    }

    private static void VerifyStoreUpdateOperationBoundaries()
    {
        Application application = CreateThemedApplication();
        AppPackage.IsPackagedOverride = true;
        try
        {
            foreach (string locale in new[] { "ko-KR", "en-US" })
            {
                using var language = UiText.UseLanguage(locale);
                var settings = new AppSettings();
                SettingsWindow window = CreateOffscreenSettingsWindow(settings);
                SynchronizationContext? previous = SynchronizationContext.Current;
                SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(window.Dispatcher));
                try
                {
                    window.Show();
                    var state = (TextBlock)window.FindName("UpdateObservedStatus");
                    var action = (Button)window.FindName("UpdateAction");
                    var progress = (ProgressBar)window.FindName("UpdateProgress");
                    var fake = new ObservableStoreUpdates { Available = true };
                    window.StoreUpdates = fake;
                    int released = 0;
                    window.PrepareStoreUpdate = () => Task.FromResult(true);
                    window.FinishStoreUpdate = () => released++;
                    Assert.True(window.RunStoreUpdateActionAsync().IsCompletedSuccessfully);
                    Assert.Equal(UiText.Get("StoreUpdate_InstallHelp"), AutomationProperties.GetHelpText(action));

                    Task failed = window.RunStoreUpdateActionAsync();
                    IProgress<StoreUpdateSnapshot> failedObserver = fake.Progress!;
                    fake.Completion!.SetException(new IOException("Store transport failure"));
                    PumpUntil(window, failed);
                    Assert.Equal(UiText.Get("StoreUpdate_StateFailed"), state.Text);
                    Assert.Equal(1, released);
                    Assert.True(window.IsEnabled);
                    Assert.Equal(UiText.Get("StoreUpdate_Check"), action.Content);
                    Assert.Equal(UiText.Get("StoreUpdate_CheckHelp"), AutomationProperties.GetHelpText(action));

                    // A prior observer must not overwrite the next asynchronous check, even when busy.
                    fake.CheckCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    Task check = window.RunStoreUpdateActionAsync();
                    Assert.Equal(UiText.Get("StoreUpdate_StateChecking"), state.Text);
                    failedObserver.Report(new(StoreUpdatePhase.Installing));
                    Task.Run(() => failedObserver.Report(new(StoreUpdatePhase.Downloading, .91))).GetAwaiter().GetResult();
                    Drain(window);
                    Assert.Equal(UiText.Get("StoreUpdate_StateChecking"), state.Text);
                    fake.CheckCompletion.SetResult(true);
                    PumpUntil(window, check);
                    Assert.Equal(2, fake.Checks); // Failure resets availability, so retry checks again.
                    Assert.Equal(UiText.Get("StoreUpdate_InstallHelp"), AutomationProperties.GetHelpText(action));

                    Task install = window.RunStoreUpdateActionAsync();
                    IProgress<StoreUpdateSnapshot> completedObserver = fake.Progress!;
                    completedObserver.Report(new(StoreUpdatePhase.Downloading, .21));
                    Task.Run(() => failedObserver.Report(new(StoreUpdatePhase.Downloading, .99))).GetAwaiter().GetResult();
                    Drain(window);
                    Assert.Equal(UiText.Get("StoreUpdate_StateDownloading"), state.Text);
                    Assert.Equal(21, progress.Value);
                    fake.Completion!.SetResult(StoreUpdatePhase.RestartRequired);
                    PumpUntil(window, install);
                    completedObserver.Report(new(StoreUpdatePhase.Installing));
                    Assert.Equal(UiText.Get("StoreUpdate_StateRestartRequired"), state.Text);

                    // Completion's observer is also invalid during a new check, not merely on failure.
                    fake.CheckCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    check = window.RunStoreUpdateActionAsync();
                    Task.Run(() => completedObserver.Report(new(StoreUpdatePhase.Installing))).GetAwaiter().GetResult();
                    Drain(window);
                    Assert.Equal(UiText.Get("StoreUpdate_StateChecking"), state.Text);
                    fake.CheckCompletion.SetException(new IOException("Check unavailable after successful deployment"));
                    PumpUntil(window, check);
                    Assert.Equal(UiText.Get("StoreUpdate_StateFailed"), state.Text);
                    Assert.Equal(UiText.Get("StoreUpdate_Check"), action.Content);
                    Assert.Equal(2, released);
                    Assert.True(window.IsEnabled && action.IsEnabled);
                }
                finally
                {
                    window.CloseForExit();
                    SynchronizationContext.SetSynchronizationContext(previous);
                }
            }
        }
        finally { application.Shutdown(); AppPackage.IsPackagedOverride = null; }
    }

    private static void VerifyObservedStoreDeployment(SettingsWindow window, ObservableStoreUpdates fake, string locale)
    {
        // StaTestHost does not run Application.Run; install the same WPF continuation context as button input.
        SynchronizationContext? previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(window.Dispatcher));
        try
        {
            var state = (TextBlock)window.FindName("UpdateObservedStatus");
            var progress = (ProgressBar)window.FindName("UpdateProgress");
            fake.Available = true;
            Assert.True(window.RunStoreUpdateActionAsync().IsCompletedSuccessfully);
            Assert.EndsWith(" · " + UiText.Get("StoreUpdate_StateAvailable"), state.Text, StringComparison.Ordinal);
            Assert.True(window.IsEnabled);
            Assert.Empty(((TextBlock)window.FindName("SettingsStatus")).Text);
            Assert.True(window.RunStoreUpdateActionAsync().IsCompletedSuccessfully);
            Assert.Equal(0, fake.Installs); // Save/discard/cancel protection must allow installation first.
            Assert.Equal(UiText.Get("StoreUpdate_StateBlocked"), state.Text);
            Assert.True(window.IsEnabled);
            int protectionReleased = 0;
            window.PrepareStoreUpdate = () => Task.FromResult(true);
            window.FinishStoreUpdate = () => protectionReleased++;
            Task install = window.RunStoreUpdateActionAsync();
            Assert.False(install.IsCompleted);
            Assert.False(window.IsEnabled);
            Assert.False(((Button)window.FindName("CancelButton")).IsEnabled);
            Assert.Equal(1, fake.Installs);
            Assert.Equal(0, protectionReleased);
            Assert.Equal(UiText.Get("StoreUpdate_StateAwaitingConsent"), state.Text);
            Assert.True(progress.IsIndeterminate);
            fake.Progress!.Report(new(StoreUpdatePhase.Downloading, .42));
            state.BringIntoView(); Drain(window);
            Assert.False(progress.IsIndeterminate);
            Assert.Equal(42, progress.Value);
            Assert.Equal(UiText.Get("StoreUpdate_StateDownloading"), state.Text);
            Assert.False(window.IsEnabled);
            SaveStoreUpdateEvidence(window, $"update-downloading-{locale}-high-contrast-780x560.png");
            fake.Progress.Report(new(StoreUpdatePhase.Installing, .95));
            Drain(window);
            Assert.True(progress.IsIndeterminate); // Download percentage cannot describe deployment.
            Assert.Equal(UiText.Get("StoreUpdate_StateInstalling"), state.Text);
            Assert.False(window.IsEnabled);
            SaveStoreUpdateEvidence(window, $"update-installing-{locale}-high-contrast-780x560.png");
            fake.Completion!.SetResult(StoreUpdatePhase.Canceled);
            PumpUntil(window, install);
            Assert.Equal(1, protectionReleased);
            Assert.Equal(UiText.Get("StoreUpdate_StateCanceled"), state.Text);
            Assert.True(window.IsEnabled);
            Assert.Equal(Visibility.Collapsed, progress.Visibility);
            Assert.True(((Button)window.FindName("UpdateAction")).IsEnabled);
            // Completed SDK deployment is restart-required; only next-launch version observation confirms completion.
            Assert.True(window.RunStoreUpdateActionAsync().IsCompletedSuccessfully);
            Task completed = window.RunStoreUpdateActionAsync();
            fake.Completion!.SetResult(StoreUpdatePhase.RestartRequired);
            PumpUntil(window, completed);
            Assert.Equal(2, protectionReleased);
            Assert.Equal(UiText.Get("StoreUpdate_StateRestartRequired"), state.Text);
            Assert.True(window.IsEnabled);

            // A transport failure must release work admission just like cancellation.
            Assert.True(window.RunStoreUpdateActionAsync().IsCompletedSuccessfully);
            Task failed = window.RunStoreUpdateActionAsync();
            Assert.False(window.IsEnabled);
            fake.Completion!.SetException(new IOException("Store unavailable"));
            PumpUntil(window, failed);
            Assert.Equal(3, protectionReleased);
            Assert.Equal(UiText.Get("StoreUpdate_StateFailed"), state.Text);
            Assert.True(window.IsEnabled);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previousContext); }
    }

    private static void PumpUntil(Window window, Task task)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!task.IsCompleted && DateTime.UtcNow < until) Drain(window);
        Assert.True(task.IsCompletedSuccessfully, task.Exception?.ToString());
    }

    /// <summary>Contact support previews diagnostics before explicit copy or URL actions.</summary>
    private static void VerifySupportPreview(SettingsWindow window, string build, string locale)
    {
        var report = (Button)window.FindName("SupportReport");
        string? copied = null;
        var opened = new List<string>();
        window.CopySupportText = value =>
        {
            Assert.Empty(opened);
            copied = value;
        };
        window.OpenSupportPage = opened.Add;
        SupportWindow? support = null;
        window.OpenSupportDialog = dialog => support = dialog;
        report.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert.NotNull(support);
        Assert.Null(copied);
        Assert.Empty(opened);
        support.CopyDiagnostics();
        Assert.Contains("%", copied, StringComparison.Ordinal);
        Assert.Contains("Windows:", copied, StringComparison.Ordinal);
        Assert.DoesNotContain("https://", copied, StringComparison.Ordinal);
        Assert.DoesNotContain("C:\\", copied, StringComparison.OrdinalIgnoreCase);
        support.OpenGitHub();
        Assert.StartsWith(SupportDiagnostics.IssueUri + "?title=", Assert.Single(opened), StringComparison.Ordinal);
        support.Close();
        report.BringIntoView(); Drain(window); AssertFits(window, report);
        SaveStoreUpdateEvidence(window, $"support-report-{build}-{locale}-high-contrast-780x560.png");
    }
    private static void AssertUpdateTabFits(SettingsWindow window, TextBlock description, Button action, Button rating)
    {
        // Within the window, and within the tab page's scroll viewport once scrolled to.
        var scroll = Assert.IsType<ScrollViewer>(window.FindName("UpdateScroll"));
        Rect text = description.TransformToAncestor(scroll).TransformBounds(new Rect(description.RenderSize));
        Assert.True(text.Left >= -0.5 && text.Right <= scroll.ViewportWidth + 0.5, $"Description {text} wider than {scroll.ViewportWidth}");
        foreach (Button button in new[] { action, rating }.Where(button => button.IsVisible))
        {
            button.BringIntoView();
            Drain(window);
            AssertFits(window, button);
            Rect bounds = button.TransformToAncestor(scroll).TransformBounds(new Rect(button.RenderSize));
            Assert.True(bounds.Left >= -0.5 && bounds.Top >= -0.5 && bounds.Right <= scroll.ViewportWidth + 0.5
                && bounds.Bottom <= scroll.ViewportHeight + 0.5,
                $"{button.Content}: {bounds} outside viewport {scroll.ViewportWidth}x{scroll.ViewportHeight}");
            foreach (ContentPresenter presenter in Descendants(button).OfType<ContentPresenter>())
            {
                Rect content = presenter.TransformToAncestor(button).TransformBounds(new Rect(presenter.RenderSize));
                Assert.True(content.Left >= 0 && content.Right <= button.ActualWidth + 0.5,
                    $"{button.Content}: label {content} outside button {button.RenderSize}");
            }
        }
    }

    private static void SaveStoreUpdateEvidence(Window window, string name)
    {
        string? requested = Environment.GetEnvironmentVariable("MYCAPTURE_STORE_UPDATE_EVIDENCE");
        if (string.IsNullOrWhiteSpace(requested)) return;
        string directory = DiagnosticOutputPaths.Create(requested);
        Drain(window);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight),
            96, 96, PixelFormats.Pbgra32);
        var backdrop = new DrawingVisual();
        using (DrawingContext context = backdrop.RenderOpen())
        {
            // Glass leaves the window transparent for Mica; give the evidence an opaque base.
            context.DrawRectangle((Brush)window.FindResource("Surface.Base"), null,
                new Rect(0, 0, window.ActualWidth, window.ActualHeight));
        }

        bitmap.Render(backdrop);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(DiagnosticOutputPaths.Child(directory, name));
        encoder.Save(stream);
    }
}
