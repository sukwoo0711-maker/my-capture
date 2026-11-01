using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using MyCapture.App.Capture;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using MyCapture.Core.Themes;
using MyCapture.Platform.Display;
using MyCapture.Platform.Shell;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>Offscreen contract checks: visual motion never delays practice state or owns an idle clock.</summary>
public sealed class PracticeMotionTests
{
    [Theory]
    [InlineData(1366, 728, 96u)]
    [InlineData(1920, 1032, 120u)]
    public void ReducedMotionChangesTheStepImmediatelyWithoutOverlaysOffsetsOrClocks(int width, int height, uint dpi) => StaTestHost.Run(() =>
    {
        using var fixture = new Fixture(reducedMotion: true, width, height, dpi,
            prepare: guide => guide.Session.StartGroup(PracticeStep.B0));
        WelcomeWindow guide = fixture.Guide;
        PracticeJourneyDriver.Complete(guide.Session);

        Assert.Equal(PracticeStep.B1, guide.Session.Step);
        Assert.Equal(UiText.Get("Journey.B1.Title"), guide.TitleText);
        Assert.Equal(UiText.Get("Journey.B0.Success"), guide.StatusText);
        Assert.True(guide.SuccessShown);
        Assert.Null(guide.TransitionOverlay);
        AssertContentAtRest(guide);
        AssertAtRest(guide.SuccessChip);
        Assert.All(guide.ProgressFills, AssertAtRest);
        Assert.All(FirstRunWelcomeTests.Descendants(guide.SuccessChip).OfType<SymbolIcon>(), icon => Assert.False(StrokeDrawMotion.IsDrawing(icon)));

        guide.NotifyHotkeyFired(GlobalHotkeyCommand.PasteToScreen);
        Assert.True(guide.KeyPressed);
        Assert.Null(guide.TransitionOverlay);
        AssertContentAtRest(guide);
        Assert.All(Keycaps(guide), AssertAtRest);
        Assert.All(FirstRunWelcomeTests.Descendants(guide).OfType<SymbolIcon>(), icon => Assert.False(StrokeDrawMotion.IsDrawing(icon)));
    });

    [Fact]
    public void StepStateAndAccessibilityChangeBeforeTheExitOverlayFinishes() => StaTestHost.Run(() =>
    {
        using var fixture = new Fixture(reducedMotion: false, prepare: guide => guide.Session.StartGroup(PracticeStep.B0));
        WelcomeWindow guide = fixture.Guide;
        PracticeJourneyDriver.Complete(guide.Session);

        Assert.Equal(UiText.Get("Journey.B1.Title"), guide.TitleText);
        Assert.Equal(UiText.Format("Journey.B1.Body", new HotkeySettings().PasteToScreen), guide.InstructionText);
        Assert.Equal(UiText.Get("Journey.B0.Success"), guide.StatusText);
        Assert.Equal("8 / 15 · " + UiText.Get("Journey.PinGroup"), guide.ProgressText);
        Assert.True(guide.SuccessShown);
        Assert.Equal(UiText.Get("Journey.ShowPicture"), guide.TryNowButton.Content);
        Assert.Equal(Visibility.Visible, guide.TryNowButton.Visibility);
        Assert.True(guide.TryNowButton.IsEnabled && guide.TryNowButton.IsDefault && guide.TryNowButton.IsTabStop);
        Assert.Equal(guide.TryNowButton.Content, AutomationProperties.GetName(guide.TryNowButton));
        Assert.Equal(guide.InstructionText, AutomationProperties.GetName(guide.InstructionBlock));

        Image overlay = Assert.IsType<Image>(guide.TransitionOverlay);
        Assert.NotNull(overlay.Source);
        Assert.False(overlay.IsHitTestVisible);
        Assert.Contains(guide.MotionParts, HasMotion);
        Assert.All(guide.MotionParts.Where(part => part.Visibility == Visibility.Visible), part => Assert.Equal(0, part.Opacity));
        PollUntil(() => guide.TransitionOverlay is null && ContentAtRest(guide)
            && AtRest(guide.SuccessChip) && guide.ProgressFills.All(AtRest), "The completed transition must release every clock.");
        Assert.Null(overlay.Source);
        AssertContentAtRest(guide);
        Assert.All(guide.ProgressFills, AssertAtRest);
    });

    [Fact]
    public void ANewPageCancelsTheOldOverlayAndSettlesAtTheNewestState() => StaTestHost.Run(() =>
    {
        using var fixture = new Fixture(reducedMotion: false, prepare: guide => guide.Session.StartGroup(PracticeStep.B0));
        WelcomeWindow guide = fixture.Guide;
        PracticeJourneyDriver.Complete(guide.Session);
        Image first = Assert.IsType<Image>(guide.TransitionOverlay);
        guide.Session.Back();
        Image second = Assert.IsType<Image>(guide.TransitionOverlay);
        Assert.NotSame(first, second);
        Assert.Null(first.Source);
        Assert.Equal(PracticeStep.B0, guide.Session.Step);
        Assert.Equal(UiText.Get("Journey.B0.Title"), guide.TitleText);
        Assert.False(guide.SuccessShown);
        Assert.Empty(guide.StatusText);
        Assert.True(guide.MotionParts.First(part => part.Visibility == Visibility.Visible).RenderTransform.Value.OffsetX < 0);

        guide.Session.StartGroup(PracticeStep.C1);
        Assert.Equal(UiText.Get("Journey.C1.Title"), guide.TitleText);
        Assert.Null(second.Source);
        Assert.True(guide.MotionParts.First(part => part.Visibility == Visibility.Visible).RenderTransform.Value.OffsetX > 0);
        PollUntil(() => guide.TransitionOverlay is null && ContentAtRest(guide), "Only the latest page transition may survive.");
        AssertContentAtRest(guide);
    });

    [Fact]
    public void BackFromTheFirstStepEntersGroupsFromTheEarlierDirection() => StaTestHost.Run(() =>
    {
        using var fixture = new Fixture(reducedMotion: false);
        WelcomeWindow guide = fixture.Guide;
        guide.Session.Back();
        Assert.Equal(PracticeStep.Groups, guide.Session.Step);
        Assert.Equal(UiText.Get("Journey.Groups.Title"), guide.TitleText);
        Assert.NotNull(guide.TransitionOverlay);
        Assert.True(guide.MotionParts.First(part => part.Visibility == Visibility.Visible).RenderTransform.Value.OffsetX < 0);
        PollUntil(() => guide.TransitionOverlay is null && ContentAtRest(guide), "Returning to Groups must release its transition clocks.");

        guide.Session.StartGroup(PracticeStep.A1);
        Assert.Equal(PracticeStep.A1, guide.Session.Step);
        Assert.Equal(UiText.Get("Journey.A1.Title"), guide.TitleText);
        Assert.NotNull(guide.TransitionOverlay);
        Assert.True(guide.MotionParts.First(part => part.Visibility == Visibility.Visible).RenderTransform.Value.OffsetX < 0);
        PollUntil(() => guide.TransitionOverlay is null && ContentAtRest(guide), "Starting the earlier group must release its transition clocks.");
    });

    [Fact]
    public void SamePageRerendersDoNotStartAPageTransitionAndNoticesClearSuccessImmediately() => StaTestHost.Run(() =>
    {
        using var fixture = new Fixture(reducedMotion: false, prepare: guide => PracticeJourneyDriver.AdvanceTo(guide.Session, PracticeStep.B1));
        WelcomeWindow guide = fixture.Guide;
        guide.UpdateShortcutAvailability(true, false, true);
        Assert.Null(guide.TransitionOverlay);
        AssertContentAtRest(guide);
        guide.UpdateHotkeys(new HotkeySettings { PasteToScreen = new Hotkey(HotkeyModifiers.None, 0x74) });
        Assert.Null(guide.TransitionOverlay);
        AssertContentAtRest(guide);
        guide.Session.Notice("Journey.CopyFailed");
        Assert.False(guide.SuccessShown);
        Assert.Equal(UiText.Get("Journey.CopyFailed"), guide.StatusText);
        Assert.Null(guide.TransitionOverlay);
        TextBlock notice = Assert.Single(guide.MotionParts.SelectMany(part => FirstRunWelcomeTests.Descendants(part)
            .Prepend(part)).OfType<TextBlock>(), text => KoreanWordWrap.Strip(text.Text) == guide.StatusText);
        Assert.Equal(BrushColor(guide, "Text.Secondary"), Assert.IsType<SolidColorBrush>(notice.Foreground).Color);
        PollUntil(() => guide.SuccessChip.Visibility == Visibility.Collapsed && AtRest(guide.SuccessChip), "An outgoing chip must collapse and release its clocks.");
        AssertContentAtRest(guide);
    });

    [Fact]
    public void SuccessReplaysWithoutMovingTheCaptionOrContentAndKeepsTheFullAccessibleSentence() => StaTestHost.Run(() =>
    {
        using var fixture = new Fixture(reducedMotion: false, prepare: guide => guide.Session.StartGroup(PracticeStep.B0));
        WelcomeWindow guide = fixture.Guide;
        double titleTop = guide.MotionParts[0].TranslatePoint(new Point(), guide).Y;
        PracticeJourneyDriver.Complete(guide.Session);
        Assert.True(guide.SuccessShown);
        TextBlock sentence = Assert.Single(FirstRunWelcomeTests.Descendants(guide.SuccessChip).OfType<TextBlock>());
        Assert.Equal(UiText.Get("Journey.B0.Success"), guide.StatusText);
        Assert.Equal(guide.StatusText, AutomationProperties.GetName(sentence));
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(sentence));
        Assert.Equal(guide.StatusText, Assert.IsType<string>(guide.SuccessChip.ToolTip));
        Assert.Equal(TextTrimming.CharacterEllipsis, sentence.TextTrimming);
        Assert.Equal(TextWrapping.NoWrap, sentence.TextWrapping);
        PollUntil(() => guide.TransitionOverlay is null && ContentAtRest(guide) && AtRest(guide.SuccessChip), "The first success must settle.");
        guide.UpdateLayout();
        Assert.Equal(titleTop, guide.MotionParts[0].TranslatePoint(new Point(), guide).Y, 3);

        Assert.True(guide.Notify(new(PracticeEventKind.PictureCopied, guide.Session.Id, ClipboardSequence: 43)));
        Assert.True(guide.SuccessShown);
        Assert.Equal(PracticeStep.B1, guide.Session.Step);
        Assert.Null(guide.TransitionOverlay);
        Assert.True(HasMotion(guide.SuccessChip));
        PollUntil(() => AtRest(guide.SuccessChip)
            && FirstRunWelcomeTests.Descendants(guide.SuccessChip).OfType<SymbolIcon>().All(icon => !StrokeDrawMotion.IsDrawing(icon)),
            "A repeated success must end its appearance and check drawing.");
        AssertAtRest(guide.SuccessChip);
    });

    [Fact]
    public void ProgressHasFifteenEqualSegmentsWithCorrectCompletedSkippedPendingAndCurrentStates() => StaTestHost.Run(() =>
    {
        using var fixture = new Fixture(reducedMotion: true);
        WelcomeWindow guide = fixture.Guide;
        Assert.Equal(15, guide.ProgressSegments.Count);
        Assert.Equal(15, guide.ProgressFills.Count);
        Assert.All(guide.ProgressSegments, segment =>
        {
            Assert.False(segment.Focusable);
            Assert.Equal(7, segment.Height);
            Assert.Equal(new CornerRadius(3.5), segment.CornerRadius);
            Assert.Equal(new Thickness(1), segment.BorderThickness);
        });
        Assert.InRange(guide.ProgressSegments.Max(segment => segment.ActualWidth) - guide.ProgressSegments.Min(segment => segment.ActualWidth), 0, 1);
        for (int i = 1; i < guide.ProgressSegments.Count; i++)
        {
            Border before = guide.ProgressSegments[i - 1], after = guide.ProgressSegments[i];
            double gap = after.TranslatePoint(new Point(), guide).X - before.TranslatePoint(new Point(before.ActualWidth, 0), guide).X;
            Assert.InRange(gap, (i is 6 or 13 ? 8 : 3) - 1, (i is 6 or 13 ? 8 : 3) + 1);
        }

        guide.Session.Skip();
        Assert.Equal(BrushColor(guide, "Border.Subtle"), SegmentColor(guide, PracticeStep.A1));
        Assert.Equal(BrushColor(guide, "Text.Primary"), SegmentColor(guide, PracticeStep.A2));
        Assert.Equal(BrushColor(guide, "Border.Control"), SegmentColor(guide, PracticeStep.A3));
        Assert.Equal(0, Filled(guide));
        guide.Session.StartGroup(PracticeStep.B0);
        PracticeJourneyDriver.Complete(guide.Session);
        Assert.Equal(guide.Session.Results.Values.Count(result => result == PracticeStepResult.Completed), Filled(guide));
        Assert.Equal(BrushColor(guide, "Text.Primary"), SegmentColor(guide, PracticeStep.B1));
        Assert.All(guide.ProgressFills, AssertAtRest);
        while (guide.Session.Step != PracticeStep.Results) guide.Session.Skip();
        Assert.Equal(guide.Session.Results.Values.Count(result => result == PracticeStepResult.Completed), Filled(guide));
        Assert.All(guide.ProgressSegments, segment => Assert.Equal(Visibility.Visible, segment.Visibility));
        for (int i = 0; i < PracticeSession.Steps.Length; i++)
            if (guide.Session.Results[PracticeSession.Steps[i]] == PracticeStepResult.Skipped)
                Assert.Equal(BrushColor(guide, "Border.Subtle"), Assert.IsType<SolidColorBrush>(guide.ProgressSegments[i].BorderBrush).Color);
    });

    [Fact]
    public void CompletionAnimatesOnlyItsSegmentAndBackAndGroupStartSnapAllFills() => StaTestHost.Run(() =>
    {
        using var fixture = new Fixture(reducedMotion: false, prepare: guide => guide.Session.StartGroup(PracticeStep.B0));
        WelcomeWindow guide = fixture.Guide;
        PracticeJourneyDriver.Complete(guide.Session);
        int completed = Array.IndexOf(PracticeSession.Steps, PracticeStep.B0);
        Assert.Equal([completed], guide.ProgressFills.Select((fill, index) => (fill, index)).Where(item => HasMotion(item.fill)).Select(item => item.index));
        Assert.Equal(new Point(0, .5), guide.ProgressFills[completed].RenderTransformOrigin);
        Assert.IsType<ScaleTransform>(guide.ProgressFills[completed].RenderTransform);
        PollUntil(() => guide.ProgressFills.All(AtRest), "A completed segment must stop its fill clock.");
        Assert.Equal(1, Filled(guide));
        guide.Session.Back();
        Assert.All(guide.ProgressFills, AssertAtRest);
        Assert.Equal(1, Filled(guide));
        guide.Session.StartGroup(PracticeStep.C1);
        Assert.All(guide.ProgressFills, AssertAtRest);
        Assert.Equal(1, Filled(guide));
    });

    [Theory]
    [InlineData("ko-KR", 1366, 728, 96u)]
    [InlineData("en-US", 1366, 728, 96u)]
    [InlineData("ko-KR", 1920, 1032, 120u)]
    [InlineData("en-US", 1920, 1032, 120u)]
    public void WaitingSlotsKeepAllFooterButtonsAtTheSameScreenPosition(string language, int width, int height, uint dpi) => StaTestHost.Run(() =>
    {
        using var culture = UiText.UseLanguage(language);
        using var fixture = new Fixture(reducedMotion: true, width, height, dpi);
        WelcomeWindow guide = fixture.Guide;
        double[]? original = null;
        double? primaryWidth = null;
        foreach (PracticeStep step in new[] { PracticeStep.A3, PracticeStep.A4, PracticeStep.B1, PracticeStep.B2 })
        {
            PracticeJourneyDriver.AdvanceTo(guide.Session, step);
            Drain(guide);
            double[] positions = new[] { guide.BackButton, guide.SkipStepButton, guide.SkipButton }
                .Select(button => button.PointToScreen(new Point()).X).ToArray();
            if (original is null) { original = positions; primaryWidth = guide.TryNowButton.ActualWidth; }
            else
            {
                for (int i = 0; i < positions.Length; i++) Assert.Equal(original[i], positions[i], 3);
                Assert.Equal(primaryWidth!.Value, guide.TryNowButton.ActualWidth, 3);
            }
            bool waiting = step is PracticeStep.A4 or PracticeStep.B2;
            Assert.Equal(Visibility.Visible, guide.TryNowButton.Visibility);
            Assert.Equal(!waiting, guide.TryNowButton.IsEnabled);
            Assert.Equal(!waiting, guide.TryNowButton.IsDefault);
            Assert.Equal(!waiting, guide.TryNowButton.IsTabStop);
            if (waiting) Assert.Equal(UiText.Get(step == PracticeStep.A4 ? "Journey.WaitEditor" : "Journey.WaitPin"), guide.TryNowButton.Content);
            Assert.Same(guide.FindResource(waiting ? "Button.Secondary" : "Button.Primary"), guide.TryNowButton.Style);
            Assert.Same(guide.FindResource("Button.Ghost"), guide.SkipStepButton.Style);
            foreach (Button button in new[] { guide.BackButton, guide.SkipStepButton, guide.TryNowButton, guide.SkipButton })
            {
                Rect bounds = button.TransformToAncestor(guide).TransformBounds(new Rect(button.RenderSize));
                Assert.InRange(bounds.Left, 0, guide.ActualWidth);
                Assert.InRange(bounds.Right, 0, guide.ActualWidth);
                double primaryY = guide.TryNowButton.PointToScreen(new Point()).Y;
                double buttonY = button.PointToScreen(new Point()).Y;
                Assert.True(Math.Round(primaryY, 3) == Math.Round(buttonY, 3),
                    $"{language} {step}: footer wrapped; primary Y={primaryY}, {AutomationProperties.GetName(button)} Y={buttonY}. "
                    + string.Join("; ", new[] { guide.BackButton, guide.SkipStepButton, guide.TryNowButton, guide.SkipButton }
                        .Select(item => $"{AutomationProperties.GetName(item)}: width={item.ActualWidth}, desired={item.DesiredSize.Width}, margin={item.Margin}, font={item.FontFamily}/{item.FontWeight}, X={item.PointToScreen(new Point()).X}")));
            }
        }
    });

    [Theory]
    [InlineData("ko-KR")]
    [InlineData("en-US")]
    public void FirstShownPrimaryButtonRendersItsLabelInsteadOfItsCommandFaceType(string language) => StaTestHost.Run(() =>
    {
        using var culture = UiText.UseLanguage(language);
        using var fixture = new Fixture(reducedMotion: true);
        WelcomeWindow guide = fixture.Guide;
        string expected = UiText.Get("Onboarding.StartCapture");
        Assert.Equal(expected, KoreanWordWrap.Strip(SurfaceShortcutHintTests.PrimaryCaption(guide.TryNowButton)));
        Assert.Equal(expected, AutomationProperties.GetName(guide.TryNowButton));
        string[] visibleText = VisualDescendants(guide.TryNowButton).OfType<TextBlock>()
            .Where(text => text.IsVisible).Select(text => KoreanWordWrap.Strip(text.Text)).ToArray();
        Assert.Contains(expected, visibleText);
        Assert.DoesNotContain(visibleText, text => text.Contains("System.Windows.", StringComparison.Ordinal));
    });

    [Theory]
    [InlineData("ko-KR")]
    [InlineData("en-US")]
    public void PracticeAfterConsentUsesTheSameFooterWidthAsDirectPractice(string language) => StaTestHost.Run(() =>
    {
        using var culture = UiText.UseLanguage(language);
        using var direct = new Fixture(reducedMotion: true);
        using var consent = new Fixture(reducedMotion: true, consent: true);
        WelcomeWindow guide = consent.Guide;
        Assert.Equal(WelcomeStep.Consent, guide.CurrentStep);
        Assert.Equal(168, guide.TryNowButton.MinWidth);
        Assert.Equal(double.PositiveInfinity, guide.TryNowButton.MaxWidth);
        Assert.Equal(UiText.Get("Practice.Start"), SurfaceShortcutHintTests.PrimaryCaption(guide.TryNowButton));

        PracticeJourneyDriver.Click(guide.SkipButton);
        Drain(guide);
        Assert.Equal(PracticeStep.A1, guide.Session.Step);
        Assert.Equal(WelcomeStep.Capture, guide.CurrentStep);
        Assert.Equal(direct.Guide.TryNowButton.MinWidth, guide.TryNowButton.MinWidth);
        Assert.Equal(direct.Guide.TryNowButton.ActualWidth, guide.TryNowButton.ActualWidth, 3);
        foreach ((Button actual, Button expected) in new[]
        {
            (guide.BackButton, direct.Guide.BackButton), (guide.SkipStepButton, direct.Guide.SkipStepButton),
            (guide.TryNowButton, direct.Guide.TryNowButton), (guide.SkipButton, direct.Guide.SkipButton),
        })
        {
            Assert.Equal(expected.TranslatePoint(new Point(), direct.Guide).X, actual.TranslatePoint(new Point(), guide).X, 3);
            Assert.Equal(expected.TranslatePoint(new Point(), direct.Guide).Y, actual.TranslatePoint(new Point(), guide).Y, 3);
        }
    });

    [Theory]
    [InlineData("ko-KR", "A1")]
    [InlineData("ko-KR", "A4")]
    [InlineData("en-US", "A1")]
    [InlineData("en-US", "A4")]
    public void InstructionFormattingPreservesTheSentenceWordWrappingAndAccessibleName(string language, string stepName) => StaTestHost.Run(() =>
    {
        using var culture = UiText.UseLanguage(language);
        PracticeStep step = Enum.Parse<PracticeStep>(stepName);
        using var fixture = new Fixture(reducedMotion: true, prepare: guide => PracticeJourneyDriver.AdvanceTo(guide.Session, step));
        WelcomeWindow guide = fixture.Guide;
        string expected = UiText.Get($"Journey.{step}.Body");
        Assert.Equal(expected, guide.InstructionText);
        Assert.Equal(expected, AutomationProperties.GetName(guide.InstructionBlock));
        Assert.Equal(language == "ko-KR" ? KoreanWordWrap.KeepWords(expected) : expected, guide.InstructionBlock.Text);
        Run[] emphasis = guide.InstructionBlock.Inlines.OfType<Run>().Where(run => run.FontWeight == FontWeights.SemiBold).ToArray();
        string[] quoted = Regex.Matches(expected, "‘([^’]+)’|“([^”]+)”|\"([^\"]+)\"")
            .Select(match => match.Groups.Cast<Group>().Skip(1).First(group => group.Success).Value).ToArray();
        Assert.Equal(quoted, emphasis.Select(run => KoreanWordWrap.Strip(run.Text)));
        Assert.All(emphasis, run => Assert.Equal(BrushColor(guide, "Text.Primary"), Assert.IsType<SolidColorBrush>(run.Foreground).Color));
        Assert.Equal(BrushColor(guide, "Text.Secondary"), Assert.IsType<SolidColorBrush>(guide.InstructionBlock.Foreground).Color);
    });

    [Theory]
    [InlineData("ko-KR")]
    [InlineData("en-US")]
    public void PreviewButtonsRemainNamedAndOperableInsideTheFrame(string language) => StaTestHost.Run(() =>
    {
        using var culture = UiText.UseLanguage(language);
        using var fixture = new Fixture(reducedMotion: true);
        WelcomeWindow guide = fixture.Guide;
        Assert.True(guide.PreviewShown);
        foreach ((Button button, string key) in new[] { (guide.ReplayButton, "Journey.Replay"), (guide.PauseButton, "Journey.Pause") })
        {
            Assert.True(guide.PreviewFrame.IsAncestorOf(button));
            Assert.Equal(UiText.Get(key), AutomationProperties.GetName(button));
            Assert.Equal(UiText.Get(key), button.ToolTip);
            Assert.IsNotType<string>(button.Content);
            Assert.Equal(30, button.ActualWidth, 3);
            Assert.Equal(30, button.ActualHeight, 3);
            Rect bounds = button.TransformToAncestor(guide.PreviewFrame).TransformBounds(new Rect(button.RenderSize));
            Assert.InRange(bounds.Left, 0, guide.PreviewFrame.ActualWidth);
            Assert.InRange(bounds.Right, 0, guide.PreviewFrame.ActualWidth);
            Assert.InRange(bounds.Top, 0, guide.PreviewFrame.ActualHeight);
            Assert.InRange(bounds.Bottom, 0, guide.PreviewFrame.ActualHeight);
            Assert.True(button.IsEnabled && button.Focusable);
            foreach ((string layer, DependencyProperty state) in new[] { ("HoverLayer", UIElement.IsMouseOverProperty), ("PressedLayer", Button.IsPressedProperty) })
            {
                var wash = Assert.IsType<Border>(button.Template.FindName(layer, button));
                Assert.Equal(0, wash.Opacity);
                Assert.False(wash.IsHitTestVisible);
                Assert.Contains(button.Template.Triggers.OfType<Trigger>(), trigger => trigger.Property == state
                    && trigger.Setters.OfType<Setter>().Any(setter => setter.TargetName == layer
                        && setter.Property == FluidMotion.FadeToProperty && Equals(setter.Value, 1d)));
            }
        }
        Rect replay = guide.ReplayButton.TransformToAncestor(guide.PreviewFrame).TransformBounds(new Rect(guide.ReplayButton.RenderSize));
        Rect pause = guide.PauseButton.TransformToAncestor(guide.PreviewFrame).TransformBounds(new Rect(guide.PauseButton.RenderSize));
        Assert.InRange(pause.Left - replay.Right, 3, 5);
        Assert.InRange(guide.PreviewFrame.ActualWidth - pause.Right, 7, 10);
        Assert.InRange(guide.PreviewFrame.ActualHeight - pause.Bottom, 7, 10);
    });

    [Fact]
    public void KeypressMovesTheExistingCapsOnceAndItsBadgeDrawEnds() => StaTestHost.Run(() =>
    {
        using var fixture = new Fixture(reducedMotion: false, prepare: guide => PracticeJourneyDriver.AdvanceTo(guide.Session, PracticeStep.B1));
        WelcomeWindow guide = fixture.Guide;
        guide.NotifyHotkeyFired(GlobalHotkeyCommand.PasteToScreen);
        Assert.True(guide.KeyPressed);
        Assert.Equal(PracticeStep.B1, guide.Session.Step);
        Assert.Null(guide.TransitionOverlay);
        Border[] caps = Keycaps(guide);
        Assert.NotEmpty(caps);
        Assert.All(caps, cap => Assert.True(HasMotion(cap)));
        FrameworkElement badge = Assert.Single(FirstRunWelcomeTests.Descendants(guide).OfType<FrameworkElement>(),
            element => AutomationProperties.GetName(element) == UiText.Get("Practice.KeyPressed"));
        SymbolIcon icon = Assert.Single(FirstRunWelcomeTests.Descendants(badge).OfType<SymbolIcon>());
        PollUntil(() => caps.All(AtRest) && !StrokeDrawMotion.IsDrawing(icon), "Keycaps and their check must release every clock.");
        Assert.All(caps, AssertAtRest);
        Assert.Equal(caps, Keycaps(guide));
        Assert.True(guide.KeyPressed);
    });

    private static Border[] Keycaps(WelcomeWindow guide) => FirstRunWelcomeTests.Descendants(guide).OfType<WrapPanel>()
        .Where(panel => AutomationProperties.GetName(panel) is "F3" or "Ctrl+Shift+C")
        .SelectMany(panel => panel.Children.OfType<Border>()).ToArray();

    private static int Filled(WelcomeWindow guide) => guide.ProgressFills.Count(fill => fill.Visibility == Visibility.Visible);
    private static Color SegmentColor(WelcomeWindow guide, PracticeStep step) =>
        Assert.IsType<SolidColorBrush>(guide.ProgressSegments[Array.IndexOf(PracticeSession.Steps, step)].BorderBrush).Color;
    private static Color BrushColor(WelcomeWindow guide, string key) => Assert.IsType<SolidColorBrush>(guide.FindResource(key)).Color;
    private static bool TransformHasMotion(Transform transform) => transform.HasAnimatedProperties
        || transform is TransformGroup group && group.Children.Any(TransformHasMotion);
    private static bool HasMotion(FrameworkElement element) => element.HasAnimatedProperties || TransformHasMotion(element.RenderTransform);
    private static bool AtRest(FrameworkElement element) => element.Opacity == 1 && element.RenderTransform.Value.IsIdentity && !HasMotion(element);
    private static bool ContentAtRest(WelcomeWindow guide) => guide.MotionParts.All(AtRest);
    private static void AssertAtRest(FrameworkElement element)
    {
        Assert.Equal(1, element.Opacity);
        Assert.True(element.RenderTransform.Value.IsIdentity, element.GetType().Name + " keeps an offset or scale.");
        Assert.False(HasMotion(element), element.GetType().Name + " keeps an animation clock.");
    }
    private static void AssertContentAtRest(WelcomeWindow guide) => Assert.All(guide.MotionParts, AssertAtRest);
    private static void Drain(WelcomeWindow guide) => guide.Dispatcher.Invoke(guide.UpdateLayout, DispatcherPriority.ApplicationIdle);
    private static IEnumerable<DependencyObject> VisualDescendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (DependencyObject descendant in VisualDescendants(child)) yield return descendant;
        }
    }

    private static void PollUntil(Func<bool> condition, string failure)
    {
        if (condition()) return;
        var frame = new DispatcherFrame();
        var watch = Stopwatch.StartNew();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) =>
        {
            if (condition() || watch.Elapsed >= TimeSpan.FromSeconds(5)) frame.Continue = false;
        };
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        Assert.True(condition(), failure);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly bool? _previousMotion = FluidMotion.AnimationsEnabledOverrideForTest;
        private readonly WindowTrackSizeScope _tracking;
        internal WelcomeWindow Guide { get; }

        internal Fixture(bool reducedMotion, int width = 1366, int height = 728, uint dpi = 96, Action<WelcomeWindow>? prepare = null, bool consent = false)
        {
            // Exercise the explicit option even when the host disables animation globally.
            FluidMotion.AnimationsEnabledOverrideForTest = true;
            var monitor = new MonitorInfo("motion-test", new RectD(0, 0, width, height + 40), new RectD(0, 0, width, height), dpi, true);
            Guide = OnboardingGuideTests.Create(new(), new WelcomeOptions
                { ReducedMotion = reducedMotion, MonitorProvider = () => monitor, ShowCoreShortcutsConsent = consent });
            Guide.Resources = SystemHighContrastThemeTests.LoadTheme();
            ThemeService.ApplyResources(AppTheme.Glass, Guide.Resources, systemHighContrast: false);
            prepare?.Invoke(Guide);
            Guide.LimitToWorkArea(monitor);
            _tracking = new WindowTrackSizeScope(Guide, new Size(width, height + 40));
            Guide.Show();
            Drain(Guide);
            // The shared window entrance translates the entire root by 8 DIP. Wait for
            // its settled geometry; practice-owned elements still must release all clocks.
            var root = (FrameworkElement)Guide.Content;
            PollUntil(() => root.Opacity == 1 && root.RenderTransform.Value.IsIdentity
                && Guide.TransitionOverlay is null && ContentAtRest(Guide) && AtRest(Guide.SuccessChip)
                && Guide.ProgressFills.All(AtRest), "Initial layout must be idle before the gesture.");
            Assert.Equal(640, Guide.ActualWidth);
            Assert.Equal(720, Guide.ActualHeight);
        }

        public void Dispose()
        {
            Guide.Close();
            _tracking.Dispose();
            FluidMotion.AnimationsEnabledOverrideForTest = _previousMotion;
        }
    }
}
