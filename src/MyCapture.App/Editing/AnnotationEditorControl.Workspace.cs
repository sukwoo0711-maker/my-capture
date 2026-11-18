using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using MyCapture.App.Media;
using MyCapture.App.Themes;

namespace MyCapture.App.Editing;

internal sealed partial class AnnotationEditorControl
{
    private UIElement _workspaceCommandBar = null!;
    private UIElement _workspaceToolRail = null!;
    private UIElement _workspaceStatusBar = null!;
    private UIElement[] _workspaceChrome = [];
    private bool _workspaceEntrancePrepared;
    private bool _workspaceEntranceRunning;
    private int _workspaceEntranceGeneration;

    // A sheet with a masking strip denotes redacted information instead of a locked feature.
    // It uses the symbol family's authored 20-unit grid and the shared 1.6-unit stroke.
    internal static Geometry PrivacyMaskGeometry { get; } = CreatePrivacyMaskGeometry();

    private static Geometry CreatePrivacyMaskGeometry()
    {
        Geometry geometry = Geometry.Parse("M4,3.5 H16 V16.5 H4 Z M7,6.5 H11 M6.5,9.25 H13.5 V11.75 H6.5 Z M7,14 H10");
        geometry.Freeze();
        return geometry;
    }

    private Button BuildResultMoreMenu()
    {
        var menu = new ContextMenu();
        var reduceExport = new MenuItem { Header = UiText.Get("Library.ExportImage") };
        AutomationName(reduceExport, UiText.Get("Library.ExportImage"));
        reduceExport.Click += (_, _) => Commit(EditorCommitAction.SaveAs, reduceExport: true);
        menu.Items.Add(reduceExport);

        var information = new MenuItem { Header = UiText.Get("MediaInfo.Title") };
        AutomationProperties.SetAutomationId(information, "MediaInfo");
        AutomationName(information, UiText.Get("MediaInfo.Title"));
        information.Click += async (_, _) =>
        {
            if (Window.GetWindow(this) is { } owner)
                await MediaDetailsWindow.ShowAsync(owner, [ReadMediaDetailsSource()]);
        };
        // File information is the final entry, following Explorer's Properties convention.
        menu.Items.Add(information);

        Button button = IconTextButton(UiText.Get("Common_More"), UiText.Get("Common_More"),
            "Icon.More", () => Geometry.Parse("M4,9.25 A0.75,0.75 0 1 1 4,10.75 A0.75,0.75 0 1 1 4,9.25 M10,9.25 A0.75,0.75 0 1 1 10,10.75 A0.75,0.75 0 1 1 10,9.25 M16,9.25 A0.75,0.75 0 1 1 16,10.75 A0.75,0.75 0 1 1 16,9.25"),
            () => { });
        AutomationProperties.SetAutomationId(button, "EditorFlowMore");
        button.ContextMenu = menu;
        CommandButton(button, borderless: true);
        CommandButtonPresentation.AlwaysIcon(button);
        button.Click += (_, _) =>
        {
            menu.PlacementTarget = button;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        };
        return button;
    }

    /// <summary>Reserves the chrome's final layout before the source's first visible frame.</summary>
    internal void PrepareWorkspaceEntrance()
    {
        CompleteWorkspaceEntrance();
        _workspaceChrome = [_workspaceCommandBar, _workspaceToolRail, _inspectorPanel, _workspaceStatusBar];
        // Always-visible shortcut captions retain their immediate, unanimated presentation.
        if (!FluidMotion.AnimationsEnabled || ShortcutPresentation.ReadEnabled?.Invoke() == true) return;
        _workspaceEntrancePrepared = true;
        foreach (UIElement chrome in _workspaceChrome) chrome.Opacity = 0;
    }

    /// <summary>Reveals chrome only, after the host has rendered the exact source layout.</summary>
    internal void BeginWorkspaceEntrance()
    {
        if (!_workspaceEntrancePrepared) return;
        _workspaceEntrancePrepared = false;
        if (!FluidMotion.AnimationsEnabled || ShortcutPresentation.ReadEnabled?.Invoke() == true)
        {
            _workspaceEntranceRunning = true;
            CompleteWorkspaceEntrance();
            return;
        }
        _workspaceEntranceRunning = true;
        int generation = ++_workspaceEntranceGeneration;
        for (int index = 0; index < _workspaceChrome.Length; index++)
        {
            UIElement chrome = _workspaceChrome[index];
            chrome.Opacity = 1;
            var animation = new DoubleAnimation(0, 1, FluidMotion.NormalDuration)
            {
                EasingFunction = FluidMotion.StandardEasing,
                FillBehavior = FillBehavior.Stop,
            };
            if (index == 0) animation.Completed += (_, _) =>
            {
                if (generation == _workspaceEntranceGeneration) CompleteWorkspaceEntrance();
            };
            chrome.BeginAnimation(OpacityProperty, animation, HandoffBehavior.SnapshotAndReplace);
        }
    }

    /// <summary>Input, unload and reduced motion settle chrome without moving any content.</summary>
    internal void CompleteWorkspaceEntrance()
    {
        if (!_workspaceEntrancePrepared && !_workspaceEntranceRunning) return;
        _workspaceEntranceGeneration++;
        _workspaceEntrancePrepared = _workspaceEntranceRunning = false;
        foreach (UIElement chrome in _workspaceChrome)
        {
            chrome.BeginAnimation(OpacityProperty, null);
            chrome.Opacity = 1;
        }
    }
}
