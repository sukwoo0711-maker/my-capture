using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.App.Editing;
using MyCapture.App.Store;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Platform;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Platform.Display;
using MyCapture.Platform.Shell;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// The shell a new user meets: start at sign-in offered on the welcome, the retention sentence,
/// the "ready" notification only when it says something new, notifications on the pointer's
/// monitor in the shared card chrome, the engagement card as an overlay, one draft recovery
/// dialog and tutorials that lead to the unassigned shortcut's settings row.
/// </summary>
public sealed class ShellFirstRunTests
{
    // ---- Start at sign-in -------------------------------------------------------------------

    [Fact]
    public void WelcomeChoiceIsAppliedThroughTheRegistrationAndRecordsWhatWindowsDoes()
    {
        using var language = UiText.UseLanguage("ko-KR");
        var enabling = new FakeRegistration(applies: true);
        StartAtSignInOutcome on = FirstRunPlan.ApplyStartAtSignIn(true, enabling);
        Assert.Equal([true], enabling.Requests);
        Assert.Equal(new StartAtSignInOutcome(true, null), on);

        // Unchecked: nothing is registered, and a stale registration is removed.
        var declining = new FakeRegistration(applies: true) { Enabled = true };
        StartAtSignInOutcome off = FirstRunPlan.ApplyStartAtSignIn(false, declining);
        Assert.Equal([false], declining.Requests);
        Assert.Equal(new StartAtSignInOutcome(false, null), off);

        // A failed write is never reported as on.
        var failing = new FakeRegistration(applies: false);
        StartAtSignInOutcome failed = FirstRunPlan.ApplyStartAtSignIn(true, failing);
        Assert.False(failed.Enabled);
        Assert.Equal("시작 프로그램 설정을 변경하지 못했습니다: 시작 프로그램 등록을 확인하지 못했습니다.", failed.Notice);
    }

    [Theory]
    [InlineData(PackagedStartupState.Disabled, PackagedStartupState.Enabled, true, null)]
    [InlineData(PackagedStartupState.Disabled, PackagedStartupState.Disabled, false, "Text_4D69B33A2FD7")]
    [InlineData(PackagedStartupState.DisabledByUser, PackagedStartupState.DisabledByUser, false, "Startup.StoreDisabledByUser")]
    [InlineData(PackagedStartupState.DisabledByPolicy, PackagedStartupState.DisabledByPolicy, false, "Startup.StoreManagedByPolicy")]
    [InlineData(PackagedStartupState.EnabledByPolicy, PackagedStartupState.EnabledByPolicy, true, null)]
    public void StoreStartupTaskStatesGetAnHonestOutcome(PackagedStartupState initial, PackagedStartupState afterRequest,
        bool enabled, string? noticeKey)
    {
        using var language = UiText.UseLanguage("ko-KR");
        var task = new FakeStartupTask(initial, afterRequest);
        StartAtSignInOutcome outcome = FirstRunPlan.ApplyStartAtSignIn(true, new PackagedStartupRegistration(task));
        Assert.Equal(enabled, outcome.Enabled);
        if (noticeKey is null) Assert.Null(outcome.Notice);
        else Assert.Equal(UiText.Format("Text_0FA1A9D03BE3", UiText.Get(noticeKey)), outcome.Notice);
        // A task the user or a policy turned off is never asked to turn on.
        Assert.Equal(initial == PackagedStartupState.Disabled ? 1 : 0, task.EnableRequests);

        // Leaving the box unchecked never asks Windows to enable anything.
        var untouched = new FakeStartupTask(PackagedStartupState.Disabled, PackagedStartupState.Enabled);
        Assert.False(FirstRunPlan.ApplyStartAtSignIn(false, new PackagedStartupRegistration(untouched)).Enabled);
        Assert.Equal(0, untouched.EnableRequests);
    }

    [Fact]
    public void WelcomeOffersStartAtSignInCheckedAndReportsAnUncheckedBox() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("en-US");
        var welcome = new WelcomeWindow(new HotkeySettings(), AfterCaptureAction.Editor);
        try
        {
            Assert.True(welcome.StartAtSignIn);
            Assert.Equal("Start MyCapture when I sign in to Windows", welcome.StartAtSignInCheck.Content);
            Assert.Equal(UiText.Get("Onboard_StartAtSignInHelp"), AutomationProperties.GetHelpText(welcome.StartAtSignInCheck));
            welcome.StartAtSignInCheck.IsChecked = false;
            Assert.False(welcome.StartAtSignIn);
            Assert.Equal("Images are deleted after 7 days unless kept or filed in a folder.", welcome.RetentionText);
        }
        finally { welcome.Close(); }
    });

    [Theory]
    [InlineData("ko-KR", 168, "이미지는 7일 뒤 자동 삭제됩니다. 보관하거나 폴더에 넣은 항목은 유지됩니다.")]
    [InlineData("ko-KR", 36, "이미지는 1일 12시간 뒤 자동 삭제됩니다. 보관하거나 폴더에 넣은 항목은 유지됩니다.")]
    [InlineData("ko-KR", 0, "이미지는 1시간 뒤 자동 삭제됩니다. 보관하거나 폴더에 넣은 항목은 유지됩니다.")]
    [InlineData("en-US", 24, "Images are deleted after 1 day unless kept or filed in a folder.")]
    [InlineData("en-US", 720, "Images are deleted after 30 days unless kept or filed in a folder.")]
    public void RetentionSentenceFollowsTheRetentionTheLibraryApplies(string locale, int hours, string expected)
    {
        using var language = UiText.UseLanguage(locale);
        Assert.Equal(expected, WelcomeWindow.RetentionSentence(hours));
    }

    // ---- Ready notification ---------------------------------------------------------------

    [Fact]
    public void ReadyNotificationRepeatsOnlyWhenTheCaptureKeyChanges()
    {
        using var language = UiText.UseLanguage("ko-KR");
        var notification = new ResidentReadyNotification();
        var shown = new List<string>();
        var before = new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Shift, Hotkey.VkC);
        var after = new Hotkey(HotkeyModifiers.Alt | HotkeyModifiers.Shift, Hotkey.VkX);

        // A settings apply that leaves the capture key alone says nothing.
        Assert.False(notification.NotifyCaptureKeyChanged(before, new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Shift, Hotkey.VkC),
            (_, message) => shown.Add(message)));
        Assert.True(notification.NotifyCaptureKeyChanged(before, after, (_, message) => shown.Add(message)));
        Assert.True(notification.NotifyCaptureKeyChanged(after, Hotkey.None, (_, message) => shown.Add(message)));
        Assert.Equal(["Alt+Shift+X 키를 누르면 화면을 캡처합니다. 트레이 아이콘에서 설정과 라이브러리를 열 수 있습니다.",
            UiText.Get("ShellRetake_ReadyWithoutShortcut")], shown);

        // The welcome's announcement is still once per resident shell.
        notification.Notify(true, after, (_, message) => shown.Add(message));
        notification.Notify(true, after, (_, message) => shown.Add(message));
        Assert.Equal(3, shown.Count);
    }

    // ---- Notifications --------------------------------------------------------------------

    [Fact]
    public void NotificationsSitInsideTheWorkAreaOfTheirMonitorIncludingAPortraitSecondary()
    {
        // A portrait monitor left of and above the primary, at 100% and at 150%.
        foreach (double scale in new[] { 1.0, 1.5 })
        {
            var work = new RectD(-1080 * scale, -400 * scale, 1080 * scale, 1880 * scale);
            Rect bounds = ThemedShellPresenter.NotificationBounds(work, scale, 384, 180);
            double right = work.Right / scale, bottom = work.Bottom / scale;
            Rect card = new(bounds.X + ThemedShellPresenter.ShadowMargin, bounds.Y + ThemedShellPresenter.ShadowMargin,
                bounds.Width - (2 * ThemedShellPresenter.ShadowMargin), bounds.Height - (2 * ThemedShellPresenter.ShadowMargin));
            Assert.Equal(right - ThemedShellPresenter.EdgeGap, card.Right, 3);
            Assert.Equal(bottom - ThemedShellPresenter.EdgeGap, card.Bottom, 3);
            Assert.True(bounds.Left >= work.Left / scale && bounds.Right <= right && bounds.Top >= work.Top / scale && bounds.Bottom <= bottom);
            Assert.True(bounds.Right < 0, "The card stays on the secondary monitor, not the primary.");
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    [InlineData(true)]
    public void NotificationCardsShareTheFloatingCardChrome(bool? supported) => StaTestHost.Run(() =>
    {
        bool layered = supported ?? NativeExclusion.SupportsLayeredWindows;
        using var mode = supported.HasValue ? new ExclusionMode(supported.Value) : null;
        using var presenter = new ThemedShellPresenter(() => false);
        Window window = presenter.CreateActionNotification("MyCapture", "Message", "Open", () => { });
        try
        {
            new WindowInteropHelper(window).EnsureHandle();
            NativeExclusion.AssertWindow(window, layered);
            Assert.Equal(WindowStyle.None, window.WindowStyle);
            Assert.False(window.ShowActivated);
            Assert.False(window.ShowInTaskbar);
            Assert.True(window.Topmost);
            double margin = layered ? ThemedShellPresenter.ShadowMargin : 0;
            Assert.Equal(ThemedShellPresenter.CardWidth + (2 * margin), window.Width);
            var surface = Assert.IsType<Grid>(window.Content);
            Assert.Equal(new Thickness(margin), surface.Margin);
            Border shadow = surface.Children.OfType<Border>().First();
            Border card = surface.Children.OfType<Border>().Last();
            Assert.Equal(layered ? ThemedShellPresenter.CardCorner : new CornerRadius(0), card.CornerRadius);
            Assert.Equal(card.CornerRadius, shadow.CornerRadius);
            Assert.Equal(new Thickness(1), card.BorderThickness);
            Assert.Equal("Border.Subtle", ResourceKey(card, Border.BorderBrushProperty));
            Assert.Equal("Surface.Floating", ResourceKey(card, Border.BackgroundProperty));
            if (layered)
            {
                Assert.Same(Brushes.Transparent, window.Background);
                Assert.Equal("Shadow.Card", ResourceKey(shadow, UIElement.EffectProperty));
            }
            else
            {
                Assert.Null(shadow.Effect);
                Assert.Null(card.Effect);
                Assert.Equal(DependencyProperty.UnsetValue, shadow.ReadLocalValue(UIElement.EffectProperty));
            }
            // The text is not drawn through the shadow effect.
            Assert.Equal(DependencyProperty.UnsetValue, card.ReadLocalValue(UIElement.EffectProperty));
            Assert.False(shadow.IsHitTestVisible);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void ShortcutConflictOffersChangeShortcutAndStaysUntilHandled() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        var monitor = new MonitorInfo("TEST", new RectD(-20000, -20000, 1080, 1920), new RectD(-20000, -20000, 1080, 1880), 96, false);
        using var presenter = new ThemedShellPresenter(() => false) { NotificationMonitor = () => monitor };
        int changes = 0;
        presenter.ShowActionNotification("단축키 충돌", "Ctrl+Shift+C 단축키를 등록할 수 없습니다. 설정에서 변경하십시오.",
            UiText.Get("Shell_ChangeShortcut"), () => changes++, TrayBalloonKind.Error);
        Assert.Equal((true, FeedbackKind.ActionRequired, (TimeSpan?)null), presenter.NotificationForTest);
        presenter.AdvanceNotificationForTest(TimeSpan.FromHours(1));
        Assert.True(presenter.NotificationForTest.Open);

        Window toast = presenter.NotificationWindowForTest!;
        Drain(toast);
        // Placed on the pointer's monitor (here a test monitor far off the desktop), not the primary.
        Assert.True(toast.Left < -10000 && toast.Top < -10000, $"Placed at {toast.Left},{toast.Top}");
        Button change = FirstRunWelcomeTests.Descendants(toast).OfType<Button>().Single(button => (string)button.Content == "단축키 변경");
        Assert.Equal("단축키 변경", AutomationProperties.GetName(change));
        change.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert.Equal(1, changes);
        Assert.False(presenter.NotificationForTest.Open);
    });

    [Fact]
    public void ConflictRowsAndTutorialRowsNameARealShortcutSetting()
    {
        string[] draftHotkeys = typeof(SettingsDraft).GetProperties()
            .Where(property => property.PropertyType == typeof(string) && property.Name.EndsWith("Hotkey", StringComparison.Ordinal))
            .Select(property => property.Name).ToArray();
        foreach (GlobalHotkeyCommand command in Enum.GetValues<GlobalHotkeyCommand>())
            Assert.Contains(ShortcutSettingTarget.ForCommand(command), draftHotkeys);
        foreach (FeatureTutorial feature in TutorialCatalog.All)
        {
            string? row = ShortcutSettingTarget.ForFeature(feature.Id);
            Assert.Equal(feature.GlobalKey is not null, row is not null);
            if (row is not null) Assert.Contains(row, draftHotkeys);
        }
    }

    // ---- Engagement card ------------------------------------------------------------------

    [Theory]
    [InlineData("4.1.2.0", "4.1.2")]
    [InlineData("4.1.2", "4.1.2")]
    [InlineData("4.1.2.7", "4.1.2.7")]
    [InlineData("preview", "preview")]
    public void UpdateNoticeShowsTheVersionAsPeopleReadIt(string version, string shown) =>
        Assert.Equal(shown, EngagementNotice.DisplayVersion(version));

    [Fact]
    public void EngagementCardIsAnOverlayThatStaysUntilClosed() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        bool? motion = FluidMotion.AnimationsEnabledOverrideForTest;
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        var content = new Border { Height = 400 };
        var window = new Window
        {
            Width = 900, Height = 600, Content = content, WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false,
        };
        try
        {
            var notice = new EngagementNotice();
            EngagementNoticeHost.Attach(window, notice, bottomInset: 76);
            window.Show();
            Drain(window);
            Point before = content.TranslatePoint(new Point(), window);
            notice.ShowUpdateCompleted("4.1.2.0");
            Drain(window);
            // Nothing under the pointer moves when the card appears.
            Assert.Equal(before, content.TranslatePoint(new Point(), window));
            Assert.Equal(Visibility.Visible, notice.Visibility);
            Assert.Equal("4.1.2 버전으로 업데이트됨", notice.Message);
            Assert.Equal(EngagementNotice.CardWidth, notice.ActualWidth, 1);
            Point corner = notice.TranslatePoint(new Point(notice.ActualWidth, notice.ActualHeight), window);
            var client = (FrameworkElement)window.Content;
            Assert.Equal(client.ActualWidth - 16, corner.X, 1);
            Assert.Equal(client.ActualHeight - 76, corner.Y, 1);
            Assert.Equal(1, notice.Opacity);

            // It stays when the window is left; only Close or the action takes it away.
            Button close = FirstRunWelcomeTests.Descendants(notice).OfType<Button>().Single(button => (string)button.Content == "닫기");
            close.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Equal(Visibility.Collapsed, notice.Visibility);
            int reviews = 0;
            notice.ReviewChosen = () => reviews++;
            notice.ShowInvitation();
            Button review = FirstRunWelcomeTests.Descendants(notice).OfType<Button>().Single(button => (string)button.Content == "평가");
            review.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Equal(1, reviews);
            Assert.Equal(Visibility.Collapsed, notice.Visibility);
        }
        finally
        {
            window.Close();
            FluidMotion.AnimationsEnabledOverrideForTest = motion;
        }
    });

    [Fact]
    public void AReturnCountsOnlyAfterTheWindowStaysActiveForASecond() => StaTestHost.Run(() =>
    {
        bool active = true;
        int returns = 0;
        Assert.True(EngagementNoticeHost.ReturnDelay >= TimeSpan.FromSeconds(1));
        var watch = new EngagementReturnWatch(Dispatcher.CurrentDispatcher, () => active, () => returns++, EngagementNoticeHost.ReturnDelay);
        Assert.Equal(EngagementNoticeHost.ReturnDelay, watch.Delay);

        // Nothing at the instant of activation: the click that activated the window lands first.
        watch.Activated();
        Assert.True(watch.IsWaiting);
        Assert.Equal(0, returns);
        watch.Elapse();
        Assert.Equal(1, returns);
        Assert.False(watch.IsWaiting);

        // Leaving before the second is up cancels it.
        watch.Activated();
        watch.Deactivated();
        watch.Elapse();
        Assert.Equal(1, returns);

        // A window that lost activation without an event is not counted either.
        watch.Activated();
        active = false;
        watch.Elapse();
        Assert.Equal(1, returns);
    });

    // ---- Draft recovery -------------------------------------------------------------------

    [Fact]
    public void DraftRecoveryListsEveryDraftInOneDialogWithATaskbarButton() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        DateTimeOffset saved = new DateTimeOffset(new DateTime(2026, 10, 12, 14, 32, 0, DateTimeKind.Local)).ToUniversalTime();
        var image = new DraftRecoveryItem(Guid.NewGuid(), false, saved, 640, 360, Solid(192, 108));
        var video = new DraftRecoveryItem(Guid.NewGuid(), true, saved.AddMinutes(-5), 1280, 720, null);
        Assert.Equal("10월 12일 14:32 · 640 × 360", image.Detail);
        Assert.Equal("이미지 편집 초안", image.Heading);
        Assert.Equal("동영상 편집 초안", video.Heading);
        Assert.Equal("10월 12일 14:32", (image with { Width = 0 }).Detail);
        // The line wraps between the time and the size, never inside either.
        Assert.Equal("10월 12일 14:32 · 640 × 360", DraftRecoveryDialog.KeepPartsTogether(image.Detail));

        var resolved = new List<(DraftRecoveryItem, DraftRecoveryChoice)>();
        bool failNext = false;
        var dialog = new DraftRecoveryDialog([image, video], (item, choice) =>
        {
            resolved.Add((item, choice));
            return Task.FromResult(!failNext);
        })
        { WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000, ShowActivated = false };
        try
        {
            Assert.True(dialog.ShowInTaskbar);
            Assert.Equal(ResizeMode.NoResize, dialog.ResizeMode);
            Assert.Equal(UiText.Get("EditRecovery_Title"), dialog.Title);
            dialog.Show();
            Drain(dialog);
            Assert.Equal([image, video], dialog.Items);
            Assert.Equal("나중에", dialog.LaterButton.Content);
            Assert.True(dialog.LaterButton.IsCancel);
            (Button recover, Button discard) = dialog.ButtonsFor(image);
            Assert.Equal("복구", recover.Content);
            Assert.Equal("초안 버리기", discard.Content);
            // Each button's name starts with its visible label and says which draft it acts on.
            Assert.Equal("복구: 10월 12일 14:32 · 640 × 360", AutomationProperties.GetName(recover));
            Assert.Equal("초안 버리기: 10월 12일 14:32 · 640 × 360", AutomationProperties.GetName(discard));
            // A 96px thumbnail beside each draft, a placeholder when there is no picture.
            Border[] frames = FirstRunWelcomeTests.Descendants(dialog).OfType<Border>()
                .Where(border => border.Width == DraftRecoveryDialog.ThumbnailWidth).ToArray();
            Assert.Equal(2, frames.Length);
            Assert.Contains(frames, frame => frame.Child is Border { Background: ImageBrush });
            Assert.Contains(frames, frame => frame.Child is SymbolIcon);

            // A failure keeps the draft listed so it can be tried again or discarded.
            failNext = true;
            discard.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Equal([image, video], dialog.Items);
            Assert.True(FirstRunWelcomeTests.Descendants(dialog).OfType<Border>().All(border => border.IsEnabled));

            failNext = false;
            discard.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Equal([video], dialog.Items);
            Assert.False(dialog.Completion.IsCompleted);
            dialog.ButtonsFor(video).Recover.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Equal([(image, DraftRecoveryChoice.Discard), (image, DraftRecoveryChoice.Discard), (video, DraftRecoveryChoice.Recover)],
                resolved);
            // The last draft handled closes the dialog and ends the initial recovery.
            Assert.True(dialog.Completion.IsCompleted);
            Assert.False(dialog.IsVisible);
        }
        finally { if (!dialog.Completion.IsCompleted) dialog.Close(); }

        // Later keeps every draft for the next launch.
        var later = new DraftRecoveryDialog([image], (_, _) => throw new InvalidOperationException("Later resolves nothing."))
        { WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000, ShowActivated = false };
        later.Show();
        Assert.Equal("Button.Primary", ResourceKey(later.ButtonsFor(image).Recover, FrameworkElement.StyleProperty));
        later.LaterButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert.True(later.Completion.IsCompleted);
    });

    [Fact]
    public async Task DraftRecoveryDescribesAnImageDraftFromItsPixelsAndAVideoFromItsRecording()
    {
        string root = OwnedTestDirectory.Create("mc-draft-recovery-");
        try
        {
            var store = new EditDraftStore(Path.Combine(root, "edit-drafts"));
            Guid imageSession = Guid.NewGuid(), videoSession = Guid.NewGuid();
            await store.WriteAsync(imageSession, 1, "image", "{}", new Dictionary<string, string>(),
                new Dictionary<string, byte[]> { ["base.png"] = Png(640, 360), ["original.png"] = Png(640, 360) }, () => true);
            string recording = """{"OutputPath":"x.mp4","DurationMs":1000,"Fps":30,"EmittedFrames":30,"Width":1280,"Height":720}""";
            await store.WriteAsync(videoSession, 1, "video", "{}", new Dictionary<string, string> { ["recording"] = recording },
                new Dictionary<string, byte[]>(), () => true);
            Dictionary<Guid, EditDraft> drafts = store.ReadAvailable().ToDictionary(entry => entry.Draft.SessionId, entry => entry.Draft);

            DraftRecoveryItem image = await EditRecoveryPresentation.DescribeAsync(store, drafts[imageSession], _ => null);
            Assert.False(image.IsVideo);
            Assert.Equal((640, 360), (image.Width, image.Height));
            Assert.Equal(EditRecoveryPresentation.ThumbnailPixelWidth, image.Thumbnail!.PixelWidth);
            Assert.True(image.Thumbnail.IsFrozen);

            DraftRecoveryItem video = await EditRecoveryPresentation.DescribeAsync(store, drafts[videoSession], _ => null);
            Assert.True(video.IsVideo);
            Assert.Equal((1280, 720), (video.Width, video.Height));
            Assert.Null(video.Thumbnail);
            Assert.Equal(drafts[videoSession].SavedUtc, video.SavedUtc);
        }
        finally { OwnedTestDirectory.Delete(root); }
    }

    // ---- Tutorials ------------------------------------------------------------------------

    [Fact]
    public void TutorialForAnUnassignedShortcutOpensItsSettingsRow() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        Action<string>? previous = FeatureTutorialWindow.OpenShortcutSetting;
        var opened = new List<string>();
        FeatureTutorialWindow.OpenShortcutSetting = opened.Add;
        try
        {
            HotkeySettings keys = NewInstallDefaults.Create().Hotkeys;
            Assert.True(FeatureTutorialWindow.OffersSetShortcut(TutorialCatalog.Get("pin-clipboard"), keys));
            Assert.False(FeatureTutorialWindow.OffersSetShortcut(TutorialCatalog.Get("capture-region"), keys));
            Assert.False(FeatureTutorialWindow.OffersSetShortcut(TutorialCatalog.Get("image-rectangle"), keys));

            var tutorial = new FeatureTutorialWindow(keys, "pin-clipboard", () => new FakeTutorialMedia())
            {
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false,
            };
            tutorial.Show();
            Assert.True(tutorial.SetShortcutButton.IsVisible || tutorial.SetShortcutButton.Parent is not null);
            Assert.NotNull(tutorial.SetShortcutButton.Parent);
            Assert.Equal("단축키 지정", tutorial.SetShortcutButton.Content);
            Assert.Equal("단축키 지정", AutomationProperties.GetName(tutorial.SetShortcutButton));
            Assert.Equal(UiText.Get("Tutorial.SetShortcutHelp"), AutomationProperties.GetHelpText(tutorial.SetShortcutButton));

            // Switching to a feature whose shortcut is assigned takes the button away.
            tutorial.SelectFeature("capture-region");
            Assert.Null(tutorial.SetShortcutButton.Parent);
            tutorial.SelectFeature("pin-hide-all");
            Assert.NotNull(tutorial.SetShortcutButton.Parent);

            bool closed = false;
            tutorial.Closed += (_, _) => closed = true;
            tutorial.SetShortcutButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.True(closed);
            Assert.Equal([nameof(SettingsDraft.HideAllPinsHotkey)], opened);
        }
        finally { FeatureTutorialWindow.OpenShortcutSetting = previous; }
    });

    [Theory]
    [InlineData("ko-KR", "영역 캡처 사용법 보기", "사용법 보기")]
    [InlineData("en-US", "Region capture — View tutorial", "View tutorial")]
    public void ViewTutorialButtonsNameTheFeatureAndContainTheirLabel(string locale, string region, string label)
    {
        using var language = UiText.UseLanguage(locale);
        Assert.Equal(label, UiText.Get("TextCapture_TutorialOpen"));
        Assert.Equal(region, FeatureTutorialWindow.OpenButtonName(TutorialCatalog.Get("capture-region")));
        foreach (FeatureTutorial feature in TutorialCatalog.All)
            Assert.Contains(label, FeatureTutorialWindow.OpenButtonName(feature), StringComparison.Ordinal);
        Assert.Equal(TutorialCatalog.All.Count, TutorialCatalog.All.Select(FeatureTutorialWindow.OpenButtonName).Distinct().Count());
        Assert.Equal(region, new MyCapture.App.Localization.TutorialNameExtension("capture-region").ProvideValue(null!));
    }

    // ---- Helpers --------------------------------------------------------------------------

    private static void Drain(Window window) =>
        window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));

    private static string? ResourceKey(DependencyObject element, DependencyProperty property) =>
        element.ReadLocalValue(property) is { } value && value.GetType().Name == "ResourceReferenceExpression"
            ? value.GetType().GetProperty("ResourceKey")?.GetValue(value) as string
            : null;

    private static BitmapSource Solid(int width, int height)
    {
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, new byte[width * height * 4], width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static byte[] Png(int width, int height)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(Solid(width, height)));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private sealed class FakeRegistration(bool applies) : IStartupRegistration
    {
        internal List<bool> Requests { get; } = [];
        internal bool Enabled { get; set; }

        public bool IsEnabled() => Enabled;

        public StartupApplyResult Apply(bool desiredEnabled)
        {
            Requests.Add(desiredEnabled);
            if (!applies) return StartupApplyResult.Fail(desiredEnabled, UiText.Get("Text_4D69B33A2FD7"));
            Enabled = desiredEnabled;
            return StartupApplyResult.Ok(desiredEnabled);
        }

        public bool ReconcileOnStartup(bool settingEnabled) => throw new InvalidOperationException("The welcome never reconciles.");
    }

    private sealed class FakeStartupTask(PackagedStartupState state, PackagedStartupState afterRequest) : IPackagedStartupTask
    {
        internal int EnableRequests { get; private set; }

        public PackagedStartupState GetState() => state;

        public PackagedStartupState RequestEnable()
        {
            EnableRequests++;
            state = afterRequest;
            return state;
        }

        public void Disable() => state = PackagedStartupState.Disabled;
    }
}
