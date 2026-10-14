namespace MyCapture.Core.Primitives;

/// <summary>Pixel-based geometry shared by both images in a read-only comparison.</summary>
public readonly record struct CompareViewport(double Scale, double X, double Y)
{
    public static (double Width, double Height) CanvasSize(
        double firstWidth, double firstHeight, double secondWidth, double secondHeight) =>
        (Positive(Math.Max(firstWidth, secondWidth)), Positive(Math.Max(firstHeight, secondHeight)));

    public static double FitScale(double canvasWidth, double canvasHeight,
        double viewportWidth, double viewportHeight, bool overlay)
    {
        double width = Positive(viewportWidth) / (overlay ? 1 : 2);
        return Math.Min(1, Math.Min(width / Positive(canvasWidth),
            Positive(viewportHeight) / Positive(canvasHeight)));
    }

    public CompareViewport ZoomAt(double factor, double pointerX, double pointerY,
        double minimumScale, double maximumScale)
    {
        if (!double.IsFinite(factor) || factor <= 0 || !double.IsFinite(pointerX)
            || !double.IsFinite(pointerY)) return this;
        double scale = Math.Clamp(Scale * factor, minimumScale, maximumScale);
        double ratio = scale / Scale;
        return new(scale, pointerX - (pointerX - X) * ratio, pointerY - (pointerY - Y) * ratio);
    }

    public CompareViewport Pan(double dx, double dy) =>
        double.IsFinite(dx) && double.IsFinite(dy) ? this with { X = X + dx, Y = Y + dy } : this;

    public static double DividerFraction(double pointerX, double viewportWidth) =>
        !double.IsFinite(pointerX) || !double.IsFinite(viewportWidth) || viewportWidth <= 0
            ? 0.5 : Math.Clamp(pointerX / viewportWidth, 0, 1);

    private static double Positive(double value) => double.IsFinite(value) && value > 0 ? value : 1;
}
