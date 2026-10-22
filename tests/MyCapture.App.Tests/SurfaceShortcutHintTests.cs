using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Capture;
using MyCapture.App.Pinning;
using MyCapture.App.Recording;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using MyCapture.Core.Recording;
using MyCapture.Core.Settings;
using MyCapture.Platform.Recording;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>Actual command surfaces, without desktop input, capture, encoding or clipboard writes.</summary>
public sealed class SurfaceShortcutHintTests
{
    [Fact]
    public void CaptureAndQuickAccessKeepTheirCommandsInsideFixedBounds() => StaTestHost.Run(() =>
    {
        using var motion = new ShortcutMotionTestScope();
        using var language = UiText.UseLanguage("en-US");
        var capture = new CaptureModeBar { Resources = SystemHighContrastThemeTests.LoadTheme() };
        capture.Configure(true, true, true, otherActions: true);
        CheckStates(capture, 800, [capture.TextButton, capture.ColorButton, capture.CancelButton]);

        BitmapSource bitmap = BitmapSource.Create(24, 16, 96, 96, PixelFormats.Bgra32, null, new byte[24 * 16 * 4], 24 * 4);
        var card = new QuickAccessWindow(bitmap, () => Task.FromResult(true), () => { }, () => Task.CompletedTask,
            () => Task.FromResult(true), () => { }, () => Task.FromResult("unused.png"));
        try
        {
            FrameworkElement root = Prepare(card);
            ButtonBase[] commands = Descendants(root).OfType<ButtonBase>()
                .Where(button => !string.IsNullOrEmpty(AutomationProperties.GetAcceleratorKey(button))).ToArray();
            Assert.Equal(6, commands.Length);
            CheckStates(root, card.Width, commands);
        }
        finally { card.Close(); }
    });

    [Fact]
    public void CompactPinCommandsKeepFullChordsInsideUnchangingButtons() => StaTestHost.Run(() =>
    {
        using var motion = new ShortcutMotionTestScope();
        using var language = UiText.UseLanguage("en-US");
        var bar = new PinHoverBar();
        try
        {
            FrameworkElement root = Prepare(bar);
            foreach (bool compact in new[] { false, true })
            {
                bar.Compact = compact;
                CheckStates(root, 700, [bar.CopyButton, bar.SaveButton, bar.CloseButton]);
            }
        }
        finally { bar.Close(); }
    });

    [Fact]
    public void RecordingStateRebuildsKeepHintsAndLongPauseChordsWithinThePalette() => StaTestHost.Run(() =>
    {
        using var motion = new ShortcutMotionTestScope();
        using var language = UiText.UseLanguage("en-US");
        var controls = new RecordingControlWindow(new RectD(100, 100, 640, 360), new RecordingSettings(),
            () => throw new InvalidOperationException("This test never records."), () => "unused.mp4", NullLogger.Instance);
        try
        {
            FrameworkElement root = Prepare(controls);
            CheckStates(root, controls.Width, [controls.CancelButtonForTest]);
            controls.ReadPauseHotkey = () => new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Shift | HotkeyModifiers.Alt, 0x50);
            foreach (bool paused in new[] { false, true, false })
            {
                controls.PreviewRecordingForTest(paused, TimeSpan.FromSeconds(12));
                Invoke(controls, "ApplyPhysicalLayout", false);
                Button pause = controls.PauseButtonForTest;
                Assert.Equal("Ctrl+Alt+Shift+P", AutomationProperties.GetAcceleratorKey(pause));
                Assert.IsType<SymbolIcon>(PrimaryContent(pause));
                Assert.Equal(UiText.Get(paused ? "RecordingPause_ResumeName" : "RecordingPause_PauseName"), AutomationProperties.GetName(pause));
                CheckStates(root, controls.Width, [controls.PrimaryButtonForTest, pause]);
                Border palette = Field<Border>(controls, "_controlStrip");
                AssertInside(pause, palette);
                AssertInside(controls.PrimaryButtonForTest, palette);
            }

            controls.ReadPauseHotkey = () => Hotkey.None;
            controls.PreviewRecordingForTest(true, TimeSpan.FromSeconds(12));
            Assert.Empty(AutomationProperties.GetAcceleratorKey(controls.PauseButtonForTest));
            Assert.IsType<SymbolIcon>(controls.PauseButtonForTest.Content);
            controls.ShowCompletionStatus("Saved");
            Assert.Empty(AutomationProperties.GetAcceleratorKey(controls.PrimaryButtonForTest));
            Assert.False(controls.PrimaryButtonForTest.IsEnabled);
        }
        finally { controls.Close(); }
    });

    [Fact]
    public void WelcomeDialogsAndExportShowOnlyTheirRealDefaultAndCancelRoutes() => StaTestHost.Run(() =>
    {
        using var motion = new ShortcutMotionTestScope();
        using var language = UiText.UseLanguage("en-US");
        var welcome = new WelcomeWindow(new HotkeySettings(), AfterCaptureAction.Editor);
        var dialog = new ThemedDialog("Message", "Title", MessageBoxButton.OKCancel, MessageBoxResult.OK);
        var recording = new RecordingResult("never-read.mp4", 6000, 30, 180, 1280, 720);
        var export = new VideoExportDialog(recording, VideoEditDocument.CreateFor(1280, 720, 6000), NullLoggerFactory.Instance, false);
        try
        {
            CheckStates(Prepare(welcome), welcome.Width, [welcome.TryNowButton, welcome.SkipButton]);
            Assert.Equal("Enter", AutomationProperties.GetAcceleratorKey(welcome.TryNowButton));
            Assert.Equal("Esc", AutomationProperties.GetAcceleratorKey(welcome.SkipButton));
            Assert.Contains("Enter", Assert.IsType<string>(welcome.TryNowButton.ToolTip), StringComparison.Ordinal);
            FrameworkElement dialogRoot = Prepare(dialog);
            ButtonBase[] choices = Descendants(dialogRoot).OfType<ButtonBase>().ToArray();
            CheckStates(dialogRoot, dialog.Width, choices);
            Assert.Equal(new[] { "Enter", "Esc" }, choices.Select(AutomationProperties.GetAcceleratorKey));
            Assert.All(choices, button => Assert.Contains(AutomationProperties.GetAcceleratorKey(button), Assert.IsType<string>(button.ToolTip), StringComparison.Ordinal));

            FrameworkElement exportRoot = Prepare(export);
            Button close = Field<Button>(export, "_close");
            Button save = Field<Button>(export, "_export");
            CheckStates(exportRoot, export.Width, [close]);
            Assert.Empty(AutomationProperties.GetAcceleratorKey(save));
            Invoke(export, "SetWorking", true);
            Button cancel = Descendants(exportRoot).OfType<Button>().Single(button => AutomationProperties.GetAcceleratorKey(button) == "Esc");
            Assert.Empty(AutomationProperties.GetAcceleratorKey(close));
            CheckStates(exportRoot, export.Width, [cancel]);
            Invoke(export, "SetWorking", false);
            CheckStates(exportRoot, export.Width, [close]);
        }
        finally { welcome.Close(); dialog.Close(); export.Close(); }
    });

    internal static object? PrimaryContent(ButtonBase button) => button.Content is Grid { Name: "CommandButtonFace" } face
        ? face.Children[0] : button.Content;

    internal static string PrimaryCaption(ButtonBase button) => PrimaryContent(button) is string text
        ? text : Assert.IsType<TextBlock>(PrimaryContent(button)).Text;

    private static FrameworkElement Prepare(Window window)
    {
        FrameworkElement root = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
        root.Resources = SystemHighContrastThemeTests.LoadTheme();
        return root;
    }

    private static void CheckStates(FrameworkElement root, double width, ButtonBase[] commands)
    {
        foreach (ButtonBase command in commands) command.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        var resting = new Dictionary<ButtonBase, Size>();
        foreach (bool reveal in new[] { false, true, false })
        {
            ShortcutPresentation.SetAltRevealed(root, reveal);
            root.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
            root.Measure(new Size(width, 1200));
            root.Arrange(new Rect(0, 0, width, root.DesiredSize.Height));
            root.UpdateLayout();
            foreach (ButtonBase command in commands)
            {
                Grid face = Assert.IsType<Grid>(command.Content);
                TextBlock hint = Assert.IsAssignableFrom<TextBlock>(face.Children[1]);
                Assert.Equal(AutomationProperties.GetAcceleratorKey(command), hint.Text);
                ShortcutMotionTestScope.AssertState(hint, reveal);
                if (resting.TryGetValue(command, out Size previous)) Assert.Equal(previous, command.RenderSize);
                else resting.Add(command, command.RenderSize);
                Assert.True(command.ActualHeight >= 36);
                AssertInside(command, root);
                if (reveal)
                {
                    AssertInside(face, command);
                    AssertInside(hint, command);
                }
            }
        }
    }

    private static void AssertInside(FrameworkElement child, FrameworkElement parent)
    {
        Rect bounds = child.TransformToAncestor(parent).TransformBounds(new Rect(child.RenderSize));
        Assert.True(bounds.Left >= -0.5 && bounds.Top >= -0.5 && bounds.Right <= parent.ActualWidth + 0.5
            && bounds.Bottom <= parent.ActualHeight + 0.5,
            $"{child.GetType().Name} {bounds} exceeds {parent.GetType().Name} {parent.RenderSize}.");
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        foreach (object child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is not DependencyObject node) continue;
            yield return node;
            foreach (DependencyObject descendant in Descendants(node)) yield return descendant;
        }
    }

    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
    private static void Invoke(object owner, string name, params object[] arguments) => owner.GetType()
        .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(owner, arguments);
}
