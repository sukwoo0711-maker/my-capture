using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using MyCapture.App.Themes;
using MyCapture.Platform.Shell;
using MyCapture.Platform.Display;

namespace MyCapture.App.Capture;

internal sealed partial class QuickAccessWindow
{
    private ContextMenu? _cardMenu;
    private OpenWithMenu? _openWithMenu;
    internal IOpenWithHandlerSource OpenWithSource { get; set; } = new OpenWithService();
    internal ContextMenu CardMenu => _cardMenu!;

    private void InitializeCardMenu(bool excludeFromCapture)
    {
        ContextMenu menu = _cardMenu = excludeFromCapture ? CaptureWindowExclusion.CreateContextMenu() : new ContextMenu();
        foreach (string action in ActionOrder)
        {
            var item = new MenuItem
            {
                Tag = action, Header = UiText.Get($"QuickAccess.{action}"),
                InputGestureText = ShortcutCatalog.Get(ShortcutScope.QuickAccess, action).Hint,
            };
            AutomationProperties.SetName(item, UiText.Get($"QuickAccess.{action}"));
            AutomationProperties.SetAcceleratorKey(item, item.InputGestureText);
            item.Click += (_, _) => _actions[action]();
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var openWith = new CaptureSubmenuItem(excludeFromCapture, () => _keyboardAccess && IsActive) { Tag = "OpenWith" };
        openWith.SetResourceReference(StyleProperty, typeof(MenuItem));
        menu.Items.Add(openWith);
        _openWithMenu = new OpenWithMenu(openWith, menu, () => OpenWithSource, () => ".png", OpenWithAsync,
            () => new WindowInteropHelper(this).Handle);
        ContextMenu = menu;
        ContextMenuOpening += (_, e) =>
        {
            OpenCardMenu(e.CursorLeft < 0 ? PlacementMode.Bottom : PlacementMode.MousePoint);
            e.Handled = true;
        };
        menu.Opened += (_, _) =>
        {
            if (!_keyboardAccess && PresentationSource.FromVisual(menu) is HwndSource popup)
                WindowStyleFacade.SetNoActivate(popup.Handle, true);
            if (excludeFromCapture) CaptureWindowExclusion.ExcludeMenuPopup(menu);
        };
        menu.Closed += (_, _) => _lastTick = Stopwatch.GetTimestamp();
        Closed += (_, _) => { menu.IsOpen = false; _openWithMenu.Dispose(); };
        _actions["Menu"] = () => OpenCardMenu(PlacementMode.Bottom);
    }

    internal void OpenCardMenu(PlacementMode placement)
    {
        if (_closed || FluidMotion.IsExiting(this)) return;
        ContextMenu menu = CardMenu;
        bool keyboard = _keyboardAccess && IsActive;
        // Mouse actions keep the destination window active, just like the existing buttons. Only
        // explicit keyboard entry enables focusable items; opening a menu never calls Activate.
        menu.Focusable = keyboard;
        foreach (MenuItem item in menu.Items.OfType<MenuItem>())
        {
            string action = (string)item.Tag;
            item.Focusable = keyboard;
            item.IsEnabled = _captureActions is not null && !_running.Contains(action);
        }
        menu.PlacementTarget = _surface;
        menu.Placement = placement;
        menu.IsOpen = true;
    }

    private async Task OpenWithAsync(Func<string, Task> invoke)
    {
        if (_captureActions is not { } bound) return;
        await RunCoreAsync("OpenWith", async () =>
        {
            try
            {
                string path = await bound.Export(); // The same independent PNG as preview drag-out.
                await invoke(path);
                return (UiText.Get("OpenWith.Opened"), FeedbackKind.Confirmation);
            }
            catch (OperationCanceledException) { return (string.Empty, FeedbackKind.Echo); }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                return (OpenWithMenu.FailureMessage(error),
                    FeedbackKind.ActionRequired);
            }
        }, "OpenWith.Preparing");
    }
}
