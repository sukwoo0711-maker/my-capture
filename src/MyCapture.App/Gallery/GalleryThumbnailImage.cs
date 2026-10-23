using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using MyCapture.App.Themes;

namespace MyCapture.App.Gallery;

/// <summary>
/// A card's thumbnail. A bitmap that arrives while the card is already on screen fades in;
/// one that is ready when the card is realized appears at once.
/// </summary>
/// <remarks>
/// <para>
/// Thumbnails decode asynchronously (<see cref="GalleryItemViewModel"/>), so a card shows its
/// plain placeholder first. When the decoded bitmap replaces that placeholder on a loaded card,
/// the image fades in over 150ms with a soft landing; its size never changes, so screenshot text
/// stays sharp throughout.
/// </para>
/// <para>
/// A cached bitmap is bound while the container is being realized, before it is loaded, so
/// scrolling back, a recycled row and a refresh that keeps the cache show the picture without a
/// fade and the grid never shimmers. Windows animation effects are read at the moment the bitmap
/// arrives; with them off the image simply appears. Only opacity moves, never layout.
/// </para>
/// </remarks>
public sealed class GalleryThumbnailImage : Image
{
    /// <summary>The library's thumbnail fade (bench 4-B), between Motion.Fast and Motion.Normal.</summary>
    internal static readonly Duration FadeDuration = new(TimeSpan.FromMilliseconds(150));

    private AnimationTimeline? _fade;

    public GalleryThumbnailImage() => Unloaded += (_, _) => StopFade();

    /// <summary>Whether the arrival fade is playing.</summary>
    internal bool IsFading => _fade is not null;

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property != SourceProperty) return;

        if (e.OldValue is null && e.NewValue is not null && IsLoaded && FluidMotion.AnimationsEnabled)
        {
            BeginFade();
        }
        else
        {
            // A cleared or replaced picture never keeps the fade of the previous one.
            StopFade();
        }
    }

    private void BeginFade()
    {
        // From zero to the image's own opacity; Stop hands back the unchanged base value.
        var fade = new DoubleAnimation
        {
            From = 0,
            Duration = FadeDuration,
            EasingFunction = FluidMotion.SoftLandingEasing,
            FillBehavior = FillBehavior.Stop,
        };
        fade.Completed += (_, _) =>
        {
            if (ReferenceEquals(_fade, fade)) StopFade();
        };
        _fade = fade;
        BeginAnimation(OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
    }

    private void StopFade()
    {
        if (_fade is null) return;
        _fade = null;
        BeginAnimation(OpacityProperty, null);
    }
}
