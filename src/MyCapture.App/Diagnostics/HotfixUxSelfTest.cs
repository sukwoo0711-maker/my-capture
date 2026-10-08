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

    internal static int Run()
    {
        string output = Directory.CreateTempSubdirectory("MyCapture-hotfix-ux-").FullName;
        Console.WriteLine("HOTFIX_UX_OUTPUT=" + output);
        var report = new StringBuilder($"MyCapture 4.0.1 real WPF/native QA\nUTC: {DateTimeOffset.UtcNow:O}\n");
        report.AppendLine("Production windows and command paths, driven in-process; no synthetic desktop keyboard/mouse input. Fresh profile; no user settings/queue or startup registry access.");
        List<Window> owned = [];
        bool? motion = FluidMotion.AnimationsEnabledOverrideForTest;
        AppTheme? oldTheme = ThemeService.IsAppliedForTest;
        Func<bool> oldCanShow = ShortcutHelpWindow.CanShowHelp;
        int result = 1;
        void Check(string label, bool passed)
        {
            report.AppendLine($"{(passed ? "PASS" : "FAIL")}: {label}");
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

            foreach (string language in new[] { "ko", "en" })
            using (MyCapture.Core.Localization.UiText.UseLanguage(language))
            foreach (AppTheme theme in new[] { AppTheme.Midnight, AppTheme.Glass, AppTheme.Workspace, AppTheme.Daylight, AppTheme.GlassLight, AppTheme.HighContrast })
            {
                ThemeService.Apply(theme);
                var receipt = Card(sample); owned.Add(receipt); receipt.IsKept = true;
                receipt.Show(); Pump(70); receipt.UpdateLayout();
                var grid = ((StackPanel)receipt.Content).Children.OfType<UniformGrid>().Single();
                foreach (Button button in grid.Children.OfType<Button>())
                {
                    var text = new FormattedText((string)button.Content, System.Globalization.CultureInfo.CurrentUICulture,
                        FlowDirection.LeftToRight, new Typeface(button.FontFamily, button.FontStyle, button.FontWeight, button.FontStretch),
                        button.FontSize, Brushes.Black, 1);
                    Check($"{language}/{theme}/{button.Content}: text={text.WidthIncludingTrailingWhitespace:0.##}, available={button.ActualWidth - button.Padding.Left - button.Padding.Right - button.BorderThickness.Left - button.BorderThickness.Right:0.##}",
                        text.WidthIncludingTrailingWhitespace <= button.ActualWidth - button.Padding.Left - button.Padding.Right - button.BorderThickness.Left - button.BorderThickness.Right);
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
                    var receipt = Card(sample); owned.Add(receipt); receipt.IsKept = true; receipt.Show(); Pump(70); receipt.PlaceOnMonitor(monitor); Pump(80);
                    var (left, top, right, bottom) = WindowStyleFacade.GetWindowBounds(new WindowInteropHelper(receipt).Handle);
                    RectD cardBounds = new(left, top, right - left, bottom - top);
                    receipt.DismissForCapture(coordinator.NoteForegroundDismissal);
                    Task settle = coordinator.WaitForPresentationDismissalAsync();
                    Check($"display{display}/{mode}: production dismissal requests compositor settle", !settle.IsCompleted);
                    Await(settle);
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
            AppPaths paths = AppPaths.CreateForRoot(Path.Combine(output, "profile"));
            var saver = new PinImageSaveService(() => settings, () => paths, NullLogger<PinImageSaveService>.Instance);
            string? saved = null; BitmapSource? pinned = null;
            var actions = new QuickAccessImageActions(edit.RenderCurrentImageAsync, bitmap => ClipboardImageService.CopyImageAsync(bitmap),
                bitmap =>
                {
                    pinned = bitmap;
                    var pin = new PinWindow(PinContent.FromImage(bitmap), new PinViewState(bitmap.PixelWidth, bitmap.PixelHeight, 1, 1, 0.1), 100, 100, () => settings.Pin);
                    owned.Add(pin); pin.Show(); Pump(100); Render(pin, Path.Combine(output, "edited-pin-window.png"), 96); pin.Close();
                }, async bitmap => { PinSaveResult save = await saver.QuickSaveAsync(bitmap); saved = save.Path; return save.Status == PinSaveStatus.Saved; });
            Check("edited receipt copies to actual Windows clipboard", Await(actions.CopyAsync()));
            object? clipboard = Clipboard.GetData("PNG");
            using var png = clipboard switch { MemoryStream stream => stream, byte[] bytes => new MemoryStream(bytes), _ => throw new InvalidDataException("Clipboard PNG is missing.") };
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
                string badImage = Path.Combine(output, "invalid-image.png"); File.WriteAllText(badImage, "invalid local QA fixture");
                Exception invalid = Failure(() => VideoLayerAssets.ReadImage(badImage, CancellationToken.None));
                foreach (Exception error in new Exception[] { limit, invalid, changed }) Check("localized error body preserved: " + error.Message, ThemedDialog.RecoveryMessage(error) == error.Message);
                var close = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
                bool visible = false;
                close.Tick += (_, _) =>
                {
                    close.Stop();
                    var dialog = Application.Current.Windows.OfType<ThemedDialog>().Single(w => w.IsVisible);
                    visible = dialog.ShowInTaskbar && dialog.Owner is null && dialog.Topmost && dialog.IsActive;
                    Render(dialog, Path.Combine(output, "ownerless-localized-error.png"), 96); dialog.Close();
                };
                close.Start(); ThemedDialog.Error(null, "MyCapture QA", changed);
                Check("ownerless error is active, topmost and taskbar visible", visible);
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
            File.WriteAllText(Path.Combine(output, "hotfix-ux-report.txt"), report.ToString(), new UTF8Encoding(false));
        }
        return result;
    }

    private static QuickAccessWindow Card(BitmapSource image) => new(image, () => Task.FromResult(true), () => { }, () => Task.CompletedTask, () => Task.FromResult(true), () => { }, () => Task.FromResult("unused.png"));
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
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
}
