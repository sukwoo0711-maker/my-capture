using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using MyCapture.Core.Primitives;

namespace MyCapture.App.Editing;

internal sealed partial class AnnotationEditorControl
{
    private static readonly (object Key, DependencyProperty Property)[] InspectorMeasureResources =
        new object[] { typeof(TextBox), typeof(TextBlock), typeof(ComboBox), typeof(CheckBox), typeof(Button),
            "Font.Ui", "FontSize.Body", "FontSize.Control", "FontSize.Caption", "Inspector.SectionLabel",
            "Button.GhostCompact", "Button.Ghost", "Surface.Raised" }
        .Select((key, index) => (key, DependencyProperty.Register("InspectorMeasureResource" + index,
            typeof(object), typeof(AnnotationEditorControl),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure))))
        .ToArray();
    private PropertyGroupsPanel _propertyGroups = null!;
    private object? _inspectorMeasureKey;
    private (bool crop, bool selection, bool color, bool thickness, bool shape, bool font,
        bool text, bool blur, bool mosaic, bool step)? _inspectorGroupState;
    private double _desiredInspectorHeight = 48;
    private double _stableInspectorHeight = 48;
    private RectD? _lastNotifiedCropRegion;
    private int _inspectorUpdateDepth;
    private bool _inspectorUpdatePending;
    internal int InspectorSynchronizationCount { get; private set; }
    internal int InspectorBudgetMeasureCount { get; private set; }

    private void TrackInspectorMeasureResources()
    {
        // A lookup in MeasureOverride does not itself subscribe to resource changes. Hidden
        // groups may have no live typography consumer, but their reserved budget still must refresh.
        foreach (var (key, property) in InspectorMeasureResources) SetResourceReference(property, key);
    }

    protected override Size MeasureOverride(Size constraint)
    {
        if (_propertyGroups is not null && double.IsFinite(constraint.Width) && constraint.Width > ToolRailWidth + 8)
        {
            double width = constraint.Width - ToolRailWidth - 8;
            DpiScale dpi = VisualTreeHelper.GetDpi(this);
            object key = (width, dpi.DpiScaleX, dpi.DpiScaleY, Language.IetfLanguageTag,
                UiText.Culture.Name, TextElement.GetFontSize(this), TextElement.GetFontFamily(this),
                TextElement.GetFontWeight(this), TextElement.GetFontStyle(this),
                (TryFindResource(typeof(TextBox)), TryFindResource(typeof(TextBlock)), TryFindResource(typeof(ComboBox)),
                    TryFindResource(typeof(CheckBox)), TryFindResource(typeof(Button))),
                (TryFindResource("Font.Ui"), TryFindResource("FontSize.Body"), TryFindResource("FontSize.Control"),
                    TryFindResource("FontSize.Caption"), TryFindResource("Inspector.SectionLabel")),
                (TryFindResource("Button.GhostCompact"), TryFindResource("Button.Ghost"), TryFindResource("Surface.Raised")));
            if (!Equals(_inspectorMeasureKey, key))
            {
                // Only geometry/typography changes invalidate this measurement. Selection and
                // typed values keep the same budget and never reveal hidden groups on screen.
                _inspectorMeasureKey = key;
                InspectorBudgetMeasureCount++;
                _desiredInspectorHeight = MeasureInspectorConfigurations(width) + 9;
            }
            double cap = double.IsFinite(constraint.Height) ? Math.Max(48, constraint.Height * 0.36) : _inspectorPanel.MaxHeight;
            _stableInspectorHeight = Math.Max(48, Math.Min(cap, _desiredInspectorHeight));
            _inspectorPanel.MaxHeight = cap;
            if (!_inspectorHeightBeforeGesture.HasValue) _inspectorPanel.Height = _stableInspectorHeight;
        }
        return base.MeasureOverride(constraint);
    }

    private double MeasureInspectorConfigurations(double width)
    {
        FrameworkElement[] groups = [_colorSection, _thicknessSection, _shapeStyleSection, _fillSection,
            _stepSection, _fontFamilySection, _fontSizeSection, _textStyleSection, _blurSection,
            _mosaicSection, _cropSection, _deleteButton];
        var sizes = new Size[groups.Length];
        // A validation message is part of the crop configuration even before a value is invalid.
        var errors = _cropInputErrors.Values.Select(error => (error.Message, error.Message.Visibility)).ToArray();
        foreach (var (message, _) in errors) message.Visibility = Visibility.Hidden;
        try
        {
            for (int index = 0; index < groups.Length; index++)
            {
                FrameworkElement group = groups[index];
                Visibility visibility = group.Visibility;
                if (visibility == Visibility.Collapsed) group.Visibility = Visibility.Hidden;
                try
                {
                    group.Measure(new Size(width, double.PositiveInfinity));
                    sizes[index] = group.DesiredSize;
                }
                finally { group.Visibility = visibility; }
            }
        }
        finally
        {
            foreach (var (message, visibility) in errors) message.Visibility = visibility;
        }

        // Enumerate reachable sets, including smaller selections: with unequal group
        // heights, removing a group can produce a taller wrap. Color/thickness/style
        // aggregate over the selection; font/text/blur depend on its primary item.
        int[][] selections =
        [
            [0, 5, 6, 7, 11], [0, 1, 5, 6, 7, 11], [0, 1, 2, 3, 5, 6, 7, 11], // text
            [0, 6, 11], [0, 1, 6, 11], [0, 1, 2, 3, 6, 11], // number
            [0, 1, 11], [0, 1, 2, 3, 11], // pen/line/shape
            [8, 11], [0, 8, 11], [0, 1, 8, 11], [0, 1, 2, 3, 8, 11], // blur
            [11], [0, 11], [0, 1, 11], [0, 1, 2, 3, 11], // image
        ];
        int[][] defaults = [[], [0, 1], [0, 1, 2, 3], [0, 5, 6, 7], [0, 4], [8], [9]];
        double maximum = WrappedHeight([10]);
        foreach (int[] configuration in defaults) maximum = Math.Max(maximum, WrappedHeight(configuration));
        foreach (int[] primary in selections)
            foreach (int extra in new[] { -1, 4, 9 })
                maximum = Math.Max(maximum, WrappedHeight(extra < 0 ? primary : primary.Append(extra).Order().ToArray()));
        return maximum;

        double WrappedHeight(int[] indices)
        {
            double lineWidth = 0, lineHeight = 0, height = 0;
            foreach (int index in indices)
            {
                Size size = sizes[index];
                if (lineWidth > 0 && lineWidth + size.Width > width)
                {
                    height += lineHeight;
                    lineWidth = lineHeight = 0;
                }
                lineWidth += size.Width;
                lineHeight = Math.Max(lineHeight, size.Height);
            }
            return height + lineHeight;
        }
    }

    private IDisposable BatchInspectorUpdate()
    {
        _inspectorUpdateDepth++;
        return new InspectorUpdateScope(this);
    }

    private sealed class InspectorUpdateScope(AnnotationEditorControl owner) : IDisposable
    {
        public void Dispose()
        {
            if (--owner._inspectorUpdateDepth == 0 && owner._inspectorUpdatePending)
            {
                owner._inspectorUpdatePending = false;
                owner.UpdateInspector();
            }
        }
    }
}
