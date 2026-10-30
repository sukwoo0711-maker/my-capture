using System.Reflection;
using System.Runtime.CompilerServices;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Pinning;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class PracticeShellTests
{
    [Fact]
    public void PracticePinLifecycleReconnectsReplacementAndUnsubscribesOnClose() => StaTestHost.Run(() =>
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var app = (App)RuntimeHelpers.GetUninitializedObject(typeof(App));
        typeof(App).GetField("_settings", flags)!.SetValue(app, new AppSettings());
        var guide = (WelcomeWindow)typeof(App).GetMethod("CreatePracticeWindow", flags)!.Invoke(app, [true, false, true])!;
        try
        {
            PracticeJourneyDriver.AdvanceTo(guide.Session, PracticeStep.B2);
            PracticeSignals.Publish(new(PracticeEventKind.PinClosed, Guid.Empty, LogicalPinId: PracticeJourneyDriver.Logical, WindowPinId: PracticeJourneyDriver.Pin, Restorable: true));
            Assert.Null(guide.Session.WindowPinId);
            PracticeSignals.Publish(new(PracticeEventKind.PinRestored, Guid.Empty, LogicalPinId: Guid.NewGuid(), WindowPinId: Guid.NewGuid()));
            Assert.Null(guide.Session.WindowPinId);
            PracticeSignals.Publish(new(PracticeEventKind.PinRestored, Guid.Empty, LogicalPinId: PracticeJourneyDriver.Logical, WindowPinId: PracticeJourneyDriver.Restored));
            Assert.Equal(PracticeJourneyDriver.Restored, guide.Session.WindowPinId);
            PracticeSignals.Publish(new(PracticeEventKind.PinResized, Guid.Empty, LogicalPinId: PracticeJourneyDriver.Logical, WindowPinId: PracticeJourneyDriver.Pin));
            Assert.Equal(PracticeStep.B2, guide.Session.Step);
            PracticeSignals.Publish(new(PracticeEventKind.PinResized, Guid.Empty, LogicalPinId: PracticeJourneyDriver.Logical, WindowPinId: PracticeJourneyDriver.Restored));
            Assert.Equal(PracticeStep.B3, guide.Session.Step);
        }
        finally { guide.Close(); }
        Assert.Null(typeof(PracticeSignals).GetField("Result", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null));
        Assert.Null(typeof(PracticeSignals).GetField("EditorReady", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null));
        Assert.Null(typeof(PracticeSignals).GetField("PictureOpened", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null));
        Assert.Null(typeof(PracticeSignals).GetField("LibraryEditorOpened", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null));
    });

    [Fact]
    public void ExitingUnansweredConsentDoesNotCreateASettingsFileOrConsumeFirstInstall() => StaTestHost.Run(() =>
    {
        string root = OwnedTestDirectory.Create("practice-unanswered-");
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var app = (App)RuntimeHelpers.GetUninitializedObject(typeof(App));
        WelcomeWindow? guide = null;
        try
        {
            var paths = AppPaths.CreateForRoot(root);
            var store = new SettingsStore(paths, NullLogger<SettingsStore>.Instance);
            using ServiceProvider services = new ServiceCollection().AddSingleton(store).BuildServiceProvider();
            AppSettings settings = store.Load();
            Assert.True(settings.General.IsFirstRun);
            typeof(App).GetField("_settings", flags)!.SetValue(app, settings);
            typeof(App).GetField("_services", flags)!.SetValue(app, services);
            typeof(App).GetField("_launchedFirstRun", flags)!.SetValue(app, true);
            typeof(App).GetMethod("ShowLaunchPresentation", flags)!.Invoke(app, [true, false, false]);
            guide = (WelcomeWindow)typeof(App).GetField("_onboardingWindow", flags)!.GetValue(app)!;
            Assert.Equal(WelcomeStep.Consent, guide.CurrentStep);
            Assert.False(File.Exists(paths.SettingsFile));
            typeof(App).GetField("_shutdownRequested", flags)!.SetValue(app, true);
            guide.Close();
            Assert.False(File.Exists(paths.SettingsFile));
            Assert.True(store.Load().General.IsFirstRun);
            Assert.False(settings.Hotkeys.CoreShortcutsAsked);
            Assert.Equal(0, settings.General.PracticeRevisionOffered);
        }
        finally
        {
            typeof(App).GetField("_shutdownRequested", flags)!.SetValue(app, true);
            guide?.Close();
            OwnedTestDirectory.Delete(root);
        }
    });

    [Theory]
    [InlineData(true, WelcomeStep.Consent, false, PracticeCloseRoute.ContinueWelcome)]
    [InlineData(true, WelcomeStep.Consent, true, PracticeCloseRoute.Ignore)]
    [InlineData(true, WelcomeStep.Capture, true, PracticeCloseRoute.CompleteFirstRunAtExit)]
    [InlineData(true, WelcomeStep.Draw, true, PracticeCloseRoute.CompleteFirstRunAtExit)]
    [InlineData(true, WelcomeStep.Finish, true, PracticeCloseRoute.CompleteFirstRunAtExit)]
    [InlineData(false, WelcomeStep.Consent, true, PracticeCloseRoute.Ignore)]
    [InlineData(false, WelcomeStep.Capture, true, PracticeCloseRoute.Ignore)]
    [InlineData(false, WelcomeStep.Consent, false, PracticeCloseRoute.Normal)]
    [InlineData(true, WelcomeStep.Capture, false, PracticeCloseRoute.Normal)]
    [InlineData(true, WelcomeStep.Finish, false, PracticeCloseRoute.Normal)]
    public void ConsentCloseAndAppExitKeepTheExistingFirstRunOutcomes(bool firstRun, object step, bool shutdown, object route) =>
        Assert.Equal((PracticeCloseRoute)route, FirstRunPlan.AfterPracticeClose(firstRun, (WelcomeStep)step, shutdown));

    [Fact]
    public void TrayReplayReplacesAnOpenFeaturePracticeWithTheFullPractice() => StaTestHost.Run(() =>
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var app = (App)RuntimeHelpers.GetUninitializedObject(typeof(App));
        typeof(App).GetField("_settings", flags)!.SetValue(app, new AppSettings());
        var feature = (WelcomeWindow)typeof(App).GetMethod("CreatePracticeWindow", flags)!.Invoke(app, [true, false, true])!;
        WelcomeWindow? replay = null;
        try
        {
            Assert.True(feature.IsFeaturePractice);
            typeof(App).GetMethod("ShowOnboardingAgain", flags)!.Invoke(app, null);
            replay = (WelcomeWindow)typeof(App).GetField("_onboardingWindow", flags)!.GetValue(app)!;
            Assert.NotSame(feature, replay);
            Assert.False(replay.IsFeaturePractice);
            Assert.True(replay.IsReplay);
            Assert.Equal(WelcomeStep.Capture, replay.CurrentStep);
        }
        finally { replay?.Close(); feature.Close(); }
    });

    [Fact]
    public void LaterCapturesDoNotReplaceTheImageBeingPractised() => StaTestHost.Run(() =>
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var app = (App)RuntimeHelpers.GetUninitializedObject(typeof(App));
        typeof(App).GetField("_settings", flags)!.SetValue(app, new AppSettings());
        var guide = (WelcomeWindow)typeof(App).GetMethod("CreateOnboardingWindow", flags)!.Invoke(app, [true])!;
        try
        {
            PracticeJourneyDriver.AdvanceTo(guide.Session, PracticeStep.A3);
            PracticeSignals.Publish(new(PracticeEventKind.CaptureSaved, guide.Session.Id, Guid.NewGuid(), Guid.NewGuid()));
            Assert.Equal(PracticeJourneyDriver.Record, guide.Session.CaptureId);
            Assert.Equal(PracticeStep.A3, guide.Session.Step);
        }
        finally { guide.Close(); }
    });

    [Fact]
    public void ResidentProbeNeverOffersPractice()
    {
        var settings = new AppSettings();
        App.ApplyResidentProbeSettings(settings);
        Assert.False(PracticeOffer.IsEligible(settings, firstRun: false));
    }
}
