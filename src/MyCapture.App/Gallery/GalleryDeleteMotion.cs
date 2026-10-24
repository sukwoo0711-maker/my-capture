using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using MyCapture.App.Themes;

namespace MyCapture.App.Gallery;

/// <summary>Where the realized capture cards are, keyed by capture, around a library refresh.</summary>
/// <param name="Cards">Each realized card's bounds in the list's coordinates, as drawn on screen.</param>
/// <param name="Headers">The list's date headings in order.</param>
/// <param name="ScrollOffset">The list's vertical scroll offset.</param>
/// <param name="Viewport">The list's visible size.</param>
/// <param name="Columns">Cards per row.</param>
internal sealed record GalleryCardLayout(IReadOnlyDictionary<Guid, Rect> Cards, IReadOnlyList<string> Headers,
    double ScrollOffset, Size Viewport, int Columns);

/// <summary>A card that starts <see cref="Offset"/> away from its new place and slides into it.</summary>
internal readonly record struct GalleryCardMove(Guid Id, Vector Offset);

/// <summary>
/// The library's delete motion (bench 4-B): deleted cards fade and shrink, the remaining cards
/// slide into the gap, and the result line rises in. Every part moves opacity or a render
/// transform the motion owns; layout always takes its final state at once.
/// </summary>
internal static class GalleryDeleteMotion
{
    /// <summary>A deleted card fades and shrinks to <see cref="ExitScale"/> over this.</summary>
    internal static readonly Duration ExitDuration = new(TimeSpan.FromMilliseconds(120));

    internal const double ExitScale = 0.94;

    /// <summary>The remaining cards slide from their old places to their new ones over this.</summary>
    internal static readonly Duration ReflowDuration = new(TimeSpan.FromMilliseconds(200));

    /// <summary>More moving cards than this reads as the grid rebuilding, not cards closing a gap.</summary>
    internal const int MaxSlidingCards = 60;

    /// <summary>How far the result line rises as it fades in.</summary>
    internal const double StatusRise = 8;

    // A sub-pixel difference is layout rounding, not a move.
    private const double MinimumMove = 0.5;

    // The transform this motion installed on an element; any other transform belongs to the screen.
    private static readonly DependencyProperty OwnedTransformProperty = DependencyProperty.RegisterAttached(
        "OwnedTransform", typeof(Transform), typeof(GalleryDeleteMotion));

    /// <summary>
    /// The cards that slide after a refresh, and from where. Empty when sliding would mislead:
    /// a date heading appeared or disappeared (whole groups move), the list scrolled (everything
    /// jumps together), the column count changed, any card moves farther than the visible height
    /// (it would cross the screen) or more than <see cref="MaxSlidingCards"/> visible cards move.
    /// Only a card realized both before and after the refresh slides, and only when it is in view
    /// at one end of its move; the rest take their places at once.
    /// </summary>
    internal static IReadOnlyList<GalleryCardMove> PlanReflow(GalleryCardLayout before, GalleryCardLayout after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        if (!before.Headers.SequenceEqual(after.Headers, StringComparer.Ordinal)
            || Math.Abs(before.ScrollOffset - after.ScrollOffset) > MinimumMove
            || before.Columns != after.Columns)
        {
            return [];
        }

        var viewport = new Rect(after.Viewport);
        var moves = new List<GalleryCardMove>();
        foreach ((Guid id, Rect now) in after.Cards)
        {
            if (!before.Cards.TryGetValue(id, out Rect then)) continue;
            Vector offset = Center(then) - Center(now);
            if (offset.Length < MinimumMove) continue;
            if (!then.IntersectsWith(viewport) && !now.IntersectsWith(viewport)) continue;
            if (Math.Abs(offset.Y) > viewport.Height || Math.Abs(offset.X) > viewport.Width) return [];
            moves.Add(new GalleryCardMove(id, offset));
        }

        return moves.Count > MaxSlidingCards ? [] : moves;
    }

    // Centres, so a card still shrunk by an earlier motion measures where it really sits.
    private static Point Center(Rect bounds) => new(bounds.X + (bounds.Width / 2), bounds.Y + (bounds.Height / 2));

    /// <summary>
    /// Fades the deleted <paramref name="cards"/> out and shrinks them, keeping their pictures
    /// while their captures are moved away. They stay invisible until <see cref="GalleryCardExit.Reset"/>.
    /// </summary>
    internal static GalleryCardExit BeginExit(IReadOnlyCollection<FrameworkElement> cards)
    {
        if (cards.Count == 0) return GalleryCardExit.None;
        var exit = new GalleryCardExit(cards.First().Dispatcher);
        foreach (FrameworkElement card in cards) exit.Add(card);
        exit.Start();
        return exit;
    }

    /// <summary>Slides <paramref name="card"/> from <paramref name="offset"/> away into its place.</summary>
    internal static bool Slide(FrameworkElement card, Vector offset)
    {
        if (TakeTransform(card, () => new TranslateTransform()) is not TranslateTransform shift) return false;
        var x = new DoubleAnimation(offset.X, 0, ReflowDuration) { EasingFunction = FluidMotion.StandardEasing, FillBehavior = FillBehavior.Stop };
        var y = new DoubleAnimation(offset.Y, 0, ReflowDuration) { EasingFunction = FluidMotion.StandardEasing, FillBehavior = FillBehavior.Stop };
        y.Completed += (_, _) => ReleaseTransform(card, shift);
        shift.BeginAnimation(TranslateTransform.XProperty, x, HandoffBehavior.SnapshotAndReplace);
        shift.BeginAnimation(TranslateTransform.YProperty, y, HandoffBehavior.SnapshotAndReplace);
        return true;
    }

    /// <summary>Puts a sliding or rising element in its place now.</summary>
    internal static void Settle(FrameworkElement element)
    {
        if (element.GetValue(OwnedTransformProperty) is TranslateTransform shift) ReleaseTransform(element, shift);
    }

    /// <summary>Whether <paramref name="element"/> is sliding or rising now.</summary>
    internal static bool IsMoving(FrameworkElement element) =>
        element.GetValue(OwnedTransformProperty) is TranslateTransform shift && ReferenceEquals(element.RenderTransform, shift);

    /// <summary>Fades <paramref name="element"/> in as it rises <see cref="StatusRise"/> into place.</summary>
    internal static void Rise(FrameworkElement element)
    {
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation
        {
            From = 0, Duration = FluidMotion.NormalDuration, EasingFunction = FluidMotion.StandardEasing, FillBehavior = FillBehavior.Stop,
        }, HandoffBehavior.SnapshotAndReplace);
        if (TakeTransform(element, () => new TranslateTransform()) is not TranslateTransform shift) return;
        var rise = new DoubleAnimation(StatusRise, 0, FluidMotion.NormalDuration)
        {
            EasingFunction = FluidMotion.StandardEasing, FillBehavior = FillBehavior.Stop,
        };
        rise.Completed += (_, _) => ReleaseTransform(element, shift);
        shift.BeginAnimation(TranslateTransform.YProperty, rise, HandoffBehavior.SnapshotAndReplace);
    }

    /// <summary>
    /// Installs a transform this motion owns, replacing one it installed before; null when the
    /// screen owns the element's transform, which motion never tramples.
    /// </summary>
    internal static T? TakeTransform<T>(FrameworkElement element, Func<T> create) where T : Transform
    {
        if (element.GetValue(OwnedTransformProperty) is Transform previous && ReferenceEquals(element.RenderTransform, previous))
        {
            StopAnimations(previous);
        }
        else if (element.ReadLocalValue(UIElement.RenderTransformProperty) != DependencyProperty.UnsetValue
            || element.RenderTransform is { Value.IsIdentity: false })
        {
            return null;
        }

        T owned = create();
        element.RenderTransform = owned;
        element.SetValue(OwnedTransformProperty, owned);
        return owned;
    }

    /// <summary>Removes <paramref name="owned"/> when it is still the element's transform.</summary>
    internal static void ReleaseTransform(FrameworkElement element, Transform owned)
    {
        StopAnimations(owned);
        if (!ReferenceEquals(element.GetValue(OwnedTransformProperty), owned)) return;
        element.ClearValue(OwnedTransformProperty);
        if (ReferenceEquals(element.RenderTransform, owned)) element.ClearValue(UIElement.RenderTransformProperty);
    }

    private static void StopAnimations(Transform transform)
    {
        switch (transform)
        {
            case TranslateTransform shift:
                shift.BeginAnimation(TranslateTransform.XProperty, null);
                shift.BeginAnimation(TranslateTransform.YProperty, null);
                break;
            case ScaleTransform scale:
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                break;
        }
    }
}

/// <summary>Deleted cards leaving the library; see <see cref="GalleryDeleteMotion.BeginExit"/>.</summary>
internal sealed class GalleryCardExit
{
    /// <summary>No card is leaving: complete from the start, nothing to reset.</summary>
    internal static readonly GalleryCardExit None = new();

    private readonly List<Leaving> _cards = [];
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly DispatcherTimer? _fallback;

    private GalleryCardExit() => _completion.SetResult();

    internal GalleryCardExit(Dispatcher dispatcher)
    {
        // Completed never fires while the window cannot render (locked session, minimised); the
        // refresh still follows shortly after the fade would have ended.
        _fallback = new DispatcherTimer(DispatcherPriority.Normal, dispatcher)
        {
            Interval = GalleryDeleteMotion.ExitDuration.TimeSpan + TimeSpan.FromMilliseconds(250),
        };
        _fallback.Tick += (_, _) => Finish();
    }

    /// <summary>Completes when the fade has ended.</summary>
    internal Task Completion => _completion.Task;

    /// <summary>The cards that are leaving.</summary>
    internal IReadOnlyList<FrameworkElement> Cards => _cards.Select(card => card.Element).ToArray();

    internal void Add(FrameworkElement card)
    {
        if (ReferenceEquals(this, None)) throw new InvalidOperationException("The empty exit takes no cards.");
        _cards.Add(new Leaving(card));
    }

    internal void Start()
    {
        foreach (Leaving card in _cards)
        {
            card.Begin(Finish);
        }

        _fallback?.Start();
    }

    /// <summary>
    /// Shows every card again as it was: the refresh has removed the deleted ones, and a capture
    /// that stayed (still being saved, or kept from permanent deletion) keeps its card.
    /// </summary>
    internal void Reset()
    {
        Finish();
        foreach (Leaving card in _cards) card.Reset();
    }

    private void Finish()
    {
        _fallback?.Stop();
        _completion.TrySetResult();
    }

    private sealed class Leaving(FrameworkElement element)
    {
        private readonly List<GalleryHeldValue> _pictures = [];
        private ScaleTransform? _scale;
        private object _origin = DependencyProperty.UnsetValue;

        internal FrameworkElement Element => element;

        internal void Begin(Action finished)
        {
            // The capture's thumbnail is released before its files move; the card keeps showing
            // the decoded picture (no file handle) while it fades.
            foreach (GalleryThumbnailImage picture in Pictures(element))
            {
                if (picture.Source is not null && GalleryHeldValue.TryReplace(picture, Image.SourceProperty, picture.Source) is { } held)
                    _pictures.Add(held);
            }

            GalleryDeleteMotion.Settle(element);
            var fade = new DoubleAnimation(0, GalleryDeleteMotion.ExitDuration)
            {
                EasingFunction = FluidMotion.StandardEasing, FillBehavior = FillBehavior.HoldEnd,
            };
            fade.Completed += (_, _) => finished();
            element.BeginAnimation(UIElement.OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);

            _origin = element.ReadLocalValue(UIElement.RenderTransformOriginProperty);
            _scale = GalleryDeleteMotion.TakeTransform(element, () => new ScaleTransform(1, 1));
            if (_scale is null) return;
            element.RenderTransformOrigin = new Point(0.5, 0.5);
            var shrink = new DoubleAnimation(GalleryDeleteMotion.ExitScale, GalleryDeleteMotion.ExitDuration)
            {
                EasingFunction = FluidMotion.StandardEasing, FillBehavior = FillBehavior.HoldEnd,
            };
            _scale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
            _scale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
        }

        internal void Reset()
        {
            element.BeginAnimation(UIElement.OpacityProperty, null);
            if (_scale is not null)
            {
                GalleryDeleteMotion.ReleaseTransform(element, _scale);
                if (_origin == DependencyProperty.UnsetValue) element.ClearValue(UIElement.RenderTransformOriginProperty);
                else element.SetValue(UIElement.RenderTransformOriginProperty, _origin);
                _scale = null;
            }

            foreach (GalleryHeldValue picture in _pictures) picture.Restore();
            _pictures.Clear();
        }

        private static IEnumerable<GalleryThumbnailImage> Pictures(DependencyObject parent)
        {
            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, index);
                if (child is GalleryThumbnailImage picture) yield return picture;
                else foreach (GalleryThumbnailImage nested in Pictures(child)) yield return nested;
            }
        }
    }
}
