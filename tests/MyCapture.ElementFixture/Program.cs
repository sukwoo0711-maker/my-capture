using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace MyCapture.ElementFixture;

/// <summary>Test-only foreign process. No activation, cursor, clipboard, profile or file access.</summary>
public static class Program
{
    [STAThread]
    public static void Main()
    {
        _ = SetThreadDpiAwarenessContext(new IntPtr(-4));
        var button = new Button { Width = 120, Height = 50, Content = "Fixture", Margin = new Thickness(25),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        var inner = new Group { Width = 300, Height = 180, Margin = new Thickness(30), Child = button,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Background = Brushes.WhiteSmoke };
        var raw = new Group { Margin = new Thickness(12), Child = inner, RawOnly = true, Background = Brushes.Gainsboro };
        var outer = new Group { Margin = new Thickness(20), Child = raw, Background = Brushes.LightGray };
        var window = new Window { Left = 80, Top = 60, Width = 600, Height = 400,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowActivated = false,
            ShowInTaskbar = false, Title = "MyCapture element test fixture", Content = outer };
        window.SourceInitialized += (_, _) =>
        {
            nint hwnd = new WindowInteropHelper(window).Handle;
            SetWindowLongW(hwnd, -20, GetWindowLongW(hwnd, -20) | 0x08000000); // GWL_EXSTYLE is 32-bit on both architectures.
        };
        window.Show();
        window.UpdateLayout();
        Warm(UIElementAutomationPeer.CreatePeerForElement(window));
        Rect Bounds(FrameworkElement element)
        {
            Point origin = element.PointToScreen(new Point());
            Point end = element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight));
            return new Rect(origin, end);
        }
        Rect box = Bounds(button);
        Console.WriteLine(JsonSerializer.Serialize(new { Handle = new WindowInteropHelper(window).Handle.ToInt64(),
            ProcessId = Environment.ProcessId, Window = Bounds(window), Outer = Bounds(outer), Raw = Bounds(raw), Inner = Bounds(inner), Button = box }));
        Console.Out.Flush();
        _ = Task.Run(() =>
        {
            while (Console.ReadLine() is { } command)
            {
                if (command == "hang")
                    window.Dispatcher.BeginInvoke(() => { Console.WriteLine("hanging"); Console.Out.Flush(); Thread.Sleep(TimeSpan.FromMinutes(2)); });
                else if (command == "wide") window.Dispatcher.BeginInvoke(() =>
                {
                    outer.Child = new Wide { Margin = new Thickness(20), Background = Brushes.WhiteSmoke };
                    window.UpdateLayout();
                    Warm(UIElementAutomationPeer.CreatePeerForElement(window));
                    Console.WriteLine("wide"); Console.Out.Flush();
                });
                else if (command == "close") { window.Dispatcher.BeginInvoke(window.Close); break; }
            }
        });
        window.Closed += (_, _) => window.Dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
        Dispatcher.Run();
    }

    private sealed class Group : Border
    {
        internal bool RawOnly { get; init; }
        protected override AutomationPeer OnCreateAutomationPeer() => new GroupPeer(this);
        private sealed class GroupPeer(Group owner) : FrameworkElementAutomationPeer(owner)
        {
            protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;
            protected override bool IsControlElementCore() => !owner.RawOnly;
            protected override string GetClassNameCore() => "ElementFixtureGroup";
        }
    }
    private static void Warm(AutomationPeer? peer)
    {
        if (peer is null) return;
        _ = peer.GetBoundingRectangle();
        if (peer.GetChildren() is { } children) foreach (AutomationPeer child in children) Warm(child);
    }
    private sealed class Wide : Border
    {
        protected override AutomationPeer OnCreateAutomationPeer() => new WidePeer(this);
        private sealed class WidePeer(Wide owner) : FrameworkElementAutomationPeer(owner)
        {
            private List<AutomationPeer>? _children;
            protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;
            protected override List<AutomationPeer> GetChildrenCore()
            {
                if (_children is not null) return _children;
                _children = new List<AutomationPeer>();
                for (int i = 0; i < 2300; i++) _children.Add(new LeafPeer(new Border(), owner));
                return _children;
            }
        }
        private sealed class LeafPeer(Border owner, Wide container) : FrameworkElementAutomationPeer(owner)
        {
            protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Pane;
            protected override List<AutomationPeer>? GetChildrenCore() => null;
            protected override bool IsOffscreenCore() => false;
            protected override Rect GetBoundingRectangleCore() => new(container.PointToScreen(new Point()),
                container.PointToScreen(new Point(container.ActualWidth, container.ActualHeight)));
        }
    }
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] private static extern int GetWindowLongW(nint hwnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLongW(nint hwnd, int index, int value);
}
