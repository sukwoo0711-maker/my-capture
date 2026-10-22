using System.ComponentModel;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Settings;
using MyCapture.App.Store;
using MyCapture.App.Updates;
using MyCapture.Core.Localization;
using MyCapture.Core.Settings;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// The update tab observes Store deployment and retains its listing fallback. No test starts a process: every
/// <see cref="StoreUpdateLink"/> gets a recording opener.
/// </summary>
public sealed class StoreUpdateTests
{
    [Fact]
    public void AutomaticUpdatePreference_IsIndependentAndIncludedInDraftChanges()
    {
        var settings = new AppSettings
        {
            Updates = { LastCheckUtc = DateTimeOffset.UnixEpoch, DismissedVersion = "4.2.0" },
        };
        var draft = new SettingsDraft(settings);
        Assert.True(draft.AutoCheckUpdates);
        Assert.False(draft.HasUnappliedChanges);
        draft.AutoCheckUpdates = false;
        Assert.True(draft.HasUnappliedChanges);
        Assert.True(settings.Updates.AutoCheck);
        AppSettings candidate = draft.ToAppSettings();
        Assert.False(candidate.Updates.AutoCheck);
        Assert.Equal(settings.Updates.LastCheckUtc, candidate.Updates.LastCheckUtc);
        Assert.Equal(settings.Updates.DismissedVersion, candidate.Updates.DismissedVersion);
        draft.LoadFrom(candidate);
        Assert.False(draft.HasUnappliedChanges);
        draft.ResetToDefaults();
        Assert.True(draft.AutoCheckUpdates);
        Assert.True(draft.HasUnappliedChanges);
    }

    [Theory]
    [InlineData("ko-KR")]
    [InlineData("en-US")]
    public void EveryObservedPhaseHasAnExplicitLocalizedStatus(string language)
    {
        using var scope = UiText.UseLanguage(language);
        StoreUpdatePhase[] phases = Enum.GetValues<StoreUpdatePhase>();
        string[] labels = phases.Select(SettingsWindow.StoreUpdateStatusText).ToArray();
        Assert.All(labels, label => Assert.False(string.IsNullOrWhiteSpace(label)));
        Assert.Equal(phases.Length, labels.Distinct(StringComparer.Ordinal).Count());
        Assert.Throws<ArgumentOutOfRangeException>(() => SettingsWindow.StoreUpdateStatusText((StoreUpdatePhase)int.MaxValue));
    }

    [Fact]
    public void Links_PointAtTheMyCaptureListing()
    {
        Assert.Equal("ms-windows-store://pdp/?productid=9PB33MC095VR", StoreUpdateLink.StoreAppUri);
        Assert.Equal("https://apps.microsoft.com/detail/9PB33MC095VR", StoreUpdateLink.WebUri);
    }

    [Fact]
    public void Open_TriesTheStoreAppOnceAndStops()
    {
        var opened = new List<string>();
        var link = new StoreUpdateLink(opened.Add);

        Assert.Equal(StoreUpdateLink.StoreAppUri, link.Open(NullLogger.Instance));
        Assert.Equal(new[] { StoreUpdateLink.StoreAppUri }, opened);
    }

    [Theory]
    [InlineData(typeof(Win32Exception))]
    [InlineData(typeof(InvalidOperationException))]
    public void Open_FallsBackToTheWebListing(Type failure)
    {
        var opened = new List<string>();
        var link = new StoreUpdateLink(uri =>
        {
            opened.Add(uri);
            if (uri == StoreUpdateLink.StoreAppUri) throw (Exception)Activator.CreateInstance(failure)!;
        });

        Assert.Equal(StoreUpdateLink.WebUri, link.Open(NullLogger.Instance));
        Assert.Equal(new[] { StoreUpdateLink.StoreAppUri, StoreUpdateLink.WebUri }, opened);
    }

    [Fact]
    public void Open_ReturnsNullWhenNeitherLinkOpens()
    {
        var opened = new List<string>();
        var link = new StoreUpdateLink(uri =>
        {
            opened.Add(uri);
            throw new Win32Exception(2);
        });

        Assert.Null(link.Open(NullLogger.Instance));
        Assert.Equal(new[] { StoreUpdateLink.StoreAppUri, StoreUpdateLink.WebUri }, opened);
    }

    [Fact]
    public void Open_DoesNotHideUnexpectedFailures()
    {
        var link = new StoreUpdateLink(_ => throw new ArgumentException("unexpected"));
        Assert.Throws<ArgumentException>(() => link.Open(NullLogger.Instance));
        Assert.Throws<ArgumentNullException>(() => new StoreUpdateLink(null!));
    }

    [Theory]
    [InlineData(typeof(SettingsWindow))]
    [InlineData(typeof(App))]
    public void SettingsWindowAndApp_UseSharedUpdatesAndKeepLegacyInstallControlsRemoved(Type owner)
    {
        const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public
            | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        // Both shell surfaces hold the same coordinator contract, so an install cannot be
        // started independently by the settings tab and the engagement card.
        Assert.Contains(owner.GetFields(all), field => field.FieldType == typeof(AppUpdateCoordinator));
        foreach (string removed in new[] { "CanExitForUpdate", "ExitForUpdate" })
            Assert.Null(typeof(SettingsWindow).GetProperty(removed, all));
        foreach (string removed in new[] { "InstallStagedUpdateAsync", "OnUpdateCancel", "ResetUpdateAction" })
            Assert.Null(typeof(SettingsWindow).GetMethod(removed, all));
    }

}
