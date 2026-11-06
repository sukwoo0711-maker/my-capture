using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.App.Diagnostics;
using MyCapture.App.Editing;
using MyCapture.App.Themes;
using MyCapture.Core.Annotations;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using MyCapture.Core.Themes;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// Explicit, synthetic render/measurement evidence. No screen pixels, input injection,
/// clipboard, application settings or production storage are used. Run this opt-in Fact
/// alone: its plain Application exists only to supply the shipped palette to follower brushes.
/// </summary>
public sealed class EditorAestheticEvidenceTests
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private sealed record Combo(double Scale, AppTheme Theme, string Language)
    {
        internal string Name => $"s{Scale * 100:000}-{(Theme == AppTheme.Glass ? "dark" : Theme == AppTheme.GlassLight ? "light" : "hc")}-{(Language == "ko-KR" ? "ko" : "en")}";
    }
    private sealed record Scene(string Id, string Name, bool Narrow = false);
    private sealed record Picture(string Name, string Path, int Width, int Height, bool Narrow);
    private static readonly Scene[] Scenes =
    [
        new("S01", "select-empty", true), new("S02", "alt-revealed"), new("S03", "rect-selected", true),
        new("S04", "arrow-selected"), new("S05", "shapes-unselected"),
        new("S06a", "pen-preview"), new("S06b", "pen-preview-thick"), new("S06c", "highlighter-preview"),
        new("S07", "text-editing", true), new("S07b", "text-editing-wrap", true), new("S08", "text-selected", true),
        new("S09", "step-numbers"), new("S10", "mosaic"), new("S10b", "mosaic-draft"), new("S11", "blur"),
        new("S12", "stamp"), new("S13", "crop", true), new("S14", "crop-dragging"), new("S15", "status-long"),
        new("S16", "small-actual"), new("S17", "large-fit"),
        new("S18-rest", "states"), new("S18-hover", "states"), new("S18-pressed", "states"),
        new("S18-focus", "states"), new("S18-disabled", "states"), new("S19", "save-menu"),
    ];

    [Fact]
    public void RenderEditorAestheticMatrix()
    {
        string? requested = Environment.GetEnvironmentVariable("MYCAPTURE_EDITOR_AES_EVIDENCE");
        if (string.IsNullOrWhiteSpace(requested)) return;
        string root = DiagnosticOutputPaths.Create(requested);
        string[] only = (Environment.GetEnvironmentVariable("MYCAPTURE_EDITOR_AES_ONLY") ?? string.Empty)
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        StaTestHost.Run(() => RenderMatrix(root, only));
    }

    private static void RenderMatrix(string root, string[] only)
    {
        // The prescribed filter starts a dedicated process. Refuse an application with
        // production services attached rather than touching any real profile or draft store.
        Assert.Null(Application.Current);
        Assert.Null(AnnotationEditorPreferences.Read);
        Assert.Null(AnnotationEditorPreferences.Write);
        Assert.Null(AnnotationEditorPreferences.ReadShowSourceWindowTitle);
        Assert.Null(AnnotationEditorPreferences.ReadPinHotkey);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        ResourceDictionary previousResources = app.Resources;
        bool? previousMotion = FluidMotion.AnimationsEnabledOverrideForTest;
        DpiScale? previousSizing = AnnotationEditorWindow.SizingDpiOverrideForTest;
        Func<bool>? previousHints = ShortcutPresentation.ReadEnabled;
        Func<string?>? previousStamp = AnnotationEditorPreferences.StampFolder;
        bool? previousContrast = ThemeService.SystemHighContrastOverrideForTest;
        SynchronizationContext? previousContext = SynchronizationContext.Current;
        var summary = new List<object>();
        string zoom = Folder(root, "zoom"), sheets = Folder(root, "sheets"), audit = Folder(root, "audit");
        string stampFolder = Folder(root, "isolated-stamp");
        var fixtures = new Dictionary<string, BitmapSource>
        {
            ["F1"] = Fixture(900, 520), ["F2"] = Fixture(320, 120), ["F3"] = Fixture(1920, 1080),
        };
        int rendered = 0;
        try
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            FluidMotion.AnimationsEnabledOverrideForTest = false;
            ThemeService.SystemHighContrastOverrideForTest = false;
            ShortcutPresentation.ReadEnabled = () => false;
            AnnotationEditorPreferences.StampFolder = () => stampFolder;
            // A synthetic logo, kept as evidence in the isolated store; no picker or cleanup.
            Save(StampImage(), DiagnosticOutputPaths.Child(stampFolder, StampStore.FileName));
            var combos = (from scale in new[] { 1d, 1.25, 1.5 }
                          from theme in new[] { AppTheme.Glass, AppTheme.GlassLight }
                          from language in new[] { "ko-KR", "en-US" }
                          select new Combo(scale, theme, language)).ToList();
            combos.Add(new Combo(1, AppTheme.HighContrast, "ko-KR"));
            string[] comboFilters = only.Where(token => combos.Any(c => c.Name.Equals(token, StringComparison.OrdinalIgnoreCase))).ToArray();
            string[] sceneFilters = only.Except(comboFilters, StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (Combo combo in combos)
            {
                if (comboFilters.Length > 0 && !comboFilters.Contains(combo.Name, StringComparer.OrdinalIgnoreCase)) continue;
                var wanted = Scenes.Where(s => Matches(s, sceneFilters)
                    && (combo.Theme != AppTheme.HighContrast || s.Id is "S01" or "S03" or "S13")
                    && (s.Id != "S19" || combo.Scale is 1 or 1.5)).ToArray();
                if (wanted.Length == 0) continue;
                using IDisposable languageScope = UiText.UseLanguage(combo.Language);
                ResourceDictionary resources = SystemHighContrastThemeTests.LoadTheme();
                ThemeService.ApplyResources(combo.Theme, resources, systemHighContrast: false);
                app.Resources = resources;
                AnnotationEditorWindow.SizingDpiOverrideForTest = new DpiScale(combo.Scale, combo.Scale);
                ShortcutPresentation.Refresh();
                string destination = Folder(root, combo.Name);
                var pictures = new List<Picture>();
                var rows = new List<Dictionary<string, object?>>();
                foreach (Scene scene in wanted)
                foreach (bool narrow in scene.Narrow ? new[] { false, true } : new[] { false })
                {
                    BitmapSource fixture = fixtures[scene.Id == "S16" ? "F2" : scene.Id == "S17" ? "F3" : "F1"];
                    using Host host = new(fixture, combo, resources, narrow, scene.Id == "S15");
                    Prepare(host, scene, combo.Language);
                    Settle(host.Window);
                    VerifyPalette(host.Surface, resources);
                    Assert.Equal(combo.Scale, VisualTreeHelper.GetDpi(host.Surface).PixelsPerDip, 6);
                    if (scene.Id == "S16") Assert.Equal(1 / combo.Scale, host.Surface.DipPerPixel, 6);
                    string name = scene.Id.StartsWith("S18-", StringComparison.Ordinal) ? scene.Id : scene.Id + "-" + scene.Name;
                    if (narrow) name += "-narrow";
                    BitmapSource bitmap;
                    object? popup = null;
                    FrameworkElement? separateRoot = null;
                    if (scene.Id == "S19")
                    {
                        // Real menu content, kept off the desktop. WPF would clamp an opened
                        // popup back onto a shared monitor, so render its separate visual root.
                        ContextMenu menu = ById<Button>(host.Editor, "EditorFlowSaveToFile").ContextMenu!;
                        menu.Resources = resources;
                        menu.SetResourceReference(Control.FontFamilyProperty, "Font.Ui");
                        ThemeFonts.ApplyText(menu);
                        VisualTreeHelper.SetRootDpi(menu, new DpiScale(combo.Scale, combo.Scale));
                        menu.ApplyTemplate();
                        menu.Measure(new Size(500, 500));
                        menu.Arrange(new Rect(menu.DesiredSize));
                        menu.UpdateLayout();
                        separateRoot = menu;
                        bitmap = Render(menu, menu.ActualWidth, menu.ActualHeight, combo.Scale);
                        popup = new { method = "real ContextMenu measured as separate root; native popup not opened", menu.ActualWidth, menu.ActualHeight };
                    }
                    else bitmap = Render(host.Window, host.Window.ActualWidth, host.Window.ActualHeight, combo.Scale);
                    string path = DiagnosticOutputPaths.Child(destination, name + ".png");
                    Save(bitmap, path);
                    pictures.Add(new Picture(name, path, bitmap.PixelWidth, bitmap.PixelHeight, narrow));
                    Dictionary<string, object?> row = AestheticAudit.Measure(host.Window, host.Editor, combo.Name, name, combo.Scale, separateRoot);
                    Assert.True((int)row["visibleElementCount"]! > 0, "The rendered root has no audited visible elements.");
                    Assert.NotEmpty((IEnumerable<object>)row["textContrast"]!);
                    row["render"] = new
                    {
                        file = name + ".png", bitmap.PixelWidth, bitmap.PixelHeight,
                        outerWidthDip = host.Window.ActualWidth, outerHeightDip = host.Window.ActualHeight,
                        host = nameof(AnnotationEditorWindow), capture = "RenderTargetBitmap over black HWND clear colour",
                        nativeCaptionIncluded = false, // DWM/non-client pixels are not part of a WPF Visual.
                        popup,
                    };
                    row["sceneState"] = SceneState(host);
                    rows.Add(row);
                    if (!narrow && combo.Scale is 1 or 1.5) SaveZooms(host, scene, combo, bitmap, zoom);
                    rendered++;
                    Console.WriteLine($"AES {combo.Name} {name}: {bitmap.PixelWidth}x{bitmap.PixelHeight}");
                }
                WriteJson(DiagnosticOutputPaths.Child(audit, combo.Name + ".json"), new
                {
                    schema = "editor-aesthetic-v1", combo = combo.Name, combo.Scale, theme = combo.Theme.ToString(), combo.Language,
                    scenes = rows,
                });
                Sheet(pictures, DiagnosticOutputPaths.Child(sheets, combo.Name + ".png"));
                Sheet(pictures.Where(p => p.Narrow).ToArray(), DiagnosticOutputPaths.Child(sheets, "narrow-" + combo.Name + ".png"));
                Sheet(pictures.Where(p => p.Name.StartsWith("S18-", StringComparison.Ordinal)).ToArray(), DiagnosticOutputPaths.Child(sheets, "states-" + combo.Name + ".png"));
                summary.Add(new
                {
                    combo = combo.Name, scenes = pictures.Select(p => p.Name).ToArray(), pngCount = pictures.Count,
                    surfaceBrushes = rows.First()["surfaceBrushes"],
                    flags = rows.SelectMany(row => ((IEnumerable<object>)row["flags"]!)).ToArray(),
                });
            }
            Assert.True(rendered > 0, "The optional combo/scene filter matched no scenes.");
            WriteJson(DiagnosticOutputPaths.Child(root, "summary.json"), new
            {
                schema = "editor-aesthetic-v1", filter = only, renderedScenes = rendered, combos = summary,
                skippedScenes = Array.Empty<string>(),
                limitations = new[]
                {
                    "Native DWM caption/frame is not in RenderTargetBitmap. Real AnnotationEditorWindow WPF visuals are captured; no caption is fabricated.",
                    "S19 uses real ContextMenu content as a separately measured root; no desktop popup is opened because WPF can reposition it onto the shared monitor.",
                    "Synthetic capture fixtures and synthetic MC logo only. Numeric flags are measurements/candidates, not aesthetic judgements.",
                    "S18 drives shipped template layers for visual state evidence; it does not simulate native pointer or keyboard focus.",
                },
            });
        }
        finally
        {
            AnnotationEditorPreferences.StampFolder = previousStamp;
            ShortcutPresentation.ReadEnabled = previousHints;
            ShortcutPresentation.Refresh();
            AnnotationEditorWindow.SizingDpiOverrideForTest = previousSizing;
            FluidMotion.AnimationsEnabledOverrideForTest = previousMotion;
            ThemeService.SystemHighContrastOverrideForTest = previousContrast;
            app.Resources = previousResources;
            app.Shutdown();
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }

    private static bool Matches(Scene scene, string[] filters) => filters.Length == 0 || filters.Any(token =>
        scene.Id.Equals(token, StringComparison.OrdinalIgnoreCase) || scene.Name.Equals(token, StringComparison.OrdinalIgnoreCase)
        || (scene.Id + "-" + scene.Name).Equals(token, StringComparison.OrdinalIgnoreCase)
        || (token.Equals("S06", StringComparison.OrdinalIgnoreCase) && scene.Id.StartsWith("S06", StringComparison.Ordinal))
        || (token.Equals("S18", StringComparison.OrdinalIgnoreCase) && scene.Id.StartsWith("S18-", StringComparison.Ordinal)));

    private sealed class Host : IDisposable
    {
        internal AnnotationEditorWindow Window { get; }
        internal AnnotationEditorControl Editor => Window.Editor;
        internal AnnotationEditorSurface Surface => Field<AnnotationEditorSurface>(Editor, "_surface");
        internal AnnotationEditorController Controller => Field<AnnotationEditorController>(Editor, "_controller");
        internal Host(BitmapSource bitmap, Combo combo, ResourceDictionary resources, bool narrow, bool source)
        {
            var region = new RectD(0, 0, bitmap.PixelWidth, bitmap.PixelHeight);
            var monitor = new MonitorInfo(@"\\.\DISPLAY_AES_FIXTURE", new RectD(0, 0, 4096, 2160), new RectD(0, 0, 4096, 2160), 96, true);
            var provenance = source ? MyCapture.Core.Capture.CaptureProvenance.Create(
                url: "https://github.com/acme/orders-api/blob/main/docs/fields.md?token=demo#L12",
                appName: "Microsoft Edge", windowTitle: "fields.md - orders-api") : null;
            Window = new AnnotationEditorWindow(new FrozenFrame(bitmap, region, monitor, 0), region, bitmap,
                pointerMonitor: monitor, provenance: provenance)
            {
                Resources = resources, WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -6000, Top = -6000, Width = narrow ? 680 : 1000, Height = narrow ? 460 : 640,
                ShowActivated = false, ShowInTaskbar = false, ConfirmClose = () => EditorCloseChoice.DontSave,
            };
            EventHandler activate = (EventHandler)typeof(AnnotationEditorWindow).GetMethod("OnContentRendered", Members, [typeof(object), typeof(EventArgs)])!
                .CreateDelegate(typeof(EventHandler), Window);
            Window.ContentRendered -= activate;
            Window.SetResourceReference(Control.FontFamilyProperty, "Font.Ui");
            ThemeFonts.ApplyText(Window);
            Editor.PickStampImagePath = () => null;
            _ = new WindowTrackSizeScope(Window, new Size(4096, 2160));
            try
            {
                Window.Show();
                Window.Width = narrow ? 680 : 1000;
                Window.Height = narrow ? 460 : 640;
                Window.Left = Window.Top = -6000;
                Settle(Window);
                DpiScale old = VisualTreeHelper.GetDpi(Surface);
                VisualTreeHelper.SetRootDpi(Window, new DpiScale(combo.Scale, combo.Scale));
                Invoke(Surface, "OnDpiChanged", old, new DpiScale(combo.Scale, combo.Scale));
                Settle(Window);
                Assert.Equal(narrow ? 680d : 1000d, Window.ActualWidth, 3);
                Assert.Equal(narrow ? 460d : 640d, Window.ActualHeight, 3);
                Assert.Equal(-6000, Window.Left);
                Assert.Equal(-6000, Window.Top);
                Assert.Equal(combo.Scale, VisualTreeHelper.GetDpi(Surface).PixelsPerDip, 6);
                Assert.Equal(combo.Scale, Field<double>(Surface, "_pixelsPerDip"), 6);
                Select(Editor, EditorTool.Select);
            }
            catch { Window.Close(); throw; }
        }
        public void Dispose()
        {
            ShortcutPresentation.SetAltRevealed(Window, false);
            Editor.CancelPendingMosaics(closing: true);
            Window.Close();
            Settle(Window);
            Assert.False(Window.IsVisible);
        }
    }

    private static void Prepare(Host host, Scene scene, string language)
    {
        var editor = host.Editor;
        var controller = host.Controller;
        string caption = language == "ko-KR" ? "주문 번호 확인" : "Check the order ID";
        void Draw(EditorTool tool, PointD start, PointD end)
        {
            Select(editor, tool);
            Settle(host.Window);
            editor.SimulatePointerDown(editor.ViewportPointOf(start));
            editor.SimulatePointerMove(editor.ViewportPointOf(end), true);
            editor.SimulatePointerUp(editor.ViewportPointOf(end));
            Settle(host.Window);
        }
        void Rectangle() => Draw(EditorTool.Rectangle, new PointD(120, 90), new PointD(420, 260));
        void Text(bool wrap, bool commit)
        {
            Select(editor, EditorTool.Text);
            Settle(host.Window);
            PointD at = wrap ? new(450, 140) : new(120, 130);
            editor.SimulatePointerDown(editor.ViewportPointOf(at));
            editor.SimulatePointerUp(editor.ViewportPointOf(at));
            TextBox box = Field<TextBox>(editor, "_activeTextBox");
            box.Text = wrap ? string.Join(" ", Enumerable.Repeat(caption + (language == "ko-KR" ? " 및 결제 금액과 처리 상태를 확인합니다." : " and verify the amount and processing status."), 5)) : caption;
            Settle(host.Window);
            if (wrap) Assert.True(box.LineCount > 1, "Long text did not wrap in the real live text box.");
            if (commit)
            {
                Invoke(editor, "CommitActiveText");
                Select(editor, EditorTool.Select);
                controller.SetSelected(controller.Document.Items.OfType<TextAnnotation>().Single());
            }
        }
        switch (scene.Id)
        {
            case "S01": Assert.False(Field<Button>(editor, "_undoButton").IsEnabled); Assert.False(Field<Button>(editor, "_redoButton").IsEnabled); break;
            case "S02": ShortcutPresentation.SetAltRevealed(host.Window, true); break;
            case "S03": Rectangle(); Select(editor, EditorTool.Select); Assert.IsType<RectangleAnnotation>(controller.Selected); break;
            case "S04": Draw(EditorTool.Arrow, new PointD(170, 130), new PointD(600, 350)); Select(editor, EditorTool.Select); break;
            case "S05":
                Draw(EditorTool.Rectangle, new PointD(80, 80), new PointD(280, 200));
                Draw(EditorTool.Ellipse, new PointD(340, 90), new PointD(540, 220));
                Draw(EditorTool.Arrow, new PointD(180, 300), new PointD(600, 360));
                Select(editor, EditorTool.Pen);
                editor.SimulatePointerDown(editor.ViewportPointOf(new PointD(70, 420)));
                foreach (PointD p in new[] { new PointD(170, 390), new PointD(240, 440), new PointD(330, 400) }) editor.SimulatePointerMove(editor.ViewportPointOf(p), true);
                editor.SimulatePointerUp(editor.ViewportPointOf(new PointD(330, 400)));
                Draw(EditorTool.Highlighter, new PointD(420, 440), new PointD(770, 440));
                Select(editor, EditorTool.Select); controller.SetSelected(null);
                Assert.Equal(5, controller.Document.Items.Count); break;
            case "S06a": case "S06b": case "S06c":
                Select(editor, scene.Id == "S06c" ? EditorTool.Highlighter : EditorTool.Pen);
                controller.StrokeThickness = scene.Id == "S06a" ? 4 : 24;
                controller.HighlighterThickness = 24;
                Invoke(editor, "UpdateInspector"); Settle(host.Window);
                Invoke(host.Surface, "UpdatePointerPreview", new PointD(450, 260), true);
                host.Surface.InvalidateVisual(); Assert.NotNull(host.Surface.BrushOutlineDip); break;
            case "S07": Text(false, false); break;
            case "S07b": Text(true, false); break;
            case "S08": Text(false, true); break;
            case "S09":
                Select(editor, EditorTool.StepNumber); Settle(host.Window);
                foreach (PointD point in new[] { new PointD(120, 130), new PointD(360, 250), new PointD(620, 370) })
                {
                    editor.SimulatePointerDown(editor.ViewportPointOf(point)); editor.SimulatePointerUp(editor.ViewportPointOf(point));
                }
                controller.SetSelected(null);
                Invoke(host.Surface, "UpdatePointerPreview", new PointD(720, 180), true);
                host.Surface.InvalidateVisual();
                Assert.Equal(new[] { "1", "2", "3" }, controller.Document.Items.OfType<TextAnnotation>().Select(t => t.Text)); break;
            case "S10": case "S10b":
                Draw(EditorTool.Mosaic, new PointD(110, 145), new PointD(530, 245));
                Wait(host.Window, () => !editor.HasUnsettledMosaicWork, "Mosaic completion");
                Assert.Contains(controller.Document.Items, i => i is ImageAnnotation);
                Select(editor, EditorTool.Mosaic); Settle(host.Window);
                if (scene.Id == "S10b")
                {
                    editor.SimulatePointerDown(editor.ViewportPointOf(new PointD(180, 300)));
                    editor.SimulatePointerMove(editor.ViewportPointOf(new PointD(700, 400)), true);
                    Assert.NotNull(host.Surface.MosaicGestureRegion);
                }
                break;
            case "S11":
                Draw(EditorTool.Blur, new PointD(110, 145), new PointD(530, 245)); Assert.Contains(controller.Document.Items, i => i is BlurAnnotation); Select(editor, EditorTool.Blur);
                // The live blur is computed off the UI thread and the canvas shows its opaque cover
                // until the frame lands; render the finished blur, as a user sees it a moment later.
                AnnotationBlurCache blurCache = Field<AnnotationBlurCache>(editor, "_blurCache");
                Settle(host.Window);
                Wait(host.Window, () => blurCache.Pending.IsCompleted && BlurFramesReady(blurCache), "Live blur");
                host.Surface.InvalidateVisual(); Settle(host.Window); break;
            case "S12":
                Task stamp = editor.PlaceStampAsync(); Wait(host.Window, () => stamp.IsCompleted, "Isolated stamp load"); stamp.GetAwaiter().GetResult();
                Assert.Single(editor.PlacedStampIds); Assert.Contains(controller.Document.Items, i => i is ImageAnnotation); break;
            case "S13": case "S14":
                Select(editor, EditorTool.Crop);
                editor.PendingCrop!.SetRegion(new RectD(60, 60, 780, 400));
                if (scene.Id == "S14")
                {
                    editor.PendingCrop.Begin(new PointD(840, 460), 7);
                    editor.PendingCrop.Drag(new PointD(800, 420), false);
                    Assert.True(editor.PendingCrop.IsDragging);
                }
                Invoke(editor, "RefreshCrop", true); break;
            case "S15":
                Invoke(editor, "SetStatus", string.Join(" · ", Enumerable.Repeat(language == "ko-KR" ? "주문 응답 필드와 처리 결과의 긴 상태 메시지를 확인합니다" : "Review the order response fields and the detailed processing result", 8))); break;
            case "S16": case "S17": break;
            case "S19": break;
            default:
                Assert.StartsWith("S18-", scene.Id);
                Rectangle(); Select(editor, EditorTool.Rectangle); Settle(host.Window);
                ApplyStates(host, scene.Id[4..]); break;
        }
    }

    private static object SceneState(Host host) => new
    {
        tool = host.Controller.Tool.ToString(), annotations = host.Controller.Document.Items.Select(i => i.GetType().Name).ToArray(),
        selected = host.Controller.Selected?.GetType().Name,
        liveText = Field<TextBox?>(host.Editor, "_activeTextBox")?.Text,
        liveTextLines = Field<TextBox?>(host.Editor, "_activeTextBox")?.LineCount,
        crop = host.Editor.PendingCrop?.Region, cropDragging = host.Editor.PendingCrop?.IsDragging,
        mosaicDraft = host.Surface.MosaicGestureRegion, status = host.Editor.StatusText,
    };

    private static void ApplyStates(Host host, string state)
    {
        Control[] controls =
        [
            Field<Button>(host.Editor, "_undoButton"), ById<Button>(host.Editor, "Capture410Crop"),
            ById<Button>(host.Editor, "EditorFlowSaveToFile"), ById<Button>(host.Editor, "EditorFlowDone"),
            ById<Button>(host.Editor, "EditorFlowPin"), host.Editor.ToolButton(EditorTool.Pen),
            host.Editor.ToolButton(EditorTool.Rectangle), Field<Button>(host.Editor, "_deleteButton"),
        ];
        foreach (Control control in controls)
        {
            control.IsEnabled = state != "disabled";
            control.ApplyTemplate();
            UIElement? Part(string name) => control.Template.FindName(name, control) as UIElement;
            if (Part("HoverLayer") is { } hover) FluidMotion.SetFadeTo(hover, state is "hover" or "pressed" ? 1 : 0);
            if (Part("PressedLayer") is { } pressed) FluidMotion.SetFadeTo(pressed, state == "pressed" ? 1 : 0);
            if (Part("FocusRing") is { } focus) focus.Opacity = state == "focus" ? 1 : 0;
            if (state == "focus" && Part("Chrome") is Border chrome) chrome.SetResourceReference(Border.BorderBrushProperty, "Border.Focus");
            if (control is ToggleButton && state is "hover" or "pressed") control.SetResourceReference(Control.ForegroundProperty, "Text.Primary");
        }
    }

    private static void VerifyPalette(AnnotationEditorSurface surface, ResourceDictionary resources)
    {
        foreach ((string field, string key) in new[] { ("_selectionBrush", "Text.Primary"), ("_handleFillBrush", "Surface.Raised"), ("_dimmerBrush", "Overlay.Dimmer") })
        {
            var actual = Assert.IsType<SolidColorBrush>(Field<Brush>(surface, field));
            var expected = Assert.IsType<SolidColorBrush>(resources[key]);
            Assert.Equal(expected.Color, actual.Color);
            Assert.Equal(expected.Opacity, actual.Opacity);
        }
    }

    private static void SaveZooms(Host host, Scene scene, Combo combo, BitmapSource bitmap, string folder)
    {
        void Tile(string name, Rect bounds)
        {
            int x = Math.Clamp((int)Math.Floor(bounds.X * combo.Scale), 0, bitmap.PixelWidth - 1);
            int y = Math.Clamp((int)Math.Floor(bounds.Y * combo.Scale), 0, bitmap.PixelHeight - 1);
            int right = Math.Clamp((int)Math.Ceiling(bounds.Right * combo.Scale), x + 1, bitmap.PixelWidth);
            int bottom = Math.Clamp((int)Math.Ceiling(bounds.Bottom * combo.Scale), y + 1, bitmap.PixelHeight);
            var crop = new CroppedBitmap(bitmap, new Int32Rect(x, y, right - x, bottom - y));
            // Explicit 4x replication preserves device pixels exactly, including alpha.
            var bgra = new FormatConvertedBitmap(crop, PixelFormats.Bgra32, null, 0);
            int stride = bgra.PixelWidth * 4;
            byte[] input = new byte[stride * bgra.PixelHeight]; bgra.CopyPixels(input, stride, 0);
            int outputStride = stride * 4;
            byte[] output = new byte[outputStride * bgra.PixelHeight * 4];
            for (int row = 0; row < bgra.PixelHeight; row++)
            for (int column = 0; column < bgra.PixelWidth; column++)
            for (int dy = 0; dy < 4; dy++)
            for (int dx = 0; dx < 4; dx++)
                Buffer.BlockCopy(input, row * stride + column * 4, output, (row * 4 + dy) * outputStride + (column * 4 + dx) * 4, 4);
            Save(BitmapSource.Create(bgra.PixelWidth * 4, bgra.PixelHeight * 4, 96, 96, PixelFormats.Bgra32, null, output, outputStride),
                DiagnosticOutputPaths.Child(folder, combo.Name + "-" + name + ".png"));
        }
        Rect AtImage(PointD point, double width, double height)
        {
            Point p = host.Surface.TranslatePoint(host.Surface.ToSurfacePoint(point), host.Window);
            return new Rect(p.X - width / 2, p.Y - height / 2, width, height);
        }
        Rect Element(FrameworkElement element)
        {
            Point p = element.TranslatePoint(default, host.Window);
            return new Rect(p.X - 4, p.Y - 4, element.ActualWidth + 8, element.ActualHeight + 8);
        }
        switch (scene.Id)
        {
            case "S01":
                Tile("image-top-left", AtImage(new PointD(0, 0), 64, 64));
                Tile("primary-done", Element(ById<Button>(host.Editor, "EditorFlowDone")));
                Tile("save-file", Element(ById<Button>(host.Editor, "EditorFlowSaveToFile")));
                Tile("rail-tool", Element(host.Editor.ToolButton(EditorTool.Select))); break;
            case "S03": Tile("selection-handle", AtImage(new PointD(120, 90), 40, 40)); break;
            case "S06a": Tile("brush-preview", AtImage(new PointD(450, 260), 40, 40)); break;
            case "S09": Tile("step-badge", AtImage(new PointD(120, 130), 64, 64)); break;
            case "S13":
                Tile("crop-handle", AtImage(new PointD(60, 60), 40, 40));
                Rect bounds = host.Surface.ToSurfaceRect(host.Editor.PendingCrop!.Region);
                var label = new FormattedText("780 × 400 px", UiText.Culture, FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"), TypeScale.Caption, Brushes.White, combo.Scale);
                double left = Math.Clamp(bounds.Left + (bounds.Width - label.Width) / 2, 4, Math.Max(4, host.Surface.ActualWidth - label.Width - 12));
                double top = bounds.Bottom + 10 + label.Height < host.Surface.ActualHeight ? bounds.Bottom + 10 : Math.Max(4, bounds.Bottom - label.Height - 14);
                Point origin = host.Surface.TranslatePoint(new Point(left - 9, top - 7), host.Window);
                Tile("crop-size-label", new Rect(origin, new Size(label.Width + 18, label.Height + 14))); break;
        }
    }

    private static BitmapSource Fixture(int width, int height)
    {
        // Reuse the existing table fixture, then add exact pixel-aligned edge markers.
        BitmapSource table = (BitmapSource)typeof(EditorUxEvidenceTests).GetMethod("SpecTable", Members)!.Invoke(null, [900, 520])!;
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            if (width == 900) dc.DrawImage(table, new Rect(0, 0, width, height));
            else if (width == 320)
            {
                dc.DrawRectangle(Brushes.WhiteSmoke, null, new Rect(0, 0, width, height));
                dc.DrawText(Text("주문 / Order 123456", 18, Brushes.Black), new Point(16, 16));
                dc.DrawText(Text("금액 / Amount: 12,000 KRW", 14, Brushes.DimGray), new Point(16, 50));
                dc.DrawText(Text("Status: PAID", 14, Brushes.DimGray), new Point(16, 78));
            }
            else
            {
                dc.DrawRectangle(Brushes.WhiteSmoke, null, new Rect(0, 0, width, height));
                dc.DrawImage(table, new Rect(0, 0, 960, 555));
                dc.DrawImage(table, new Rect(960, 0, 960, 555));
                dc.DrawImage(table, new Rect(0, 555, 960, 555));
                dc.DrawImage(table, new Rect(960, 555, 960, 555));
            }
            dc.DrawRectangle(Brushes.Red, null, new Rect(0, 0, width, 2));
            dc.DrawRectangle(Brushes.Red, null, new Rect(0, height - 2, width, 2));
            dc.DrawRectangle(Brushes.Red, null, new Rect(0, 0, 2, height));
            dc.DrawRectangle(Brushes.Red, null, new Rect(width - 2, 0, 2, height));
            dc.DrawRectangle(Brushes.Black, null, new Rect(4, 4, width - 8, 1));
            dc.DrawRectangle(Brushes.Black, null, new Rect(4, height - 5, width - 8, 1));
            dc.DrawRectangle(Brushes.Black, null, new Rect(4, 4, 1, height - 8));
            dc.DrawRectangle(Brushes.Black, null, new Rect(width - 5, 4, 1, height - 8));
        }
        return Render(visual, width, height, 1);
    }

    private static BitmapSource StampImage()
    {
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(24, 64, 124)), null, new Rect(0, 0, 140, 56), 8, 8);
            dc.DrawText(Text("MC", 28, Brushes.White), new Point(16, 8));
            dc.DrawLine(new Pen(Brushes.White, 3), new Point(88, 16), new Point(120, 40));
            dc.DrawLine(new Pen(Brushes.White, 3), new Point(120, 16), new Point(88, 40));
        }
        return Render(visual, 140, 56, 1);
    }

    private static void Sheet(IReadOnlyList<Picture> pictures, string path)
    {
        if (pictures.Count == 0) return;
        const int tileWidth = 600, gap = 16, label = 32;
        int imageHeight = (int)Math.Ceiling(pictures.Max(p => p.Height * (double)tileWidth / p.Width));
        int cellHeight = imageHeight + label + gap;
        int width = gap + 3 * (tileWidth + gap), height = gap + (pictures.Count + 2) / 3 * cellHeight;
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.WhiteSmoke, null, new Rect(0, 0, width, height));
            for (int i = 0; i < pictures.Count; i++)
            {
                Picture p = pictures[i];
                using FileStream stream = File.OpenRead(p.Path);
                var image = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                double x = gap + i % 3 * (tileWidth + gap), y = gap + i / 3 * cellHeight;
                dc.DrawImage(image, new Rect(x, y, tileWidth, p.Height * (double)tileWidth / p.Width));
                dc.DrawText(Text(p.Name, 16, Brushes.Black), new Point(x, y + imageHeight + 6));
            }
        }
        Save(Render(visual, width, height, 1), path);
    }

    private static BitmapSource Render(Visual visual, double width, double height, double scale)
    {
        Assert.True(width > 0 && height > 0);
        var drawing = new DrawingVisual();
        using (DrawingContext dc = drawing.RenderOpen())
        {
            dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, width, height));
            dc.DrawRectangle(new VisualBrush(visual) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top,
                ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, width, height) }, null, new Rect(0, 0, width, height));
        }
        var result = new RenderTargetBitmap((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        result.Render(drawing); result.Freeze(); return result;
    }
    private static void Save(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(path); encoder.Save(stream);
        Assert.True(stream.Length > 100);
    }
    private static void Settle(Window window)
    {
        for (int i = 0; i < 3; i++)
        {
            window.UpdateLayout();
            var frame = new DispatcherFrame();
            window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
        window.UpdateLayout();
    }
    private static void Wait(Window window, Func<bool> completed, string operation)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(30);
        while (!completed() && DateTime.UtcNow < deadline) { Settle(window); Thread.Sleep(5); }
        Assert.True(completed(), operation + " timed out.");
    }
    private static bool BlurFramesReady(AnnotationBlurCache cache)
    {
        var entries = (System.Collections.IEnumerable)typeof(AnnotationBlurCache).GetField("_entries", Members)!.GetValue(cache)!;
        object[] all = entries.Cast<object>().ToArray();
        return all.Length > 0 && all.All(entry => entry.GetType().GetProperty("Frame", Members)!.GetValue(entry) is not null);
    }
    private static void Select(AnnotationEditorControl editor, EditorTool tool) => Invoke(editor, "SelectTool", tool);
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Members)!.GetValue(target)!;
    private static void Invoke(object target, string name, params object[] arguments) => target.GetType().GetMethod(name, Members)!.Invoke(target, arguments);
    private static T ById<T>(DependencyObject root, string id) where T : FrameworkElement => EditorKeysTests.Descendants(root).OfType<T>().Single(e => AutomationProperties.GetAutomationId(e) == id);
    private static string Folder(string parent, string name) => DiagnosticOutputPaths.Create(DiagnosticOutputPaths.Child(parent, name));
    private static void WriteJson(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value, JsonOptions));
    private static FormattedText Text(string text, double size, Brush brush) => new(text, CultureInfo.GetCultureInfo("ko-KR"), FlowDirection.LeftToRight, new Typeface("Malgun Gothic"), size, brush, 1);

    // Numeric evidence only. Every flag retains the actual scene and visual element identity.
    private static class AestheticAudit
    {
        private const BindingFlags AFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        internal static Dictionary<string, object?> Measure(Window window, AnnotationEditorControl editor,
            string combo, string scene, double scale, FrameworkElement? separateRoot = null)
        {
            FrameworkElement visualRoot = separateRoot ?? window;
            var nodes = AWalk(visualRoot).OfType<FrameworkElement>().Where(AVisible).ToList();
            var index = nodes.Select((node, i) => (node, i)).ToDictionary(item => item.node, item => item.i);
            var bounds = nodes.ToDictionary(node => node, node => ABounds(node, visualRoot));
            string Id(FrameworkElement node)
            {
                string id = System.Windows.Automation.AutomationProperties.GetAutomationId(node);
                string name = System.Windows.Automation.AutomationProperties.GetName(node);
                return $"{node.GetType().Name}[{(id.Length > 0 ? id : node.Name.Length > 0 ? node.Name : name.Length > 0 ? name : "visual")}]#{(index.TryGetValue(node, out int i) ? i : -1)}";
            }

            var roots = new List<(FrameworkElement Node, string Bar)>();
            foreach ((string field, string bar) in new[]
            {
                ("_editCommands", "command"), ("_cropCommands", "command"),
                ("_inspectorPanel", "property"), ("_propertyGroups", "property")
            })
                if (AField(editor, field) is FrameworkElement value) roots.Add((value, bar));
            if (AWalk(editor).OfType<FrameworkElement>().FirstOrDefault(e => e.Name == "AnnotationStatusBar") is { } statusRoot)
                roots.Add((statusRoot, "status"));
            if (separateRoot is not null) roots.Add((separateRoot, "popup"));
            var railButtons = AField(editor, "_toolButtons") as System.Collections.IDictionary;
            if (AField(editor, "_stampButton") is FrameworkElement stampButton) roots.Add((stampButton, "rail"));
            if (railButtons is not null)
                foreach (object? item in railButtons.Values)
                    if (item is FrameworkElement button) roots.Add((button, "rail"));
            string Bar(FrameworkElement node)
            {
                for (DependencyObject? current = node; current is not null; current = AParent(current))
                {
                    foreach ((FrameworkElement root, string bar) in roots)
                        if (ReferenceEquals(current, root)) return bar;
                }
                return "other";
            }

            var text = new List<object>();
            var disabledText = new List<object>();
            var nonText = new List<object>();
            var disabledNonText = new List<object>();
            var geometry = new List<object>();
            var clipping = new List<object>();
            var overlaps = new List<object>();
            var alignment = new List<object>();
            var flags = new List<object>();
            Color baseColor = ABase(window);
            void Flag(string kind, FrameworkElement node, object details) =>
                flags.Add(new { kind, combo, scene, element = Id(node), bar = Bar(node), details });

            foreach (FrameworkElement node in nodes)
            {
                string bar = Bar(node);
                Rect box = bounds[node];
                bool disabled = !node.IsEnabled;
                if (node is System.Windows.Controls.TextBlock || node is System.Windows.Controls.TextBox)
                {
                    string value;
                    FontFamily family;
                    double fontSize;
                    FontWeight weight;
                    FontStyle style;
                    FontStretch stretch;
                    Brush foreground;
                    string trimming;
                    TextWrapping wrapping;
                    Thickness padding;
                    if (node is System.Windows.Controls.TextBlock tb)
                    {
                        value = tb.Text; family = tb.FontFamily; fontSize = tb.FontSize;
                        weight = tb.FontWeight; style = tb.FontStyle; stretch = tb.FontStretch;
                        foreground = tb.Foreground; trimming = tb.TextTrimming.ToString(); wrapping = tb.TextWrapping;
                        padding = tb.Padding;
                    }
                    else
                    {
                        var input = (System.Windows.Controls.TextBox)node;
                        value = input.Text; family = input.FontFamily; fontSize = input.FontSize;
                        weight = input.FontWeight; style = input.FontStyle; stretch = input.FontStretch;
                        foreground = input.Foreground; trimming = "None"; wrapping = input.TextWrapping; padding = input.Padding;
                    }
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        Color bg = ABackground(node, window, baseColor);
                        Color rawFg = AColor(foreground);
                        Color fg = AComposite(rawFg, bg, foreground.Opacity * AOpacity(node));
                        double ratio = AContrast(fg, bg);
                        bool large = fontSize >= 24 || (fontSize >= 18.67 && weight.ToOpenTypeWeight() >= 700);
                        double threshold = large ? 3 : 4.5;
                        double available = Math.Max(0, node.ActualWidth - padding.Left - padding.Right);
                        var formatted = new FormattedText(value,
                            System.Globalization.CultureInfo.CurrentUICulture, node.FlowDirection,
                            new Typeface(family, style, weight, stretch), fontSize, Brushes.Black,
                            VisualTreeHelper.GetDpi(node).PixelsPerDip);
                        double desired = formatted.WidthIncludingTrailingWhitespace;
                        bool widthOverflow = desired > available + 0.5;
                        bool clipFlag = widthOverflow && trimming == "None" && wrapping == TextWrapping.NoWrap;
                        var row = new
                        {
                            element = Id(node), bar, text = value, fontFamily = family.Source,
                            fontSize = ARound(fontSize), fontWeight = weight.ToOpenTypeWeight(),
                            fontStyle = style.ToString(), foreground = AHex(rawFg),
                            effectiveForeground = AHex(fg), effectiveBackground = AHex(bg),
                            effectiveOpacity = ARound(AOpacity(node)), contrastRatio = ARound(ratio), threshold,
                            disabled, belowThreshold = !disabled && ratio < threshold,
                            bounds = ARect(box), desiredUntrimmedWidth = ARound(desired),
                            arrangedTextWidth = ARound(available), trimming, wrapping = wrapping.ToString(),
                            untrimmedWidthExceedsArranged = widthOverflow, clippingWithoutEllipsis = clipFlag
                        };
                        if (disabled) disabledText.Add(row); else text.Add(row);
                        if (!disabled && ratio < threshold) Flag("text-contrast", node, new { ratio = ARound(ratio), threshold, text = value });
                        if (clipFlag)
                        {
                            clipping.Add(new { element = Id(node), kind = "text-width", desired = ARound(desired), available = ARound(available), trimming, wrapping = wrapping.ToString() });
                            Flag("clipping", node, new { reason = "untrimmed-width", desired = ARound(desired), available = ARound(available) });
                        }
                    }
                }

                bool relevantControl = bar != "other" && node is System.Windows.Controls.Control &&
                    (node is System.Windows.Controls.Primitives.ButtonBase || node is System.Windows.Controls.ComboBox ||
                     node is System.Windows.Controls.TextBox || node is System.Windows.Controls.Slider || node is MenuItem ||
                     node.GetType().Name.Contains("Value", StringComparison.Ordinal));
                if (relevantControl)
                {
                    var control = (System.Windows.Controls.Control)node;
                    var chrome = control.Template?.FindName("Chrome", control) as System.Windows.Controls.Border;
                    var siblings = AParent(node) is DependencyObject parent
                        ? AChildren(parent).OfType<FrameworkElement>().Where(AVisible).ToList() : [];
                    int siblingIndex = siblings.IndexOf(node);
                    FrameworkElement? previous = siblingIndex > 0 ? siblings[siblingIndex - 1] : null;
                    Rect previousBox = previous is null ? Rect.Empty : ABounds(previous, visualRoot);
                    double? gapX = previousBox.IsEmpty ? null : ARound(box.Left - previousBox.Right);
                    double? gapY = previousBox.IsEmpty ? null : ARound(box.Top - previousBox.Bottom);
                    var values = new Dictionary<string, double>
                    {
                        ["x"] = box.X, ["y"] = box.Y, ["width"] = box.Width, ["height"] = box.Height,
                        ["marginLeft"] = node.Margin.Left, ["marginTop"] = node.Margin.Top,
                        ["marginRight"] = node.Margin.Right, ["marginBottom"] = node.Margin.Bottom,
                        ["paddingLeft"] = control.Padding.Left, ["paddingTop"] = control.Padding.Top,
                        ["paddingRight"] = control.Padding.Right, ["paddingBottom"] = control.Padding.Bottom
                    };
                    if (chrome is not null)
                    {
                        values["cornerTopLeft"] = chrome.CornerRadius.TopLeft; values["cornerTopRight"] = chrome.CornerRadius.TopRight;
                        values["cornerBottomRight"] = chrome.CornerRadius.BottomRight; values["cornerBottomLeft"] = chrome.CornerRadius.BottomLeft;
                    }
                    if (gapX is double gx) values["horizontalGapToPrevious"] = gx;
                    if (gapY is double gy) values["verticalGapToPrevious"] = gy;
                    object[] icons = AWalk(node).OfType<FrameworkElement>().Where(AVisible)
                        .Where(icon => icon is System.Windows.Controls.Viewbox || icon is System.Windows.Controls.Canvas || icon is System.Windows.Shapes.Path)
                        .Select(icon => (object)new
                        {
                            type = icon.GetType().Name, name = icon.Name,
                            width = ARound(icon.ActualWidth), height = ARound(icon.ActualHeight),
                            strokeThickness = icon is System.Windows.Shapes.Path path ? ARound(path.StrokeThickness) : (double?)null
                        }).ToArray();
                    geometry.Add(new
                    {
                        element = Id(node), bar, automationId = System.Windows.Automation.AutomationProperties.GetAutomationId(node),
                        automationName = System.Windows.Automation.AutomationProperties.GetName(node), bounds = ARect(box),
                        margin = AThickness(node.Margin), padding = AThickness(control.Padding),
                        cornerRadius = chrome is null ? null : new[] { ARound(chrome.CornerRadius.TopLeft), ARound(chrome.CornerRadius.TopRight), ARound(chrome.CornerRadius.BottomRight), ARound(chrome.CornerRadius.BottomLeft) },
                        previousSibling = previous is null ? null : Id(previous), horizontalGapToPrevious = gapX, verticalGapToPrevious = gapY,
                        notMultiplesOf4 = values.Where(pair => Math.Abs(pair.Value / 4 - Math.Round(pair.Value / 4)) > 0.0025)
                            .ToDictionary(pair => pair.Key, pair => ARound(pair.Value)),
                        icons, disabled, templateOwner = node.TemplatedParent?.GetType().Name
                    });
                }

                if (bar != "other")
                {
                    if (node is System.Windows.Shapes.Shape shape && shape.Stroke is not null && shape.StrokeThickness > 0)
                        AuditStroke(node, shape.Stroke, "icon-stroke", shape.StrokeThickness);
                    if (node is System.Windows.Shapes.Shape filled && filled.Fill is not null &&
                        (node is System.Windows.Shapes.Path || node is System.Windows.Shapes.Polygon))
                        AuditStroke(node, filled.Fill, "icon-fill", 0);
                    if (node is System.Windows.Controls.Border border && border.BorderBrush is not null &&
                        (border.BorderThickness.Left > 0 || border.BorderThickness.Top > 0 || border.BorderThickness.Right > 0 || border.BorderThickness.Bottom > 0))
                        AuditStroke(node, border.BorderBrush, "control-outline", border.BorderThickness.Left);
                }

                // Bounds are geometric candidates; clipping flags retain parent clips for interpretation.
                if (!box.IsEmpty)
                {
                    Rect windowRect = new(0, 0, visualRoot.ActualWidth, visualRoot.ActualHeight);
                    if (!AContains(windowRect, box))
                    {
                        clipping.Add(new { element = Id(node), kind = "outside-window", bounds = ARect(box), parent = ARect(windowRect) });
                        Flag("clipping", node, new { reason = "outside-window" });
                    }
                    if (AParent(node) is FrameworkElement parent && AVisible(parent))
                    {
                        Rect parentBox = ABounds(parent, visualRoot);
                        if (!parentBox.IsEmpty && !AContains(parentBox, box))
                        {
                            clipping.Add(new { element = Id(node), kind = "outside-parent", bounds = ARect(box), parentElement = Id(parent), parent = ARect(parentBox), parentClipsToBounds = parent.ClipToBounds, hasClipGeometry = parent.Clip is not null });
                            Flag("clipping", node, new { reason = "outside-parent", parentElement = Id(parent), parentClipsToBounds = parent.ClipToBounds });
                        }
                    }
                }

                bool region = roots.Any(root => ReferenceEquals(root.Node, node)) ||
                    ReferenceEquals(AField(editor, "_viewport"), node) ||
                    (AField(editor, "_viewport") is DependencyObject viewport && ReferenceEquals(AParent(viewport), node));
                if (scale > 1 && (relevantControl || region))
                {
                    double[] fractions = [AFrac(box.X * scale), AFrac(box.Y * scale), AFrac(box.Width * scale), AFrac(box.Height * scale)];
                    double[] distances = fractions.Select(v => Math.Min(v, 1 - v)).ToArray();
                    bool misaligned = distances.Any(v => v > 0.01);
                    alignment.Add(new { element = Id(node), bar, bounds = ARect(box), deviceFractions = fractions, distanceToNearestDevicePixel = distances, exceedsPoint01 = misaligned });
                    if (misaligned) Flag("fractional-alignment", node, new { deviceFractions = fractions, distanceToNearestDevicePixel = distances });
                }

                void AuditStroke(FrameworkElement element, Brush brush, string role, double thickness)
                {
                    Color bg = ABackground(element, window, baseColor);
                    Color[] samples = ASamples(brush).ToArray();
                    if (samples.Length == 0 || samples.All(color => color.A == 0)) return;
                    double opacity = brush.Opacity * AOpacity(element);
                    double[] ratios = samples.Select(color => AContrast(AComposite(color, bg, opacity), bg)).ToArray();
                    double minimum = ratios.Min();
                    var row = new
                    {
                        element = Id(element), bar, role, brushType = brush.GetType().Name,
                        colors = samples.Select(AHex).ToArray(), effectiveBackground = AHex(bg),
                        contrastRatios = ratios.Select(ARound).ToArray(), minimumContrastRatio = ARound(minimum),
                        threshold = 3.0, belowThreshold = !disabled && minimum < 3, disabled,
                        effectiveOpacity = ARound(opacity), strokeThickness = ARound(thickness)
                    };
                    if (disabled) disabledNonText.Add(row); else nonText.Add(row);
                    if (!disabled && minimum < 3) Flag("non-text-contrast", element, new { role, ratio = ARound(minimum), threshold = 3.0 });
                }
            }

            foreach (FrameworkElement panel in nodes.Where(node => node is System.Windows.Controls.Panel && node.TemplatedParent is null))
            {
                var siblings = AChildren(panel).OfType<FrameworkElement>()
                    .Where(node => AVisible(node) && Bar(node) != "other").ToArray();
                for (int i = 0; i < siblings.Length; i++)
                    for (int j = i + 1; j < siblings.Length; j++)
                    {
                        Rect first = ABounds(siblings[i], visualRoot), second = ABounds(siblings[j], visualRoot);
                        Rect shared = Rect.Intersect(first, second);
                        if (shared.IsEmpty || shared.Width <= 0.5 || shared.Height <= 0.5) continue;
                        overlaps.Add(new { parent = Id(panel), first = Id(siblings[i]), second = Id(siblings[j]), bar = Bar(siblings[i]), intersection = ARect(shared), areaDipSquared = ARound(shared.Width * shared.Height) });
                        Flag("overlap", siblings[i], new { other = Id(siblings[j]), parent = Id(panel), areaDipSquared = ARound(shared.Width * shared.Height) });
                    }
            }

            var surface = (AnnotationEditorSurface)AField(editor, "_surface")!;
            var brushReadback = new Dictionary<string, object?>();
            foreach (string name in new[] { "_selectionBrush", "_handleFillBrush", "_dimmerBrush" })
            {
                object? brush = AField(surface, name);
                brushReadback[name] = brush is Brush value ? new
                {
                    type = value.GetType().Name, colors = ASamples(value).Select(AHex).ToArray(), opacity = ARound(value.Opacity)
                } : null;
            }
            Point imageOrigin = surface.TranslatePoint(surface.FrameOrigin, window);
            Rect imageBounds = surface.FrameRectDip;
            double[] imageFractions = [AFrac(imageOrigin.X * scale), AFrac(imageOrigin.Y * scale)];
            if (scale > 1 && imageFractions.Any(f => Math.Min(f, 1 - f) > 0.01))
                Flag("fractional-alignment", surface, new { reason = "image-origin", deviceFractions = imageFractions });
            object image = new
            {
                dipPerPixel = ARound(surface.DipPerPixel), devicePixelsPerImagePixel = ARound(surface.DipPerPixel * scale),
                surfaceDpi = ARound(VisualTreeHelper.GetDpi(surface).PixelsPerDip),
                imageOriginDip = new[] { ARound(imageOrigin.X), ARound(imageOrigin.Y) },
                imageOriginDevice = new[] { ARound(imageOrigin.X * scale), ARound(imageOrigin.Y * scale) },
                imageOriginDeviceFraction = new[] { AFrac(imageOrigin.X * scale), AFrac(imageOrigin.Y * scale) },
                imageWidthDip = ARound(imageBounds.Width), imageHeightDip = ARound(imageBounds.Height)
            };
            return new Dictionary<string, object?>
            {
                ["combo"] = combo, ["scene"] = scene, ["scale"] = scale,
                ["windowDip"] = new[] { ARound(window.ActualWidth), ARound(window.ActualHeight) },
                ["auditRoot"] = new
                {
                    kind = separateRoot is null ? "editor-window" : "save-menu",
                    coordinateSpace = separateRoot is null ? "window-local-DIP" : "menu-local-DIP",
                    widthDip = ARound(visualRoot.ActualWidth), heightDip = ARound(visualRoot.ActualHeight),
                    imageAndSurfaceBrushesReferTo = "editor-window",
                },
                ["visibleElementCount"] = nodes.Count, ["windowBaseColor"] = AHex(baseColor),
                ["textContrast"] = text, ["disabledTextContrast"] = disabledText,
                ["nonTextContrast"] = nonText, ["disabledNonTextContrast"] = disabledNonText,
                ["geometry"] = geometry, ["clipping"] = clipping, ["overlaps"] = overlaps,
                ["fractionalAlignment"] = alignment, ["surfaceBrushes"] = brushReadback,
                ["image"] = image, ["flags"] = flags,
                ["measurementNotes"] = new[]
                {
                    "Contrast is a visual-tree estimate, not a rendered-pixel WCAG certification. Ancestor solid/gradient-midpoint backgrounds are alpha-composited onto the opaque window base; sibling hover/pressed overlay layers, images and effects are not background ancestors.",
                    "Foreground opacity includes visual ancestor opacity. Gradient stroke contrast uses all gradient-stop colors; gradient background uses the midpoint color. Unsupported brushes use transparent fallback and must be reviewed in the PNG.",
                    "Text metrics use the TextBlock/TextBox base typeface. Mixed Inline formatting and glyph rasterization are not modelled. Wrapped text is recorded but not flagged solely because its unwrapped width is larger.",
                    "Parent overflow and sibling overlap are geometric candidates, including intentional layout overlap. Only visible, nonzero-area, nonzero-opacity visuals are measured; custom DrawingContext text in the canvas is covered by images and separate brush read-back.",
                    "Fractional values are x/y/width/height times requested scale; a flag uses distance to the nearest integer > 0.01. Offscreen desktop position is excluded."
                }
            };
        }

        private static object? AField(object owner, string name) => owner.GetType().GetField(name, AFlags)?.GetValue(owner);
        private static DependencyObject? AParent(DependencyObject node) => node is Visual || node is System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        private static IEnumerable<DependencyObject> AChildren(DependencyObject node)
        {
            if (node is not Visual && node is not System.Windows.Media.Media3D.Visual3D) yield break;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) yield return VisualTreeHelper.GetChild(node, i);
        }
        private static IEnumerable<DependencyObject> AWalk(DependencyObject node)
        {
            yield return node;
            foreach (DependencyObject child in AChildren(node))
                foreach (DependencyObject descendant in AWalk(child)) yield return descendant;
        }
        private static bool AVisible(FrameworkElement node)
        {
            // A measured ContextMenu is rendered without opening its desktop Popup, so
            // IsVisible is false even though its arranged content is in the PNG.
            if (node.ActualWidth <= 0.001 || node.ActualHeight <= 0.001 || AOpacity(node) <= 0.001) return false;
            for (DependencyObject? current = node; current is not null; current = AParent(current))
                if (current is UIElement element && element.Visibility != Visibility.Visible) return false;
            return true;
        }
        private static double AOpacity(DependencyObject node)
        {
            double opacity = 1;
            for (DependencyObject? current = node; current is not null; current = AParent(current))
                if (current is UIElement element) opacity *= element.Opacity;
            return opacity;
        }
        private static Rect ABounds(FrameworkElement node, FrameworkElement window)
        {
            if (ReferenceEquals(node, window)) return new Rect(0, 0, window.ActualWidth, window.ActualHeight);
            try { return node.TransformToAncestor(window).TransformBounds(new Rect(0, 0, node.ActualWidth, node.ActualHeight)); }
            catch (InvalidOperationException) { return Rect.Empty; }
        }
        private static object ARect(Rect rect) => rect.IsEmpty
            ? new { x = 0.0, y = 0.0, width = 0.0, height = 0.0, empty = true }
            : new { x = ARound(rect.X), y = ARound(rect.Y), width = ARound(rect.Width), height = ARound(rect.Height), empty = false };
        private static double[] AThickness(Thickness value) => [ARound(value.Left), ARound(value.Top), ARound(value.Right), ARound(value.Bottom)];
        private static bool AContains(Rect outer, Rect inner) => inner.Left >= outer.Left - 0.5 && inner.Top >= outer.Top - 0.5 && inner.Right <= outer.Right + 0.5 && inner.Bottom <= outer.Bottom + 0.5;
        private static double ARound(double value) => double.IsFinite(value) ? Math.Round(value, 6) : 0;
        private static double AFrac(double value)
        {
            double fraction = value - Math.Floor(value);
            return fraction < 0.000001 || fraction > 0.999999 ? 0 : ARound(fraction);
        }
        private static Brush? ABackgroundBrush(DependencyObject node) => node switch
        {
            System.Windows.Controls.Border border => border.Background,
            System.Windows.Controls.Panel panel => panel.Background,
            System.Windows.Controls.Control control => control.Background,
            System.Windows.Controls.TextBlock text => text.Background,
            _ => null
        };
        private static Color ABase(Window window)
        {
            Brush? brush = window.Background;
            if (brush is null || AColor(brush).A == 0) brush = window.TryFindResource("Surface.Base") as Brush;
            return AComposite(AColor(brush), Colors.Black, brush?.Opacity ?? 1);
        }
        private static Color ABackground(FrameworkElement node, Window window, Color baseColor)
        {
            var chain = new List<DependencyObject>();
            for (DependencyObject? current = node; current is not null && !ReferenceEquals(current, window); current = AParent(current)) chain.Add(current);
            Color color = baseColor;
            for (int i = chain.Count - 1; i >= 0; i--)
                if (ABackgroundBrush(chain[i]) is Brush brush)
                    color = AComposite(AColor(brush), color, brush.Opacity * AOpacity(chain[i]));
            return color;
        }
        private static IEnumerable<Color> ASamples(Brush? brush)
        {
            if (brush is SolidColorBrush solid) yield return solid.Color;
            else if (brush is GradientBrush gradient)
                foreach (GradientStop stop in gradient.GradientStops) yield return stop.Color;
        }
        private static Color AColor(Brush? brush)
        {
            if (brush is SolidColorBrush solid) return solid.Color;
            if (brush is GradientBrush gradient && gradient.GradientStops.Count > 0)
            {
                GradientStop[] stops = gradient.GradientStops.OrderBy(stop => stop.Offset).ToArray();
                GradientStop before = stops.LastOrDefault(stop => stop.Offset <= 0.5) ?? stops[0];
                GradientStop after = stops.FirstOrDefault(stop => stop.Offset >= 0.5) ?? stops[^1];
                double amount = after.Offset <= before.Offset ? 0 : Math.Clamp((0.5 - before.Offset) / (after.Offset - before.Offset), 0, 1);
                byte Mix(byte a, byte b) => (byte)Math.Round(a + (b - a) * amount);
                return Color.FromArgb(Mix(before.Color.A, after.Color.A), Mix(before.Color.R, after.Color.R), Mix(before.Color.G, after.Color.G), Mix(before.Color.B, after.Color.B));
            }
            return Colors.Transparent;
        }
        private static Color AComposite(Color foreground, Color background, double opacity)
        {
            double alpha = foreground.A / 255.0 * Math.Clamp(opacity, 0, 1);
            byte Mix(byte front, byte back) => (byte)Math.Clamp(Math.Round(front * alpha + back * (1 - alpha)), 0, 255);
            return Color.FromRgb(Mix(foreground.R, background.R), Mix(foreground.G, background.G), Mix(foreground.B, background.B));
        }
        private static string AHex(Color color) => $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
        private static double AContrast(Color a, Color b)
        {
            static double Component(byte value) { double x = value / 255.0; return x <= 0.04045 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4); }
            static double Luminance(Color value) => 0.2126 * Component(value.R) + 0.7152 * Component(value.G) + 0.0722 * Component(value.B);
            double first = Luminance(a), second = Luminance(b);
            return (Math.Max(first, second) + 0.05) / (Math.Min(first, second) + 0.05);
        }
    }

}

