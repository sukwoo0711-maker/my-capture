using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Pinning;
using MyCapture.Core.Pin;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
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

    private static Task RunStaAsync(Func<Task> action)
    {
        var completion = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherSynchronizationContext(dispatcher));

            try
            {
                Task work = action();
                _ = work.ContinueWith(
                    _ => dispatcher.BeginInvokeShutdown(DispatcherPriority.Send),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                Dispatcher.Run();
                work.GetAwaiter().GetResult();
                completion.TrySetResult(null);
            }
            catch (Exception ex)
            {
                completion.TrySetException(
                    new Xunit.Sdk.XunitException($"Async STA body threw: {ex}"));
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
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

    [Fact]
    public void PinContextMenu_BeginsWithSaveCommandsAndDocumentedGestures()
    {
        RunSta(() =>
        {
            PinManager manager = NewManager();
            PinWindow pin = manager.PinImage(SolidImage());
            ContextMenu menu = Assert.IsType<ContextMenu>(pin.ContextMenu);

            MenuItem saveAs = Assert.IsType<MenuItem>(menu.Items[0]);
            Assert.Equal("다른 이름으로 저장…", saveAs.Header);
            Assert.Equal("Ctrl+Shift+S", saveAs.InputGestureText);

            MenuItem quickSave = Assert.IsType<MenuItem>(menu.Items[1]);
            Assert.Equal("빠른 저장", quickSave.Header);
            Assert.Equal("Ctrl+S", quickSave.InputGestureText);

            // The shared theme contract separately verifies Icon.SaveAs and Icon.Save are
            // shipped geometries. These first two commands intentionally consume those
            // existing vector assets instead of adding bitmap artwork.
            manager.CloseAll();
        });
    }

    [Fact]
    public void SaveRequest_ForwardsTheOriginalFrozenSourceBitmap()
    {
        RunSta(() =>
        {
            PinManager manager = NewManager();
            BitmapSource source = SolidImage(37, 23);
            PinWindow pin = manager.PinImage(source);
            PinSaveRequestedEventArgs? request = null;
            pin.SaveRequested += (_, args) => request = args;

            pin.SimulateSaveRequestForTest(PinSaveMode.SaveAs);

            Assert.NotNull(request);
            Assert.Equal(PinSaveMode.SaveAs, request!.Mode);
            Assert.Same(source, request.Image);
            Assert.True(request.Image.IsFrozen);
            Assert.Equal(37, request.Image.PixelWidth);
            Assert.Equal(23, request.Image.PixelHeight);

            manager.CloseAll();
        });
    }

    [Fact]
    public Task PinContextMenu_SaveCommands_RouteThroughManagerToRealSaveService()
    {
        return RunStaAsync(async () =>
        {
            using var temp = new TempDirectory();
            var settings = new AppSettings();
            settings.Export.FileNamePattern = "pin-from-manager";
            AppPaths paths = AppPaths.CreateForRoot(temp.Path);
            var saveService = new PinImageSaveService(
                () => settings,
                () => paths,
                NullLogger<PinImageSaveService>.Instance);
            string saveAsPath = Path.Combine(temp.Path, "chosen-by-context-menu.png");
            Window? saveAsOwner = null;
            saveService.SaveAsPrompt = (owner, _) =>
            {
                saveAsOwner = owner;
                return saveAsPath;
            };

            var manager = new PinManager(
                () => settings.Pin,
                saveService,
                NullLogger.Instance);
            PinWindow pin = manager.PinImage(SolidImage(37, 23));
            ContextMenu menu = Assert.IsType<ContextMenu>(pin.ContextMenu);

            MenuItem saveAs = Assert.IsType<MenuItem>(menu.Items[0]);
            saveAs.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Task<PinSaveResult>? saveAsOperation = manager.LastSaveOperationForTest;
            Assert.NotNull(saveAsOperation);
            PinSaveResult saveAsResult = await saveAsOperation;

            Assert.Equal(PinSaveStatus.Saved, saveAsResult.Status);
            Assert.Equal(saveAsPath, saveAsResult.Path);
            Assert.Same(pin, saveAsOwner);
            AssertPngDimensions(saveAsPath, 37, 23);

            MenuItem quickSave = Assert.IsType<MenuItem>(menu.Items[1]);
            quickSave.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Task<PinSaveResult>? quickSaveOperation = manager.LastSaveOperationForTest;
            Assert.NotNull(quickSaveOperation);
            Assert.NotSame(saveAsOperation, quickSaveOperation);
            PinSaveResult quickSaveResult = await quickSaveOperation;

            Assert.Equal(PinSaveStatus.Saved, quickSaveResult.Status);
            Assert.NotNull(quickSaveResult.Path);
            Assert.NotEqual(saveAsPath, quickSaveResult.Path);
            AssertPngDimensions(quickSaveResult.Path!, 37, 23);
            Assert.Equal(
                2,
                Directory.EnumerateFiles(temp.Path, "*.png", SearchOption.AllDirectories).Count());

            manager.CloseAll();
        });
    }

    private static void AssertPngDimensions(string path, int width, int height)
    {
        using var stream = File.OpenRead(path);
        var decoder = new PngBitmapDecoder(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        Assert.Equal(width, decoder.Frames[0].PixelWidth);
        Assert.Equal(height, decoder.Frames[0].PixelHeight);
    }

    private sealed class TempDirectory : IDisposable
    {
        internal TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "mycapture-pin-manager-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup; a failing assertion must remain the primary failure.
            }
            catch (UnauthorizedAccessException)
            {
                // Best-effort cleanup on scanners briefly retaining a generated PNG.
            }
        }
    }
}
