using Xunit;

namespace MyCapture.App.Tests;

// Test-only native observations. This type neither queries Windows nor injects input.
internal sealed class EditorCaptionCompletionOracle(long owner, int x, int y, long startedTicks)
{
    public string? Error { get; private set; }
    public int DownCount { get; private set; }
    public int CommandCount { get; private set; }
    public int ReleaseCount { get; private set; }
    public long DownTicks { get; private set; }
    public long CommandTicks { get; private set; }
    public long ReleaseTicks { get; private set; }
    public long CallerReturnedTicks { get; private set; }
    public bool QueueDownObserved { get; private set; }
    private readonly long _startedTicks = startedTicks;
    private long _lastTicks = startedTicks;

    public void Fail(string reason) => Error ??= reason;

    public void Observe(long hwnd, int message, long wparam, long lparam, long ticks, bool queueLeftDown)
    {
        if (Error is not null) return;
        if (owner == 0 || hwnd != owner || ticks < _lastTicks) { Fail("Native owner/clock mismatch."); return; }
        _lastTicks = ticks;
        bool samePoint = unchecked((short)(lparam & 0xFFFF)) == x
            && unchecked((short)((lparam >> 16) & 0xFFFF)) == y;
        switch (message)
        {
            case 0x00A1: // WM_NCLBUTTONDOWN
                DownCount++;
                if (DownCount != 1 || CommandCount != 0 || wparam != 2 || !samePoint || !queueLeftDown)
                { Fail("Exact HTCAPTION queue-down was not observed once."); return; }
                QueueDownObserved = true;
                DownTicks = ticks;
                break;
            case 0x0112: // WM_SYSCOMMAND
                CommandCount++;
                if (DownCount != 1 || CommandCount != 1 || ReleaseCount != 0
                    || (wparam & 0xFFF0) != 0xF010 || !samePoint || ticks < DownTicks)
                { Fail("Exact SC_MOVE did not follow the owned caption down once."); return; }
                CommandTicks = ticks;
                break;
            case 0x0215: // WM_CAPTURECHANGED
                ReleaseCount++;
                if (CommandCount != 1 || ReleaseCount != 1 || lparam != 0 || ticks < CommandTicks)
                { Fail("Owned capture release did not follow SC_MOVE once."); return; }
                ReleaseTicks = ticks;
                break;
            case 0x001F: // WM_CANCELMODE
            case 0x0201: // An additional client down is not this caption gesture.
                Fail("Unexpected cancel/client input during caption completion.");
                break;
        }
    }

    // Called only after the existing synchronous Dispatcher.Invoke returns to Wait's caller.
    public void AfterDispatcherReturned(long ticks)
    {
        if (Error is not null) return;
        if (ticks < _lastTicks) { Fail("Dispatcher clock moved backwards."); return; }
        if (CommandCount == 1 && ReleaseCount == 1) CallerReturnedTicks = ticks;
    }

    public bool ReadyForState => Error is null && QueueDownObserved && DownCount == 1
        && CommandCount == 1 && ReleaseCount == 1 && CallerReturnedTicks >= ReleaseTicks
        && CallerReturnedTicks > _startedTicks;

    public bool IsComplete(bool captionAttempt, uint sent, CaptionCompletionState state) =>
        captionAttempt && sent == 3 && ReadyForState && state.Ticks >= CallerReturnedTicks
        && state.GuiRead && (state.GuiFlags & 0x1E) == 0
        && state.Active == owner && state.Focus == owner && state.Foreground == owner
        && state.Capture == 0 && state.MenuOwner == 0 && state.MoveSize == 0 && state.ThreadCapture == 0
        && state.ExactOwner && state.SameRect && !state.QueueLeftDown && state.AllNeutral
        && state.FlyoutClosed && state.ModelUnchanged;
}

internal readonly record struct CaptionCompletionState
{
    public long Ticks { get; init; }
    public bool GuiRead { get; init; }
    public uint GuiFlags { get; init; }
    public long Active { get; init; }
    public long Focus { get; init; }
    public long Foreground { get; init; }
    public long Capture { get; init; }
    public long MenuOwner { get; init; }
    public long MoveSize { get; init; }
    public long ThreadCapture { get; init; }
    public bool ExactOwner { get; init; }
    public bool SameRect { get; init; }
    public bool QueueLeftDown { get; init; }
    public bool AllNeutral { get; init; }
    public bool FlyoutClosed { get; init; }
    public bool ModelUnchanged { get; init; }
}

public sealed class EditorCaptionCompletionOracleTests
{
    private const long Owner = 23;
    private static long Point(int x, int y) => unchecked((uint)((y << 16) | (x & 0xFFFF)));
    private static EditorCaptionCompletionOracle New() => new(Owner, 415, 151, 100);
    private static CaptionCompletionState Terminal => new()
    {
        Ticks = 150, GuiRead = true, Active = Owner, Focus = Owner, Foreground = Owner,
        ExactOwner = true, SameRect = true, AllNeutral = true, FlyoutClosed = true, ModelUnchanged = true,
    };
    private static EditorCaptionCompletionOracle CompleteChain()
    {
        var oracle = New();
        oracle.Observe(Owner, 0xA1, 2, Point(415, 151), 110, true);
        oracle.Observe(Owner, 0x112, 0xF012, Point(415, 151), 120, false);
        oracle.Observe(Owner, 0x215, 0, 0, 130, false);
        return oracle;
    }

    [Fact]
    public void ExactOrderedChainRequiresCallerReturnAndQueueRelease()
    {
        var oracle = New();
        oracle.AfterDispatcherReturned(105); // A pre-command pump cannot satisfy completion.
        oracle.Observe(Owner, 0xA1, 2, Point(415, 151), 110, true);
        oracle.Observe(Owner, 0x112, 0xF012, Point(415, 151), 120, false);
        oracle.AfterDispatcherReturned(125); // Neither can a return before capture release.
        oracle.Observe(Owner, 0x215, 0, 0, 130, false);
        Assert.False(oracle.IsComplete(true, 3, Terminal));
        oracle.AfterDispatcherReturned(140);
        Assert.False(oracle.IsComplete(true, 3, Terminal with { QueueLeftDown = true }));
        Assert.True(oracle.IsComplete(true, 3, Terminal));
        Assert.True(oracle.IsComplete(true, 3, Terminal with { GuiFlags = 1 })); // Caret blink is not a modal state.
    }

    [Fact]
    public void MissingWrongDuplicateAndReorderedNativeEvidenceCannotComplete()
    {
        var down = (Owner, 0xA1, 2L, Point(415, 151), 110L, true);
        var move = (Owner, 0x112, 0xF012L, Point(415, 151), 120L, false);
        var release = (Owner, 0x215, 0L, 0L, 130L, false);
        (string Name, (long Hwnd, int Message, long Wparam, long Lparam, long Ticks, bool Down)[] Events)[] cases =
        [
            ("missing-down", [move, release]), ("missing-command", [down, release]),
            ("missing-release", [down, move]), ("release-before-command", [down, release, move]),
            ("duplicate-down", [down, down, move, release]), ("duplicate-command", [down, move, move, release]),
            ("duplicate-release", [down, move, release, release]),
            ("foreign-owner", [(99, 0xA1, 2, Point(415, 151), 110, true), move, release]),
            ("wrong-hit", [(Owner, 0xA1, 1, Point(415, 151), 110, true), move, release]),
            ("wrong-point", [(Owner, 0xA1, 2, Point(416, 151), 110, true), move, release]),
            ("no-queue-down", [(Owner, 0xA1, 2, Point(415, 151), 110, false), move, release]),
            ("wrong-command", [down, (Owner, 0x112, 0xF020, Point(415, 151), 120, false), release]),
            ("command-point", [down, (Owner, 0x112, 0xF012, Point(415, 152), 120, false), release]),
            ("capture-to-other", [down, move, (Owner, 0x215, 0, 99, 130, false)]),
            ("before-attempt", [(Owner, 0xA1, 2, Point(415, 151), 99, true), move, release]),
            ("cancel", [down, move, (Owner, 0x1F, 0, 0, 125, false), release]),
        ];
        foreach (var item in cases)
        {
            var oracle = New();
            foreach (var e in item.Events) oracle.Observe(e.Hwnd, e.Message, e.Wparam, e.Lparam, e.Ticks, e.Down);
            oracle.AfterDispatcherReturned(140);
            Assert.False(oracle.IsComplete(true, 3, Terminal), item.Name);
        }
    }

    [Fact]
    public void EveryTerminalSafetyBoundaryIsRequired()
    {
        var oracle = CompleteChain(); oracle.AfterDispatcherReturned(140);
        (string Name, CaptionCompletionState State)[] cases =
        [
            ("stale-sample", Terminal with { Ticks = 139 }), ("GUI-read", Terminal with { GuiRead = false }),
            ("movesize", Terminal with { GuiFlags = 2 }), ("menu", Terminal with { GuiFlags = 4 }),
            ("system-menu", Terminal with { GuiFlags = 8 }), ("popup-menu", Terminal with { GuiFlags = 16 }),
            ("active", Terminal with { Active = 99 }), ("focus", Terminal with { Focus = 99 }),
            ("foreground", Terminal with { Foreground = 99 }), ("capture", Terminal with { Capture = Owner }),
            ("menu-owner", Terminal with { MenuOwner = Owner }), ("move-owner", Terminal with { MoveSize = Owner }),
            ("thread-capture", Terminal with { ThreadCapture = Owner }), ("PID-STA", Terminal with { ExactOwner = false }),
            ("rect", Terminal with { SameRect = false }), ("queue-down", Terminal with { QueueLeftDown = true }),
            ("neutral", Terminal with { AllNeutral = false }), ("flyout", Terminal with { FlyoutClosed = false }),
            ("model", Terminal with { ModelUnchanged = false }),
        ];
        foreach (var item in cases) Assert.False(oracle.IsComplete(true, 3, item.State), item.Name);
    }

    [Fact]
    public void ClientAndPartialInputCannotUseTheCaptionAlternative()
    {
        var oracle = CompleteChain(); oracle.AfterDispatcherReturned(140);
        Assert.False(oracle.IsComplete(false, 3, Terminal));
        foreach (uint sent in new uint[] { 0, 1, 2, 4 }) Assert.False(oracle.IsComplete(true, sent, Terminal));
    }

    [Fact]
    public void ObservationErrorRemainsFailClosedAfterOtherwiseValidEvidence()
    {
        var oracle = CompleteChain(); oracle.Fail("native read/overflow"); oracle.AfterDispatcherReturned(140);
        Assert.False(oracle.IsComplete(true, 3, Terminal));
        Assert.Equal("native read/overflow", oracle.Error);
        var reverse = CompleteChain(); reverse.AfterDispatcherReturned(129);
        Assert.False(reverse.IsComplete(true, 3, Terminal));
    }

    [Fact]
    public void SignedNegativeMonitorCoordinatesMatchExactly()
    {
        var oracle = new EditorCaptionCompletionOracle(Owner, -415, -151, 100);
        oracle.Observe(Owner, 0xA1, 2, Point(-415, -151), 110, true);
        oracle.Observe(Owner, 0x112, 0xF012, Point(-415, -151), 120, false);
        oracle.Observe(Owner, 0x215, 0, 0, 130, false); oracle.AfterDispatcherReturned(140);
        Assert.True(oracle.IsComplete(true, 3, Terminal));
        var wrong = new EditorCaptionCompletionOracle(Owner, -415, -151, 100);
        wrong.Observe(Owner, 0xA1, 2, Point(415, -151), 110, true);
        Assert.NotNull(wrong.Error);
    }
}
