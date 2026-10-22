using System.ComponentModel;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
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

    [Fact]
    public void LocalAltRaisesNoPreferenceNotificationOrMeasureArrangePass() => StaTestHost.Run(() =>
    {
        using var host = new FaceHost();
        using var unrelated = new FaceHost();
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
    [InlineData("A")]
    [InlineData("B")]
    public void MidAnimationReversalStartsAtTheCurrentValue(string profile) => StaTestHost.Run(() =>
    {
        using var host = new FaceHost(profile);
        ShortcutPresentation.SetAltRevealed(host.Window, true);
        Pump(50);
        var scale = ((TransformGroup)host.Glyph.RenderTransform).Children.OfType<ScaleTransform>().Single();
        var shift = ((TransformGroup)host.Glyph.RenderTransform).Children.OfType<TranslateTransform>().Single();
        var captionShift = ((TransformGroup)host.Hint.RenderTransform).Children.OfType<TranslateTransform>().Single();
        Assert.InRange(scale.ScaleX, .800001, .999999);
        double beforeScale = scale.ScaleX, beforeShift = shift.Y, beforeOpacity = host.Hint.Opacity, beforeCaption = captionShift.Y;
        ShortcutPresentation.SetAltRevealed(host.Window, false);
        Assert.Equal(beforeScale, scale.ScaleX, 6);
        Assert.Equal(beforeShift, shift.Y, 6);
        Assert.Equal(beforeOpacity, host.Hint.Opacity, 6);
        Assert.Equal(beforeCaption, captionShift.Y, 6);
        Pump(25);
        beforeScale = scale.ScaleX;
        beforeOpacity = host.Hint.Opacity;
        ShortcutPresentation.SetAltRevealed(host.Window, true);
        Assert.Equal(beforeScale, scale.ScaleX, 6);
        Assert.Equal(beforeOpacity, host.Hint.Opacity, 6);
        Pump(250);
        Assert.Equal(.8, scale.ScaleX);
        Assert.Equal(1, host.Hint.Opacity);
        ShortcutPresentation.SetAltRevealed(host.Window, false);
        Pump(250);
        Assert.Equal(1, scale.ScaleX);
        Assert.Equal(ShortcutRevealMotion.PrimaryRestOffset, shift.Y);
        Assert.Equal(0, host.Hint.Opacity);
        Assert.Equal(ShortcutRevealMotion.CaptionExitOffset, captionShift.Y);
    });

    [Theory]
    [InlineData("A", false)]
    [InlineData("B", false)]
    [InlineData("C", true)]
    public void ReducedMotionSwitchesGeometryImmediatelyAndOnlyFadesTheCaption(string profile, bool windowsAnimation) => StaTestHost.Run(() =>
    {
        using var host = new FaceHost(profile);
        FluidMotion.AnimationsEnabledOverrideForTest = windowsAnimation;
        ShortcutPresentation.SetAltRevealed(host.Window, true);
        var scale = ((TransformGroup)host.Glyph.RenderTransform).Children.OfType<ScaleTransform>().Single();
        Assert.Equal(.8, scale.ScaleX);
        Assert.False(scale.HasAnimatedProperties);
        var shift = ((TransformGroup)host.Hint.RenderTransform).Children.OfType<TranslateTransform>().Single();
        Assert.Equal(0, shift.Y);
        Assert.False(shift.HasAnimatedProperties);
        Assert.True(host.Hint.HasAnimatedProperties);
        Pump(160);
        Assert.Equal(1, host.Hint.Opacity);
        ShortcutPresentation.SetAltRevealed(host.Window, false);
        Assert.Equal(1, scale.ScaleX);
        Pump(160);
        Assert.Equal(0, host.Hint.Opacity);
    });

    [Fact]
    public void AlwaysShowAndThemeRefreshSettleWithoutAnimation() => StaTestHost.Run(() =>
    {
        using var host = new FaceHost();
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

    [Fact]
    public void AlwaysShowInitialFaceAndDpiRefreshSettleDirectly() => StaTestHost.Run(() =>
    {
        using var host = new FaceHost();
        ShortcutPresentation.ReadEnabled = () => true;
        var icon = SymbolIcon.Create(Geometry.Empty);
        var permanent = new Button { Content = icon };
        ShortcutPresentation.Attach(permanent, ShortcutScope.Annotation, "Save");
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
        internal FaceHost(string profile = "A")
        {
            ShortcutPresentation.ReadEnabled = () => false;
            FluidMotion.AnimationsEnabledOverrideForTest = true;
            ShortcutRevealMotion.ProfileForTest = profile;
            Button = new Button { Content = Glyph };
            ShortcutPresentation.Attach(Button, ShortcutScope.Annotation, "Save");
            Counter = new CountingDecorator { Child = Button };
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
