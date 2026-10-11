using System.Windows;
using System.Windows.Controls;

namespace MyCapture.App.Gallery;

/// <summary>
/// Lays one row of library cards out in equal columns that share the row's whole width, and
/// gives every card the height that matches its width.
/// </summary>
/// <remarks>
/// <para>
/// The gallery picks the column count from a minimum card track (see
/// <see cref="GalleryRowBuilder.ColumnCountForWidth"/>); this panel then divides the actual row
/// width by that count, so the cards grow until another column fits and a row never ends in an
/// unexplained empty band. A short final row keeps the same cell width, so its cards line up
/// with the rows above and arrow keys keep the column.
/// </para>
/// <para>
/// A card grows taller as it grows wider, from <see cref="BaseCardWidthProperty"/> x
/// <see cref="BaseCardHeightProperty"/> by <see cref="HeightPerWidthProperty"/>, which is the
/// thumbnail's own proportion: the time, caption and command rows keep their size and the
/// preview area grows. The image itself keeps its aspect ratio (Uniform) inside it.
/// </para>
/// <para>
/// Measured without a width limit (a card row hosted outside the library), each cell takes the
/// base card width, so the row looks like the fixed-size cards it replaced.
/// </para>
/// </remarks>
public sealed class GalleryCardRowPanel : Panel
{
    /// <summary>Cards per row; zero or less lays every child out in its own column.</summary>
    public static readonly DependencyProperty ColumnsProperty = DependencyProperty.Register(
        nameof(Columns), typeof(int), typeof(GalleryCardRowPanel),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty BaseCardWidthProperty = DependencyProperty.Register(
        nameof(BaseCardWidth), typeof(double), typeof(GalleryCardRowPanel),
        new FrameworkPropertyMetadata(244.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty BaseCardHeightProperty = DependencyProperty.Register(
        nameof(BaseCardHeight), typeof(double), typeof(GalleryCardRowPanel),
        new FrameworkPropertyMetadata(292.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>Height added for every DIP of card width beyond the base width.</summary>
    public static readonly DependencyProperty HeightPerWidthProperty = DependencyProperty.Register(
        nameof(HeightPerWidth), typeof(double), typeof(GalleryCardRowPanel),
        new FrameworkPropertyMetadata(0.8, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>
    /// Space around a card inside its cell, both sides together: a capture card's margins and
    /// selection outline, or a folder card's margins.
    /// </summary>
    public static readonly DependencyProperty CardInsetProperty = DependencyProperty.Register(
        nameof(CardInset), typeof(double), typeof(GalleryCardRowPanel),
        new FrameworkPropertyMetadata(16.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public int Columns
    {
        get => (int)GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    public double BaseCardWidth
    {
        get => (double)GetValue(BaseCardWidthProperty);
        set => SetValue(BaseCardWidthProperty, value);
    }

    public double BaseCardHeight
    {
        get => (double)GetValue(BaseCardHeightProperty);
        set => SetValue(BaseCardHeightProperty, value);
    }

    public double HeightPerWidth
    {
        get => (double)GetValue(HeightPerWidthProperty);
        set => SetValue(HeightPerWidthProperty, value);
    }

    public double CardInset
    {
        get => (double)GetValue(CardInsetProperty);
        set => SetValue(CardInsetProperty, value);
    }

    /// <summary>The card height for a card width, rounded to whole DIPs so rows stay crisp.</summary>
    public static double CardHeightFor(double cardWidth, double baseWidth, double baseHeight, double heightPerWidth) =>
        Math.Max(1, Math.Round(baseHeight + (cardWidth - baseWidth) * heightPerWidth));

    private int ColumnCount => Columns > 0 ? Columns : Math.Max(1, InternalChildren.Count);

    private double CellWidth(double rowWidth) =>
        double.IsInfinity(rowWidth) || double.IsNaN(rowWidth) || rowWidth <= 0
            ? BaseCardWidth + CardInset
            : rowWidth / ColumnCount;

    private double CellHeight(double cellWidth) =>
        CardHeightFor(cellWidth - CardInset, BaseCardWidth, BaseCardHeight, HeightPerWidth) + CardInset;

    protected override Size MeasureOverride(Size availableSize)
    {
        double cell = CellWidth(availableSize.Width);
        var cellSize = new Size(cell, CellHeight(cell));
        foreach (UIElement child in InternalChildren) child.Measure(cellSize);
        if (InternalChildren.Count == 0) return new Size();
        double width = double.IsInfinity(availableSize.Width) ? cell * InternalChildren.Count : availableSize.Width;
        return new Size(width, cellSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double cell = CellWidth(finalSize.Width);
        double height = CellHeight(cell);
        for (int index = 0; index < InternalChildren.Count; index++)
            InternalChildren[index].Arrange(new Rect(index * cell, 0, cell, height));
        return finalSize;
    }
}
