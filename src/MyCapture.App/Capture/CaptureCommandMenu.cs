using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using MyCapture.App.Themes;
using MyCapture.Core.Settings;
using MyCapture.Platform.Shell;

namespace MyCapture.App.Capture;

/// <summary>One discoverable menu for the existing application capture commands.</summary>
internal static class CaptureCommandMenu
{
    internal static Button CreateButton(bool compact = false)
    {
        var button = new Button { Name = "NewCapture", ToolTip = UiText.Get("CaptureEntry.NewCapture") };
        button.SetResourceReference(FrameworkElement.StyleProperty, "Button.Secondary");
        var icon = new SymbolIcon { Size = 16, Fill = Brushes.Transparent };
        icon.SetResourceReference(SymbolIcon.DataProperty, "Icon.Capture");
        icon.SetBinding(SymbolIcon.StrokeProperty, new Binding(nameof(Control.Foreground)) { Source = button });
        var label = new TextBlock { Text = UiText.Get("CaptureEntry.NewCapture"), Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        button.Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { icon, label } };
        AutomationProperties.SetName(button, label.Text);
        CommandButtonPresentation.Apply(button, compact);
        CommandButtonPresentation.KeepLabel(button);
        button.Click += (_, _) => Open(button);
        return button;
    }

    internal static void Open(FrameworkElement anchor, Func<HotkeySettings>? readHotkeys = null,
        Action<GlobalHotkeyCommand>? execute = null)
    {
        ContextMenu menu = Create(readHotkeys, execute);
        menu.PlacementTarget = anchor;
        menu.Placement = PlacementMode.Bottom;
        anchor.ContextMenu = menu;
        menu.IsOpen = true;
    }

    internal static ContextMenu Create(Func<HotkeySettings>? readHotkeys = null,
        Action<GlobalHotkeyCommand>? execute = null)
    {
        readHotkeys ??= () => (Application.Current as App)?.ReadCaptureHotkeys() ?? new HotkeySettings();
        execute ??= command => (Application.Current as App)?.ExecuteCaptureCommand(command);
        var menu = new ContextMenu();
        Add(GlobalHotkeyCommand.CaptureRegion, "CaptureEntry.Region", settings => settings.Capture);
        Add(GlobalHotkeyCommand.CaptureFullScreen, "CaptureEntry.FullScreen", settings => settings.CaptureFullScreen);
        Add(GlobalHotkeyCommand.CaptureWindow, "CaptureEntry.Window", settings => settings.CaptureWindow);
        Add(GlobalHotkeyCommand.RecordRegion, "CaptureEntry.Recording", settings => settings.RecordRegion);
        return menu;

        void Add(GlobalHotkeyCommand command, string key, Func<HotkeySettings, Hotkey> shortcut)
        {
            var item = new MenuItem { Header = UiText.Get(key), Tag = command };
            AutomationProperties.SetName(item, UiText.Get(key));
            void Refresh()
            {
                Hotkey hotkey = shortcut(readHotkeys());
                item.InputGestureText = hotkey.IsAssigned ? hotkey.ToString() : string.Empty;
                AutomationProperties.SetAcceleratorKey(item, item.InputGestureText);
            }
            Refresh();
            menu.Opened += (_, _) => Refresh();
            item.Click += (_, _) =>
            {
                menu.IsOpen = false;
                // Let the popup release capture before window targeting and frame acquisition.
                _ = menu.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                    new Action(() => execute(command)));
            };
            menu.Items.Add(item);
        }
    }
}
