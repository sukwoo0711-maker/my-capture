using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Capture;
using MyCapture.App.Editing;
using MyCapture.App.Gallery;
using MyCapture.App.Pinning;
using MyCapture.App.Recording;
using MyCapture.App.Themes;
using MyCapture.Core.Annotations;
using MyCapture.Core.Pin;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Core.Themes;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;
using MyCapture.Platform.Imaging;

namespace MyCapture.App.Diagnostics;

/// <summary>Real WPF/native-display checks for the passive capture receipt. Uses a fresh diagnostic profile.</summary>
internal static class HotfixUxSelfTest
{
    internal const string CommandLineSwitch = "--selftest-hotfix-ux";

    internal static int Run(string output)
    {
        try { output = ValidatedFilePaths.CreateDirectory(output); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Trace.TraceError("{0}: {1}", UiText.Get("Dialog.Storage"), error);
            return 1;
        }
        Console.WriteLine("HOTFIX_UX_OUTPUT=" + output);
        var report = new StringBuilder($"MyCapture {typeof(HotfixUxSelfTest).Assembly.GetName().Version} real WPF/native QA\nUTC: {DateTimeOffset.UtcNow:O}\n");
        report.AppendLine("Production windows and command paths. Bounded synthetic keys target the owned foreground input fixture and receipt only; cursor is restored. Fresh profile; no user settings/queue or startup registry access. Clipboard writer is isolated to an in-memory sink; the user clipboard is untouched.");
        List<Window> owned = [];
        bool? motion = FluidMotion.AnimationsEnabledOverrideForTest;
        AppTheme? oldTheme = ThemeService.IsAppliedForTest;
        Func<bool> oldCanShow = ShortcutHelpWindow.CanShowHelp;
        int result = 1;
        void Check(string label, bool passed)
        {
            report.AppendLine($"{(passed ? "PASS" : "FAIL")}: {label}");
            File.WriteAllText(ValidatedFilePaths.FileWithinRoot(output, "hotfix-ux-report.txt", ".txt"), report.ToString(), new UTF8Encoding(false));
            if (!passed) throw new InvalidOperationException(label);
        }
        try
        {
            FluidMotion.AnimationsEnabledOverrideForTest = false;
            ThemeService.Apply(AppTheme.Midnight);
            BitmapSource sample = Sample();
            var rect = new RectD(0, 0, sample.PixelWidth, sample.PixelHeight);
            var doc = AnnotationDocument.CreateFor(sample.PixelWidth, sample.PixelHeight);
            doc.Items.Add(new RectangleAnnotation { Rect = new RectD(30, 30, 160, 90), Stroke = ColorRgba.FromRgb(255, 0, 0), StrokeThickness = 8 });
            var editor = new AnnotationEditorWindow(new FrozenFrame(sample, rect, null, 0), rect, sample, initialDocument: doc);
            owned.Add(editor); editor.Show(); Pump(200); editor.Activate(); editor.Editor.Focus();
            var card = Card(sample); owned.Add(card);
            IInputElement? focus = Keyboard.FocusedElement;
            IntPtr foreground = GetForegroundWindow();
            card.Show(); Pump(150);
            Check("card Show preserves native foreground and WPF editor focus", foreground == GetForegroundWindow() && ReferenceEquals(focus, Keyboard.FocusedElement) && !card.IsActive);
            Render(card, Path.Combine(output, "passive-card.png"), 96);
            foreach (Key key in new[] { Key.R, Key.A })
            {
                var keyEvent = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(editor), Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                editor.Editor.RaiseEvent(keyEvent);
                Check("editor routed " + key + " while card is visible", keyEvent.Handled && !card.IsActive && editor.IsActive);
            }
            Check("editor Ctrl+Z command while card visible", editor.Editor.HandleShortcut(Key.Z, ModifierKeys.Control) && !card.IsActive);
            EditorCommitAction? committed = null;
            editor.CommitRequested = value => { committed = value.Action; return Task.FromResult(false); };
            Check("editor Ctrl+C command accepted", editor.Editor.HandleShortcut(Key.C, ModifierKeys.Control));
            Pump(150);
            Check("Ctrl+C reaches editor commit, not receipt", committed == EditorCommitAction.CopyToClipboard);
            // Keep the real dispatcher timer running while editor commands execute.
            var lifetime = Stopwatch.StartNew();
            while (card.IsVisible && lifetime.Elapsed < TimeSpan.FromSeconds(12)) Pump(100);
            report.AppendLine($"Receipt lifetime wait={lifetime.Elapsed.TotalSeconds:0.000}s; visible={card.IsVisible}; mouseOver={card.IsMouseOver}; editorActive={editor.IsActive}");
            report.AppendLine(card.TimeoutStateForTest);
            Check("passive receipt closes on its real dispatcher timer", !card.IsVisible);
            Check("editor Esc command accepted", editor.Editor.HandleShortcut(Key.Escape, ModifierKeys.None));
            Pump(50); Check("Esc closes editor", !editor.IsVisible);

            // Verify the scoped native keyboard listener against an owned foreground
            // text box; callbacks are fixture sinks and never touch the real clipboard.
            var inputBox = new TextBox();
            int foregroundCopyKeys = 0;
            inputBox.PreviewKeyDown += (_, key) => { if (key.Key == Key.C || key.ImeProcessedKey == Key.C) foregroundCopyKeys++; };
            var inputHost = new Window { Content = inputBox, Width = 260, Height = 120, Left = 40, Top = 40,
                WindowStartupLocation = WindowStartupLocation.Manual, ShowInTaskbar = false };
            owned.Add(inputHost); inputHost.Show(); inputHost.Activate(); inputBox.Focus(); Pump(100);
            int hoverCopies = 0, keyboardSaves = 0;
            var keyboardCard = new QuickAccessWindow(sample, () => { hoverCopies++; return Task.FromResult(true); }, () => { },
                () => Task.CompletedTask, () => { keyboardSaves++; return Task.FromResult(true); }, () => { }, () => Task.FromResult("unused.png")) { IsKept = true };
            owned.Add(keyboardCard); keyboardCard.Show(); Pump(100);
            var oldCursor = WindowStyleFacade.GetCursorPosition();
            try
            {
                var bounds = WindowStyleFacade.GetWindowBounds(new WindowInteropHelper(keyboardCard).Handle);
                Check("move pointer to receipt for bounded hover-key fixture", SetCursorPos(bounds.Left + 20, bounds.Top + 20)); Pump(100);
                report.AppendLine($"Hover evidence: cardBounds={bounds}; cursor={WindowStyleFacade.GetCursorPosition()}; mouseOver={keyboardCard.IsMouseOver}; active={keyboardCard.IsActive}; foreground={GetForegroundWindow()}; card={new WindowInteropHelper(keyboardCard).Handle}; fixture={new WindowInteropHelper(inputHost).Handle}");
                Check("receipt pointer hover is observed without activation", keyboardCard.IsPointerOver && !keyboardCard.IsActive);
                Check("owned keyboard fixture still has foreground before input", GetForegroundWindow() == new WindowInteropHelper(inputHost).Handle);
                SendKey(0x43); Pump(120);
                Check("hover C invokes copy without sending C to the foreground text box", hoverCopies == 1 && foregroundCopyKeys == 0 && inputHost.IsActive);
                Check("move pointer away from receipt", SetCursorPos(60, 60)); Pump(80);
                SendKey(0x43); Pump(100);
                Check("C outside receipt stays with the foreground app", hoverCopies == 1 && foregroundCopyKeys == 1);
                KeyEvent(0x12, 0, 0, UIntPtr.Zero); SendKey(0x51); KeyEvent(0x12, 0, 2, UIntPtr.Zero); Pump(120);
                Check("Alt+Q explicitly activates receipt from owned foreground app", keyboardCard.IsActive);
                SendKey(0x53); Pump(100);
                Check("keyboard S reaches receipt save command", keyboardSaves == 1);
                SendKey(0x1B); Pump(100);
                Check("keyboard Esc closes receipt", !keyboardCard.IsVisible);
            }
            finally { _ = SetCursorPos(oldCursor.X, oldCursor.Y); keyboardCard.Close(); inputHost.Close(); }

            foreach (string language in new[] { "ko", "en" })
            using (MyCapture.Core.Localization.UiText.UseLanguage(language))
            foreach (AppTheme theme in new[] { AppTheme.Midnight, AppTheme.Glass, AppTheme.Workspace, AppTheme.Daylight, AppTheme.GlassLight, AppTheme.HighContrast })
            {
                ThemeService.Apply(theme);
                var receipt = Card(sample); owned.Add(receipt); receipt.IsKept = true;
                receipt.Show(); Pump(70); receipt.UpdateLayout();
                var grid = ((StackPanel)receipt.Content).Children.OfType<DockPanel>().First().Children.OfType<UniformGrid>().Single();
                foreach (Button button in grid.Children.OfType<Button>())
                {
                    var content = (Grid)button.Content;
                    var hint = content.Children.OfType<TextBlock>().Single();
                    Check($"{language}/{theme}/{hint.Text}: icon and shortcut fit the action",
                        content.ActualWidth <= button.ActualWidth && content.ActualHeight <= button.ActualHeight
                        && hint.ActualWidth <= content.ActualWidth && hint.ActualHeight <= content.ActualHeight);
                }
                foreach (int dpi in new[] { 96, 144, 192 }) Render(receipt, Path.Combine(output, $"card-{language}-{theme}-{dpi}.png"), dpi);
                receipt.Close();
            }

            ThemeService.Apply(AppTheme.Midnight);
            var engine = new ScreenCaptureEngine(NullLogger<ScreenCaptureEngine>.Instance);
            using var coordinator = new CaptureOverlayCoordinator(engine, new WindowCandidateService(NullLogger<WindowCandidateService>.Instance), NullLogger<CaptureOverlayCoordinator>.Instance);
            int display = 0;
            foreach (MonitorInfo monitor in MonitorEnumerator.GetAll())
            {
                display++;
                report.AppendLine($"Monitor {display}: {monitor.DeviceName}, {monitor.Bounds}, DPI={monitor.Dpi}");
                var backdrop = new Window { WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
                    WindowStartupLocation = WindowStartupLocation.Manual, Background = new SolidColorBrush(Color.FromRgb(17, 31, 47)),
                    Width = monitor.Bounds.Width / monitor.ScaleFactor, Height = monitor.Bounds.Height / monitor.ScaleFactor,
                    Left = monitor.Bounds.Left / monitor.ScaleFactor, Top = monitor.Bounds.Top / monitor.ScaleFactor, Topmost = true };
                owned.Add(backdrop); backdrop.Show();
                PhysicalWindowPositioner.PlaceTopmost(new WindowInteropHelper(backdrop).Handle, monitor.Bounds); Pump(350);
                FrozenFrame baseline = engine.CaptureMonitor(monitor, false);
                ImageCodec.SavePng(baseline.Bitmap, Path.Combine(output, $"display{display}-baseline.png"));
                foreach (string mode in new[] { "region", "window", "full" })
                {
                    var receipt = Card(sample, excludeFromCapture: false); owned.Add(receipt); receipt.IsKept = true; receipt.Show(); Pump(70); receipt.PlaceOnMonitor(monitor); Pump(80);
                    var (left, top, right, bottom) = WindowStyleFacade.GetWindowBounds(new WindowInteropHelper(receipt).Handle);
                    RectD cardBounds = new(left, top, right - left, bottom - top);
                    BitmapSource before = engine.CaptureRegion(cardBounds, false);
                    BitmapSource behind = ScreenCaptureEngine.Crop(baseline,
                        new RectD(cardBounds.X - monitor.Bounds.X, cardBounds.Y - monitor.Bounds.Y, cardBounds.Width, cardBounds.Height));
                    ImageCodec.SavePng(before, Path.Combine(output, $"display{display}-{mode}-before-card.png"));
                    Check($"display{display}/{mode}: exclusion disabled, card visible in capture before dismissal", !Pixels(before).SequenceEqual(Pixels(behind)));
                    IntPtr receiptHandle = new WindowInteropHelper(receipt).Handle;
                    var dismissal = Stopwatch.StartNew();
                    receipt.DismissForCapture(coordinator.NoteForegroundDismissal);
                    Check($"display{display}/{mode}: dismissal closes HWND before capture", !receipt.IsVisible && !IsWindow(receiptHandle));
                    Task settle = coordinator.WaitForPresentationDismissalAsync();
                    Check($"display{display}/{mode}: production dismissal requests compositor settle", !settle.IsCompleted);
                    Await(settle);
                    Check($"display{display}/{mode}: presentation wait completed after {dismissal.Elapsed.TotalMilliseconds:0.0}ms", dismissal.Elapsed.TotalMilliseconds >= 95);
                    BitmapSource captured = mode switch
                    {
                        "region" => engine.CaptureRegion(cardBounds, false),
                        "window" => engine.CaptureRegion(monitor.Bounds, false),
                        _ => engine.CaptureMonitor(monitor, false).Bitmap,
                    };
                    ImageCodec.SavePng(captured, Path.Combine(output, $"display{display}-{mode}-after-card.png"));
                    // Compare against the same desktop backdrop, including any OS watermark.
                    BitmapSource reference = mode == "region" ? ScreenCaptureEngine.Crop(baseline,
                        new RectD(cardBounds.X - monitor.Bounds.X, cardBounds.Y - monitor.Bounds.Y, cardBounds.Width, cardBounds.Height)) : baseline.Bitmap;
                    byte[] pixels = Pixels(captured), original = Pixels(reference); long different = 0;
                    for (int i = 0; i < pixels.Length; i += 4)
                        if (Math.Abs(pixels[i] - original[i]) > 3 || Math.Abs(pixels[i + 1] - original[i + 1]) > 3 || Math.Abs(pixels[i + 2] - original[i + 2]) > 3) different++;
                    Check($"display{display}/{mode}: no card pixels; differing={different}/{pixels.Length / 4}", different == 0);
                }
                backdrop.Close();
            }

            var edit = new AnnotationEditorControl(new FrozenFrame(sample, rect, null, 0), rect, sample, doc.Clone(), null);
            edit.ApplyCrop(new RectD(10, 10, 230, 130));
            BitmapSource expected = Await(edit.RenderCurrentImageAsync());
            ImageCodec.SavePng(expected, Path.Combine(output, "edited-expected.png"));
            var settings = new AppSettings();
            AppPaths paths = AppPaths.CreateForRoot(ValidatedFilePaths.CreateTemporaryDirectory());
            var saver = new PinImageSaveService(() => settings, () => paths, NullLogger<PinImageSaveService>.Instance);
            var updatedCard = Card(sample); owned.Add(updatedCard); updatedCard.IsKept = true; updatedCard.Show(); Pump(70);
            Render(updatedCard, Path.Combine(output, "preview-before-update.png"), 96);
            updatedCard.UpdatePreview(expected); updatedCard.UpdateLayout();
            Render(updatedCard, Path.Combine(output, "preview-after-update.png"), 96);
            Check("preview dimensions reflect edited crop", ((StackPanel)updatedCard.Content).Children.OfType<DockPanel>().Last()
                .Children.OfType<TextBlock>().Single().Text == "230 × 130");
            updatedCard.Close();
            string? saved = null; BitmapSource? pinned = null; byte[]? clipboardPng = null;
            var actions = new QuickAccessImageActions(edit.RenderCurrentImageAsync,
                bitmap => ClipboardImageService.CopyImageAsync(bitmap, null, data =>
                {
                    using var sink = new MemoryStream();
                    ((Stream)data.GetData("PNG")!).CopyTo(sink);
                    clipboardPng = sink.ToArray();
                }),
                bitmap =>
                {
                    pinned = bitmap;
                    var pin = new PinWindow(PinContent.FromImage(bitmap), new PinViewState(bitmap.PixelWidth, bitmap.PixelHeight, 1, 1, 0.1), 100, 100, () => settings.Pin);
                    owned.Add(pin); pin.Show(); Pump(100); Render(pin, Path.Combine(output, "edited-pin-window.png"), 96); pin.Close();
                }, async bitmap => { PinSaveResult save = await saver.QuickSaveAsync(bitmap); saved = save.Path; return save.Status == PinSaveStatus.Saved; },
                exportService: new GalleryDragExportService(ValidatedFilePaths.CreateTemporaryDirectory()));
            Check("edited receipt copy uses production PNG payload with isolated clipboard sink", Await(actions.CopyAsync()) && clipboardPng is not null);
            using var png = new MemoryStream(clipboardPng!);
            BitmapSource copied = BitmapDecoder.Create(png, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
            ImageCodec.SavePng(copied, Path.Combine(output, "edited-clipboard.png"));
            Await(actions.PinAsync()); Check("edited receipt saves PNG", Await(actions.SaveAsync()));
            string drag = Await(actions.ExportAsync());
            File.Copy(drag, Path.Combine(output, "edited-drag.png"));
            foreach ((string name, BitmapSource image) in new[] { ("clipboard", copied), ("pin", pinned!), ("save", ImageCodec.TryLoad(saved!)!), ("drag", ImageCodec.TryLoad(drag)!) })
                Check($"edited {name}: dimensions={image.PixelWidth}x{image.PixelHeight}, pixel-identical to edited/cropped canvas", image.PixelWidth == expected.PixelWidth && image.PixelHeight == expected.PixelHeight && Pixels(expected).SequenceEqual(Pixels(image)));

            ShortcutHelpWindow.CanShowHelp = () => false;
            ShortcutHelpWindow.ShowHelp();
            Check("help does not open while capture predicate is false", !Application.Current.Windows.OfType<ShortcutHelpWindow>().Any(w => w.IsVisible));
            ShortcutHelpWindow.CanShowHelp = () => true;
            var selection = new CaptureOverlayWindow(new FrozenFrame(sample, rect, null, 0), abortOnFocusLoss: false);
            owned.Add(selection); selection.Show(); Pump(100);
            foreach (Key key in new[] { Key.F1, Key.OemQuestion })
            {
                var input = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(selection), Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                ShortcutHelpWindow.OnWindowKey(selection, input);
                Check($"visible capture overlay suppresses help key {key}", !input.Handled && !Application.Current.Windows.OfType<ShortcutHelpWindow>().Any(w => w.IsVisible));
            }
            selection.Close();
            ShortcutHelpWindow.ShowHelp(); Pump(70);
            Check("help opens outside capture", Application.Current.Windows.OfType<ShortcutHelpWindow>().Any(w => w.IsVisible));
            Check("capture dismisses help", ShortcutHelpWindow.DismissForCapture());
            Check("help is no longer visible", !Application.Current.Windows.OfType<ShortcutHelpWindow>().Any(w => w.IsVisible));

            using (MyCapture.Core.Localization.UiText.UseLanguage("ko"))
            {
                Exception limit = Failure(() => VideoLayerResourceBudget.ValidateFileLength(VideoLayerResourceBudget.MaximumDocumentFileBytes + 1));
                var changedOverlay = new CaptureOverlayWindow(new FrozenFrame(sample, rect, null, 0), abortOnFocusLoss: false);
                owned.Add(changedOverlay);
                Exception changed = Failure(() => changedOverlay.AttachFrame(new FrozenFrame(sample, new RectD(1, 1, rect.Width, rect.Height), null, 0)));
                string badImage = ValidatedFilePaths.FileWithinRoot(paths.DataRoot, "invalid-image.png", ".png"); File.WriteAllText(badImage, "invalid local QA fixture");
                Exception invalid = Failure(() => VideoLayerAssets.ReadImage(badImage, CancellationToken.None));
                foreach (Exception error in new Exception[] { limit, invalid, changed }) Check("localized error body preserved: " + error.Message, ThemedDialog.RecoveryMessage(error) == error.Message);
                var close = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
                bool visible = false;
                close.Tick += (_, _) =>
                {
                    close.Stop();
                    var dialog = Application.Current.Windows.OfType<ThemedDialog>().Single(w => w.IsVisible);
                    visible = dialog.ShowInTaskbar && dialog.Owner is null && !dialog.Topmost && dialog.IsActive;
                    Render(dialog, Path.Combine(output, "ownerless-localized-error.png"), 96); dialog.Close();
                };
                close.Start(); ThemedDialog.Error(null, "MyCapture QA", changed);
                Check("ownerless error is active and taskbar visible, without persistent Topmost", visible);
                void VerifyTopmostDialog(Window obstruction, bool explicitOwner, bool escape)
                {
                    owned.Add(obstruction); obstruction.Show(); Pump(100); obstruction.Activate();
                    Check($"{obstruction.GetType().Name}: excluded only from fallback owners", !ThemedDialog.IsSuitableOwner(obstruction));
                    bool above = false, dismissed = false;
                    var dismiss = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                    dismiss.Tick += (_, _) =>
                    {
                        dismiss.Stop();
                        ThemedDialog dialog = Application.Current.Windows.OfType<ThemedDialog>().Single(window => window.IsVisible);
                        above = dialog.IsActive && GetForegroundWindow() == new WindowInteropHelper(dialog).Handle
                            && (explicitOwner ? ReferenceEquals(dialog.Owner, obstruction) : dialog.Owner is null && dialog.Topmost)
                            && IsAbove(new WindowInteropHelper(dialog).Handle, new WindowInteropHelper(obstruction).Handle);
                        Render(dialog, Path.Combine(output, $"dialog-above-{obstruction.GetType().Name}-{explicitOwner}.png"), 96);
                        if (escape)
                        {
                            var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(dialog), Environment.TickCount, Key.Escape)
                                { RoutedEvent = Keyboard.KeyDownEvent, Source = dialog };
                            InputManager.Current.ProcessInput(key);
                        }
                        else
                        {
                            var panel = (StackPanel)((ScrollViewer)dialog.Content).Content;
                            Button button = panel.Children.OfType<WrapPanel>().Single().Children.OfType<Button>().Single();
                            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        }
                        dismissed = !dialog.IsVisible;
                        if (!dismissed) dialog.Close();
                    };
                    dismiss.Start();
                    if (explicitOwner) ThemedDialog.Error(obstruction, "MyCapture QA", changed);
                    else ThemedDialog.ShowUnhandled(changed);
                    Check($"{obstruction.GetType().Name}: dialog remains above visible topmost UI; explicitOwner={explicitOwner}", above);
                    Check($"{obstruction.GetType().Name}: dialog dismissed by {(escape ? "Esc" : "button")}", dismissed);
                    obstruction.Close();
                }
                VerifyTopmostDialog(new CaptureOverlayWindow(new FrozenFrame(sample, rect, null, 0), abortOnFocusLoss: false), false, true);
                VerifyTopmostDialog(new PinWindow(PinContent.FromImage(sample), new PinViewState(320, 180, 1, 1, 0.1), 100, 100, () => settings.Pin), true, false);
                VerifyTopmostDialog(new PinWindow(PinContent.FromImage(sample), new PinViewState(320, 180, 1, 1, 0.1), 100, 100, () => settings.Pin), false, false);
                VerifyTopmostDialog(new RecordingControlWindow(new RectD(100, 100, 640, 360), new MyCapture.Core.Recording.RecordingSettings(),
                    () => throw new InvalidOperationException("Recording must not start in this dialog diagnostic."),
                    () => Path.Combine(output, "unused-recording.mp4"), NullLogger.Instance), false, false);
                VerifyTopmostDialog(new CountdownWindow(60), false, false);
                string? fallback = null;
                ThemedDialog.ShowUnhandled(changed, _ => throw new InvalidOperationException("injected theme failure"), (text, _) => fallback = text);
                Check("theme failure reaches MessageBox fallback boundary with localized body", fallback == changed.Message);
            }
            result = 0;
        }
        catch (Exception error) { report.AppendLine(error.ToString()); }
        finally
        {
            foreach (Window window in owned) window.Close();
            ShortcutHelpWindow.DismissForCapture(); ShortcutHelpWindow.CanShowHelp = oldCanShow;
            FluidMotion.AnimationsEnabledOverrideForTest = motion;
            if (oldTheme is AppTheme theme) ThemeService.Apply(theme);
            report.AppendLine(result == 0 ? "RESULT: PASS" : "RESULT: FAIL");
            try
            {
                File.WriteAllText(ValidatedFilePaths.FileWithinRoot(output, "hotfix-ux-report.txt", ".txt"),
                    report.ToString(), new UTF8Encoding(false));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Trace.TraceError("{0}: {1}", UiText.Get("Dialog.Storage"), error);
                result = 1;
            }
        }
        return result;
    }

    private static QuickAccessWindow Card(BitmapSource image, bool excludeFromCapture = true) => new(image, () => Task.FromResult(true), () => { }, () => Task.CompletedTask, () => Task.FromResult(true), () => { }, () => Task.FromResult("unused.png"), excludeFromCapture);
    private static Exception Failure(Action action)
    {
        try { action(); } catch (Exception error) { return error; }
        throw new InvalidOperationException("Expected the invalid fixture to be rejected.");
    }
    private static BitmapSource Sample()
    {
        var image = new RenderTargetBitmap(320, 180, 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual(); using (DrawingContext draw = visual.RenderOpen())
        { draw.DrawRectangle(Brushes.White, null, new Rect(0, 0, 320, 180)); draw.DrawRectangle(Brushes.DodgerBlue, null, new Rect(70, 50, 120, 50)); }
        image.Render(visual); image.Freeze(); return image;
    }
    private static byte[] Pixels(BitmapSource image)
    {
        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[image.PixelWidth * image.PixelHeight * 4]; converted.CopyPixels(pixels, image.PixelWidth * 4, 0); return pixels;
    }
    private static void Render(Window window, string path, int dpi)
    {
        window.UpdateLayout(); double scale = dpi / 96d;
        var image = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(window.ActualWidth * scale)), Math.Max(1, (int)Math.Ceiling(window.ActualHeight * scale)), dpi, dpi, PixelFormats.Pbgra32);
        image.Render(window); ImageCodec.SavePng(image, path);
    }
    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }
    private static void Await(Task task)
    {
        var watch = Stopwatch.StartNew();
        while (!task.IsCompleted && watch.Elapsed < TimeSpan.FromSeconds(20)) Pump(20);
        if (!task.IsCompleted) throw new TimeoutException("Diagnostic operation did not complete in 20 seconds.");
        task.GetAwaiter().GetResult();
    }
    private static T Await<T>(Task<T> task) { Await((Task)task); return task.GetAwaiter().GetResult(); }
    private static bool IsAbove(IntPtr dialog, IntPtr obstruction)
    {
        for (IntPtr window = GetWindow(dialog, 2 /* GW_HWNDNEXT */); window != IntPtr.Zero; window = GetWindow(window, 2))
            if (window == obstruction) return true;
        return false;
    }
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint command);
    private static void SendKey(byte key) { KeyEvent(key, 0, 0, UIntPtr.Zero); KeyEvent(key, 0, 2, UIntPtr.Zero); }
    [DllImport("user32.dll", EntryPoint = "keybd_event")] private static extern void KeyEvent(byte key, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(IntPtr hwnd);
}
