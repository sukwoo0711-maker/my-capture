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
    [InlineData(SelectionRatio.Free)]
    [InlineData(SelectionRatio.Ratio16x9)]
    public void RealInvokePatternOpensTheMenuWithoutChangingTheLock(SelectionRatio ratio) => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("en-US");
        CaptureOverlayView view = View();
        Window host = Host(view);
        try
        {
            Show(host, view);
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
        finally { Close(view, host); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClickAndEnterOpenTheSameMenuWithoutTogglingTheLock(bool keyboard) => StaTestHost.Run(() =>
    {
        CaptureOverlayView view = View();
        Window host = Host(view);
        try
        {
            Show(host, view);
            view.SelectRatio(SelectionRatio.Ratio4x3);
            CaptureModeBar bar = view.ModeBarForTest!;
            if (keyboard)
            {
                KeyEventArgs enter = Overlay.KeyArgs(Key.Enter);
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
        finally { Close(view, host); }
    });

    [Fact]
    public void DisabledRatioButtonRejectsAutomationActivation() => StaTestHost.Run(() =>
    {
        CaptureOverlayView view = View();
        Window host = Host(view);
        try
        {
            Show(host, view);
            view.CopyColorText = _ => Task.FromResult(true);
            view.SelectMode(CaptureSelectorMode.Color);
            CaptureModeBar bar = view.ModeBarForTest!;
            AutomationPeer peer = UIElementAutomationPeer.CreatePeerForElement(bar.RatioButton);
            var invoke = Assert.IsAssignableFrom<IInvokeProvider>(peer.GetPattern(PatternInterface.Invoke));

            Assert.Throws<ElementNotEnabledException>(() => invoke.Invoke());
            Assert.Null(bar.RatioMenuForTest);
            Assert.Equal(SelectionRatio.Free, view.Ratio);
        }
        finally { Close(view, host); }
    });

    private static CaptureOverlayView View()
    {
        var view = new CaptureOverlayView(Desktop(), showMagnifier: false)
        {
            ReadCursor = () => new PointD(300, 300),
            PlaceCursor = _ => false,
            ReadMonitors = () => Monitors,
            ShowModeBar = true,
        };
        view.InitializePointer();
        return view;
    }

    private static Window Host(CaptureOverlayView view) => new()
    {
        Content = view,
        Width = 800,
        Height = 500,
        WindowStyle = WindowStyle.None,
        ResizeMode = ResizeMode.NoResize,
        ShowInTaskbar = false,
    };

    private static void Show(Window host, CaptureOverlayView view)
    {
        // RenderTargetBitmap alone leaves the button detached. A popup needs the live owner
        // and input source that the real selector provides throughout its dispatcher turns.
        host.Show();
        host.UpdateLayout();
        Overlay.PumpUntil(() => view.ModeBarForTest?.RatioButton is { IsLoaded: true, IsVisible: true });
        Assert.NotNull(PresentationSource.FromVisual(view.ModeBarForTest!.RatioButton));
    }

    private static void AssertOpenMenu(CaptureModeBar bar)
    {
        Overlay.PumpUntil(() => bar.RatioMenuForTest is { IsOpen: true, IsLoaded: true, IsVisible: true });
        Assert.NotNull(PresentationSource.FromVisual(bar.RatioMenuForTest!));
    }

    private static void Close(CaptureOverlayView view, Window host)
    {
        if (view.ModeBarForTest?.RatioMenuForTest is { } menu) menu.IsOpen = false;
        view.ReleaseResources();
        host.Close();
    }
}
