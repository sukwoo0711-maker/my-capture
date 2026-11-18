using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MyCapture.App.Themes;

/// <summary>Input chrome for composite fields, with the original Border renderer in contrast themes.</summary>
public class InputFieldBorder : Border
{
    private readonly InputEdgeDrawing _bottomEdge = new();
    private Pen? _edgePen;
    private Pen? _focusPen;
    public static readonly DependencyProperty IsInvalidProperty = DependencyProperty.Register(
        nameof(IsInvalid), typeof(bool), typeof(InputFieldBorder), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty IsReadOnlyProperty = DependencyProperty.Register(
        nameof(IsReadOnly), typeof(bool), typeof(InputFieldBorder), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty IsInputFocusedProperty = DependencyProperty.Register(
        nameof(IsInputFocused), typeof(bool), typeof(InputFieldBorder), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty IsInputHoveredProperty = DependencyProperty.Register(
        nameof(IsInputHovered), typeof(bool), typeof(InputFieldBorder), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly DependencyProperty InputFillProperty = Paint("InputFill");
    private static readonly DependencyProperty HoverFillProperty = Paint("HoverFill");
    private static readonly DependencyProperty ActiveFillProperty = Paint("ActiveFill");
    private static readonly DependencyProperty SubtleEdgeProperty = Paint("SubtleEdge");
    private static readonly DependencyProperty RestEdgeProperty = Paint("RestEdge");
    private static readonly DependencyProperty FocusEdgeProperty = Paint("FocusEdge");
    private static readonly DependencyProperty ErrorEdgeProperty = Paint("ErrorEdge");
    private static readonly DependencyProperty KeyboardEdgeProperty = Paint("KeyboardEdge");

    static InputFieldBorder()
    {
        ControlLanguage.EnabledProperty.OverrideMetadata(typeof(InputFieldBorder),
            new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));
    }

    public InputFieldBorder()
    {
        SetResourceReference(ControlLanguage.EnabledProperty, "ControlLanguage.Enabled");
        SetResourceReference(InputFillProperty, "Fill.Input");
        SetResourceReference(HoverFillProperty, "Fill.InputHover");
        SetResourceReference(ActiveFillProperty, "Fill.InputActive");
        SetResourceReference(SubtleEdgeProperty, "Border.Subtle");
        SetResourceReference(RestEdgeProperty, "Stroke.InputEdge");
        SetResourceReference(FocusEdgeProperty, "Text.Primary");
        SetResourceReference(ErrorEdgeProperty, "State.Danger");
        SetResourceReference(KeyboardEdgeProperty, "Border.Focus");
        SetResourceReference(CornerRadiusProperty, "Radius.Control");
        KeyboardFocusCue.AddVisibilityChangedHandler(this, (_, _) => InvalidateVisual());
        IsKeyboardFocusWithinChanged += (_, _) => InvalidateVisual();
        MouseEnter += (_, _) => InvalidateVisual();
        MouseLeave += (_, _) => InvalidateVisual();
    }

    public bool IsInvalid { get => (bool)GetValue(IsInvalidProperty); set => SetValue(IsInvalidProperty, value); }
    public bool IsReadOnly { get => (bool)GetValue(IsReadOnlyProperty); set => SetValue(IsReadOnlyProperty, value); }
    public bool IsInputFocused { get => (bool)GetValue(IsInputFocusedProperty); set => SetValue(IsInputFocusedProperty, value); }
    public bool IsInputHovered { get => (bool)GetValue(IsInputHoveredProperty); set => SetValue(IsInputHoveredProperty, value); }
    public static bool GetIsInvalid(DependencyObject element) => (bool)element.GetValue(IsInvalidProperty);
    public static void SetIsInvalid(DependencyObject element, bool value) => element.SetValue(IsInvalidProperty, value);

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (!ControlLanguage.GetEnabled(this)) { base.OnRender(drawingContext); return; }
        if (RenderSize.Width <= 1 || RenderSize.Height <= 1) return;
        bool focused = IsInputFocused || IsKeyboardFocusWithin;
        bool hovered = IsInputHovered || IsMouseOver;
        Brush? fill = IsReadOnly ? Brushes.Transparent : (Brush?)GetValue(
            focused ? ActiveFillProperty : hovered ? HoverFillProperty : InputFillProperty);
        Brush? edge = (Brush?)GetValue(IsInvalid ? ErrorEdgeProperty : SubtleEdgeProperty);
        double radius = CornerRadius.TopLeft;
        if (edge is null) _edgePen = null;
        else if (_edgePen is null || !ReferenceEquals(_edgePen.Brush, edge)) _edgePen = new Pen(edge, 1);
        drawingContext.DrawRoundedRectangle(fill, null, new Rect(RenderSize), radius, radius);
        drawingContext.DrawRoundedRectangle(null, _edgePen,
            new Rect(.5, .5, RenderSize.Width - 1, RenderSize.Height - 1), Math.Max(0, radius - .5), Math.Max(0, radius - .5));
        if (focused && KeyboardFocusCue.GetIsVisible(this) && RenderSize.Width > 2 && RenderSize.Height > 2
            && GetValue(KeyboardEdgeProperty) is Brush keyboardEdge)
        {
            if (_focusPen is null || !ReferenceEquals(_focusPen.Brush, keyboardEdge)) _focusPen = new Pen(keyboardEdge, 2);
            drawingContext.DrawRoundedRectangle(null, _focusPen,
                new Rect(1, 1, RenderSize.Width - 2, RenderSize.Height - 2), Math.Max(0, radius - 1), Math.Max(0, radius - 1));
        }
        // Validation is visible with either input modality, independently of the keyboard ring.
        if (!IsReadOnly || IsInvalid)
            _bottomEdge.Draw(drawingContext, RenderSize, focused ? 2 : 1,
                (Brush?)GetValue(IsInvalid ? ErrorEdgeProperty : focused ? FocusEdgeProperty : RestEdgeProperty), radius);
    }

    private static DependencyProperty Paint(string name) => DependencyProperty.Register(name, typeof(Brush),
        typeof(InputFieldBorder), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
}
