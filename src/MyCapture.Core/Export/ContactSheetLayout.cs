using System.Globalization;

namespace MyCapture.Core.Export;

public enum ContactSheetPageSize { A4Portrait, A4Landscape, LetterPortrait }
public enum ContactSheetGrid { OneByOne, OneByTwo, TwoByTwo, TwoByThree, ThreeByFour }
public enum ContactSheetCaption { Title, CaptureTime, TitleAndCaptureTime, None }

public sealed record ContactSheetOptions(
    ContactSheetPageSize PageSize = ContactSheetPageSize.A4Portrait,
    ContactSheetGrid Grid = ContactSheetGrid.TwoByTwo,
    ContactSheetCaption Caption = ContactSheetCaption.Title,
    bool ShowSource = false);

/// <summary>PDF points, measured from the top left of a page or cell.</summary>
public readonly record struct ContactSheetRect(double X, double Y, double Width, double Height);

/// <summary>Fixed half-inch margins and one-sixth-inch gutters, shared by preview and export.</summary>
public sealed class ContactSheetLayout
{
    public const int MaximumItems = 500;
    public const int MaximumPages = 100;
    public const int ExportDpi = 200;
    public const double Margin = 36;
    public const double Gap = 12;

    public ContactSheetLayout(ContactSheetOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!Enum.IsDefined(options.Caption)) throw new ArgumentOutOfRangeException(nameof(options));
        (PageWidth, PageHeight) = options.PageSize switch
        {
            ContactSheetPageSize.A4Portrait => (595.276, 841.89),
            ContactSheetPageSize.A4Landscape => (841.89, 595.276),
            ContactSheetPageSize.LetterPortrait => (612, 792),
            _ => throw new ArgumentOutOfRangeException(nameof(options)),
        };
        (Columns, Rows) = options.Grid switch
        {
            ContactSheetGrid.OneByOne => (1, 1),
            ContactSheetGrid.OneByTwo => (1, 2),
            ContactSheetGrid.TwoByTwo => (2, 2),
            ContactSheetGrid.TwoByThree => (2, 3),
            ContactSheetGrid.ThreeByFour => (3, 4),
            _ => throw new ArgumentOutOfRangeException(nameof(options)),
        };
    }

    public double PageWidth { get; }
    public double PageHeight { get; }
    public int Columns { get; }
    public int Rows { get; }
    public int CellsPerPage => Columns * Rows;

    public int PageCount(int itemCount)
    {
        if (itemCount is < 1 or > MaximumItems) throw new ArgumentOutOfRangeException(nameof(itemCount));
        int pages = (itemCount + CellsPerPage - 1) / CellsPerPage;
        if (pages > MaximumPages) throw new ArgumentOutOfRangeException(nameof(itemCount));
        return pages;
    }

    public ContactSheetRect Cell(int index)
    {
        if (index < 0 || index >= CellsPerPage) throw new ArgumentOutOfRangeException(nameof(index));
        double width = (PageWidth - 2 * Margin - (Columns - 1) * Gap) / Columns;
        double height = (PageHeight - 2 * Margin - (Rows - 1) * Gap) / Rows;
        return new(Margin + index % Columns * (width + Gap), Margin + index / Columns * (height + Gap), width, height);
    }

    public static ContactSheetRect Fit(double width, double height, ContactSheetRect bounds)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0
            || !double.IsFinite(bounds.X) || !double.IsFinite(bounds.Y)
            || !double.IsFinite(bounds.Width) || !double.IsFinite(bounds.Height) || bounds.Width <= 0 || bounds.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(bounds));
        double scale = Math.Min(bounds.Width / width, bounds.Height / height);
        double fittedWidth = width * scale, fittedHeight = height * scale;
        return new(bounds.X + (bounds.Width - fittedWidth) / 2, bounds.Y + (bounds.Height - fittedHeight) / 2,
            fittedWidth, fittedHeight);
    }
}

public static class ContactSheetCaptions
{
    /// <summary>Only explicitly requested metadata is returned; no filename or source-title fallback.</summary>
    public static IReadOnlyList<string> Lines(ContactSheetOptions options, string title, DateTimeOffset capturedAt,
        string? sourceApp, string? sourceWindow, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(culture);
        var lines = new List<string>();
        switch (options.Caption)
        {
            case ContactSheetCaption.Title: Add(title); break;
            case ContactSheetCaption.CaptureTime: Time(); break;
            case ContactSheetCaption.TitleAndCaptureTime: Add(title); Time(); break;
            case ContactSheetCaption.None: break;
            default: throw new ArgumentOutOfRangeException(nameof(options));
        }
        if (options.ShowSource) { Add(sourceApp); Add(sourceWindow); }
        return lines;

        void Time() => lines.Add(capturedAt.ToLocalTime().ToString("g", culture));
        void Add(string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                lines.Add(value.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Trim());
        }
    }
}
