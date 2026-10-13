using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Pinning;
using MyCapture.Core.Pin;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using MyCapture.Platform.Display;
using MyCapture.Platform.Shell;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// Reopening closed pins through <see cref="PinManager"/>: what is recorded, the LIFO and
/// limit rules on live windows, unchanged source content, placement, and the entry points.
/// </summary>
/// <remarks>
/// Pins are real WPF windows, so each body runs on its own STA thread and closes every pin
/// it opened. The pure history and placement rules are covered in the Core tests.
/// </remarks>
public sealed class PinRestoreTests : KoreanCaptionTest
{
    private static readonly Hotkey CtrlAltF3 = new(HotkeyModifiers.Control | HotkeyModifiers.Alt, Hotkey.VkF3);

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = WpfTestThread.Create(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException($"STA body threw: {failure}");
        }
    }

    private static BitmapSource SolidImage(int width = 64, int height = 48)
    {
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (DrawingContext ctx = visual.RenderOpen())
        {
            ctx.DrawRectangle(Brushes.SeaGreen, null, new Rect(0, 0, width, height));
        }

        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static PinManager NewManager(PinSettings settings) => new(() => settings, NullLogger.Instance);

    [Fact]
    public void ClosedPin_ReopensWithSameFrozenContentZoomAndOpacity_AndWithoutClickThrough()
    {
        RunSta(() =>
        {
            PinManager manager = NewManager(new PinSettings());
            try
            {
                BitmapSource source = SolidImage(37, 23);
                PinWindow pin = manager.PinImage(source);
                pin.State.SetZoom(1.5);
                pin.State.SetOpacity(0.6);
                pin.ApplyClickThrough(enabled: true);
                Assert.False(manager.CanRestoreClosed);

                pin.Close();

                Assert.Equal(1, manager.ClosedCount);
                Assert.True(manager.CanRestoreClosed);
                Assert.Equal(37L * 23 * 4, manager.ClosedRetainedBytes);

                PinWindow? restored = manager.RestoreLastClosed();

                Assert.NotNull(restored);
                Assert.NotSame(pin, restored);
                Assert.False(restored!.IsClosed);
                Assert.Same(pin.PinnedContent, restored.PinnedContent);
                Assert.Same(source, restored.PinnedContent.Image);
                Assert.True(restored.PinnedContent.Image.IsFrozen);
                Assert.Equal(1.5, restored.State.Zoom, 6);
                Assert.Equal(0.6, restored.State.Opacity, 6);
                Assert.Equal(0.6, restored.Opacity, 6);
                Assert.Equal(pin.State.ImageWidthDip, restored.State.ImageWidthDip, 6);
                Assert.Equal(pin.State.ImageHeightDip, restored.State.ImageHeightDip, 6);
                // Restored to be worked with: no WS_EX_TRANSPARENT and no click-through state.
                Assert.False(restored.State.IsClickThrough);
                Assert.NotEqual(IntPtr.Zero, restored.Handle);
                Assert.False(WindowStyleFacade.IsClickThrough(restored.Handle));
                Assert.Equal(0, manager.ClosedCount);
                Assert.Equal(1, manager.Count);
            }
            finally
            {
                manager.CloseAllForExit();
            }
        });
    }

    [Fact]
    public void Restore_IsLastInFirstOut_ThenEmpty()
    {
        RunSta(() =>
        {
            PinManager manager = NewManager(new PinSettings());
            try
            {
                PinWindow first = manager.PinImage(SolidImage(30, 20));
                PinWindow second = manager.PinImage(SolidImage(40, 20));
                PinContent firstContent = first.PinnedContent;
                PinContent secondContent = second.PinnedContent;

                first.Close();
                second.Close();
                Assert.Equal(2, manager.ClosedCount);

                Assert.Same(secondContent, manager.RestoreLastClosed()!.PinnedContent);
                Assert.Same(firstContent, manager.RestoreLastClosed()!.PinnedContent);
                Assert.Null(manager.RestoreLastClosed());
                Assert.False(manager.CanRestoreClosed);
            }
            finally
            {
                manager.CloseAllForExit();
            }
        });
    }

    [Fact]
    public void ZeroLimit_RecordsNothing()
    {
        RunSta(() =>
        {
            PinManager manager = NewManager(new PinSettings { ClosedWindowRestoreLimit = 0 });
            try
            {
                manager.PinImage(SolidImage()).Close();

                Assert.Equal(0, manager.ClosedCount);
                Assert.False(manager.CanRestoreClosed);
                Assert.Null(manager.RestoreLastClosed());
                Assert.Equal(0, manager.Count);
            }
            finally
            {
                manager.CloseAllForExit();
            }
        });
    }

    [Fact]
    public void CloseAll_RecordsUpToLimit_NewestFirst_AndLoweringLimitTrims()
    {
        RunSta(() =>
        {
            var settings = new PinSettings { ClosedWindowRestoreLimit = 2 };
            PinManager manager = NewManager(settings);
            try
            {
                manager.PinImage(SolidImage(10, 10));
                PinContent middle = manager.PinImage(SolidImage(20, 10)).PinnedContent;
                PinContent newest = manager.PinImage(SolidImage(30, 10)).PinnedContent;

                manager.CloseAll();

                Assert.Equal(0, manager.Count);
                Assert.Equal(2, manager.ClosedCount);

                // Applying settings trims at once; the app calls this from its settings publish.
                settings.ClosedWindowRestoreLimit = 1;
                manager.TrimClosedHistory();
                Assert.Equal(1, manager.ClosedCount);
                Assert.Equal(30L * 10 * 4, manager.ClosedRetainedBytes);
                Assert.Same(newest, manager.RestoreLastClosed()!.PinnedContent);
                Assert.Null(manager.RestoreLastClosed());
                Assert.NotSame(middle, newest);
            }
            finally
            {
                manager.CloseAllForExit();
            }
        });
    }

    [Fact]
    public void HideAll_IsNotAClose_AndExitDiscardsHistory()
    {
        RunSta(() =>
        {
            PinManager manager = NewManager(new PinSettings());
            try
            {
                manager.PinImage(SolidImage());
                manager.HideOrShowAll();
                Assert.True(manager.AreHidden);
                Assert.Equal(0, manager.ClosedCount);

                manager.HideOrShowAll();
                Assert.Equal(0, manager.ClosedCount);

                manager.PinImage(SolidImage()).Close();
                Assert.Equal(1, manager.ClosedCount);

                manager.CloseAllForExit();

                Assert.Equal(0, manager.Count);
                Assert.Equal(0, manager.ClosedCount);
                Assert.Equal(0, manager.ClosedRetainedBytes);
                Assert.Null(manager.RestoreLastClosed());
            }
            finally
            {
                manager.CloseAllForExit();
            }
        });
    }

    [Fact]
    public void RestoredPin_LandsWhereRestorePlacementResolves()
    {
        RunSta(() =>
        {
            PinManager manager = NewManager(new PinSettings());
            try
            {
                PinWindow pin = manager.PinImage(SolidImage(120, 80));
                pin.Close();
                PinClosedPlacement closed = Assert.IsType<PinClosedPlacement>(pin.ClosedPlacement);
                Assert.False(closed.Bounds.IsEmpty);
                Assert.False(string.IsNullOrEmpty(closed.DeviceName));

                PinWindow restored = manager.RestoreLastClosed()!;
                PinDisplayArea[] displays = MonitorEnumerator.GetAll()
                    .Select(m => new PinDisplayArea(m.DeviceName, m.Bounds, m.WorkArea, m.ScaleFactor))
                    .ToArray();
                PinRestorePlacement.Result expected = PinRestorePlacement.Resolve(
                    closed, restored.State.WidthDip, restored.State.HeightDip, displays);
                (int left, int top, int right, int bottom) = restored.PhysicalBounds;

                Assert.InRange(left, expected.Left - 1, expected.Left + 1);
                Assert.InRange(top, expected.Top - 1, expected.Top + 1);
                Assert.True(right > left && bottom > top);
                // A pin opened inside the work area is restored to the exact same spot.
                if (!expected.Adjusted)
                {
                    Assert.InRange(left, closed.Bounds.Left - 1, closed.Bounds.Left + 1);
                    Assert.InRange(top, closed.Bounds.Top - 1, closed.Bounds.Top + 1);
                }
            }
            finally
            {
                manager.CloseAllForExit();
            }
        });
    }

    [Fact]
    public void RestoredTablePin_KeepsExactSourceTextAndCommands()
    {
        RunSta(() =>
        {
            const string source = "제품\t수량\r\n연필\t3\r\n";
            PinManager manager = NewManager(new PinSettings());
            try
            {
                PinWindow pin = manager.PinRenderedText(SolidImage(), source, isTable: true);
                long expectedBytes = pin.PinnedContent.EstimatedRetainedBytes;
                Assert.Equal((64L * 48 * 4) + (source.Length * 2), expectedBytes);
                pin.Close();
                Assert.Equal(expectedBytes, manager.ClosedRetainedBytes);

                PinWindow restored = manager.RestoreLastClosed()!;

                Assert.Equal(PinContentKind.Table, restored.ContentKind);
                Assert.Equal(source, restored.OriginalText);
                ContextMenu menu = Assert.IsType<ContextMenu>(restored.ContextMenu);
                Assert.Contains("원문 텍스트 복사", menu.Items.OfType<MenuItem>().Select(item => item.Header?.ToString()));
            }
            finally
            {
                manager.CloseAllForExit();
            }
        });
    }

    [Fact]
    public void RestoredPin_SaveCopyAndOcrUseTheOriginalBitmap()
    {
        RunSta(() =>
        {
            PinManager manager = NewManager(new PinSettings());
            try
            {
                BitmapSource source = SolidImage(37, 23);
                manager.PinImage(source).Close();
                PinWindow restored = manager.RestoreLastClosed()!;

                BitmapSource? saved = null;
                BitmapSource? copied = null;
                BitmapSource? recognized = null;
                restored.SaveRequested += (_, args) => saved = args.Image;
                restored.CopyRequested += (_, request) => copied = request.Image;
                manager.OcrRequested += (_, image) => recognized = image;

                restored.SimulateSaveRequestForTest(PinSaveMode.SaveAs);
                ContextMenu menu = Assert.IsType<ContextMenu>(restored.ContextMenu);
                MenuItem copy = menu.Items.OfType<MenuItem>().Single(item => item.InputGestureText == "Ctrl+C");
                copy.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                restored.SimulateCtrlDoubleClickForTest();

                Assert.Same(source, saved);
                Assert.Same(source, copied);
                Assert.Same(source, recognized);
            }
            finally
            {
                manager.CloseAllForExit();
            }
        });
    }

    [Fact]
    public void ContextMenuRestoreItem_FollowsHistoryAndReopensTheLastClosedPin()
    {
        RunSta(() =>
        {
            Func<HotkeySettings> previousHotkeys = ShortcutHelpWindow.ReadHotkeys;
            ShortcutHelpWindow.ReadHotkeys = () => new HotkeySettings { RestoreClosedPin = CtrlAltF3 };
            PinManager manager = NewManager(new PinSettings());
            try
            {
                PinWindow keeper = manager.PinImage(SolidImage());
                PinWindow closing = manager.PinImage(SolidImage(50, 30));
                MenuItem item = Assert.IsType<MenuItem>(keeper.RestoreClosedMenuItemForTest);

                keeper.RefreshRestoreClosedMenuItem();
                Assert.False(item.IsEnabled);
                // Same words as the tray menu's entry (glossary: "핀").
                Assert.Equal("닫은 핀 다시 열기", item.Header);
                Assert.Equal("닫은 핀 다시 열기", AutomationProperties.GetName(item));
                Assert.Equal("Ctrl+Alt+F3", item.InputGestureText);

                closing.Close();
                keeper.RefreshRestoreClosedMenuItem();
                Assert.True(item.IsEnabled);

                item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

                // The only remembered pin was reopened: two pins are open again, none remain.
                Assert.Equal(2, manager.Count);
                Assert.Equal(0, manager.ClosedCount);
                Assert.Equal(0, manager.ClosedRetainedBytes);
                keeper.RefreshRestoreClosedMenuItem();
                Assert.False(item.IsEnabled);
            }
            finally
            {
                ShortcutHelpWindow.ReadHotkeys = previousHotkeys;
                manager.CloseAllForExit();
            }
        });
    }

    [Fact]
    public void RestoreHotkey_IsOptionalAndRoutesThroughTheSharedRegistrationPath()
    {
        RunSta(() =>
        {
            Assert.Contains(GlobalHotkeyCommand.RestoreClosedPin, Enum.GetValues<GlobalHotkeyCommand>());

            var registrar = new RecordingRegistrar();
            using var window = new NativeMessageWindow();
            using var service = new GlobalHotkeyService(window, registrar, NullLogger<GlobalHotkeyService>.Instance);

            service.Initialize(new HotkeySettings());
            Assert.DoesNotContain(GlobalHotkeyCommand.RestoreClosedPin, service.RegisteredCommands);

            HotkeyReconfigureResult result = service.Reconfigure(new HotkeySettings { RestoreClosedPin = CtrlAltF3 });

            Assert.True(result.Applied);
            Assert.Contains(GlobalHotkeyCommand.RestoreClosedPin, service.RegisteredCommands);
            Assert.Equal(CtrlAltF3, service.GetAssigned(GlobalHotkeyCommand.RestoreClosedPin));

            // A chord another command owns rolls the whole set back, as for every command.
            HotkeyReconfigureResult collision = service.Reconfigure(new HotkeySettings { RestoreClosedPin = new HotkeySettings().PasteToScreen });
            Assert.False(collision.Applied);
            Assert.Equal(CtrlAltF3, service.GetAssigned(GlobalHotkeyCommand.RestoreClosedPin));
        });
    }

    /// <summary>Fake registration seam: never touches RegisterHotKey, rejects duplicates.</summary>
    private sealed class RecordingRegistrar : IHotkeyRegistrar
    {
        private readonly System.Collections.Generic.Dictionary<int, Hotkey> _held = [];

        public bool TryRegister(int id, Hotkey hotkey, out int errorCode, out string errorMessage)
        {
            if (_held.Values.Contains(hotkey))
            {
                errorCode = 1409;
                errorMessage = "Hot key is already registered.";
                return false;
            }

            _held[id] = hotkey;
            errorCode = 0;
            errorMessage = string.Empty;
            return true;
        }

        public void Unregister(int id) => _held.Remove(id);
    }
}
