using MyCapture.Core.Settings;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class PracticeOfferTests
{
    [Theory]
    [InlineData(true, true, true, 0, false)]
    [InlineData(false, false, true, 0, false)]
    [InlineData(false, true, false, 0, false)]
    [InlineData(false, true, true, 0, true)]
    [InlineData(false, true, true, 1, false)]
    [InlineData(false, true, true, 2, false)]
    public void OffersOnlyAnsweredUpdatedInstallsAtIdleOncePerRevision(bool firstRun, bool answered,
        bool idle, int revision, bool expected)
    {
        var settings = new AppSettings { Hotkeys = { CoreShortcutsAsked = answered }, General = { PracticeRevisionOffered = revision } };
        int shows = 0, saves = 0;
        Assert.Equal(expected, PracticeOffer.TryShow(settings, firstRun, idle, () => { shows++; return true; }, () => saves++));
        Assert.Equal(expected ? 1 : 0, shows);
        Assert.Equal(expected ? 1 : 0, saves);
        Assert.False(PracticeOffer.TryShow(settings, firstRun, idle, () => throw new InvalidOperationException(), () => saves++));
    }

    [Fact]
    public void QuietLaunchWaitsForIdleAndSuppressedPresentationDoesNotConsumeOffer()
    {
        var settings = new AppSettings { Hotkeys = { CoreShortcutsAsked = true } };
        Assert.False(PracticeOffer.TryShow(settings, false, false, () => throw new InvalidOperationException(), () => { }));
        Assert.False(PracticeOffer.TryShow(settings, false, true, () => false, () => throw new InvalidOperationException()));
        Assert.Equal(0, settings.General.PracticeRevisionOffered);
        Assert.True(PracticeOffer.TryShow(settings, false, true, () => true, () => { }));
        Assert.Equal(PracticeOffer.CurrentRevision, settings.General.PracticeRevisionOffered);
    }

    [Fact]
    public void FullPracticePreventsTheUpdateOfferOnLaterLaunches()
    {
        var settings = new AppSettings { Hotkeys = { CoreShortcutsAsked = true } };
        PracticeOffer.MarkPresented(settings, () => { });
        Assert.False(PracticeOffer.IsEligible(settings, firstRun: false));
    }

    [Fact]
    public void FailedPersistenceRestoresThePreviousRevision()
    {
        var settings = new AppSettings { Hotkeys = { CoreShortcutsAsked = true } };
        Assert.Throws<InvalidOperationException>(() => PracticeOffer.MarkPresented(settings,
            () => throw new InvalidOperationException("Synthetic save failure")));
        Assert.Equal(0, settings.General.PracticeRevisionOffered);
    }
}
