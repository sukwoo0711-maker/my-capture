using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;

namespace MyCapture.App.Themes;

/// <summary>
/// Token brushes are swapped on a theme change, never recoloured in place.
/// </summary>
/// <remarks>
/// <para>
/// WPF's composition engine keeps the render data drawing with a brush in a pointer multiset
/// (dotnet/wpf <c>WpfGfx/core/common/ptrset.cpp</c>). Above 150 entries it removes by binary
/// search, but a compacting removal keeps a stale "sorted" flag, so a later removal can miss
/// its entry. The render data is then freed while the brush still lists it, and the next
/// recolour of that brush calls the freed object on the render thread: an access violation in
/// wpfgfx_cor3, a CFG fast fail, or <c>_purecall</c> abort (0xC0000409). In the UX review
/// fixtures the application's Text.Primary brush alone held 605 entries.
/// </para>
/// <para>
/// Shared brushes are therefore frozen and replaced. Code that keeps a brush across theme
/// changes takes a private brush from <see cref="Follow(FrameworkElement?, string, Brush)"/>;
/// only those few-consumer brushes are recoloured.
/// </para>
/// </remarks>
internal static class ThemeBrushes
{
    private static readonly ConditionalWeakTable<ResourceDictionary, List<Follower>> Followers = new();

    /// <summary>A frozen copy of <paramref name="current"/> in <paramref name="color"/>, keeping opacity and transforms.</summary>
    internal static SolidColorBrush Recolored(SolidColorBrush current, Color color)
    {
        SolidColorBrush next = current.CloneCurrentValue();
        next.Color = color;
        if (next.CanFreeze)
        {
            next.Freeze();
        }

        return next;
    }

    /// <summary>A private brush that follows an application token through theme changes.</summary>
    internal static Brush Follow(string key, Color fallback) => Follow((FrameworkElement?)null, key, new SolidColorBrush(fallback));

    /// <inheritdoc cref="Follow(string, Color)"/>
    internal static Brush Follow(string key, Brush fallback) => Follow((FrameworkElement?)null, key, fallback);

    /// <summary>
    /// A private brush that follows the token as <paramref name="scope"/> resolves it: the
    /// scope's own resources (a workspace window palette) first, then the application's.
    /// </summary>
    internal static Brush Follow(FrameworkElement? scope, string key, Color fallback) =>
        Follow(scope, key, new SolidColorBrush(fallback));

    /// <inheritdoc cref="Follow(FrameworkElement?, string, Color)"/>
    internal static Brush Follow(FrameworkElement? scope, string key, Brush fallback)
    {
        ResourceDictionary? resources = scope?.Resources is { } own && own.Contains(key)
            ? own
            : Application.Current?.Resources is { } app && app.Contains(key) ? app : null;
        return resources is null ? fallback : Follow(resources, key, fallback);
    }

    /// <summary>A private brush that follows <paramref name="key"/> in <paramref name="resources"/>.</summary>
    internal static Brush Follow(ResourceDictionary resources, string key, Brush fallback)
    {
        if (resources[key] is not Brush token)
        {
            return fallback;
        }

        // Non-solid tokens are not palette colours; the shared instance never changes.
        if (token is not SolidColorBrush solid)
        {
            return token;
        }

        var brush = new SolidColorBrush(solid.Color) { Opacity = solid.Opacity };
        List<Follower> followers = Followers.GetOrCreateValue(resources);
        lock (followers)
        {
            followers.Add(new Follower(key, new WeakReference<SolidColorBrush>(brush)));
        }

        return brush;
    }

    /// <summary>Recolours the private brushes of <paramref name="resources"/> after its tokens were swapped.</summary>
    internal static void Refresh(ResourceDictionary resources)
    {
        if (!Followers.TryGetValue(resources, out List<Follower>? followers))
        {
            return;
        }

        lock (followers)
        {
            followers.RemoveAll(follower => !follower.Brush.TryGetTarget(out _));
            foreach (Follower follower in followers)
            {
                // A caller may have frozen its brush (for example inside a frozen pen); it
                // then keeps the colour it had, as a frozen shared token did before.
                if (follower.Brush.TryGetTarget(out SolidColorBrush? brush) && !brush.IsFrozen
                    && resources[follower.Key] is SolidColorBrush token)
                {
                    brush.Color = token.Color;
                }
            }
        }
    }

    private readonly record struct Follower(string Key, WeakReference<SolidColorBrush> Brush);
}
