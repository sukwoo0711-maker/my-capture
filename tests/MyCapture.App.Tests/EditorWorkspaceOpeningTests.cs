using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MyCapture.App.Editing;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using Xunit;
using static MyCapture.App.Tests.EditorKeysTests;

namespace MyCapture.App.Tests;

public sealed class EditorWorkspaceOpeningTests
{
    [Fact]
    public void RealEditorWindowKeepsLoadedSourceInPlaceWhenContentRenderedStartsChrome() => StaTestHost.Run(() =>
    {
        using var dpi = TestDpiContext.At96Dpi();
        using var motion = new EntrancePreferences();
        FluidMotion.AnimationsEnabledOverrideForTest = true;
        Rect? loadedBounds = null;
        bool rendered = false;
        using EditorWindowHost host = EditorWindowHost.Create(Solid(320, 180, 0x30), beforeShow: window =>
        {
            window.Resources = SystemHighContrastThemeTests.LoadTheme();
            TestDpiContext.Initialize96DpiWindow(window);
            window.Loaded += (_, _) =>
            {
                var surface = Field<AnnotationEditorSurface>(window.Editor, "_surface");
                loadedBounds = surface.TransformToAncestor(window).TransformBounds(surface.FrameRectDip);
                Assert.Equal(1, surface.DipPerPixel);
                Assert.Equal(new Size(320, 180), loadedBounds.Value.Size);
                Assert.Equal(1, surface.Opacity);
                Assert.Equal(1, window.Editor.Opacity);
                Assert.True(window.Editor.RenderTransform.Value.IsIdentity);
                Assert.False(FluidMotion.GetWindowEntrance(window));
                Assert.All(Field<UIElement[]>(window.Editor, "_workspaceChrome"), chrome => Assert.Equal(0, chrome.Opacity));
            };
            window.ContentRendered += (_, _) =>
            {
                Rect originalBounds = Assert.IsType<Rect>(loadedBounds);
                var surface = Field<AnnotationEditorSurface>(window.Editor, "_surface");
                Assert.Equal(originalBounds, surface.TransformToAncestor(window).TransformBounds(surface.FrameRectDip));
                Assert.Equal(1, surface.DipPerPixel);
                Assert.Equal(1, surface.Opacity);
                Assert.Equal(1, window.Editor.Opacity);
                Assert.True(window.Editor.RenderTransform.Value.IsIdentity);
                rendered = true;
            };
        });
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        while (!rendered && deadline.Elapsed < TimeSpan.FromSeconds(5))
            host.Window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
        Assert.True(rendered, "The real editor did not deliver ContentRendered within the bounded wait.");
        WaitForActiveEntrance(Field<UIElement[]>(host.Editor, "_workspaceChrome"));
        Assert.Equal(Assert.IsType<Rect>(loadedBounds), Field<AnnotationEditorSurface>(host.Editor, "_surface")
            .TransformToAncestor(host.Window).TransformBounds(Field<AnnotationEditorSurface>(host.Editor, "_surface").FrameRectDip));
        host.Editor.CompleteWorkspaceEntrance();
        AssertSettled(Field<UIElement[]>(host.Editor, "_workspaceChrome"));
    });

    [Theory]
    [InlineData("ko-KR")]
    [InlineData("en-US")]
    public void OpeningRevealsOnlyChromeWhileSourceCoordinatesAndOneHundredPercentStayExact(string language) => StaTestHost.Run(() =>
    {
        using var culture = UiText.UseLanguage(language);
        using var dpi = TestDpiContext.At96Dpi();
        using var motion = new EntrancePreferences();
        using EditorWindowHost host = EditorWindowHost.Create(Solid(320, 180, 0x30), beforeShow: window =>
        {
            window.Resources = SystemHighContrastThemeTests.LoadTheme();
            TestDpiContext.Initialize96DpiWindow(window);
        });
        AnnotationEditorControl editor = host.Editor;
        var surface = Field<AnnotationEditorSurface>(editor, "_surface");
        Rect before = surface.TransformToAncestor(host.Window).TransformBounds(surface.FrameRectDip);
        double scale = surface.DipPerPixel;
        Assert.Equal(1, scale);
        Assert.False(FluidMotion.GetWindowEntrance(host.Window));
        Assert.Equal(167, FluidMotion.NormalDuration.TimeSpan.TotalMilliseconds);

        FluidMotion.AnimationsEnabledOverrideForTest = true;
        editor.PrepareWorkspaceEntrance();
        UIElement[] chrome = Field<UIElement[]>(editor, "_workspaceChrome");
        Assert.Equal(4, chrome.Length);
        Assert.All(chrome, element => Assert.Equal(0, element.Opacity));
        AssertSourceUnchanged();

        editor.BeginWorkspaceEntrance();
        WaitForActiveEntrance(chrome);
        Assert.All(chrome, element =>
        {
            Assert.True(DependencyPropertyHelper.GetValueSource(element, UIElement.OpacityProperty).IsAnimated);
            Assert.Equal(1, (double)element.GetAnimationBaseValue(UIElement.OpacityProperty));
            Assert.True(element.RenderTransform.Value.IsIdentity);
        });
        AssertSourceUnchanged();

        editor.CompleteWorkspaceEntrance();
        AssertSettled(chrome);
        AssertSourceUnchanged();

        void AssertSourceUnchanged()
        {
            host.Window.UpdateLayout();
            Assert.Equal(scale, surface.DipPerPixel);
            Assert.Equal(before, surface.TransformToAncestor(host.Window).TransformBounds(surface.FrameRectDip));
            for (DependencyObject? current = surface; current is UIElement element; current = VisualTreeHelper.GetParent(current))
            {
                Assert.Equal(1, element.Opacity);
                Assert.False(DependencyPropertyHelper.GetValueSource(element, UIElement.OpacityProperty).IsAnimated);
                Assert.True(element.RenderTransform.Value.IsIdentity);
                Assert.True(element is not FrameworkElement framework || framework.LayoutTransform.Value.IsIdentity);
            }
        }
    });

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void ReducedMotionAndPersistentCaptionsShowAllChromeImmediately(bool animations, bool persistentCaptions) => StaTestHost.Run(() =>
    {
        using var motion = new EntrancePreferences();
        using var host = new InspectorContractHost();
        FluidMotion.AnimationsEnabledOverrideForTest = animations;
        ShortcutPresentation.ReadEnabled = () => persistentCaptions;
        host.Editor.PrepareWorkspaceEntrance();
        host.Editor.BeginWorkspaceEntrance();
        AssertSettled(Field<UIElement[]>(host.Editor, "_workspaceChrome"));
    });

    [Fact]
    public void EnablingReducedMotionBetweenPreparationAndRenderingSettlesChrome() => StaTestHost.Run(() =>
    {
        using var motion = new EntrancePreferences();
        using var host = new InspectorContractHost();
        FluidMotion.AnimationsEnabledOverrideForTest = true;
        host.Editor.PrepareWorkspaceEntrance();
        Assert.All(Field<UIElement[]>(host.Editor, "_workspaceChrome"), element => Assert.Equal(0, element.Opacity));
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        host.Editor.BeginWorkspaceEntrance();
        AssertSettled(Field<UIElement[]>(host.Editor, "_workspaceChrome"));
    });

    [Theory]
    [InlineData("keyboard")]
    [InlineData("alt")]
    [InlineData("unload")]
    public void InputAndUnloadRemoveTheEntranceClocks(string interruption) => StaTestHost.Run(() =>
    {
        using var motion = new EntrancePreferences();
        using var host = new InspectorContractHost();
        FluidMotion.AnimationsEnabledOverrideForTest = true;
        host.Editor.PrepareWorkspaceEntrance();
        host.Editor.BeginWorkspaceEntrance();
        UIElement[] chrome = Field<UIElement[]>(host.Editor, "_workspaceChrome");
        WaitForActiveEntrance(chrome);
        Assert.All(chrome, element => Assert.True(DependencyPropertyHelper.GetValueSource(element, UIElement.OpacityProperty).IsAnimated));
        if (interruption == "unload")
            host.Editor.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        else
            Press(host.Editor, interruption == "alt" ? Key.LeftAlt : Key.Tab, ime: false);
        AssertSettled(chrome);
        Assert.Equal(1, Field<AnnotationEditorSurface>(host.Editor, "_surface").Opacity);
    });

    private static void AssertSettled(IEnumerable<UIElement> chrome) => Assert.All(chrome, element =>
    {
        Assert.Equal(1, element.Opacity);
        Assert.False(DependencyPropertyHelper.GetValueSource(element, UIElement.OpacityProperty).IsAnimated);
    });

    private static void WaitForActiveEntrance(UIElement[] chrome)
    {
        // BeginAnimation installs clocks immediately, but WPF does not expose an animated
        // effective dependency-property value until its next timing/render tick. Observe
        // that real frame instead of mistaking the pre-tick base value for no animation.
        bool active = false;
        var frame = new DispatcherFrame();
        var timeout = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromSeconds(5) };
        timeout.Tick += (_, _) => frame.Continue = false;
        void Sample(object? sender, EventArgs args)
        {
            active = chrome.All(element =>
                DependencyPropertyHelper.GetValueSource(element, UIElement.OpacityProperty).IsAnimated
                && element.Opacity >= 0 && element.Opacity < 1);
            if (active) frame.Continue = false;
        }
        CompositionTarget.Rendering += Sample;
        try
        {
            timeout.Start();
            Dispatcher.PushFrame(frame);
        }
        finally
        {
            timeout.Stop();
            CompositionTarget.Rendering -= Sample;
        }
        Assert.True(active, "No live frame exposed all four entrance opacity animations within the bounded wait.");
    }

    private sealed class EntrancePreferences : IDisposable
    {
        private readonly bool? _animations = FluidMotion.AnimationsEnabledOverrideForTest;
        private readonly Func<bool>? _captions = ShortcutPresentation.ReadEnabled;

        internal EntrancePreferences()
        {
            FluidMotion.AnimationsEnabledOverrideForTest = false;
            ShortcutPresentation.ReadEnabled = () => false;
        }

        public void Dispose()
        {
            FluidMotion.AnimationsEnabledOverrideForTest = _animations;
            ShortcutPresentation.ReadEnabled = _captions;
            ShortcutPresentation.Refresh();
        }
    }
}
