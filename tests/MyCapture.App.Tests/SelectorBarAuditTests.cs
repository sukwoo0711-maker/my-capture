using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.App.Capture;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;
using Xunit;
using Xunit.Abstractions;

namespace MyCapture.App.Tests;

/// <summary>
/// Opt-in audit: shows a real native selector (focus-loss abort on) and presses each bar button,
/// then each first menu choice, through UI Automation, and prints what happened. Nothing reads or
/// moves the real pointer, keyboard or clipboard.
/// </summary>
public sealed class SelectorBarAuditTests(ITestOutputHelper output)
{
    private static readonly RectD Bounds = new(0, 0, CaptureCueEvidenceTests.ShownWidth, 720);

    [Fact]
    public void PressEveryBarButton() => StaTestHost.Run(() =>
    {
        if (Environment.GetEnvironmentVariable("MYCAPTURE_SELMODE_AUDIT") != "1") return;
        var table = new StringBuilder();
        table.AppendLine("button | closed | mode | requests | completed | color | menu");
        string[] names =
        [
            "CaptureKindButton", "RecordKindButton", "CaptureButton", "WindowButton", "FreeformButton", "FullScreenButton",
            "ScrollButton", "DelayButton", "RatioButton", "TextButton", "ColorButton", "MoreButton", "HelpButton", "CancelButton",
        ];
        foreach (string name in names)
        {
            table.AppendLine(Press(name, menuChoice: -1));
            if (name is "DelayButton" or "RatioButton" or "MoreButton")
            {
                for (int choice = 0; choice < 5; choice++)
                {
                    string? row = Press(name, choice);
                    if (row.Length == 0) break;
                    table.AppendLine(row);
                }
            }
        }
        output.WriteLine(table.ToString());
        string? directory = Environment.GetEnvironmentVariable("MYCAPTURE_SELMODE_AUDIT_OUT");
        if (!string.IsNullOrEmpty(directory) && Path.IsPathFullyQualified(directory))
            File.WriteAllText(Path.Combine(directory, "bar-audit.txt"), table.ToString());
    });

    private static string Press(string buttonName, int menuChoice)
    {
        NativeSelectorChromeCache.Trim();
        var requests = new List<string>();
        int completed = 0;
        string color = "-";
        var window = new WindowCandidate(new IntPtr(77), new RectD(200, 200, 500, 300), "Editor");
        int width = (int)Bounds.Width, height = (int)Bounds.Height;
        BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, new byte[width * height * 4], width * 4);
        bitmap.Freeze();
        using var owner = new NativeCaptureOverlay(Bounds, true, true)
        {
            ReadCursor = () => new PointD(450, 350),
            PlaceCursor = _ => false,
            ReadMonitors = () => [new MonitorInfo("audit", Bounds, Bounds, 96, true)],
            DetectWindows = true,
            SourceWindows = [window],
            TextCaptureAvailable = true,
            OffersOtherActions = true,
            CopyColorText = _ => Task.FromResult(true),
        };
        owner.AttachFrame(new FrozenFrame(bitmap, Bounds, null, 0));
        owner.ActionRequested += (_, request) => requests.Add(request.ToString());
        owner.SelectionCompleted += (_, _) => completed++;
        owner.ColorCopied = feedback => color = feedback.Value;
        try
        {
            owner.Show();
            WindowDetectOverlayTests.PumpUntil(() => owner.IsChromeReady);
            CaptureModeBar bar = owner.ModeBar;
            var button = (ButtonBase)typeof(CaptureModeBar).GetProperty(buttonName, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(bar)!;
            string label = buttonName.Replace("Button", string.Empty, StringComparison.Ordinal);
            if (button.Visibility != Visibility.Visible || !button.IsEnabled)
                return menuChoice >= 0 ? string.Empty : $"{label} | (hidden or disabled: {button.Visibility}, {button.IsEnabled})";
            Invoke(button);
            Settle();
            ContextMenu? menu = OpenMenu(bar);
            string menuText = menu is null ? "-" : string.Join(", ", menu.Items.OfType<MenuItem>().Select(Describe));
            if (menuChoice >= 0)
            {
                if (menu is null) return string.Empty;
                MenuItem[] items = menu.Items.OfType<MenuItem>().ToArray();
                if (menuChoice >= items.Length) return string.Empty;
                MenuItem item = items[menuChoice];
                if (item.HasItems) item = item.Items.OfType<MenuItem>().First();
                label += " > " + item.Header;
                if (!item.IsEnabled) return $"{label} | (disabled)";
                Invoke(item);
                Settle();
                menu = OpenMenu(bar);
                menuText = menu is null ? "-" : "still open";
            }
            string mode = owner.IsClosed ? "-" : owner.Mode.ToString();
            return $"{label} | {owner.IsClosed} | {mode} | {(requests.Count == 0 ? "-" : string.Join("; ", requests))} | {completed} | {color} | {menuText}";
        }
        finally
        {
            if (!owner.IsClosed) owner.ModeBar.DismissMenus();
            owner.Close();
            NativeSelectorChromeCache.Trim();
        }
    }

    private static string Describe(MenuItem item) =>
        item.HasItems ? $"{item.Header} [{string.Join("/", item.Items.OfType<MenuItem>().Select(Describe))}]"
            : $"{item.Header}{(item.IsChecked ? " (checked)" : string.Empty)}{(item.IsEnabled ? string.Empty : " (disabled)")}";

    private static ContextMenu? OpenMenu(CaptureModeBar bar)
    {
        foreach (string field in new[] { "_delayMenu", "_ratioMenu", "_moreMenu" })
            if (typeof(CaptureModeBar).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(bar) is ContextMenu { IsOpen: true } menu)
                return menu;
        return null;
    }

    private static void Invoke(UIElement element)
    {
        AutomationPeer peer = UIElementAutomationPeer.CreatePeerForElement(element);
        if (peer.GetPattern(PatternInterface.Invoke) is IInvokeProvider invoke) invoke.Invoke();
        else if (peer.GetPattern(PatternInterface.SelectionItem) is ISelectionItemProvider selection) selection.Select();
        else if (peer.GetPattern(PatternInterface.Toggle) is IToggleProvider toggle) toggle.Toggle();
        else throw new InvalidOperationException("No automation pattern for " + element);
    }

    // Lets queued input, focus checks (Background) and menu opening run for a short, fixed time.
    private static void Settle()
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < 400)
        {
            var frame = new DispatcherFrame();
            _ = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(10);
        }
    }
}
