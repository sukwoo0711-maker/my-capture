using System.ComponentModel;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MyCapture.App.Themes;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class ShortcutRevealMotionTests
{
    [Theory]
    [InlineData(null, "A")]
    [InlineData("A", "A")]
    [InlineData("b", "B")]
    [InlineData("C", "C")]
    [InlineData("unknown", "A")]
    public void ComparisonProfileDefaultsToA(string? value, string expected) =>
        Assert.Equal(expected, ShortcutRevealMotion.ParseProfile(value));

    [Theory]
    [InlineData(false, 20, 16)]
    [InlineData(true, 24, 18)]
    public void GlyphsAreCenteredAtRestAndUseTheirSpecifiedRenderedSizes(bool corner, double rest, double revealed) => StaTestHost.Run(() =>
    {
        using var motion = new ShortcutMotionTestScope();
        var glyph = SymbolIcon.Create(Geometry.Parse("M2,2 L18,2 18,18 2,18Z"));
        var button = new Button { Content = glyph, Width = 80, Height = 44 };
        ShortcutPresentation.Attach(button, ShortcutScope.Annotation, "Rectangle", corner);
        Grid face = Assert.IsType<Grid>(button.Content);
        face.Measure(new Size(100, 44));
        face.Arrange(new Rect(face.DesiredSize));
        Rect bounds = glyph.TransformToAncestor(face).TransformBounds(new Rect(glyph.RenderSize));
        Assert.Equal(rest, bounds.Width, 6);
        Assert.Equal(rest, bounds.Height, 6);
        Assert.InRange(bounds.Left + bounds.Width / 2, face.ActualWidth / 2 - .5, face.ActualWidth / 2 + .5);
        Assert.InRange(bounds.Top + bounds.Height / 2, face.ActualHeight / 2 - .5, face.ActualHeight / 2 + .5);
        if (corner) Assert.Equal(SymbolIcon.GridStrokeThickness, glyph.RenderedStrokeThickness, 6);
        TextBlock hint = face.Children.OfType<TextBlock>().Single();
        Assert.Null(UIElementAutomationPeer.CreatePeerForElement(hint));
        Assert.False(hint.IsHitTestVisible);
        Size size = face.RenderSize;
        ShortcutPresentation.SetAltRevealed(button, true);
        Assert.True(face.IsMeasureValid);
        Assert.True(face.IsArrangeValid);
        bounds = glyph.TransformToAncestor(face).TransformBounds(new Rect(glyph.RenderSize));
        Assert.Equal(revealed, bounds.Width, 6);
        Assert.Equal(revealed, bounds.Height, 6);
        Assert.Equal(size, face.RenderSize);
        if (corner)
        {
            Rect letter = hint.TransformToAncestor(face).TransformBounds(new Rect(hint.RenderSize));
            Assert.False(letter.IntersectsWith(bounds));
            Assert.True(new Rect(size).Contains(bounds));
            Assert.True(new Rect(size).Contains(letter));
        }
    });

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void LocalAltRaisesNoPreferenceNotificationOrMeasureArrangePass(bool withoutShortcut, bool unrelatedWithoutShortcut) => StaTestHost.Run(() =>
    {
        using var host = new FaceHost(withoutShortcut: withoutShortcut);
        using var unrelated = new FaceHost(withoutShortcut: unrelatedWithoutShortcut);
        int notifications = 0;
        PropertyChangedEventHandler handler = (_, _) => notifications++;
        ShortcutPresentation.Current.PropertyChanged += handler;
        int measures = host.Counter.Measures, arranges = host.Counter.Arranges;
        Size buttonSize = host.Button.RenderSize, faceSize = host.Face.RenderSize;
        int sizeChanges = 0;
        host.Button.SizeChanged += (_, _) => sizeChanges++;
        host.Face.SizeChanged += (_, _) => sizeChanges++;
        try
        {
            foreach (bool state in new[] { true, false, true, false })
            {
                ShortcutPresentation.SetAltRevealed(host.Window, state);
                Assert.True(host.Counter.IsMeasureValid && host.Counter.IsArrangeValid);
                Assert.True(host.Face.IsMeasureValid && host.Face.IsArrangeValid);
                Pump(35);
                Assert.Equal(buttonSize, host.Button.RenderSize);
                Assert.Equal(faceSize, host.Face.RenderSize);
                Assert.Equal(measures, host.Counter.Measures);
                Assert.Equal(arranges, host.Counter.Arranges);
                Assert.Equal(0, sizeChanges);
                Assert.False(ShortcutPresentation.IsRevealed(unrelated.Hint));
            }
            Pump(200);
            Assert.Equal(measures, host.Counter.Measures);
            Assert.Equal(arranges, host.Counter.Arranges);
            Assert.Equal(0, notifications);
        }
        finally { ShortcutPresentation.Current.PropertyChanged -= handler; }
    });

    [Theory]
    [InlineData("A", false, true)]
    [InlineData("A", true, true)]
    [InlineData("B", false, true)]
    [InlineData("B", true, true)]
    [InlineData("C", false, true)]
    [InlineData("C", true, true)]
    [InlineData("A", false, false)]
    [InlineData("B", true, false)]
    public void FirstAndRapidRepeatedGesturesCommitTheWholeFaceWithoutWaitingForAnimation(
        string profile, bool withoutShortcut, bool windowsAnimation) => StaTestHost.Run(() =>
    {
        using var host = new FaceHost(profile, withoutShortcut);
        FluidMotion.AnimationsEnabledOverrideForTest = windowsAnimation;
        var scale = ((TransformGroup)host.Glyph.RenderTransform).Children.OfType<ScaleTransform>().Single();
        var shift = ((TransformGroup)host.Glyph.RenderTransform).Children.OfType<TranslateTransform>().Single();
        var captionShift = ((TransformGroup)host.Hint.RenderTransform).Children.OfType<TranslateTransform>().Single();
        // No dispatcher pump between gestures: even a tap before the next frame must commit
        // the requested state, and the following press must not inherit a delayed clock.
        for (int attempt = 0; attempt < 20; attempt++)
        {
            foreach (bool revealed in new[] { true, false })
            {
                ShortcutPresentation.SetAltRevealed(host.Window, revealed);
                Assert.Equal(revealed ? .8 : 1, scale.ScaleX);
                Assert.Equal(revealed ? .8 : 1, scale.ScaleY);
                Assert.Equal(revealed ? 0 : ShortcutRevealMotion.PrimaryRestOffset, shift.Y);
                Assert.Equal(revealed ? 0 : ShortcutRevealMotion.CaptionEntranceOffset, captionShift.Y);
                Assert.Equal(revealed ? 1 : 0, host.Hint.Opacity);
                Assert.Equal(revealed ? 1 : 0, ShortcutRevealMotion.WidthProgress(host.Hint));
                Assert.False(scale.HasAnimatedProperties);
                Assert.False(shift.HasAnimatedProperties);
                Assert.False(captionShift.HasAnimatedProperties);
                Assert.False(host.Hint.HasAnimatedProperties);
            }
        }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AlwaysShowAndThemeRefreshSettleWithoutAnimation(bool withoutShortcut) => StaTestHost.Run(() =>
    {
        using var host = new FaceHost(withoutShortcut: withoutShortcut);
        ShortcutPresentation.SetAltRevealed(host.Window, true);
        Pump(30);
        ShortcutPresentation.ReadEnabled = () => true;
        ShortcutPresentation.Refresh();
        var scale = ((TransformGroup)host.Glyph.RenderTransform).Children.OfType<ScaleTransform>().Single();
        Assert.Equal(.8, scale.ScaleX);
        Assert.False(scale.HasAnimatedProperties);
        Assert.Equal(1, host.Hint.Opacity);
        Assert.False(host.Hint.HasAnimatedProperties);
        ShortcutPresentation.SetAltRevealed(host.Window, false);
        Assert.Equal(.8, scale.ScaleX);
        Assert.Equal(1, host.Hint.Opacity);
        ShortcutPresentation.ReadEnabled = () => false;
        ShortcutPresentation.Refresh();
        ShortcutPresentation.SetAltRevealed(host.Window, true);
        Pump(30);
        ThemeService.ApplyResources(MyCapture.Core.Themes.AppTheme.Midnight, host.Window.Resources, systemHighContrast: false);
        Assert.Equal(.8, scale.ScaleX);
        Assert.False(scale.HasAnimatedProperties);
        Assert.Equal(1, host.Hint.Opacity);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AlwaysShowInitialFaceAndDpiRefreshSettleDirectly(bool withoutShortcut) => StaTestHost.Run(() =>
    {
        using var host = new FaceHost(withoutShortcut: withoutShortcut);
        ShortcutPresentation.ReadEnabled = () => true;
        var icon = SymbolIcon.Create(Geometry.Empty);
        var permanent = new Button { Content = icon };
        if (withoutShortcut)
        {
            CommandButtonPresentation.Apply(permanent);
            CommandButtonPresentation.RefreshHint(permanent);
        }
        else ShortcutPresentation.Attach(permanent, ShortcutScope.Annotation, "Save");
        var permanentFace = Assert.IsType<Grid>(permanent.Content);
        var permanentHint = permanentFace.Children.OfType<TextBlock>().Single();
        var permanentScale = ((TransformGroup)icon.RenderTransform).Children.OfType<ScaleTransform>().Single();
        Assert.Equal(.8, permanentScale.ScaleX);
        Assert.Equal(1, permanentHint.Opacity);
        Assert.False(permanentScale.HasAnimatedProperties);
        Assert.False(permanentHint.HasAnimatedProperties);
        ShortcutPresentation.ReadEnabled = () => false;
        ShortcutPresentation.Refresh();
        ShortcutPresentation.SetAltRevealed(host.Window, true);
        Pump(30);
        double dpi = VisualTreeHelper.GetDpi(host.Window).DpiScaleX;
        VisualTreeHelper.SetRootDpi(host.Window, new DpiScale(dpi * 1.25, dpi * 1.25));
        host.Window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(host.Window.UpdateLayout));
        var scale = ((TransformGroup)host.Glyph.RenderTransform).Children.OfType<ScaleTransform>().Single();
        Assert.Equal(.8, scale.ScaleX);
        Assert.False(scale.HasAnimatedProperties);
        Assert.Equal(1, host.Hint.Opacity);
        Assert.False(host.Hint.HasAnimatedProperties);
    });

    [Fact]
    public void WindowWithOnlyABlankCaptionRespondsToAltKeyEvents() => StaTestHost.Run(() =>
    {
        using var host = new FaceHost(withoutShortcut: true);
        Assert.Empty(host.Hint.Text);
        Assert.Equal(Visibility.Visible, host.Hint.Visibility);
        Assert.True(host.Hint.IsVisible);
        Assert.Equal(0, host.Face.MinWidth);
        Assert.Null(UIElementAutomationPeer.CreatePeerForElement(host.Hint));
        Assert.False(host.Hint.IsHitTestVisible);
        var source = PresentationSource.FromVisual(host.Window);
        Assert.NotNull(source);
        var scale = ((TransformGroup)host.Glyph.RenderTransform).Children.OfType<ScaleTransform>().Single();

        var down = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.LeftAlt)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        host.Window.RaiseEvent(down);
        Assert.False(down.Handled);
        Assert.True(ShortcutPresentation.IsRevealed(host.Hint));
        Pump(200);
        Assert.Equal(.8, scale.ScaleX);

        var up = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.LeftAlt)
            { RoutedEvent = Keyboard.PreviewKeyUpEvent };
        host.Window.RaiseEvent(up);
        Assert.False(up.Handled);
        Assert.False(ShortcutPresentation.IsRevealed(host.Hint));
        Pump(200);
        Assert.Equal(1, scale.ScaleX);
    });

    [Theory]
    [InlineData("A")]
    [InlineData("B")]
    public void WideningCommandCommitsItsFullChordAndWidthTogetherOnEveryGesture(string profile) => StaTestHost.Run(() =>
    {
        using var host = new FaceHost(profile);
        var button = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children = { SymbolIcon.Create(Geometry.Parse("M2,2 L18,2 18,18 2,18Z")), new TextBlock { Text = "Auto-redact" } },
            },
        };
        ShortcutPresentation.Attach(button, ShortcutScope.Annotation, "Privacy");
        CommandButtonPresentation.AlwaysIcon(button);
        host.Bar.Children.Add(button);
        host.Window.UpdateLayout();
        Pump(50);
        var face = Assert.IsType<Grid>(button.Content);
        var chord = Assert.IsAssignableFrom<TextBlock>(face.Children[1]);
        Assert.True(CommandButtonPresentation.Widens(chord));
        // The neighbouring glyph-only command does not widen and keeps the shared timing.
        Assert.False(CommandButtonPresentation.Widens(host.Hint));
        double rest = button.ActualWidth;
        for (int attempt = 0; attempt < 20; attempt++)
        {
            ShortcutPresentation.SetAltRevealed(host.Window, true);
            Assert.Equal(1, chord.Opacity);
            Assert.Equal(1, host.Hint.Opacity);
            Assert.Equal(1, ShortcutRevealMotion.WidthProgress(chord));
            Assert.Equal(CommandButtonPresentation.CaptionWidth(face), chord.MaxWidth);
            Assert.False(chord.HasAnimatedProperties);
            host.Window.UpdateLayout();
            Assert.True(button.ActualWidth > rest + 20);
            Assert.True(face.ActualWidth >= CommandButtonPresentation.CaptionWidth(face) - 1);
            Rect captionBounds = chord.TransformToAncestor(button).TransformBounds(new Rect(chord.RenderSize));
            Assert.True(captionBounds.Left >= -0.5 && captionBounds.Right <= button.ActualWidth + 0.5);

            ShortcutPresentation.SetAltRevealed(host.Window, false);
            Assert.Equal(0, chord.Opacity);
            Assert.Equal(0, host.Hint.Opacity);
            Assert.Equal(0, ShortcutRevealMotion.WidthProgress(chord));
            Assert.False(chord.HasAnimatedProperties);
            host.Window.UpdateLayout();
            Assert.Equal(rest, button.ActualWidth, 2);
        }
    });

    [Theory]
    [InlineData("A")]
    [InlineData("B")]
    public void KeyedAndBlankIconsInTheSameBarCommitGeometryTogether(string profile) => StaTestHost.Run(() =>
    {
        using var host = new FaceHost(profile);
        var glyph = SymbolIcon.Create(Geometry.Parse("M2,2 L18,2 18,18 2,18Z"));
        var pressTransform = new ScaleTransform(.96, .96);
        var button = new Button { Content = glyph, RenderTransform = pressTransform };
        CommandButtonPresentation.Apply(button);
        host.Bar.Children.Add(button);
        host.Window.UpdateLayout();
        Pump(50);
        var face = Assert.IsType<Grid>(button.Content);
        var hint = face.Children.OfType<TextBlock>().Single();
        Assert.Empty(hint.Text);
        var keyedScale = ((TransformGroup)host.Glyph.RenderTransform).Children.OfType<ScaleTransform>().Single();
        var blankScale = ((TransformGroup)glyph.RenderTransform).Children.OfType<ScaleTransform>().Single();
        var keyedShift = ((TransformGroup)host.Glyph.RenderTransform).Children.OfType<TranslateTransform>().Single();
        var blankShift = ((TransformGroup)glyph.RenderTransform).Children.OfType<TranslateTransform>().Single();
        var keyedCaption = ((TransformGroup)host.Hint.RenderTransform).Children.OfType<TranslateTransform>().Single();
        var blankCaption = ((TransformGroup)hint.RenderTransform).Children.OfType<TranslateTransform>().Single();
        int measures = host.Counter.Measures, arranges = host.Counter.Arranges;
        Size buttonSize = button.RenderSize, faceSize = face.RenderSize;

        void AssertTogether()
        {
            Assert.Equal(keyedScale.ScaleX, blankScale.ScaleX, 6);
            Assert.Equal(keyedScale.ScaleY, blankScale.ScaleY, 6);
            Assert.Equal(keyedShift.Y, blankShift.Y, 6);
            Assert.Equal(host.Hint.Opacity, hint.Opacity, 6);
            Assert.Equal(keyedCaption.Y, blankCaption.Y, 6);
            Assert.Equal(buttonSize, button.RenderSize);
            Assert.Equal(faceSize, face.RenderSize);
            Assert.Equal(measures, host.Counter.Measures);
            Assert.Equal(arranges, host.Counter.Arranges);
            Assert.Same(pressTransform, button.RenderTransform);
            Assert.Equal(.96, pressTransform.ScaleX);
            Assert.Equal(.96, pressTransform.ScaleY);
            Assert.False(pressTransform.HasAnimatedProperties);
        }

        AssertTogether();
        ShortcutPresentation.SetAltRevealed(host.Window, true);
        Assert.Equal(.8, blankScale.ScaleX);
        AssertTogether();
        ShortcutPresentation.SetAltRevealed(host.Window, false);
        AssertTogether();
        ShortcutPresentation.SetAltRevealed(host.Window, true);
        Assert.Equal(.8, blankScale.ScaleX);
        Assert.Equal(0, blankShift.Y);
        AssertTogether();
        ShortcutPresentation.SetAltRevealed(host.Window, false);
        Assert.Equal(1, blankScale.ScaleX);
        Assert.Equal(ShortcutRevealMotion.PrimaryRestOffset, blankShift.Y);
        AssertTogether();
    });

    [Theory]
    [InlineData("A")]
    [InlineData("B")]
    public void BlankTextFaceOnlyTranslatesAndPreservesTheButtonTransform(string profile) => StaTestHost.Run(() =>
    {
        using var host = new FaceHost(profile);
        var pressTransform = new ScaleTransform(.96, .96);
        var button = new Button { Content = "옵션", RenderTransform = pressTransform };
        CommandButtonPresentation.Apply(button);
        host.Bar.Children.Add(button);
        host.Window.UpdateLayout();
        Pump(50);
        var face = Assert.IsType<Grid>(button.Content);
        var primary = Assert.IsType<TextBlock>(face.Children[0]);
        var hint = Assert.IsAssignableFrom<TextBlock>(face.Children[1]);
        Assert.Empty(hint.Text);
        var transforms = Assert.IsType<TransformGroup>(primary.RenderTransform);
        var shift = Assert.IsType<TranslateTransform>(Assert.Single(transforms.Children));
        var keyedShift = ((TransformGroup)host.Glyph.RenderTransform).Children.OfType<TranslateTransform>().Single();
        double fontSize = primary.FontSize;
        Size primarySize = primary.RenderSize, buttonSize = button.RenderSize;

        foreach (bool revealed in new[] { true, false })
        {
            ShortcutPresentation.SetAltRevealed(host.Window, revealed);
            Assert.Equal(keyedShift.Y, shift.Y, 6);
            Assert.Equal(revealed ? 0 : ShortcutRevealMotion.PrimaryRestOffset, shift.Y);
            Assert.Same(shift, Assert.Single(transforms.Children));
            Assert.Equal(fontSize, primary.FontSize);
            Assert.Equal(primarySize, primary.RenderSize);
            Assert.Equal(buttonSize, button.RenderSize);
            Assert.Same(pressTransform, button.RenderTransform);
            Assert.Equal(.96, pressTransform.ScaleX);
            Assert.Equal(.96, pressTransform.ScaleY);
            Assert.False(pressTransform.HasAnimatedProperties);
        }
    });

    [Theory]
    [InlineData(false, Visibility.Hidden)]
    [InlineData(false, Visibility.Collapsed)]
    [InlineData(true, Visibility.Hidden)]
    [InlineData(true, Visibility.Collapsed)]
    public void HiddenFacesSettleWithoutAnimationClocks(bool withoutShortcut, Visibility visibility) => StaTestHost.Run(() =>
    {
        using var host = new FaceHost(withoutShortcut: withoutShortcut);
        host.Button.Visibility = visibility;
        host.Window.UpdateLayout();
        var primary = Assert.IsType<TransformGroup>(host.Glyph.RenderTransform);
        var scale = primary.Children.OfType<ScaleTransform>().Single();
        var shift = primary.Children.OfType<TranslateTransform>().Single();
        var caption = Assert.IsType<TransformGroup>(host.Hint.RenderTransform);
        foreach (bool revealed in new[] { true, false, true })
        {
            ShortcutPresentation.SetAltRevealed(host.Window, revealed);
            Assert.Equal(revealed, ShortcutPresentation.IsRevealed(host.Hint));
            Assert.Equal(revealed ? .8 : 1, scale.ScaleX);
            Assert.Equal(revealed ? 0 : ShortcutRevealMotion.PrimaryRestOffset, shift.Y);
            Assert.Equal(revealed ? 1 : 0, host.Hint.Opacity);
            Assert.All(primary.Children, transform => Assert.False(transform.HasAnimatedProperties));
            Assert.All(caption.Children, transform => Assert.False(transform.HasAnimatedProperties));
            Assert.False(host.Hint.HasAnimatedProperties);
        }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnloadingAFaceKeepsItsStateFreeOfAnimationClocks(bool withoutShortcut) => StaTestHost.Run(() =>
    {
        using var host = new FaceHost(withoutShortcut: withoutShortcut);
        ShortcutPresentation.SetAltRevealed(host.Window, true);
        Pump(30);
        var primary = Assert.IsType<TransformGroup>(host.Glyph.RenderTransform);
        Assert.False(primary.Children.OfType<ScaleTransform>().Single().HasAnimatedProperties);
        host.Window.Content = null;
        Pump(50);
        Assert.False(host.Hint.IsLoaded);
        Assert.All(primary.Children, transform => Assert.False(transform.HasAnimatedProperties));
        Assert.All(((TransformGroup)host.Hint.RenderTransform).Children,
            transform => Assert.False(transform.HasAnimatedProperties));
        Assert.False(host.Hint.HasAnimatedProperties);
    });

    internal static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private sealed class CountingDecorator : Decorator
    {
        internal int Measures, Arranges;
        protected override Size MeasureOverride(Size constraint) { Measures++; return base.MeasureOverride(constraint); }
        protected override Size ArrangeOverride(Size arrangeSize) { Arranges++; return base.ArrangeOverride(arrangeSize); }
    }

    private sealed class FaceHost : IDisposable
    {
        private readonly Func<bool>? _preference = ShortcutPresentation.ReadEnabled;
        private readonly bool? _animations = FluidMotion.AnimationsEnabledOverrideForTest;
        private readonly string? _profile = ShortcutRevealMotion.ProfileForTest;
        internal Window Window { get; }
        internal Button Button { get; }
        internal SymbolIcon Glyph { get; } = SymbolIcon.Create(Geometry.Parse("M2,2 L18,2 18,18 2,18Z"));
        internal Grid Face => (Grid)Button.Content;
        internal TextBlock Hint => Face.Children.OfType<TextBlock>().Single();
        internal CountingDecorator Counter { get; }
        internal StackPanel Bar { get; }
        internal FaceHost(string profile = "A", bool withoutShortcut = false)
        {
            ShortcutPresentation.ReadEnabled = () => false;
            FluidMotion.AnimationsEnabledOverrideForTest = true;
            ShortcutRevealMotion.ProfileForTest = profile;
            Button = new Button { Content = Glyph };
            if (withoutShortcut) CommandButtonPresentation.Apply(Button);
            else ShortcutPresentation.Attach(Button, ShortcutScope.Annotation, "Save");
            Bar = new StackPanel { Orientation = Orientation.Horizontal };
            Bar.Children.Add(Button);
            Counter = new CountingDecorator { Child = Bar };
            Window = new Window { Content = Counter, Width = 200, Height = 100, Left = -10000, Top = -10000,
                ShowActivated = false, ShowInTaskbar = false, Resources = SystemHighContrastThemeTests.LoadTheme() };
            FluidMotion.SetWindowEntrance(Window, false);
            Window.Show();
            Window.UpdateLayout();
            Pump(50);
        }
        public void Dispose()
        {
            Window.Close();
            ShortcutPresentation.ReadEnabled = _preference;
            FluidMotion.AnimationsEnabledOverrideForTest = _animations;
            ShortcutRevealMotion.ProfileForTest = _profile;
            ShortcutPresentation.Refresh();
        }
    }
}
