using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Diagnostics;
using MyCapture.App.Editing;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using MyCapture.Core.Themes;
using MyCapture.Platform.Capture;
using Xunit;

namespace MyCapture.App.Tests;

public sealed partial class SettingsThemePreviewTests
{
    private const string EditorFlowEvidence = "MYCAPTURE_EDITOR_FLOW_EVIDENCE";

    /// <summary>
    /// Renders the image editor's command bar, crop mode, the narrowest window and the
    /// step-number inspector in the real editor window under the glass and high-contrast themes.
    /// </summary>
    /// <remarks>
    /// Set <c>MYCAPTURE_EDITOR_FLOW_EVIDENCE</c> to a directory to keep the PNGs, and
    /// <c>MYCAPTURE_EDITOR_FLOW_PHASE</c> to label them (for example before and after a fix).
    /// </remarks>
    [Fact]
    public Task EditorFlow_CommandBarCropModeNarrowWindowAndStepPanelRenderInEveryEvidenceTheme() =>
        RunIsolated(nameof(EditorFlow_CommandBarCropModeNarrowWindowAndStepPanelRenderInEveryEvidenceTheme), VerifyEditorFlowEvidence);

    private static void VerifyEditorFlowEvidence()
    {
        using var language = UiText.UseLanguage("ko-KR");
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{name}.xaml"),
            });
        string phase = Environment.GetEnvironmentVariable("MYCAPTURE_EDITOR_FLOW_PHASE") is { Length: > 0 } label ? label : "after";
        try
        {
            foreach (AppTheme theme in new[] { AppTheme.Glass, AppTheme.HighContrast })
            {
                ThemeService.Apply(theme);
                foreach ((string scene, double width, double height) in new[] { ("default", 1000.0, 640.0), ("narrow", 680.0, 460.0) })
                {
                    RenderEditorFlowScene(theme, phase, scene, width, height, crop: false);
                    RenderEditorFlowScene(theme, phase, scene, width, height, crop: true);
                }

                RenderEditorFlowStepScene(theme, phase);
                RenderEditorFlowCloseDialog(theme, phase);
            }
        }
        finally
        {
            application.Shutdown();
            // Application.Shutdown only queues the exit on a dispatcher that is not running.
            // Shut it down now so WPF's hidden windows are destroyed while this STA thread is
            // still alive, not by Windows during thread exit (a CLR fail-fast in the child).
            System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
        }
    }

    private static void RenderEditorFlowCloseDialog(AppTheme theme, string phase)
    {
        var dialog = new EditorCloseDialog
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000, Top = -10000, ShowActivated = false,
        };
        FluidMotion.SetWindowEntrance(dialog, false);
        try
        {
            dialog.Show();
            Drain(dialog);
            // The action first and the way out last, with Save and close the one filled button.
            Assert.True(ReferenceEquals(dialog.SaveButton.Style, dialog.FindResource("Button.Primary")));
            Assert.True(ReferenceEquals(dialog.CancelButton.Style, dialog.FindResource("Button.Secondary")));
            double Left(FrameworkElement element) => element.TranslatePoint(default, dialog).X;
            Assert.True(Left(dialog.SaveButton) < Left(dialog.DontSaveButton) && Left(dialog.DontSaveButton) < Left(dialog.CancelButton));
            SaveEditorFlowEvidence(RenderEditorWindow(dialog), $"editor-close-dialog-{theme}-{phase}.png");
        }
        finally
        {
            dialog.Close();
            Drain(dialog);
        }
    }

    /// <summary>
    /// The command bar shares the title bar's surface with no rule under it, and Done is its
    /// only filled button. (How a disabled button looks belongs to the shared button styles.)
    /// </summary>
    private static void VerifyEditorFlowHeader(AnnotationEditorWindow window, bool crop)
    {
        Border bar = EditorKeysTests.Descendants(window.Editor).OfType<Border>().Single(border => border.Name == "AnnotationCommandBar");
        Assert.True(ReferenceEquals(window.FindResource("Surface.Base"), bar.Background));
        Assert.Equal(default, bar.BorderThickness);
        if (crop) return;

        object primary = window.FindResource("Button.Primary");
        Button[] commands = [.. EditorKeysTests.Descendants(bar).OfType<Button>().Where(button => button.IsVisible)];
        Button filled = Assert.Single(commands, button => BasedOn(button.Style, primary));
        Assert.Equal("EditorFlowDone", System.Windows.Automation.AutomationProperties.GetAutomationId(filled));

        static bool BasedOn(Style? style, object target)
        {
            for (; style is not null; style = style.BasedOn)
                if (ReferenceEquals(style, target)) return true;
            return false;
        }
    }

    /// <summary>
    /// At the 680 × 460 DIP minimum window, with the real theme styles and the shortcut key
    /// lines revealed, command groups fit on one row in both modes and languages,
    /// no command is reduced to its key letter, and every rail tool is fully visible.
    /// </summary>
    [Fact]
    public Task EditorFlow_MinimumWindowKeepsEveryCommandLabelAndEveryTool() =>
        RunIsolated(nameof(EditorFlow_MinimumWindowKeepsEveryCommandLabelAndEveryTool), VerifyMinimumEditorLayout);

    private static void VerifyMinimumEditorLayout()
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{name}.xaml"),
            });
        var report = new System.Text.StringBuilder();
        try
        {
            ThemeService.Apply(AppTheme.Glass);
            foreach (string culture in new[] { "ko-KR", "en-US" })
            using (UiText.UseLanguage(culture))
            {
                foreach (bool crop in new[] { false, true })
                {
                    AnnotationEditorWindow window = EditorFlowWindow(680, 460);
                    try
                    {
                        window.Show();
                        Drain(window);
                        if (crop)
                        {
                            Assert.True(window.Editor.HandleShortcut(Key.X, ModifierKeys.None));
                            Drain(window);
                        }

                        ShortcutPresentation.SetAltRevealed(window, true);
                        Drain(window);

                        string scene = $"{culture} {(crop ? "crop" : "edit")}";
                        CommandGroupsPanel bar = EditorKeysTests.Descendants(window.Editor).OfType<CommandGroupsPanel>().Single(panel => panel.IsVisible);
                        var left = (FrameworkElement)bar.Children[0];
                        var right = (FrameworkElement)bar.Children[1];
                        report.AppendLine($"{scene}: bar {bar.ActualWidth:0.#} left {left.ActualWidth:0.#} right {right.ActualWidth:0.#}");
                        Assert.True(left.ActualWidth <= bar.ActualWidth + 0.5 && right.ActualWidth <= bar.ActualWidth + 0.5,
                            $"{scene}: a command group exceeds the available width.");
                        Rect leftBounds = left.TransformToAncestor(bar).TransformBounds(new Rect(left.RenderSize));
                        Rect rightBounds = right.TransformToAncestor(bar).TransformBounds(new Rect(right.RenderSize));
                        bool wraps = left.ActualWidth + 8 + right.ActualWidth > bar.ActualWidth;
                        Assert.False(wraps, $"{scene}: command groups must fit at the minimum width.");
                        Assert.Equal(wraps, rightBounds.Top >= leftBounds.Bottom - 0.5);
                        Rect overlap = Rect.Intersect(leftBounds, rightBounds);
                        Assert.True(overlap.IsEmpty || overlap.Width <= 0.5 || overlap.Height <= 0.5,
                            $"{scene}: the command groups overlap.");
                        Assert.InRange(bar.ActualHeight, (wraps ? left.ActualHeight + 8 + right.ActualHeight : Math.Max(left.ActualHeight, right.ActualHeight)) - 0.5,
                            (wraps ? left.ActualHeight + 8 + right.ActualHeight : Math.Max(left.ActualHeight, right.ActualHeight)) + 0.5);
                        if (wraps) Assert.Equal(8, rightBounds.Top - leftBounds.Bottom, 1);

                        Rect client = new(0, 0, window.Editor.ActualWidth, window.Editor.ActualHeight);
                        foreach (System.Windows.Controls.Primitives.ButtonBase button in EditorKeysTests.Descendants(bar)
                            .OfType<System.Windows.Controls.Primitives.ButtonBase>().Where(button => button.IsVisible && button.TemplatedParent is not ComboBox))
                        {
                            Rect bounds = button.TransformToAncestor(window.Editor).TransformBounds(new Rect(button.RenderSize));
                            string name = System.Windows.Automation.AutomationProperties.GetName(button);
                            report.AppendLine($"  {name}: {bounds.Width:0.#} × {bounds.Height:0.#}");
                            Assert.True(client.Contains(bounds), $"{scene}: {name} is clipped ({bounds}).");
                            string key = System.Windows.Automation.AutomationProperties.GetAcceleratorKey(button);
                            bool label = EditorKeysTests.Descendants(button).OfType<TextBlock>()
                                .Any(text => text.IsVisible && text.Text.Length > 0 && text.Text != key);
                            bool icon = EditorKeysTests.Descendants(button).OfType<Viewbox>().Any(viewbox => viewbox.IsVisible);
                            Assert.True(label || icon, $"{scene}: {name} shows only its key ({key}).");
                        }

                        var rail = (FrameworkElement)EditorKeysTests.Descendants(window.Editor).OfType<Border>().Single(border => border.Name == "AnnotationToolRail");
                        Rect railBounds = rail.TransformToAncestor(window.Editor).TransformBounds(new Rect(rail.RenderSize));
                        foreach (System.Windows.Controls.Primitives.ToggleButton tool in EditorKeysTests.Descendants(rail).OfType<System.Windows.Controls.Primitives.ToggleButton>())
                        {
                            Rect bounds = tool.TransformToAncestor(window.Editor).TransformBounds(new Rect(tool.RenderSize));
                            Assert.True(tool.IsVisible && railBounds.Contains(bounds),
                                $"{scene}: {System.Windows.Automation.AutomationProperties.GetName(tool)} is cut off by the rail ({bounds} in {railBounds}).");
                        }

                        report.AppendLine($"  rail {railBounds.Height:0.#} high");
                    }
                    finally
                    {
                        window.Close();
                        Drain(window);
                    }
                }
            }
        }
        finally
        {
            if (Environment.GetEnvironmentVariable(EditorFlowEvidence) is { Length: > 0 } requested)
                File.WriteAllText(DiagnosticOutputPaths.Child(DiagnosticOutputPaths.Create(requested), "editor-minimum-window-layout.txt"), report.ToString());
            application.Shutdown();
            // Application.Shutdown only queues the exit on a dispatcher that is not running.
            // Shut it down now so WPF's hidden windows are destroyed while this STA thread is
            // still alive, not by Windows during thread exit (a CLR fail-fast in the child).
            System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
        }
    }

    private static AnnotationEditorWindow EditorFlowWindow(double width, double height)
    {
        BitmapSource bitmap = EditorEvidenceBackground(720, 300);
        var region = new RectD(0, 0, bitmap.PixelWidth, bitmap.PixelHeight);
        var window = new AnnotationEditorWindow(new FrozenFrame(bitmap, region, null, 0), region, bitmap)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000, Top = -10000, Width = width, Height = height,
            ShowActivated = false, ShowInTaskbar = false,
        };
        FluidMotion.SetWindowEntrance(window, false);
        return window;
    }

    private static void RenderEditorFlowScene(AppTheme theme, string phase, string scene, double width, double height, bool crop)
    {
        AnnotationEditorWindow window = EditorFlowWindow(width, height);
        try
        {
            window.Show();
            Drain(window);
            if (crop)
            {
                Assert.True(window.Editor.HandleShortcut(Key.X, ModifierKeys.None));
                Drain(window);
            }

            if (phase != "before") VerifyEditorFlowHeader(window, crop);
            SaveEditorFlowEvidence(RenderEditorWindow(window), $"editor-{scene}{(crop ? "-crop" : string.Empty)}-{theme}-{phase}.png");
        }
        finally
        {
            window.Close();
            Drain(window);
        }
    }

    private static void RenderEditorFlowStepScene(AppTheme theme, string phase)
    {
        AnnotationEditorWindow window = EditorFlowWindow(1000, 640);
        try
        {
            window.Show();
            Drain(window);
            AnnotationEditorControl editor = window.Editor;
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var controller = (AnnotationEditorController)typeof(AnnotationEditorControl).GetField("_controller", flags)!.GetValue(editor)!;
            var inspector = (FrameworkElement)typeof(AnnotationEditorControl).GetField("_inspectorPanel", flags)!.GetValue(editor)!;
            MethodInfo selectTool = typeof(AnnotationEditorControl).GetMethod("SelectTool", flags)!;

            // Two numbers already placed, then a third through the tool, so the panel shows 4
            // and the status line reports the badge just added.
            controller.Tool = EditorTool.StepNumber;
            controller.AddStepNumber(new PointD(60, 60));
            controller.AddStepNumber(new PointD(160, 60));
            controller.SetSelected(null);
            selectTool.Invoke(editor, [EditorTool.StepNumber]);
            controller.HitTolerance = 1;
            controller.PointerDown(new PointD(260, 60));
            controller.PointerUp(new PointD(260, 60));
            Drain(window);

            BitmapSource full = RenderEditorWindow(window);
            SaveEditorFlowEvidence(Crop(full, window, inspector, 0), $"editor-step-panel-{theme}-{phase}.png");
            SaveEditorFlowEvidence(full, $"editor-step-window-{theme}-{phase}.png");
        }
        finally
        {
            window.Close();
            Drain(window);
        }
    }

    private static void SaveEditorFlowEvidence(BitmapSource bitmap, string name)
    {
        string? requested = Environment.GetEnvironmentVariable(EditorFlowEvidence);
        if (string.IsNullOrWhiteSpace(requested)) return;
        string directory = DiagnosticOutputPaths.Create(requested);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(DiagnosticOutputPaths.Child(directory, name));
        encoder.Save(stream);
    }
}
