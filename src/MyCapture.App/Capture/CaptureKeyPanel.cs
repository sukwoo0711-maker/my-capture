using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using MyCapture.App.Themes;
using MyCapture.Core.Settings;

namespace MyCapture.App.Capture;

internal readonly record struct CaptureKeyPanelContext(bool Expanded, bool CanDrag, bool WindowDetection,
    bool HasSelection, bool Ratio, bool Color, bool Text, bool TextMode, bool PinAvailable, Hotkey PinKey);

/// <summary>Input-transparent key rows. A collapsed panel retains only the F1 discovery chip.</summary>
internal sealed class CaptureKeyPanel : Border
{
    private readonly StackPanel _rows = new();
    private readonly FontFamily _mono;
    private CaptureKeyPanelContext? _context;
    internal IReadOnlyList<string> RowTexts { get; private set; } = [];
    internal int ContentBuildCount { get; private set; }
    internal int MeasureCount { get; private set; }
    internal int ArrangeCount { get; private set; }

    protected override Size MeasureOverride(Size constraint)
    {
        MeasureCount++;
        return base.MeasureOverride(constraint);
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        ArrangeCount++;
        return base.ArrangeOverride(arrangeSize);
    }

    internal CaptureKeyPanel(FontFamily ui, FontFamily mono)
    {
        _mono = mono;
        TextElement.SetFontFamily(this, ui);
        IsHitTestVisible = false;
        Focusable = false;
        SnapsToDevicePixels = true;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(8);
        Padding = new Thickness(12, 8, 12, 8);
        SetResourceReference(BackgroundProperty, CaptureModeBar.SurfaceKey);
        SetResourceReference(BorderBrushProperty, CaptureModeBar.BorderKey);
        AutomationProperties.SetName(this, UiText.Get("CaptureKeyPanel.Title"));
        Child = _rows;
    }

    internal bool Configure(CaptureKeyPanelContext context)
    {
        if (_context == context) return false;
        _context = context;
        ContentBuildCount++;
        _rows.Children.Clear();
        var texts = new List<string>();
        var keyColumns = new List<(ColumnDefinition Column, Border Cap)>();
        if (context.Expanded)
        {
            if (context.CanDrag) Add(UiText.Get("CaptureKeyPanel.Drag"), "CaptureKeyPanel.Region");
            if (context.WindowDetection) Add(UiText.Get("CaptureKeyPanel.Click"), "CaptureKeyPanel.Window");
            if (context.HasSelection)
            {
                Add("← ↑ → ↓", "CaptureKeyPanel.Move");
                Add("Shift + ← ↑ → ↓", "CaptureKeyPanel.Resize");
            }
            Add("Ctrl + A", "CaptureKeyPanel.All");
            if (context.Ratio) Add("R", "CaptureKeyPanel.Ratio");
            if (context.Color) Add("C", "CaptureKeyPanel.Color");
            if (context.Text) Add("T", context.TextMode ? "CaptureKeyPanel.Image" : "CaptureKeyPanel.Text");
            if (context.HasSelection && context.PinAvailable && context.PinKey.IsAssigned) Add(context.PinKey.ToString(), "CaptureKeyPanel.Pin");
            Add(context.HasSelection ? "Enter · Esc" : "Esc",
                context.HasSelection ? "CaptureKeyPanel.ConfirmCancel" : "CaptureKeyPanel.Cancel");
        }
        Add("F1", context.Expanded ? "CaptureKeyPanel.Hide" : "CaptureKeyPanel.Show");
        // Each prebuilt row set owns its column width; the hidden selection variant must not
        // widen the ready panel. This work runs only when the available rows change.
        double keyWidth = 0;
        foreach (var (_, cap) in keyColumns)
        {
            cap.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            keyWidth = Math.Max(keyWidth, cap.DesiredSize.Width);
        }
        foreach (var (column, _) in keyColumns) column.Width = new GridLength(keyWidth);
        RowTexts = texts;
        return true;

        void Add(string keys, string resource)
        {
            string label = UiText.Get(resource);
            string name = $"{keys} — {label}";
            texts.Add(name);
            var row = new KeyRow(name) { Margin = new Thickness(0, 3, 0, 3) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var key = new TextBlock { Text = keys, FontFamily = _mono, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
            key.SetResourceReference(TextBlock.ForegroundProperty, CaptureModeBar.TextKey);
            var cap = new Border { Child = key, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3),
                Padding = new Thickness(5, 2, 5, 2), VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right };
            cap.SetResourceReference(BorderBrushProperty, CaptureModeBar.BorderKey);
            var text = new TextBlock { Text = label, FontSize = 12, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            text.SetResourceReference(TextBlock.ForegroundProperty, keys == "F1" ? CaptureModeBar.MutedKey : CaptureModeBar.TextKey);
            Grid.SetColumn(text, 1);
            row.Children.Add(cap);
            row.Children.Add(text);
            _rows.Children.Add(row);
            keyColumns.Add((row.ColumnDefinitions[0], cap));
        }
    }

    internal void ApplyPalette(IReadOnlyDictionary<string, Brush> palette)
    {
        Resources[CaptureModeBar.SurfaceKey] = palette["CaptureGuide.ChipSurface"];
        Resources[CaptureModeBar.BorderKey] = palette["CaptureGuide.ChipBorder"];
        Resources[CaptureModeBar.TextKey] = palette["CaptureGuide.Text"];
        Resources[CaptureModeBar.MutedKey] = palette["CaptureGuide.MutedText"];
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new PanelPeer(this);
    private sealed class PanelPeer(CaptureKeyPanel owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetClassNameCore() => nameof(CaptureKeyPanel);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;
        protected override bool IsControlElementCore() => owner.Visibility == Visibility.Visible;
        protected override bool IsContentElementCore() => owner.Visibility == Visibility.Visible;
        protected override List<AutomationPeer>? GetChildrenCore() =>
            owner.Visibility == Visibility.Visible ? base.GetChildrenCore() : null;
    }
    private sealed class KeyRow(string name) : Grid
    {
        protected override AutomationPeer OnCreateAutomationPeer() => new RowPeer(this, name);
    }
    private sealed class RowPeer(FrameworkElement owner, string name) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetNameCore() => name;
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;
        protected override List<AutomationPeer>? GetChildrenCore() => null;
    }
}
