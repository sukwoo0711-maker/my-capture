using System;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Pinning;
using MyCapture.Core.Pin;
using MyCapture.Core.Settings;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// PinManager bookkeeping and hide/show behaviour.
/// </summary>
/// <remarks>
/// PinWindow is a real WPF <see cref="Window"/>, so these run on a dedicated STA thread and
/// close their windows before the thread ends. The manager's counting, hide-all toggle, and
/// close-all are what is verified; the pure geometry/hit-testing is covered in the Core
/// tests where no window is needed.
/// </remarks>
public sealed class PinManagerTests
{
    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
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
            ctx.DrawRectangle(Brushes.CornflowerBlue, null, new Rect(0, 0, width, height));
        }

        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static PinManager NewManager() =>
        new(() => new PinSettings(), NullLogger.Instance);

    [Fact]
    public void PinImage_IncrementsCount()
    {
        RunSta(() =>
        {
            PinManager manager = NewManager();
            Assert.Equal(0, manager.Count);

            PinWindow first = manager.PinImage(SolidImage());
            Assert.Equal(1, manager.Count);

            manager.PinImage(SolidImage());
            Assert.Equal(2, manager.Count);

            Assert.False(first.IsClosed);
            manager.CloseAll();
            Assert.Equal(0, manager.Count);
        });
    }

    [Fact]
    public void ClosingAPin_IsPrunedFromCount()
    {
        RunSta(() =>
        {
            PinManager manager = NewManager();
            PinWindow pin = manager.PinImage(SolidImage());
            Assert.Equal(1, manager.Count);

            pin.Close();
            Assert.True(pin.IsClosed);
            Assert.Equal(0, manager.Count);

            manager.CloseAll();
        });
    }

    [Fact]
    public void HideOrShowAll_TogglesHiddenState()
    {
        RunSta(() =>
        {
            PinManager manager = NewManager();
            manager.PinImage(SolidImage());
            manager.PinImage(SolidImage());

            Assert.False(manager.AreHidden);

            manager.HideOrShowAll();
            Assert.True(manager.AreHidden);

            manager.HideOrShowAll();
            Assert.False(manager.AreHidden);

            manager.CloseAll();
        });
    }

    [Fact]
    public void HideOrShowAll_NoPins_DoesNothing()
    {
        RunSta(() =>
        {
            PinManager manager = NewManager();
            manager.HideOrShowAll();
            Assert.False(manager.AreHidden);
        });
    }

    [Fact]
    public void CloseAll_ResetsHiddenState()
    {
        RunSta(() =>
        {
            PinManager manager = NewManager();
            manager.PinImage(SolidImage());
            manager.HideOrShowAll();
            Assert.True(manager.AreHidden);

            manager.CloseAll();
            Assert.False(manager.AreHidden);
            Assert.Equal(0, manager.Count);
        });
    }

    [Fact]
    public void PinViewState_InitialSizeMatchesImageAtFit()
    {
        RunSta(() =>
        {
            PinManager manager = NewManager();
            PinWindow pin = manager.PinImage(SolidImage(64, 48));

            // A tiny image fits at 1:1, so the state size equals the image size in DIP.
            Assert.Equal(1.0, pin.State.Zoom, 6);
            Assert.Equal(64, pin.State.WidthDip, 3);
            Assert.Equal(48, pin.State.HeightDip, 3);

            manager.CloseAll();
        });
    }

    [Fact]
    public void ToggleClickThroughUnderCursor_NoPinUnderCursor_ReturnsFalse()
    {
        RunSta(() =>
        {
            PinManager manager = NewManager();
            // No pins at all: nothing under the cursor.
            Assert.False(manager.ToggleClickThroughUnderCursor());
        });
    }

    [Fact]
    public void HideOrShowAll_RevealClearsClickThrough_MakesHiddenPinInteractiveAgain()
    {
        RunSta(() =>
        {
            PinManager manager = NewManager();
            PinWindow pin = manager.PinImage(SolidImage());

            // The pin turns on click-through (as the context menu would), which carries
            // WS_EX_TRANSPARENT and leaves no mouse path to reverse it when the global
            // toggle hotkey is unassigned.
            Assert.True(pin.ToggleClickThrough());
            Assert.True(pin.State.IsClickThrough);

            // Two Shift+F3 presses = hide, then reveal. Revealing is the documented safety
            // escape: it clears click-through so the pin is interactive again.
            manager.HideOrShowAll();
            Assert.True(manager.AreHidden);

            manager.HideOrShowAll();
            Assert.False(manager.AreHidden);

            Assert.False(pin.State.IsClickThrough);

            manager.CloseAll();
        });
    }

    [Fact]
    public void ShowFeedback_AutoHidesOnTimerTick_AndStopsOnClose()
    {
        RunSta(() =>
        {
            PinManager manager = NewManager();
            PinWindow pin = manager.PinImage(SolidImage());

            // Any action that surfaces feedback (here: a zoom via the state mirror) makes
            // the overlay visible and arms the auto-hide timer.
            pin.ToggleClickThrough();
            Assert.True(pin.IsFeedbackVisible);
            Assert.True(pin.IsFeedbackTimerRunning);

            // Firing the tick immediately (no brittle sleep) hides the overlay and stops
            // the timer.
            pin.ForceFeedbackTimeoutForTest();
            Assert.False(pin.IsFeedbackVisible);
            Assert.False(pin.IsFeedbackTimerRunning);

            // Re-showing feedback re-arms the timer; closing must stop it.
            pin.ToggleClickThrough();
            Assert.True(pin.IsFeedbackTimerRunning);

            pin.Close();
            Assert.False(pin.IsFeedbackTimerRunning);

            manager.CloseAll();
        });
    }

    [Fact]
    public void CtrlSingleClick_ArmsCopyDebounce_ThenFiresCopy()
    {
        RunSta(() =>
        {
            PinManager manager = NewManager();
            BitmapSource? copied = null;
            PinWindow pin = manager.PinImage(SolidImage());
            pin.CopyRequested += (_, image) => copied = image;

            pin.SimulateCtrlSingleClickForTest();
            Assert.True(pin.IsCtrlClickTimerRunning);
            Assert.Null(copied);

            // When no second click arrives, the debounce elapses and the copy fires.
            pin.ForceCtrlClickTimeoutForTest();
            Assert.False(pin.IsCtrlClickTimerRunning);
            Assert.NotNull(copied);

            manager.CloseAll();
        });
    }

    [Fact]
    public void CtrlDoubleClick_CancelsPendingCopy_AndRequestsOcr()
    {
        RunSta(() =>
        {
            PinManager manager = NewManager();
            bool copied = false;
            BitmapSource? ocrImage = null;
            PinWindow pin = manager.PinImage(SolidImage());
            pin.CopyRequested += (_, _) => copied = true;
            pin.OcrRequested += (_, image) => ocrImage = image;

            // A Ctrl+single-click arms the copy; a following Ctrl+double-click must stop the
            // debounce (no copy) and request OCR instead — the two never race the clipboard.
            pin.SimulateCtrlSingleClickForTest();
            Assert.True(pin.IsCtrlClickTimerRunning);

            pin.SimulateCtrlDoubleClickForTest();
            Assert.False(pin.IsCtrlClickTimerRunning);
            Assert.False(copied);
            Assert.NotNull(ocrImage);

            manager.CloseAll();
        });
    }

    [Fact]
    public void PinManager_ForwardsOcrRequestedFromPin()
    {
        RunSta(() =>
        {
            PinManager manager = NewManager();
            BitmapSource? bubbled = null;
            manager.OcrRequested += (_, image) => bubbled = image;

            PinWindow pin = manager.PinImage(SolidImage());

            pin.SimulateCtrlDoubleClickForTest();
            Assert.NotNull(bubbled);

            manager.CloseAll();
        });
    }
}
