using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Capture;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class NativeSelectorAccessibilityTests
{
    private static readonly RectD Bounds = new(0, 0, 64, 48);

    [Theory]
    [InlineData("ko-KR")]
    [InlineData("en-US")]
    public void NativeBackgroundExposesTheSameLocalizedNameAndWindowRole(string culture) => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage(culture);
        using var native = new NativeCaptureOverlay(Bounds, false, false);
        var wpf = new CaptureOverlayWindow(Bounds, false, false);
        try
        {
            _ = new WindowInteropHelper(wpf).EnsureHandle();
            var peer = new WindowAutomationPeer(wpf);
            Assert.False(native.AutomationProviderCreatedForTest);
            IRawElementProviderSimple provider = native.AutomationProvider;
            Assert.Equal(peer.GetName(), provider.GetPropertyValue(AutomationElementIdentifiers.NameProperty.Id));
            Assert.Equal(AutomationControlType.Window, peer.GetAutomationControlType());
            Assert.Equal(ControlType.Window.Id, provider.GetPropertyValue(AutomationElementIdentifiers.ControlTypeProperty.Id));
            Assert.Equal(ProviderOptions.ServerSideProvider, provider.ProviderOptions);
            Assert.True(native.AutomationProviderCreatedForTest);
        }
        finally { wpf.Close(); }
    });

    [Fact]
    public void PixelsAndChromePreparationDoNotCreateAnAutomationProvider() => StaTestHost.Run(() =>
    {
        using var native = new NativeCaptureOverlay(Bounds, false, false);
        Assert.False(native.AutomationProviderCreatedForTest);
        native.AttachFrame(Frame());
        Assert.False(native.AutomationProviderCreatedForTest);
        Assert.Null(native.Chrome);
        Assert.False(native.FirstFramePresentedForTest);
        IRawElementProviderSimple provider = native.AutomationProvider;
        Assert.Same(provider, native.AutomationProvider);
        Assert.Null(native.Chrome);
        Assert.False(native.FirstFramePresentedForTest);
    });

    [Fact]
    public void ProviderReportsNativeWindowStateAndRejectsAClosedWindow() => StaTestHost.Run(() =>
    {
        using var native = new NativeCaptureOverlay(Bounds, false, false);
        IRawElementProviderSimple provider = native.AutomationProvider;
        Assert.Equal(unchecked((int)native.Handle.ToInt64()), provider.GetPropertyValue(AutomationElementIdentifiers.NativeWindowHandleProperty.Id));
        Assert.Equal(Environment.ProcessId, provider.GetPropertyValue(AutomationElementIdentifiers.ProcessIdProperty.Id));
        Assert.Equal(true, provider.GetPropertyValue(AutomationElementIdentifiers.IsKeyboardFocusableProperty.Id));
        Assert.Equal(false, provider.GetPropertyValue(AutomationElementIdentifiers.HasKeyboardFocusProperty.Id));
        Assert.Equal(true, provider.GetPropertyValue(AutomationElementIdentifiers.IsEnabledProperty.Id));
        Assert.Equal(true, provider.GetPropertyValue(AutomationElementIdentifiers.IsOffscreenProperty.Id));
        Assert.Equal(true, provider.GetPropertyValue(AutomationElementIdentifiers.IsControlElementProperty.Id));
        Assert.Equal(true, provider.GetPropertyValue(AutomationElementIdentifiers.IsContentElementProperty.Id));
        native.Close();
        Assert.Throws<ElementNotAvailableException>(() => provider.GetPropertyValue(AutomationElementIdentifiers.NameProperty.Id));
        Assert.Throws<ElementNotAvailableException>(() => provider.HostRawElementProvider);
    });

    [Theory]
    [InlineData(-25L)]
    [InlineData(0xFFFFFFE7L)]
    public void RealGetObjectRequestsAcceptSignedAndZeroExtendedRootObjectIds(long objectId) => StaTestHost.Run(() =>
    {
        using var native = new NativeCaptureOverlay(Bounds, false, false);
        Assert.False(native.AutomationProviderCreatedForTest);
        IntPtr parameter = IntPtr.Size == 8 ? new IntPtr(objectId) : new IntPtr(unchecked((int)objectId));
        IntPtr result = SendMessage(native.Handle, 0x003D, IntPtr.Zero, parameter);
        Assert.True(native.AutomationProviderCreatedForTest);
        Assert.NotEqual(IntPtr.Zero, result);
    });

    [Fact]
    public void UiaClientFromHandleFindsTheExportedCustomProvider() => StaTestHost.Run(() =>
    {
        using var native = new NativeCaptureOverlay(Bounds, false, false);
        Assert.False(native.AutomationProviderCreatedForTest);
        IntPtr handle = native.Handle;
        string expectedName = UiText.Get("Text_3816B940D53C");
        var query = new TaskCompletionSource<(string ClassName, string Name, int ControlType)>(TaskCreationOptions.RunContinuationsAsynchronously);
        // UIA calls back into the window's STA. Query on a separate MTA while that STA pumps.
        var client = new Thread(() =>
        {
            try
            {
                AutomationElement element = AutomationElement.FromHandle(handle);
                AutomationElement.AutomationElementInformation current = element.Current;
                query.TrySetResult((current.ClassName, current.Name, current.ControlType.Id));
            }
            catch (Exception error) { query.TrySetException(error); }
        }) { IsBackground = true, Name = "Native selector UIA client test" };
        client.SetApartmentState(ApartmentState.MTA);
        client.Start();
        WindowDetectOverlayTests.PumpUntil(() => query.Task.IsCompleted);
        var result = query.Task.GetAwaiter().GetResult();
        Assert.Equal(nameof(NativeCaptureOverlay), result.ClassName);
        Assert.Equal(expectedName, result.Name);
        Assert.Equal(ControlType.Window.Id, result.ControlType);
        Assert.True(native.AutomationProviderCreatedForTest);
    });

    [Fact]
    public void AClientHoldingTheProviderDoesNotRetainTheClosedSessionOrPixels() => StaTestHost.Run(() =>
    {
        var retained = CloseWithProvider();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.False(retained.Owner.IsAlive);
        Assert.False(retained.Frame.IsAlive);
        Assert.Throws<ElementNotAvailableException>(() => retained.Provider.GetPropertyValue(AutomationElementIdentifiers.NameProperty.Id));
        GC.KeepAlive(retained.Provider);
    });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (IRawElementProviderSimple Provider, WeakReference Owner, WeakReference Frame) CloseWithProvider()
    {
        using var native = new NativeCaptureOverlay(Bounds, false, false);
        FrozenFrame frame = Frame();
        native.AttachFrame(frame);
        var result = (native.AutomationProvider, new WeakReference(native), new WeakReference(frame));
        native.Close();
        return result;
    }

    private static FrozenFrame Frame()
    {
        var bitmap = BitmapSource.Create(64, 48, 96, 96, PixelFormats.Bgr32, null, new byte[64 * 48 * 4], 64 * 4);
        bitmap.Freeze();
        return new FrozenFrame(bitmap, Bounds, null, 0);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
}
