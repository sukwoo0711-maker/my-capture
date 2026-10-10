using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;
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
using MyCapture.Platform.Shell;

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
        report.AppendLine("Production windows and command paths. Bounded synthetic input targets owned editors/cards and an isolated external PowerShell/WinForms image-paste/file-drop process. Every injection checks its expected foreground HWND; foreground loss aborts. Cursor is restored only while an owned target remains foreground. Fresh profile; no user settings/queue or startup registry access. The card copy/paste check writes a synthetic 320x180 image through the production Windows clipboard writer.");
        List<Window> owned = [];
        bool? motion = FluidMotion.AnimationsEnabledOverrideForTest;
        AppTheme? oldTheme = ThemeService.IsAppliedForTest;
        Func<bool> oldCanShow = ShortcutHelpWindow.CanShowHelp;
        int result = 1;
        var originalCursor = WindowStyleFacade.GetCursorPosition();
        void Check(string label, bool passed)
        {
            report.AppendLine($"{(passed ? "PASS" : "FAIL")}: {label}");
            Console.WriteLine($"{(passed ? "PASS" : "FAIL")}: {label}");
            if (!passed) throw new InvalidOperationException(label);
        }
        try
        {
            FluidMotion.AnimationsEnabledOverrideForTest = false;
            ThemeService.Apply(AppTheme.Midnight);
            BitmapSource sample = Sample();
            VerifyEditorMouseShortcuts(sample, owned, Check);
            var rect = new RectD(0, 0, sample.PixelWidth, sample.PixelHeight);
            var doc = AnnotationDocument.CreateFor(sample.PixelWidth, sample.PixelHeight);
            doc.Items.Add(new RectangleAnnotation { Rect = new RectD(30, 30, 160, 90), Stroke = ColorRgba.FromRgb(255, 0, 0), StrokeThickness = 8 });
            var editor = new AnnotationEditorWindow(new FrozenFrame(sample, rect, null, 0), rect, sample, initialDocument: doc);
            owned.Add(editor); editor.Show(); Pump(200); editor.Activate(); editor.Editor.Focus();
            var editorBounds = WindowStyleFacade.GetWindowBounds(new WindowInteropHelper(editor).Handle);
            MoveCursor(new WindowInteropHelper(editor).Handle, (editorBounds.Left + editorBounds.Right) / 2,
                (editorBounds.Top + editorBounds.Bottom) / 2);
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
            // Esc steps back inside the editor (selection, tool) and never closes it.
            Pump(50); Check("Esc keeps editor open", editor.IsVisible);
            editor.Close(); Pump(50); Check("editor closes from code", !editor.IsVisible);

            // A separate application/process owns the input. It has no capture-card code,
            // global shortcuts, user document, or profile. Its paste/drop handlers
            // accept only the synthetic data supplied by this diagnostic.
            using (var external = ExternalInputFixture.Start())
            {
                IntPtr inputHandle = external.Handle;
                Check($"external text fixture PID={external.Id}, HWND={inputHandle}; separate process", external.Id != Environment.ProcessId);
                int copies = 0, saves = 0;
                Task<bool>? lastCopy = null;
                string dragPath = ValidatedFilePaths.FileWithinRoot(output, "native-card-drag.png", ".png");
                ImageCodec.SavePng(sample, dragPath);
                var keyboardCard = new QuickAccessWindow(sample, () => { copies++; return lastCopy = ClipboardImageService.CopyImageAsync(sample); }, () => { },
                    () => Task.CompletedTask, () => { saves++; return Task.FromResult(true); }, () => { }, () => Task.FromResult(dragPath));
                owned.Add(keyboardCard); keyboardCard.Show(); Pump(100);
                IntPtr cardHandle = new WindowInteropHelper(keyboardCard).Handle;
                report.AppendLine($"Native input targets: external={inputHandle}; card={cardHandle}");
                try
                {
                    var bounds = WindowStyleFacade.GetWindowBounds(cardHandle);
                    foreach (bool kept in new[] { false, true })
                    {
                        keyboardCard.IsKept = kept;
                        foreach (bool hover in new[] { false, true })
                        {
                            MoveCursor(inputHandle, hover ? bounds.Left + 20 : 60, hover ? bounds.Top + 20 : 60);
                            Pump(80);
                            Check($"kept={kept}/hover={hover}: external foreground preserved", GetForegroundWindow() == inputHandle && !keyboardCard.IsActive);
                            Check($"kept={kept}/hover={hover}: native pointer state", keyboardCard.IsPointerOver == hover);
                            string before = external.Text;
                            foreach (byte key in new byte[] { 0x43, 0x45, 0x50, 0x53 }) { SendKey(inputHandle, key); Pump(50); }
                            SendKey(inputHandle, 0x1B); Pump(50);
                            Check($"kept={kept}/hover={hover}: external C/E/P/S unchanged; text={external.Text}", external.Text == before + "ceps");
                            Check($"kept={kept}/hover={hover}: no card action or Esc dismissal", copies == 0 && saves == 0 && keyboardCard.IsVisible);
                        }
                        // RegisterHotKey cannot target a HWND owned by another process.
                        // A separate thread registration proves the card released the global chord.
                        var probe = new NativeHotkeyRegistrar(IntPtr.Zero);
                        bool available = probe.TryRegister(0x7103, new Hotkey(HotkeyModifiers.Alt, 0x51), out _, out _);
                        if (available) probe.Unregister(0x7103);
                        Check($"Alt+Q available to a separate registration iff kept={kept}", available == kept);
                    }
                    var mouseRow = ((StackPanel)((Border)keyboardCard.Content).Child).Children.OfType<DockPanel>().First();
                    var mouseActions = mouseRow.Children.OfType<UniformGrid>().Single().Children.OfType<Button>().ToArray();
                    Click(inputHandle, mouseActions[0]);
                    Check("copy click preserves external foreground", GetForegroundWindow() == inputHandle && !keyboardCard.IsActive && copies == 1);
                    Check("card writes actual Windows image clipboard", lastCopy is not null && Await(lastCopy));
                    InjectKey(inputHandle, 0x11, 0); SendKey(inputHandle, 0x56); InjectKey(inputHandle, 0x11, 2); Pump(200);
                    Check("Ctrl+V pastes image in external app without refocusing", external.Text.EndsWith("[image:320x180]", StringComparison.Ordinal));
                    Click(inputHandle, mouseActions[2]); Click(inputHandle, mouseActions[3]);
                    Check("pin/save clicks preserve external foreground", GetForegroundWindow() == inputHandle && !keyboardCard.IsActive && saves == 1);
                    var preview = mouseRow.Children.OfType<Image>().Single();
                    Click(inputHandle, preview);
                    Check("thumbnail click preserves external foreground", GetForegroundWindow() == inputHandle && !keyboardCard.IsActive);
                    DragToExternal(inputHandle, preview);
                    Check($"native thumbnail file drop reaches external app and preserves foreground; text={external.Text}; foreground={GetForegroundWindow()}",
                        external.Text.EndsWith("[drop:" + dragPath + "]", StringComparison.Ordinal) && GetForegroundWindow() == inputHandle && !keyboardCard.IsActive);
                    var keepButton = mouseRow.Children.OfType<ToggleButton>().Single();
                    Click(inputHandle, keepButton); Click(inputHandle, keepButton);
                    Check("keep toggle clicks preserve external foreground", GetForegroundWindow() == inputHandle && keyboardCard.IsKept && !keyboardCard.IsActive);
                    foreach (bool library in new[] { false, true })
                    {
                        int invoked = 0;
                        var actionCard = new QuickAccessWindow(sample, () => Task.FromResult(true), () => invoked++,
                            () => Task.CompletedTask, () => Task.FromResult(true), () => invoked++, () => Task.FromResult(dragPath)) { IsKept = true };
                        owned.Add(actionCard); actionCard.Show(); Pump(80);
                        var action = Ux402VisualMetrics.Descendants(actionCard).OfType<Button>().Single(b => library
                            ? AutomationProperties.GetName(b) == UiText.Get("QuickAccess.Library")
                            : AutomationProperties.GetAcceleratorKey(b) == "E");
                        Click(inputHandle, action);
                        Check($"{(library ? "library" : "edit")} click invokes action without activating card", invoked == 1 && GetForegroundWindow() == inputHandle && !actionCard.IsVisible);
                    }
                    // Kept cards enter only by a non-action background click.
                    var dimensions = ((StackPanel)((Border)keyboardCard.Content).Child).Children.OfType<DockPanel>().Last().Children.OfType<TextBlock>().Single();
                    double originalLeft = keyboardCard.Left;
                    Point dragStart = dimensions.PointToScreen(new Point(dimensions.ActualWidth / 2, dimensions.ActualHeight / 2));
                    MoveCursor(inputHandle, (int)dragStart.X, (int)dragStart.Y);
                    MouseButton(inputHandle, 0x0002); Pump(50);
                    for (int delta = 10; delta <= 80; delta += 10)
                    {
                        MoveCursor(inputHandle, (int)dragStart.X - delta, (int)dragStart.Y); Pump(25);
                    }
                    MouseButton(inputHandle, 0x0004); Pump(80);
                    Check("background drag moves the card without activating or invoking an action",
                        Math.Abs(keyboardCard.Left - originalLeft) > 40 && GetForegroundWindow() == inputHandle
                        && !keyboardCard.IsActive && copies == 1 && saves == 1);
                    Click(inputHandle, dimensions);
                    Check("background click activates the kept card", keyboardCard.IsActive && keyboardCard.IsKeyboardFocusWithin);
                    SendKey(cardHandle, 0x43); Pump(80);
                    Check("focused card C invokes copy", copies == 2 && lastCopy is not null && Await(lastCopy));

                    keyboardCard.IsKept = false;
                    external.Activate(); Pump(100);
                    InjectKey(inputHandle, 0x12, 0);
                    SendKey(inputHandle, 0x51);
                    InjectKey(inputHandle, 0x12, 2);
                    Pump(120);
                    Check("Alt+Q activates an automatic card", keyboardCard.IsActive && keyboardCard.IsKeyboardFocusWithin);
                    SendKey(cardHandle, 0x53); Pump(80);
                    Check("focused card S invokes save", saves == 2);
                    // Observe real pointer-triggered tooltips on this owned foreground HWND.
                    var cardRow = ((StackPanel)((Border)keyboardCard.Content).Child).Children.OfType<DockPanel>().First();
                    var tooltipTargets = cardRow.Children.OfType<UniformGrid>().Single().Children.OfType<ButtonBase>()
                        .Concat(cardRow.Children.OfType<ToggleButton>()).ToArray();
                    foreach (ButtonBase target in tooltipTargets)
                    {
                        Point center = target.PointToScreen(new Point(target.ActualWidth / 2, target.ActualHeight / 2));
                        var latency = Stopwatch.StartNew();
                        double? openedAt = null;
                        ToolTipEventHandler opened = (_, _) => openedAt = latency.Elapsed.TotalMilliseconds;
                        target.ToolTipOpening += opened;
                        MoveCursor(cardHandle, (int)center.X, (int)center.Y); Pump(300);
                        target.ToolTipOpening -= opened;
                        // The service creates a ToolTip popup for a string-valued tooltip;
                        // IsOpen lives on that ToolTip, not on the owner button.
                        bool visible = PresentationSource.CurrentSources.OfType<HwndSource>()
                            .Where(source => source.RootVisual is not null)
                            .SelectMany(source => Ux402VisualMetrics.Descendants(source.RootVisual))
                            .OfType<ToolTip>().Any(tooltip => tooltip.IsOpen && Equals(tooltip.Content, target.ToolTip));
                        Check($"card tooltip {target.ToolTip}: visible={visible}, latency={openedAt:0}ms",
                            visible && openedAt is >= 0 and <= 300);
                    }
                    // Exercise the safety gate without injecting anything into a foreign window.
                    bool guarded = false;
                    try { RequireForeground(inputHandle); } catch (InvalidOperationException) { guarded = true; }
                    Check("foreground mismatch aborts injection", guarded);
                    SendKey(cardHandle, 0x1B); Pump(80);
                    Check("focused card Esc closes it", !keyboardCard.IsVisible);
                    external.Activate(); Pump(80);
                    var timed = Card(sample); owned.Add(timed); timed.Show(); Pump(100);
                    var timedCopy = Ux402VisualMetrics.Descendants(timed).OfType<Button>().Single(b => AutomationProperties.GetAcceleratorKey(b) == "C");
                    Click(inputHandle, timedCopy);
                    Check("automatic card remains passive after copy click", GetForegroundWindow() == inputHandle && !timed.IsActive);
                    MoveCursor(inputHandle, 70, 100);
                    var closeWatch = Stopwatch.StartNew();
                    while (timed.IsVisible && closeWatch.Elapsed < TimeSpan.FromSeconds(8)) Pump(100);
                    Check($"passive clicked card closes after pointer leaves ({closeWatch.Elapsed.TotalSeconds:0.00}s)", !timed.IsVisible && GetForegroundWindow() == inputHandle);
                    MoveCursor(inputHandle, originalCursor.X, originalCursor.Y);
                    report.AppendLine($"External input measurement: PID={external.Id}; final text={external.Text}; 4 scenarios x C/E/P/S/Esc; all key and pointer events foreground-guarded.");
                }
                finally
                {
                    IntPtr current = GetForegroundWindow();
                    if (current == inputHandle || current == cardHandle) MoveCursor(current, originalCursor.X, originalCursor.Y);
                    keyboardCard.Close();
                }
            }

            foreach (string language in new[] { "ko", "en" })
            using (MyCapture.Core.Localization.UiText.UseLanguage(language))
            foreach (AppTheme theme in new[] { AppTheme.Midnight, AppTheme.Glass, AppTheme.Workspace, AppTheme.Daylight, AppTheme.GlassLight, AppTheme.HighContrast })
            {
                ThemeService.Apply(theme);
                var receipt = Card(sample); owned.Add(receipt); receipt.IsKept = true;
                receipt.Show(); Pump(70); receipt.UpdateLayout();
                var keep = ((StackPanel)((Border)receipt.Content).Child).Children.OfType<DockPanel>().First().Children.OfType<ToggleButton>().Single();
                string locked = ((System.Windows.Shapes.Path)keep.Content).Data.ToString();
                receipt.IsKept = false;
                string unlocked = ((System.Windows.Shapes.Path)keep.Content).Data.ToString();
                Check($"{language}/{theme}: keep on/off glyphs differ from each other and screen pin",
                    locked != unlocked && locked != ((Geometry)receipt.FindResource("Icon.Pin")).ToString()
                    && unlocked != ((Geometry)receipt.FindResource("Icon.Pin")).ToString());
                receipt.IsKept = true;
                var grid = ((StackPanel)((Border)receipt.Content).Child).Children.OfType<DockPanel>().First().Children.OfType<UniformGrid>().Single();
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
            Check("preview dimensions reflect edited crop", ((StackPanel)((Border)updatedCard.Content).Child).Children.OfType<DockPanel>().Last()
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
        Size client = Ux402VisualMetrics.ClientSize(window);
        var image = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(client.Width * scale)), Math.Max(1, (int)Math.Ceiling(client.Height * scale)), dpi, dpi, PixelFormats.Pbgra32);
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
    private static void RequireForeground(IntPtr expected)
    {
        IntPtr actual = GetForegroundWindow();
        if (expected == IntPtr.Zero || actual != expected)
            throw new InvalidOperationException($"Input aborted: expected foreground {expected}, actual {actual}.");
    }
    private static void SendKey(IntPtr target, byte key) { InjectKey(target, key, 0); InjectKey(target, key, 2); }
    private static void Click(IntPtr foreground, FrameworkElement control)
    {
        Point point = control.PointToScreen(new Point(control.ActualWidth / 2, control.ActualHeight / 2));
        MoveCursor(foreground, (int)point.X, (int)point.Y);
        MouseButton(foreground, 0x0002); MouseButton(foreground, 0x0004); Pump(100);
    }

    private static void DragToExternal(IntPtr foreground, FrameworkElement preview)
    {
        Point start = preview.PointToScreen(new Point(preview.ActualWidth / 2, preview.ActualHeight / 2));
        MoveCursor(foreground, (int)start.X, (int)start.Y); MouseButton(foreground, 0x0002); Pump(50);
        // OLE's drag loop can suspend dispatcher timers. The bounded native input
        // worker remains independent of that loop and never traverses other apps.
        Task gesture = Task.Run(() =>
        {
            MoveCursor(foreground, (int)start.X + 12, (int)start.Y + 2);
            Thread.Sleep(150);
            MoveCursor(foreground, 120, 110);
            Thread.Sleep(150);
            MouseButton(foreground, 0x0004);
        });
        Await(gesture); Pump(200);
    }

    private static void VerifyEditorMouseShortcuts(BitmapSource sample, List<Window> owned, Action<string, bool> check)
    {
        var rect = new RectD(0, 0, sample.PixelWidth, sample.PixelHeight);
        var window = new AnnotationEditorWindow(new FrozenFrame(sample, rect, null, 0), rect, sample);
        owned.Add(window); window.Show(); Pump(150); window.Activate(); window.Editor.Focus();
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        ButtonBase Button(string key) => Ux402VisualMetrics.Descendants(window).OfType<ButtonBase>()
            .Single(b => AutomationProperties.GetAcceleratorKey(b) == key && (key != "X" || b is ToggleButton));
        Click(hwnd, Button("A"));
        check("mouse rail click returns editor keyboard focus", ReferenceEquals(Keyboard.FocusedElement, window.Editor));
        foreach (var (key, hint) in new[] { ((byte)0x52, "R"), ((byte)0x45, "E"), ((byte)0x58, "X") })
        {
            SendKey(hwnd, key); Pump(60);
            check($"native rail click then {hint} selects tool", ((ToggleButton)Button(hint)).IsChecked == true);
        }
        SendKey(hwnd, 0x1B); Pump(50);
        check("native Esc cancels pending crop", window.Editor.PendingCrop is null && window.IsVisible);
        Click(hwnd, Button("R"));
        var swatches = (WrapPanel)typeof(AnnotationEditorControl).GetField("_swatchPanel", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window.Editor)!;
        swatches.BringIntoView(); Pump(50);
        check("native color swatch is visible", swatches.IsVisible);
        Click(hwnd, swatches.Children.OfType<Button>().First());
        check("mouse color swatch returns editor keyboard focus", ReferenceEquals(Keyboard.FocusedElement, window.Editor));
        SendKey(hwnd, 0x52); Pump(50);
        check("native color swatch click then R selects Rectangle", ((ToggleButton)Button("R")).IsChecked == true);
        var customInput = Ux402VisualMetrics.Descendants(window).OfType<TextBox>().Single(b => AutomationProperties.GetName(b) == UiText.Get("Tools.CustomColor"));
        customInput.BringIntoView(); Pump(50);
        Click(hwnd, customInput);
        var customApply = Ux402VisualMetrics.Descendants(window).OfType<Button>().Single(b => AutomationProperties.GetName(b) == UiText.Get("Tools.CustomColor"));
        Click(hwnd, customApply);
        check("mouse custom color apply leaves input and returns editor focus", ReferenceEquals(Keyboard.FocusedElement, window.Editor));
        SendKey(hwnd, 0x45); Pump(50);
        check("native custom color apply then E selects Ellipse", ((ToggleButton)Button("E")).IsChecked == true);
        Click(hwnd, Button("X"));
        var widthInput = (TextBox)typeof(AnnotationEditorControl).GetField("_cropWidth", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window.Editor)!;
        var heightInput = (TextBox)typeof(AnnotationEditorControl).GetField("_cropHeight", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window.Editor)!;
        Click(hwnd, widthInput); widthInput.Text = "240"; SendKey(hwnd, 0x0D); Pump(50);
        check("native width Enter confirms value and preserves pending crop/input focus", window.Editor.PendingCrop?.Region.Width == 240
            && window.Editor.CreateCurrentImageSnapshot().CanvasWidth == 320 && ReferenceEquals(Keyboard.FocusedElement, widthInput));
        Click(hwnd, heightInput); heightInput.Text = "100"; SendKey(hwnd, 0x0D); Pump(50);
        check("native height Enter remains editable after width Enter", window.Editor.PendingCrop?.Region.Height == 100 && ReferenceEquals(Keyboard.FocusedElement, heightInput));
        Click(hwnd, widthInput); widthInput.Text = "abc"; SendKey(hwnd, 0x0D); Pump(50);
        Click(hwnd, Button("Ctrl+Enter"));
        check("native Done blocks invalid input and preserves text/error", window.IsVisible && window.Editor.PendingCrop is not null
            && widthInput.Text == "abc" && !string.IsNullOrEmpty(AutomationProperties.GetHelpText(widthInput)));
        SendKey(hwnd, 0x1B); Pump(50);
        var topCrop = Ux402VisualMetrics.Descendants(window).OfType<Button>()
            .Single(b => AutomationProperties.GetAcceleratorKey(b) == "X");
        Click(hwnd, topCrop); window.Editor.PendingCrop!.SetWidth(200);
        SendKey(hwnd, 0x0D); Pump(80);
        check("native top crop button click then Enter applies crop", window.Editor.PendingCrop is null && window.Editor.CreateCurrentImageSnapshot().CanvasWidth == 200);
        Click(hwnd, Button("X")); SendKey(hwnd, 0x1B); Pump(50);
        check("native crop button click then Esc cancels", window.Editor.PendingCrop is null && window.IsVisible);
        window.Editor.Focus(); SendKey(hwnd, 0x09); Pump(50);
        check("native Tab retains button keyboard navigation", Keyboard.FocusedElement is ButtonBase);
        Click(hwnd, Button("W"));
        check("mouse top command returns editor keyboard focus", ReferenceEquals(Keyboard.FocusedElement, window.Editor));
        SendKey(hwnd, 0x1B); Pump(80);
        check("native top button click then Esc closes editor", !window.IsVisible);
    }
    private static void InjectKey(IntPtr target, byte key, uint flags)
    {
        RequireForeground(target);
        KeyEvent(key, 0, flags, UIntPtr.Zero);
    }
    private static void MoveCursor(IntPtr target, int x, int y)
    {
        RequireForeground(target);
        if (!SetCursorPos(x, y)) throw new InvalidOperationException("Diagnostic cursor move failed.");
    }
    private static void MouseButton(IntPtr target, uint flags)
    {
        RequireForeground(target);
        MouseEvent(flags, 0, 0, 0, UIntPtr.Zero);
    }
    [DllImport("user32.dll", EntryPoint = "mouse_event")] private static extern void MouseEvent(uint flags, uint x, uint y, uint data, UIntPtr extra);

    private sealed class ExternalInputFixture : IDisposable
    {
        private readonly Process _process;
        internal IntPtr Handle { get; }
        internal int Id => _process.Id;
        private ExternalInputFixture(Process process, IntPtr handle) { _process = process; Handle = handle; }
        internal static ExternalInputFixture Start()
        {
            var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"))
                { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-STA"); start.ArgumentList.Add("-Command");
            start.ArgumentList.Add("Add-Type -AssemblyName System.Windows.Forms; " +
                "$f=New-Object System.Windows.Forms.Form; $f.Text='MyCapture external input measurement'; " +
                "$f.Width=340; $f.Height=160; $f.StartPosition='Manual'; $f.Left=40; $f.Top=40; " +
                "$t=New-Object System.Windows.Forms.TextBox; $t.Multiline=$true; $t.Dock='Fill'; $f.Controls.Add($t); " +
                "$t.AllowDrop=$true; $t.Add_DragEnter({param($sender,$e) if($e.Data.GetDataPresent('FileDrop')){$e.Effect=[System.Windows.Forms.DragDropEffects]::Copy}}); " +
                "$t.Add_DragDrop({param($sender,$e) $t.AppendText('[drop:'+($e.Data.GetData('FileDrop')[0])+']')}); " +
                "$t.Add_KeyDown({param($sender,$e) if($e.Control -and $e.KeyCode -eq 'V' -and [System.Windows.Forms.Clipboard]::ContainsImage()){$img=[System.Windows.Forms.Clipboard]::GetImage(); $t.AppendText('[image:'+$img.Width+'x'+$img.Height+']'); $img.Dispose(); $e.SuppressKeyPress=$true}}); " +
                "$f.Add_Shown({[System.Windows.Forms.InputLanguage]::CurrentInputLanguage=[System.Windows.Forms.InputLanguage]::FromCulture([System.Globalization.CultureInfo]::GetCultureInfo('en-US')); $f.Activate(); $t.Focus(); $f.Text='MyCapture external input measurement ready'}); " +
                "[System.Windows.Forms.Application]::Run($f)");
            Process process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start external input fixture.");
            try
            {
                var timeout = Stopwatch.StartNew();
                while (timeout.Elapsed < TimeSpan.FromSeconds(15))
                {
                    process.Refresh();
                    if (process.HasExited) throw new InvalidOperationException("External fixture exited before showing its window.");
                    if (process.MainWindowHandle is var hwnd && hwnd != IntPtr.Zero)
                    {
                        var title = new StringBuilder(128);
                        _ = SendMessage(hwnd, 0x000D, new IntPtr(title.Capacity), title);
                        if (!title.ToString().EndsWith(" ready", StringComparison.Ordinal)) { Pump(50); continue; }
                        if (GetForegroundWindow() != hwnd) _ = SetForegroundWindow(hwnd);
                        if (GetForegroundWindow() == hwnd) return new ExternalInputFixture(process, hwnd);
                    }
                    Pump(50);
                }
                throw new TimeoutException($"External fixture HWND={process.MainWindowHandle}, foreground={GetForegroundWindow()} did not acquire foreground; no input was injected.");
            }
            catch { if (!process.HasExited) process.Kill(); process.Dispose(); throw; }
        }
        internal string Text
        {
            get
            {
                var text = new StringBuilder(1024);
                _ = SendMessage(GetWindow(Handle, 5 /* GW_CHILD */), 0x000D /* WM_GETTEXT */, new IntPtr(text.Capacity), text);
                return text.ToString();
            }
        }
        internal void Activate()
        {
            if (!SetForegroundWindow(Handle)) throw new InvalidOperationException("External fixture activation failed.");
        }
        public void Dispose()
        {
            if (!_process.HasExited)
            {
                _process.CloseMainWindow();
                if (!_process.WaitForExit(3000)) _process.Kill();
            }
            _process.Dispose();
        }
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wParam, StringBuilder text);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll", EntryPoint = "keybd_event")] private static extern void KeyEvent(byte key, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(IntPtr hwnd);
}
