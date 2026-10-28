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
    internal static Button CreateButton(bool compact = false, Func<HotkeySettings>? readHotkeys = null,
        Action<GlobalHotkeyCommand>? execute = null)
    {
        readHotkeys ??= ReadHotkeys;
        execute ??= Execute;
        var button = new Button { Name = "NewCapture" };
        button.SetResourceReference(FrameworkElement.StyleProperty, "Button.Secondary");
        var icon = new SymbolIcon { Size = 16, Fill = Brushes.Transparent };
        icon.SetResourceReference(SymbolIcon.DataProperty, "Icon.Capture");
        icon.SetBinding(SymbolIcon.StrokeProperty, new Binding(nameof(Control.Foreground)) { Source = button });
        var label = new TextBlock { Text = UiText.Get("CaptureEntry.NewCapture"), Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        button.Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { icon, label } };
        AutomationProperties.SetName(button, label.Text);
        CommandButtonPresentation.Apply(button, compact);
        CommandButtonPresentation.KeepLabel(button);
        void RefreshToolTip()
        {
            Hotkey hotkey = readHotkeys().Capture;
            button.ToolTip = hotkey.IsAssigned
                ? UiText.Format("LibraryUx_CommandShortcut", UiText.Get("CaptureEntry.Region"), hotkey.ToString())
                : UiText.Get("CaptureEntry.Region");
        }
        RefreshToolTip();
        button.ToolTipOpening += (_, _) => RefreshToolTip();
        button.Click += (_, _) => execute(GlobalHotkeyCommand.CaptureRegion);
        return button;
    }

    internal static FrameworkElement CreateSplitButton(bool compact = false, Func<HotkeySettings>? readHotkeys = null,
        Action<GlobalHotkeyCommand>? execute = null)
    {
        Button main = CreateButton(compact, readHotkeys, execute);
        var options = new Button { Name = "CaptureOptions", ToolTip = UiText.Get("CaptureEntry.MoreOptions") };
        options.SetResourceReference(FrameworkElement.StyleProperty, "Button.Secondary");
        var chevron = new SymbolIcon { Size = 16, Fill = Brushes.Transparent };
        chevron.SetResourceReference(SymbolIcon.DataProperty, "Icon.ChevronDown");
        chevron.SetBinding(SymbolIcon.StrokeProperty, new Binding(nameof(Control.Foreground)) { Source = options });
        options.Content = chevron;
        AutomationProperties.SetName(options, UiText.Get("CaptureEntry.MoreOptions"));
        CommandButtonPresentation.Apply(options, compact);
        options.MinWidth = 28;
        options.Padding = new Thickness(5, 0, 5, 0);
        options.Click += (_, _) => Open(options, readHotkeys, execute);
        main.Margin = options.Margin = new Thickness(0);
        ButtonSizing.SetSplitPart(main, SplitButtonPart.Main);
        ButtonSizing.SetSplitPart(options, SplitButtonPart.Options);
        // Native Button activation supplies Enter/Space for both independent tab stops.
        return new StackPanel
        {
            Name = "NewCaptureSplit", Orientation = Orientation.Horizontal,
            Margin = new Thickness(3, 0, 3, 0), VerticalAlignment = VerticalAlignment.Center,
            Children = { main, options },
        };
    }

    private static HotkeySettings ReadHotkeys() => (Application.Current as App)?.ReadCaptureHotkeys() ?? new HotkeySettings();
    private static void Execute(GlobalHotkeyCommand command) => (Application.Current as App)?.ExecuteCaptureCommand(command);

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
        readHotkeys ??= ReadHotkeys;
        execute ??= Execute;
        var menu = new ContextMenu();
        Add(GlobalHotkeyCommand.CaptureRegion, "CaptureEntry.Region", "Icon.Capture", settings => settings.Capture);
        Add(GlobalHotkeyCommand.CaptureFullScreen, "CaptureEntry.FullScreen", "Icon.CaptureFullScreen", settings => settings.CaptureFullScreen);
        Add(GlobalHotkeyCommand.CaptureWindow, "CaptureEntry.Window", "Icon.CaptureWindow", settings => settings.CaptureWindow);
        Add(GlobalHotkeyCommand.RecordRegion, "CaptureEntry.Recording", "Icon.Record", settings => settings.RecordRegion);
        return menu;

        void Add(GlobalHotkeyCommand command, string key, string iconKey, Func<HotkeySettings, Hotkey> shortcut)
        {
            var item = new MenuItem { Header = UiText.Get(key), Tag = command };
            var icon = new SymbolIcon { Size = 16, Fill = Brushes.Transparent };
            icon.SetResourceReference(SymbolIcon.DataProperty, iconKey);
            icon.SetBinding(SymbolIcon.StrokeProperty, new Binding(nameof(Control.Foreground)) { Source = item });
            if (command == GlobalHotkeyCommand.RecordRegion)
                icon.SetBinding(SymbolIcon.FillProperty, new Binding(nameof(Control.Foreground)) { Source = item });
            item.Icon = icon;
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
