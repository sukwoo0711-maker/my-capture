using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.App.Editing;
using MyCapture.App.Ocr;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using MyCapture.Ocr;
using MyCapture.Platform.Capture;

internal static class Program
{
    private static readonly object Gate = new();
    private static readonly List<object> Measurements = [];
    private static string Output = "", Stage = "starting";
    private static string[] Arguments = [];
    private static readonly Stopwatch Lifetime = Stopwatch.StartNew();
    private static long Peak;
    private static bool Holding;
    private static int LimitSeconds = 60;
    private static object? LatestHoldState;

    public static int Start(string[] args)
    {
        if (args.Length is not (4 or 5) || args[0] is not ("ocr" or "editor" or "editor-render" or "editor-hold" or "ocr-hold" or "ocr-editor-hold")
            || !int.TryParse(args[1], out int count) || count is < 1 or > 1_000_000 || args[2] is not ("long" or "lines")) return 2;
        Holding = args[0].EndsWith("-hold", StringComparison.Ordinal);
        if (Holding && (count > 100_000 || args.Length > 4)) return 2;
        LimitSeconds = Holding ? 600 : 60;
        Arguments = args;
        Output = Path.GetFullPath(args[3]);
        if (File.Exists(Output)) throw new IOException("Use a new output file; preserve previous evidence.");
        using var guard = new System.Threading.Timer(_ =>
        {
            using Process own = Process.GetCurrentProcess();
            own.Refresh();
            Interlocked.Exchange(ref Peak, Math.Max(Peak, own.PrivateMemorySize64));
            if (own.PrivateMemorySize64 > 2L * 1024 * 1024 * 1024 || Lifetime.Elapsed.TotalSeconds > LimitSeconds)
            {
                try { Save("GUARD", $"Owned child exceeded {LimitSeconds} seconds or 2 GiB private memory."); }
                finally { Environment.Exit(124); }
            }
        }, null, 0, 100);
        try
        {
            if (args.Length > 4 && args[4].Contains("line-selection", StringComparison.Ordinal))
                AppContext.SetSwitch("Switch.System.Windows.Controls.Text.UseAdornerForTextboxSelectionRendering", false);
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            foreach (string dictionary in new[] { "Tokens", "Symbols", "Controls" })
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{dictionary}.xaml")
                });
            string text = Fixture(int.Parse(args[1]), args[2]);
            if (Holding) Hold(text, args[0]);
            else if (args[0] == "ocr") Ocr(text);
            else if (args[0] == "editor-render") RenderEditor(text);
            else Editor(text);
            Save(Holding ? "HOLD_CLOSED" : "PASS", null);
            app.Shutdown();
            return 0;
        }
        catch (Exception error)
        {
            Save("FAIL", error.ToString());
            return 1;
        }
    }

    private static string Fixture(int count, string shape)
    {
        const string seed = "검증 가나다😀 abcXYZ0123456789 ";
        var text = new StringBuilder(count + 40);
        while (text.Length < count)
        {
            text.Append(seed);
            if (shape == "lines") text.Append('\n');
        }
        text.Length = count;
        if (char.IsHighSurrogate(text[^1])) text[^1] = '가';
        return text.ToString();
    }

    private static void Ocr(string text)
    {
        var window = new OcrResultWindow
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -4000, Top = -4000, ShowActivated = false, ShowInTaskbar = false,
            Width = 1040, Height = 720,
        };
        TextBox box = Field<TextBox>(window, "_textBox");
        if (Arguments.Length > 4 && Arguments[4].Contains("readonly-no-ime", StringComparison.Ordinal))
        {
            if (!box.IsReadOnly) throw new InvalidOperationException("IME ablation requires a readonly result.");
            System.Windows.Input.InputMethod.SetIsInputMethodEnabled(box, false);
        }
        ConfigureHost(box);
        RoutedEventHandler? suppressAutoSelection = null;
        if (Arguments.Length > 4 && Arguments[4] == "no-auto-selection")
        {
            suppressAutoSelection = (_, _) =>
            {
                if (box.Text.Length > 0 && box.SelectionLength == box.Text.Length)
                {
                    box.Select(0, 0);
                    box.ScrollToHome();
                }
            };
            box.SelectionChanged += suppressAutoSelection;
        }
        Measure("ocr.ShowResult", () => window.ShowResult(OcrResult.Success(text, "ko-KR", [], TimeSpan.Zero), "Synthetic stress"));
        if (suppressAutoSelection is not null) box.SelectionChanged -= suppressAutoSelection;
        Measure("ocr.layout-render", () => Settle(window));
        DescribeViewport(box, "ocr.viewport-start");
        Equal(text, box.Text, "OCR whole content");
        Measure("ocr.select-all", () => box.SelectAll());
        Equal(text, box.SelectedText, "OCR selected copy value");
        Measure("ocr.selection-settle", () => Settle(window));
        Measure("ocr.scroll-start-no-selection", () => { box.Select(0, 0); box.ScrollToHome(); Settle(window); });
        Measure("ocr.scroll-end-no-selection", () => { box.ScrollToEnd(); Settle(window); });
        DescribeViewport(box, "ocr.viewport-end");
        Measure("ocr.repeated-result", () => window.ShowResult(OcrResult.Success(text, "ko-KR", [], TimeSpan.Zero), "Synthetic stress"));
        Equal(text, window.ExtractionText, "OCR repeat content");
        Measure("ocr.close", () => { window.CloseForExit(); Pump(); });
        if (window.IsVisible) throw new InvalidOperationException("OCR did not close.");
    }

    private static void Editor(string text)
    {
        byte[] pixels = new byte[800 * 600 * 4];
        Array.Fill(pixels, (byte)240);
        BitmapSource bitmap = BitmapSource.Create(800, 600, 96, 96, PixelFormats.Bgr32, null, pixels, 3200);
        bitmap.Freeze();
        var region = new RectD(0, 0, 800, 600);
        var window = new AnnotationEditorWindow(new FrozenFrame(bitmap, region, null, 0), region, bitmap)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -4000, Top = -4000, Width = 1000, Height = 640,
            ShowActivated = false, ShowInTaskbar = false, ConfirmClose = () => EditorCloseChoice.DontSave,
        };
        Measure("editor.open", () => { window.Show(); Settle(window); });
        AnnotationEditorControl editor = window.Editor;
        Invoke(editor, "PlaceTextBox", new PointD(40, 40));
        TextBox box = Field<TextBox>(editor, "_activeTextBox");
        ConfigureHost(box);
        if (Arguments.Length > 4 && Arguments[4].Contains("bounded-view", StringComparison.Ordinal))
        {
            Canvas canvas = Field<Canvas>(editor, "_overlayCanvas");
            box.MaxHeight = Math.Max(40, canvas.ActualHeight - Canvas.GetTop(box));
            box.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        }
        Measure("editor.insert-selection", () => { box.BeginChange(); box.SelectedText = text; box.EndChange(); });
        Equal(text, box.Text, "Editor inserted all content");
        Measure("editor.layout-render", () => Settle(window));
        DescribeViewport(box, "editor.viewport-start");
        Measure("editor.append", () => { box.BeginChange(); box.AppendText("추가😀"); box.EndChange(); });
        Equal(text + "추가😀", box.Text, "Editor append");
        SettleEdit(window, "editor.append-settle");
        Measure("editor.native-undo", () => box.Undo());
        Equal(text, box.Text, "Editor native undo");
        SettleEdit(window, "editor.undo-settle");
        Measure("editor.native-redo", () => box.Redo());
        Equal(text + "추가😀", box.Text, "Editor native redo");
        SettleEdit(window, "editor.redo-settle");
        DescribeViewport(box, "editor.viewport-before-selection");
        Measure("editor.select-all", () => box.SelectAll());
        Equal(text + "추가😀", box.SelectedText, "Editor selected copy value");
        Measure("editor.selection-settle", () => Settle(window));
        Measure("editor.scroll-start-no-selection", () => { box.Select(0, 0); box.ScrollToHome(); Settle(window); });
        Measure("editor.scroll-end-no-selection", () => { box.ScrollToEnd(); Settle(window); });
        DescribeViewport(box, "editor.viewport-end");
        Equal(text + "추가😀", editor.CreateCurrentImageSnapshot().Items.OfType<TextAnnotation>().Single().Text, "Snapshot/export whole text");
        Measure("editor.commit", () => Invoke(editor, "CommitActiveText"));
        AnnotationEditorController controller = Field<AnnotationEditorController>(editor, "_controller");
        Equal(text + "추가😀", controller.Document.Items.OfType<TextAnnotation>().Single().Text, "Committed document");
        var committed = controller.Document.Items.OfType<TextAnnotation>().Single();
        lock (Gate) Measurements.Add(new { Contract = "Complete fitted document geometry", committed.Rect, committed.FontSize, committed.AutoSize });
        Measure("editor.document-undo", () => controller.PerformUndo());
        Equal("", controller.Document.Items.OfType<TextAnnotation>().Single().Text, "Document undo");
        Measure("editor.document-redo", () => controller.PerformRedo());
        Equal(text + "추가😀", controller.Document.Items.OfType<TextAnnotation>().Single().Text, "Document redo");
        Measure("editor.close", () => { window.Close(); Pump(); });
        if (window.IsVisible) throw new InvalidOperationException("Editor did not close.");
    }

    private static void RenderEditor(string text)
    {
        byte[] pixels = new byte[800 * 600 * 4];
        Array.Fill(pixels, (byte)240);
        BitmapSource bitmap = BitmapSource.Create(800, 600, 96, 96, PixelFormats.Bgr32, null, pixels, 3200);
        bitmap.Freeze();
        var region = new RectD(0, 0, 800, 600);
        var window = new AnnotationEditorWindow(new FrozenFrame(bitmap, region, null, 0), region, bitmap)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -4000, Top = -4000, Width = 1000, Height = 640,
            ShowActivated = false, ShowInTaskbar = false, ConfirmClose = () => EditorCloseChoice.DontSave,
        };
        window.Show();
        Settle(window);
        Invoke(window.Editor, "PlaceTextBox", new PointD(40, 40));
        TextBox box = Field<TextBox>(window.Editor, "_activeTextBox");
        box.SelectedText = text;
        box.Select(0, 0);
        box.ScrollToHome();
        Settle(window);
        SettleAnimation();
        SaveWindow(window, Path.ChangeExtension(Output, null) + "-top.png");
        box.ScrollToEnd();
        Settle(window);
        SaveWindow(window, Path.ChangeExtension(Output, null) + "-bottom.png");
        Equal(text, box.Text, "Render fixture whole content");
        var caption = window.Editor.CreateCurrentImageSnapshot().Items.OfType<TextAnnotation>().Single();
        lock (Gate) Measurements.Add(new { Contract = "Render fixture complete document geometry", caption.Rect, caption.AutoSize });
        window.Close();
        Pump();
        if (window.IsVisible) throw new InvalidOperationException("Render fixture did not close.");
    }

    private static void Hold(string text, string mode)
    {
        string initialImeState = InputMethod.Current.ImeState.ToString();
        var compositions = new Dictionary<TextBox, CompositionTelemetry>();
        void ObserveComposition(TextBox box)
        {
            if (compositions.ContainsKey(box)) return;
            var counts = new CompositionTelemetry();
            compositions.Add(box, counts);
            TextCompositionManager.AddPreviewTextInputStartHandler(box, (_, _) => counts.Start++);
            TextCompositionManager.AddPreviewTextInputUpdateHandler(box, (_, _) => counts.Update++);
            TextCompositionManager.AddPreviewTextInputHandler(box, (_, _) => counts.Completed++);
        }
        var windows = new List<Window>();
        AnnotationEditorWindow? editorWindow = null;
        OcrResultWindow? ocrWindow = null;
        TextBox? lastEditorBox = null;
        if (mode is "ocr-hold" or "ocr-editor-hold")
        {
            ocrWindow = new OcrResultWindow
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Width = 1040, Height = 720, ShowInTaskbar = true,
                Title = "MyCapture TextStress OCR (synthetic)",
            };
            windows.Add(ocrWindow);
            ocrWindow.ShowResult(OcrResult.Success(text, "ko-KR", [], TimeSpan.Zero), "Synthetic hold fixture");
            Equal(text, ocrWindow.ExtractionText, "Hold OCR initial content");
        }
        void OpenEditor()
        {
            byte[] pixels = new byte[800 * 600 * 4];
            Array.Fill(pixels, (byte)240);
            var bitmap = BitmapSource.Create(800, 600, 96, 96, PixelFormats.Bgr32, null, pixels, 3200);
            bitmap.Freeze();
            var region = new RectD(0, 0, 800, 600);
            editorWindow = new AnnotationEditorWindow(new FrozenFrame(bitmap, region, null, 0), region, bitmap)
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Width = 1000, Height = 640, ShowActivated = true, ShowInTaskbar = true,
                Title = "MyCapture TextStress Editor (synthetic)",
                ConfirmClose = () => EditorCloseChoice.DontSave,
            };
            windows.Add(editorWindow);
            editorWindow.Show();
            Settle(editorWindow);
            Invoke(editorWindow.Editor, "PlaceTextBox", new PointD(40, 40));
            lastEditorBox = Field<TextBox>(editorWindow.Editor, "_activeTextBox");
            ObserveComposition(lastEditorBox);
            lastEditorBox.BeginChange();
            lastEditorBox.SelectedText = text;
            lastEditorBox.EndChange();
            lastEditorBox.Select(0, 0);
            lastEditorBox.ScrollToHome();
            Settle(editorWindow);
            Equal(text, lastEditorBox.Text, "Hold editor initial content");
        }
        if (mode == "editor-hold") OpenEditor();
        var frame = new DispatcherFrame();
        void Observe()
        {
            // Product OCR Close hides its reusable window. Open a fresh editor only after
            // that scope loses focus, so its live textbox is not prematurely committed.
            if (mode == "ocr-editor-hold" && editorWindow is null && ocrWindow is { IsVisible: false })
            {
                ocrWindow.CloseForExit();
                OpenEditor();
            }
            TextBox? currentEditor = editorWindow is null ? null : Field<TextBox?>(editorWindow.Editor, "_activeTextBox");
            if (currentEditor is not null) lastEditorBox = currentEditor;
            var ocrBox = ocrWindow is null ? null : Field<TextBox>(ocrWindow, "_textBox");
            if (currentEditor is not null) ObserveComposition(currentEditor);
            if (ocrBox is not null) ObserveComposition(ocrBox);
            object? State(TextBox? box) => box is null ? null : new
            {
                Length = box.Text.Length,
                Sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(box.Text))),
                Suffix = box.Text[^Math.Min(64, box.Text.Length)..],
                box.IsReadOnly, InputMethodEnabled = System.Windows.Input.InputMethod.GetIsInputMethodEnabled(box),
                box.SelectionStart, box.SelectionLength, box.CaretIndex, box.IsKeyboardFocusWithin,
                box.IsKeyboardFocused, box.IsLoaded, box.VerticalOffset, box.ViewportHeight, box.ExtentHeight,
                Composition = compositions.TryGetValue(box, out var counts)
                    ? new { counts.Start, counts.Update, counts.Completed } : null,
            };
            LatestHoldState = new
            {
                ManualFixture = true, Mode = mode, EditorHasLiveTextBox = currentEditor is not null,
                Editor = State(lastEditorBox), Ocr = State(ocrBox),
                InitialImeState = initialImeState, CurrentImeState = InputMethod.Current.ImeState.ToString(),
                EditorPendingAfterOcr = mode == "ocr-editor-hold" && editorWindow is null,
                VisibleWindows = windows.Count(window => window.IsVisible),
            };
            Save("HOLD_READY", null);
            if (windows.All(window => !window.IsVisible)) frame.Continue = false;
        }
        Stage = "manual-hold";
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => Observe();
        try
        {
            Observe();
            timer.Start();
            Dispatcher.PushFrame(frame);
            Observe();
        }
        finally
        {
            timer.Stop();
            ocrWindow?.CloseForExit();
            if (editorWindow?.IsVisible == true) editorWindow.Close();
        }
    }

    private sealed class CompositionTelemetry
    {
        public int Start;
        public int Update;
        public int Completed;
    }

    private static void SaveWindow(Window window, string path)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        encoder.Save(stream);
    }

    private static void Equal(string expected, string actual, string contract)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal)) throw new InvalidOperationException(contract + " differs.");
        lock (Gate) Measurements.Add(new { Contract = contract, Length = actual.Length, Sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(actual))) });
        Save("RUNNING", null);
    }

    private static void Measure(string name, Action action)
    {
        Stage = name;
        Save("RUNNING", null);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        var watch = Stopwatch.StartNew();
        action();
        watch.Stop();
        lock (Gate) Measurements.Add(new { Stage = name, Milliseconds = watch.Elapsed.TotalMilliseconds, AllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocated });
        Save("RUNNING", null);
    }

    private static void Settle(Window window) { window.UpdateLayout(); Pump(); window.UpdateLayout(); }
    private static void SettleEdit(Window window, string stage)
    {
        if (Arguments.Length > 4 && Arguments[4].Contains("settled-edits", StringComparison.Ordinal))
            Measure(stage, () => Settle(window));
    }
    private static void SettleAnimation()
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromMilliseconds(350) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
    private static void ConfigureHost(TextBox box)
    {
        if (Arguments.Length > 4 && Arguments[4].Contains("line-selection", StringComparison.Ordinal))
        {
            box.SelectionOpacity = 0.4;
            box.SelectionTextBrush = box.Foreground;
        }
        box.ApplyTemplate();
        var host = (ScrollViewer)box.Template.FindName("PART_ContentHost", box);
        lock (Gate) Measurements.Add(new { Contract = "Actual PART_ContentHost before diagnostic override", CanContentScroll = host.CanContentScroll, box.ActualWidth, box.ActualHeight });
        if (Arguments.Length > 4 && Arguments[4].Contains("content-scroll", StringComparison.Ordinal)) host.CanContentScroll = true;
    }
    private static void DescribeViewport(TextBox box, string stage)
    {
        var host = (ScrollViewer)box.Template.FindName("PART_ContentHost", box);
        var view = host.Content as System.Windows.Controls.Primitives.IScrollInfo;
        object? internalViewport = host.Content?.GetType().GetProperty("Viewport", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(host.Content);
        object? viewport = internalViewport is Rect rect ? new
        {
            rect.IsEmpty, X = rect.IsEmpty ? null : (double?)rect.X, Y = rect.IsEmpty ? null : (double?)rect.Y,
            Width = rect.IsEmpty ? null : (double?)rect.Width, Height = rect.IsEmpty ? null : (double?)rect.Height,
        } : null;
        lock (Gate) Measurements.Add(new
        {
            Contract = stage, host.CanContentScroll, box.ActualWidth, box.ActualHeight, box.SelectionLength, box.CaretIndex,
            host.ViewportWidth, host.ViewportHeight, host.ExtentHeight, host.VerticalOffset, host.ScrollableHeight,
            ContentHeight = (host.Content as FrameworkElement)?.ActualHeight,
            ViewScrollOwnerMatchesHost = ReferenceEquals(view?.ScrollOwner, host),
            ViewViewportHeight = view?.ViewportHeight, ViewVerticalOffset = view?.VerticalOffset,
            InternalViewViewport = viewport,
        });
        Save("RUNNING", null);
    }
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        _ = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
    private static void Invoke(object owner, string method, params object[] values) => owner.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(owner, values);
    private static void Save(string status, string? error)
    {
        lock (Gate) File.WriteAllText(Output, JsonSerializer.Serialize(new
        {
            Status = status, Stage, Arguments, Error = error, ProcessId = Environment.ProcessId,
            PeakPrivateBytes = Peak, TotalMilliseconds = Lifetime.Elapsed.TotalMilliseconds,
            ProductAssembly = typeof(AnnotationEditorWindow).Assembly.Location,
            FrameworkDescription = RuntimeInformation.FrameworkDescription,
            RuntimeVersion = Environment.Version.ToString(),
            Limits = Holding
                ? "600s/2GiB owned manual fixture; synthetic text; no product App startup/profile/persistence/Clipboard APIs; native input results require operator observation; HOLD_CLOSED is lifecycle completion, not automated IME validation"
                : "60s whole child; 2GiB private memory; synthetic text; no Clipboard APIs; actual production controls; first WPF layout/render dispatch is not DWM/startup/persistence latency",
            CoreClrPath = typeof(object).Assembly.Location,
            WpfPath = typeof(Window).Assembly.Location,
            HoldState = LatestHoldState,
            Measurements,
        }, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
    }
}
