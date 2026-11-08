using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using MyCapture.App.Capture;
using MyCapture.Core.Capture;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using Xunit;
using static MyCapture.App.Tests.CaptureCueEvidenceTests;
using Overlay = MyCapture.App.Tests.WindowDetectOverlayTests;

namespace MyCapture.App.Tests;

public sealed class CaptureRatioAutomationTests
{
    [Theory]
    [InlineData(false, SelectionRatio.Free)]
    [InlineData(false, SelectionRatio.Ratio16x9)]
    [InlineData(true, SelectionRatio.Free)]
    [InlineData(true, SelectionRatio.Ratio16x9)]
    public void RealInvokePatternOpensTheMenuWithoutChangingTheLock(bool native, SelectionRatio ratio) => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("en-US");
        RatioSelector view = View(native);
        try
        {
            Show(view);
            view.SelectRatio(ratio);
            CaptureModeBar bar = view.ModeBarForTest!;
            AutomationPeer peer = UIElementAutomationPeer.CreatePeerForElement(bar.RatioButton);
            var invoke = Assert.IsAssignableFrom<IInvokeProvider>(peer.GetPattern(PatternInterface.Invoke));
            Assert.Equal(AutomationControlType.Button, peer.GetAutomationControlType());
            Assert.Null(peer.GetPattern(PatternInterface.Toggle));
            Assert.Equal(CaptureModeBar.RatioName(ratio), peer.GetItemStatus());

            int clicks = 0;
            bar.RatioButton.Click += (_, _) => clicks++;
            invoke.Invoke();
            Overlay.PumpUntil(() => clicks == 1);
            AssertOpenMenu(bar);

            Assert.Equal(ratio, view.Ratio);
            Assert.Equal(ratio != SelectionRatio.Free, bar.RatioButton.IsChecked);
            MenuItem selected = Assert.Single(bar.RatioMenuForTest!.Items.OfType<MenuItem>(), item => item.IsChecked);
            Assert.Equal(CaptureModeBar.RatioName(ratio), selected.Header);
        }
        finally { view.ReleaseResources(); }
    });

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ClickAndEnterOpenTheSameMenuWithoutTogglingTheLock(bool native, bool keyboard) => StaTestHost.Run(() =>
    {
        RatioSelector view = View(native);
        try
        {
            Show(view);
            view.SelectRatio(SelectionRatio.Ratio4x3);
            CaptureModeBar bar = view.ModeBarForTest!;
            if (keyboard)
            {
                KeyEventArgs enter = Overlay.KeyArgs(Key.Enter);
                if (native) enter.RoutedEvent = Keyboard.PreviewKeyDownEvent;
                bar.RatioButton.RaiseEvent(enter);
                Assert.True(enter.Handled);
            }
            else
            {
                bar.RatioButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            }

            AssertOpenMenu(bar);
            Assert.Equal(SelectionRatio.Ratio4x3, view.Ratio);
            Assert.True(bar.RatioButton.IsChecked);
        }
        finally { view.ReleaseResources(); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DisabledRatioButtonRejectsAutomationActivation(bool native) => StaTestHost.Run(() =>
    {
        RatioSelector view = View(native);
        try
        {
            Show(view);
            view.CopyColorText = _ => Task.FromResult(true);
            view.SelectMode(CaptureSelectorMode.Color);
            CaptureModeBar bar = view.ModeBarForTest!;
            AutomationPeer peer = UIElementAutomationPeer.CreatePeerForElement(bar.RatioButton);
            var invoke = Assert.IsAssignableFrom<IInvokeProvider>(peer.GetPattern(PatternInterface.Invoke));

            Assert.Throws<ElementNotEnabledException>(() => invoke.Invoke());
            Assert.Null(bar.RatioMenuForTest);
            Assert.Equal(SelectionRatio.Free, view.Ratio);
        }
        finally { view.ReleaseResources(); }
    });

    private static RatioSelector View(bool native) => new(native, Desktop(), () => new PointD(300, 300), false, Monitors);

    private static void Show(RatioSelector view)
    {
        // RenderTargetBitmap alone leaves the button detached. A popup needs the live owner
        // and input source that the real selector provides throughout its dispatcher turns.
        view.Show();
        Overlay.PumpUntil(() => view.ModeBarForTest?.RatioButton is { IsLoaded: true, IsVisible: true });
        Assert.NotNull(PresentationSource.FromVisual(view.ModeBarForTest!.RatioButton));
    }

    private static void AssertOpenMenu(CaptureModeBar bar)
    {
        Overlay.PumpUntil(() => bar.RatioMenuForTest is { IsOpen: true, IsLoaded: true, IsVisible: true });
        Assert.NotNull(PresentationSource.FromVisual(bar.RatioMenuForTest!));
    }

}
