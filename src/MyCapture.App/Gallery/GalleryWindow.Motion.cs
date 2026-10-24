using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using MyCapture.App.Themes;

namespace MyCapture.App.Gallery;

/// <summary>
/// Library motion around commands: deleted cards leave and the rest close the gap (bench 4-B),
/// the result line rises in, and a copy is confirmed inside the button that made it.
/// </summary>
internal sealed partial class GalleryWindow
{
    /// <summary>
    /// Whether a delete plays its motion: Windows animation effects are on (read at the moment of
    /// the command) and the list is on screen to be seen.
    /// </summary>
    private bool DeleteMotionEnabled => FluidMotion.AnimationsEnabled && IsVisible && WindowState != WindowState.Minimized;

    /// <summary>Starts the exit of every realized card among <paramref name="targets"/>.</summary>
    private GalleryCardExit BeginDeleteExit(IReadOnlyCollection<Guid> targets)
    {
        var deleted = targets.ToHashSet();
        FrameworkElement[] leaving = RealizedCards()
            .Where(card => card.DataContext is GalleryItemViewModel tile && deleted.Contains(tile.Id))
            .ToArray<FrameworkElement>();
        return GalleryDeleteMotion.BeginExit(leaving);
    }

    /// <summary>The realized capture cards: the containers of the tiles in the rows the list has realized.</summary>
    internal IEnumerable<ListBoxItem> RealizedCards() => RealizedCards(RowsList);

    private static IEnumerable<ListBoxItem> RealizedCards(DependencyObject parent)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            if (child is ListBoxItem { DataContext: GalleryItemViewModel } card) yield return card;
            else foreach (ListBoxItem nested in RealizedCards(child)) yield return nested;
        }
    }

    /// <summary>Where the realized cards are drawn now, with the list's headings, scroll and size.</summary>
    private GalleryCardLayout CaptureCardLayout() => CaptureCardLayout(RealizedCards());

    private GalleryCardLayout CaptureCardLayout(IEnumerable<ListBoxItem> cards)
    {
        var bounds = new Dictionary<Guid, Rect>();
        foreach (ListBoxItem card in cards)
        {
            if (card.DataContext is GalleryItemViewModel tile && RowsList.IsAncestorOf(card))
                bounds[tile.Id] = card.TransformToAncestor(RowsList).TransformBounds(new Rect(card.RenderSize));
        }

        string[] headers = _viewModel.Rows.OfType<GalleryHeaderRow>().Select(row => row.Heading).ToArray();
        double offset = FindVisualChild<ScrollViewer>(RowsList)?.VerticalOffset ?? 0;
        return new GalleryCardLayout(bounds, headers, offset, RowsList.RenderSize, _viewModel.ColumnCount);
    }

    /// <summary>
    /// After a refresh, slides each card that moved from where <paramref name="before"/> saw it.
    /// The new layout is measured before the frame that would show it, so no card jumps first.
    /// </summary>
    private void PlayReflow(GalleryCardLayout before)
    {
        foreach (ListBoxItem card in RealizedCards()) GalleryDeleteMotion.Settle(card);
        RowsList.UpdateLayout();
        Dictionary<Guid, ListBoxItem> cards = [];
        foreach (ListBoxItem card in RealizedCards())
        {
            if (card.DataContext is GalleryItemViewModel tile) cards[tile.Id] = card;
        }

        foreach (GalleryCardMove move in GalleryDeleteMotion.PlanReflow(before, CaptureCardLayout(cards.Values)))
        {
            if (cards.TryGetValue(move.Id, out ListBoxItem? card)) GalleryDeleteMotion.Slide(card, move.Offset);
        }
    }

    /// <summary>The delete result and its Undo fade in as they rise into the status line.</summary>
    private void RiseActionStatus()
    {
        GalleryDeleteMotion.Rise(LibraryActionStatus);
        if (LibraryStatusActionPanel.Visibility == Visibility.Visible) GalleryDeleteMotion.Rise(LibraryStatusActionPanel);
    }

    /// <summary>
    /// Confirms a copy that succeeded: inside the copy button that made it, or on the status line
    /// for a shortcut or menu command, which has no button to show it.
    /// </summary>
    private void ConfirmCopy(object? source)
    {
        if (source is ButtonBase button && GalleryCopyFeedback.Confirm(button)) return;
        ShowActionStatus(UiText.Get("Library.Image.Copied"));
    }
}
