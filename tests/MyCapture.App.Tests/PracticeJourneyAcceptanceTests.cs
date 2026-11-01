using System.Windows;
using System.Windows.Controls;
using MyCapture.Core.Settings;
using Xunit;

namespace MyCapture.App.Tests;

internal static class PracticeJourneyDriver
{
    internal static readonly Guid Record = Guid.Parse("11111111-1111-1111-1111-111111111111");
    internal static readonly Guid Editor = Guid.Parse("22222222-2222-2222-2222-222222222222");
    internal static readonly Guid Logical = Guid.Parse("33333333-3333-3333-3333-333333333333");
    internal static readonly Guid Pin = Guid.Parse("44444444-4444-4444-4444-444444444444");
    internal static readonly Guid Restored = Guid.Parse("55555555-5555-5555-5555-555555555555");
    internal static PracticeEvent Signal(PracticeSession session) => new(session.Step switch
    {
        PracticeStep.A2 => PracticeEventKind.CaptureSaved, PracticeStep.A3 => PracticeEventKind.EditorReady,
        PracticeStep.A4 => PracticeEventKind.ArrowSelected, PracticeStep.A5 => PracticeEventKind.ArrowCommitted,
        PracticeStep.A6 => PracticeEventKind.EditorCopiedAndClosed, PracticeStep.B0 => PracticeEventKind.PictureCopied,
        PracticeStep.B1 => PracticeEventKind.PinOpened, PracticeStep.B2 => PracticeEventKind.PinResized,
        PracticeStep.B3 => PracticeEventKind.OpacityMenuOpened, PracticeStep.B4 => PracticeEventKind.OpacityChanged,
        PracticeStep.B5 => PracticeEventKind.PinClosed, PracticeStep.B6 => PracticeEventKind.PinRestored,
        PracticeStep.C1 => PracticeEventKind.LibraryReady, PracticeStep.C2 => PracticeEventKind.LibraryEditorOpened,
        _ => throw new InvalidOperationException(),
    }, session.Id, session.CaptureRequestId, Record, Editor, Logical,
        session.Step == PracticeStep.B6 ? Restored : Pin, 42, .6, true);
    internal static void Complete(PracticeSession session)
    {
        if (session.Step == PracticeStep.A1)
        {
            Guid request = session.BeginCapture();
            Assert.True(session.Accept(new(PracticeEventKind.SelectorReady, session.Id, request)));
        }
        else Assert.True(session.Accept(Signal(session)));
    }
    internal static void AdvanceTo(PracticeSession session, PracticeStep target)
    {
        for (int i = 0; i < 20 && session.Step != target; i++) Complete(session);
        Assert.Equal(target, session.Step);
    }
    internal static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
}

public sealed class PracticeJourneyAcceptanceTests
{
    [Fact]
    public void ClosingTheOpacityMenuWithoutChoosingReturnsToItsAction()
    {
        var session = new PracticeSession();
        PracticeJourneyDriver.AdvanceTo(session, PracticeStep.B4);
        var closed = PracticeJourneyDriver.Signal(session) with { Kind = PracticeEventKind.OpacityMenuClosed };
        Assert.True(session.Accept(closed));
        Assert.Equal(PracticeStep.B3, session.Step); Assert.Null(session.LastCompleted);
        Assert.Equal(PracticeStepResult.Pending, session.Results[PracticeStep.B4]);
        PracticeJourneyDriver.Complete(session); PracticeJourneyDriver.Complete(session);
        Assert.False(session.Accept(closed));
        Assert.Equal(PracticeStep.B5, session.Step);
    }

    [Fact]
    public void LateCloseEncodingCannotDetachAnAlreadyRestoredPin()
    {
        var session = new PracticeSession();
        PracticeJourneyDriver.AdvanceTo(session, PracticeStep.B2);
        var closing = new PracticeEvent(PracticeEventKind.PinClosing, session.Id,
            LogicalPinId: PracticeJourneyDriver.Logical, WindowPinId: PracticeJourneyDriver.Pin);
        Assert.True(session.Accept(closing));
        Assert.True(session.PinRestorePending);
        Assert.False(session.Accept(PracticeJourneyDriver.Signal(session)));
        Assert.True(session.Accept(closing with { Kind = PracticeEventKind.PinRestored, WindowPinId = PracticeJourneyDriver.Restored }));
        Assert.False(session.Accept(closing with { Kind = PracticeEventKind.PinClosed, Restorable = true }));
        Assert.Equal(PracticeJourneyDriver.Restored, session.WindowPinId);
        Assert.False(session.PinRestorePending);
        Assert.Equal(PracticeStep.B2, session.Step);
    }

    [Fact]
    public void MissingRestoreOrDeletedCaptureOffersPreparationWithoutFalseSuccess() => StaTestHost.Run(() =>
    {
        var guide = new WelcomeWindow(new HotkeySettings(), AfterCaptureAction.Editor, options: new WelcomeOptions { ReducedMotion = true });
        try
        {
            PracticeJourneyDriver.AdvanceTo(guide.Session, PracticeStep.B6);
            guide.Session.CannotRestorePin();
            Assert.False(guide.SuccessShown);
            PracticeJourneyDriver.Click(guide.TryNowButton);
            Assert.Equal(PracticeStep.B0, guide.Session.Step);
            guide.Session.StartGroup(PracticeStep.C1);
            PracticeJourneyDriver.Complete(guide.Session);
            guide.Session.ForgetCapture();
            Assert.False(guide.SuccessShown);
            Assert.False(guide.Session.Accept(PracticeJourneyDriver.Signal(guide.Session)));
            PracticeJourneyDriver.Click(guide.TryNowButton);
            Assert.Equal(PracticeStep.A1, guide.Session.Step);
        }
        finally { guide.Close(); }
    });

    [Fact]
    public void EveryStepRequiresItsResultAndRejectsPreviousSessions()
    {
        var session = new PracticeSession();
        Assert.False(session.Accept(new(PracticeEventKind.SelectorReady, session.Id, Guid.NewGuid())));
        PracticeJourneyDriver.Complete(session);
        while (session.Step != PracticeStep.Results)
        {
            PracticeStep step = session.Step;
            PracticeEvent signal = PracticeJourneyDriver.Signal(session);
            Assert.False(session.Accept(signal with { SessionId = Guid.NewGuid() }));
            Assert.Equal(step, session.Step);
            Assert.NotEqual(PracticeStepResult.Completed, session.Results[step]);
            Assert.True(session.Accept(signal));
            Assert.Equal(PracticeStepResult.Completed, session.Results[step]);
            Assert.Equal(PracticeSession.Next(step), session.Step);
        }
        Assert.All(session.Results.Values, result => Assert.Equal(PracticeStepResult.Completed, result));
    }

    [Fact]
    public void WrongRequestRecordEditorPinCopyOrOpacityDoesNotComplete()
    {
        foreach (PracticeStep step in PracticeSession.Steps.Where(step => step is not (PracticeStep.A1 or PracticeStep.B0 or PracticeStep.C1)))
        {
            var session = new PracticeSession();
            PracticeJourneyDriver.AdvanceTo(session, step);
            PracticeEvent signal = PracticeJourneyDriver.Signal(session);
            PracticeEvent wrong = step switch
            {
                PracticeStep.A2 => signal with { RequestId = Guid.NewGuid() },
                PracticeStep.A3 or PracticeStep.C2 => signal with { RecordId = Guid.NewGuid() },
                PracticeStep.A4 or PracticeStep.A5 or PracticeStep.A6 => signal with { EditorId = Guid.NewGuid() },
                PracticeStep.B1 => signal with { ClipboardSequence = 41 },
                PracticeStep.B6 => signal with { LogicalPinId = Guid.NewGuid() },
                _ => signal with { WindowPinId = Guid.NewGuid() },
            };
            Assert.False(session.Accept(wrong));
            Assert.Equal(step, session.Step);
            if (step == PracticeStep.B4)
                Assert.False(session.Accept(signal with { Value = .8 }));
            if (step is PracticeStep.A4 or PracticeStep.A5 or PracticeStep.A6)
                Assert.False(session.Accept(signal with { RecordId = Guid.NewGuid() }));
            Assert.True(session.Accept(signal));
        }
    }

    [Fact]
    public void CancelSkipBackAndEndKeepResultsHonest()
    {
        var session = new PracticeSession();
        Guid oldRequest = session.BeginCapture();
        Assert.True(session.Accept(new(PracticeEventKind.CaptureCancelled, session.Id, oldRequest)));
        Assert.Null(session.LastCompleted);
        Assert.Equal(PracticeStepResult.Pending, session.Results[PracticeStep.A2]);
        Guid request = session.BeginCapture();
        Assert.False(session.Accept(new(PracticeEventKind.CaptureSaved, session.Id, oldRequest, PracticeJourneyDriver.Record)));
        session.Skip();
        Assert.Equal(PracticeStepResult.Skipped, session.Results[PracticeStep.A2]);
        Assert.False(session.Accept(new(PracticeEventKind.CaptureSaved, session.Id, request, PracticeJourneyDriver.Record)));
        session.Back();
        Assert.Equal(PracticeStep.A2, session.Step);
        Assert.False(session.CapturePending);
        Assert.Null(session.CaptureId);
        session.Back(); session.Back();
        Assert.Equal(PracticeStep.Groups, session.Step);
        session.StartGroup(PracticeStep.B0);
        session.End();
        Assert.False(session.Accept(new(PracticeEventKind.PictureCopied, session.Id, ClipboardSequence: 42)));
        Assert.Equal(0u, session.ClipboardSequence);
    }

    [Theory]
    [InlineData(PracticeStep.B2)]
    [InlineData(PracticeStep.B3)]
    [InlineData(PracticeStep.B4)]
    public void ReopenedPinContinuesTheSameStepAndRejectsAnotherPin(object target)
    {
        var session = new PracticeSession();
        PracticeJourneyDriver.AdvanceTo(session, (PracticeStep)target);
        Assert.True(session.Accept(new(PracticeEventKind.PinClosed, session.Id, LogicalPinId: PracticeJourneyDriver.Logical, WindowPinId: PracticeJourneyDriver.Pin, Restorable: true)));
        Assert.Null(session.WindowPinId);
        Assert.False(session.Accept(new(PracticeEventKind.PinOpened, session.Id, LogicalPinId: PracticeJourneyDriver.Logical, WindowPinId: Guid.NewGuid(), ClipboardSequence: 42)));
        Assert.True(session.Accept(new(PracticeEventKind.PinRestored, session.Id, LogicalPinId: PracticeJourneyDriver.Logical, WindowPinId: PracticeJourneyDriver.Restored)));
        Assert.Equal((PracticeStep)target, session.Step);
        Assert.False(session.Accept(PracticeJourneyDriver.Signal(session)));
        Assert.True(session.Accept(PracticeJourneyDriver.Signal(session) with { WindowPinId = PracticeJourneyDriver.Restored }));
    }

    [Fact]
    public void ClipboardReplacementInvalidatesTheSampleUntilExplicitCopy()
    {
        var session = new PracticeSession(true);
        PracticeJourneyDriver.Complete(session);
        Assert.True(session.Accept(new(PracticeEventKind.ClipboardChanged, session.Id, ClipboardSequence: 43)));
        Assert.False(session.Accept(PracticeJourneyDriver.Signal(session)));
        session.Back();
        Assert.Equal(0u, session.ClipboardSequence);
        Assert.True(session.Accept(new(PracticeEventKind.PictureCopied, session.Id, ClipboardSequence: 44)));
        Assert.True(session.Accept(PracticeJourneyDriver.Signal(session) with { ClipboardSequence = 44 }));
    }

    [Fact]
    public void CancelAndFailedCopyShowNoCheckAndBackDoesNotCopy() => StaTestHost.Run(() =>
    {
        int copies = 0;
        var guide = new WelcomeWindow(new HotkeySettings(), AfterCaptureAction.Editor,
            options: new WelcomeOptions { ReducedMotion = true, CopyPracticePicture = () => { copies++; return 0; } });
        try
        {
            guide.DismissForCapture(); guide.NotifyCaptureCancelled();
            Assert.False(guide.SuccessShown);
            guide.Session.StartGroup(PracticeStep.B0);
            PracticeJourneyDriver.Click(guide.TryNowButton);
            Assert.Equal(1, copies); Assert.False(guide.SuccessShown);
            Assert.Equal(PracticeStep.B0, guide.Session.Step);
            PracticeJourneyDriver.Click(guide.BackButton);
            Assert.Equal(1, copies);
            Assert.Equal(PracticeStep.A6, guide.Session.Step);
            PracticeJourneyDriver.Click(guide.SkipButton);
            Assert.True(guide.Session.Ended);
        }
        finally { guide.Close(); }
    });
}
