namespace MyCapture.Core.Pin;

/// <summary>
/// How large a spreadsheet's selected range looked on screen when its picture was pinned: the
/// range's size in points at 100% sheet zoom and the sheet window's zoom in percent.
/// </summary>
public readonly record struct SpreadsheetSelectionView(double WidthPoints, double HeightPoints, double ZoomPercent)
{
    /// <summary>Smallest sheet zoom a spreadsheet window offers (10%).</summary>
    public const double MinZoomPercent = 10;

    /// <summary>Largest sheet zoom a spreadsheet window offers (400%).</summary>
    public const double MaxZoomPercent = 400;

    /// <summary>Largest range edge, in points, accepted as a description of a pinned picture.</summary>
    public const double MaxEdgePoints = 100_000;

    /// <summary>Whether every value is finite and inside the range a spreadsheet window reports.</summary>
    public bool IsPlausible =>
        double.IsFinite(WidthPoints) && double.IsFinite(HeightPoints) && double.IsFinite(ZoomPercent)
        && WidthPoints > 0 && HeightPoints > 0 && WidthPoints <= MaxEdgePoints && HeightPoints <= MaxEdgePoints
        && ZoomPercent >= MinZoomPercent && ZoomPercent <= MaxZoomPercent;
}
