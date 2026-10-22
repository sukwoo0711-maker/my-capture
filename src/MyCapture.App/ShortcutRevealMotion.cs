using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using MyCapture.App.Themes;

namespace MyCapture.App;

/// <summary>Alt feedback owns only inner render transforms and opacity; layout is fixed.</summary>
internal static class ShortcutRevealMotion
{
    internal const double CommandIconSize = 20;
    internal const double CommandIconScale = 0.8;
    internal const double PrimaryRestOffset = 7;
    internal const double CornerFaceSize = 32;
    internal const double CornerIconSize = 24;
    internal const double CornerIconScale = 0.75;
    internal const double CornerIconOffset = -5;
    internal const double CornerCaptionScale = 0.8;
    internal const double CaptionEntranceOffset = 4;
    internal const double CaptionExitOffset = 2;
    internal const int AEntranceMilliseconds = 120;
    internal const int AExitMilliseconds = 100;
    internal const int AOpacityInMilliseconds = 83;
    internal const int AOpacityOutMilliseconds = 67;
    internal const int BEntranceMilliseconds = 160;
    internal const int BExitMilliseconds = 120;
    internal const int BOpacityDelayMilliseconds = 20;
    internal const int BOpacityInMilliseconds = 100;
    internal const int BOpacityOutMilliseconds = 80;
    internal const int ReducedFadeMilliseconds = 83;
    private static readonly Point AControl1 = new(0, 0);
    private static readonly Point AControl2 = new(0, 1);
    private static readonly Point BControl1 = new(0.2, 0.15);
    private static readonly Point BControl2 = new(0.25, 1);
    private static readonly Point BOpacityControl1 = new(0.2, 0);
    private static readonly Point BOpacityControl2 = new(0, 1);

    // Initialized once by app startup; tests can select a profile on their own STA thread.
    internal static string Profile { get; } = ParseProfile(Environment.GetEnvironmentVariable("MYCAPTURE_ALT_MOTION"));
    internal static string ParseProfile(string? value) => value?.ToUpperInvariant() is "B" or "C" ? value.ToUpperInvariant() : "A";
    internal static void Initialize() => _ = Profile;
    [ThreadStatic] internal static bool SettleImmediatelyForTest;
    [ThreadStatic] internal static string? ProfileForTest;
    [ThreadStatic] private static List<WeakReference<Face>>? s_faces;

    private static readonly DependencyProperty FaceProperty = DependencyProperty.RegisterAttached(
        "Face", typeof(Face), typeof(ShortcutRevealMotion));
    private static readonly DependencyProperty ShiftProperty = DependencyProperty.RegisterAttached(
        "Shift", typeof(TranslateTransform), typeof(ShortcutRevealMotion));
    private static readonly DependencyProperty ScaleProperty = DependencyProperty.RegisterAttached(
        "Scale", typeof(ScaleTransform), typeof(ShortcutRevealMotion));

    internal static void Register(TextBlock hint)
    {
        var face = new Face(hint);
        hint.SetValue(FaceProperty, face);
        s_faces ??= [];
        s_faces.RemoveAll(reference => !reference.TryGetTarget(out _));
        s_faces.Add(new WeakReference<Face>(face));
        hint.Loaded += (_, _) => face.Update(animate: false);
        hint.Unloaded += (_, _) => face.Update(animate: false);
        face.Update(animate: false);
    }

    internal static void Configure(TextBlock hint, FrameworkElement primary, bool corner)
    {
        var face = (Face)hint.GetValue(FaceProperty);
        face.Corner = corner;
        face.PrimaryShift = Shift(primary);
        FrameworkElement? icon = primary is TextBlock ? null : primary is StackPanel row
            ? row.Children.OfType<FrameworkElement>().FirstOrDefault(child => child is not TextBlock)
            : primary;
        if (icon is not null)
        {
            PrepareIcon(icon, corner ? CornerIconSize : CommandIconSize, preserveStroke: corner);
            face.IconScale = Scale(icon);
        }
        if (corner)
        {
            hint.RenderTransformOrigin = new Point(1, 1);
            face.CaptionScale = Scale(hint);
        }
        face.Update(animate: false);
    }

    internal static void PreparePrimary(FrameworkElement primary)
    {
        PrepareCommandIcon(primary);
        primary.VerticalAlignment = VerticalAlignment.Center;
        Shift(primary).Y = PrimaryRestOffset;
    }

    internal static void PrepareCommandIcon(object? content)
    {
        if (content is Grid { Name: "CommandButtonFace", Children.Count: > 0 } face) content = face.Children[0];
        if (content is StackPanel row) content = row.Children.OfType<FrameworkElement>().FirstOrDefault(child => child is not TextBlock);
        if (content is SymbolIcon or Viewbox or Shape)
            PrepareIcon((FrameworkElement)content, CommandIconSize, preserveStroke: false);
    }

    internal static void ResetPrimary(FrameworkElement primary)
    {
        Animate(Shift(primary), TranslateTransform.YProperty, PrimaryRestOffset, 0);
        FrameworkElement? icon = primary is StackPanel row
            ? row.Children.OfType<FrameworkElement>().FirstOrDefault(child => child is not TextBlock)
            : primary is TextBlock ? null : primary;
        if (icon?.GetValue(ScaleProperty) is ScaleTransform scale)
        {
            Animate(scale, ScaleTransform.ScaleXProperty, 1, 0);
            Animate(scale, ScaleTransform.ScaleYProperty, 1, 0);
        }
    }

    internal static void Update(TextBlock hint, bool animate) => ((Face?)hint.GetValue(FaceProperty))?.Update(animate);

    internal static void SettleAll()
    {
        if (s_faces is null) return;
        s_faces.RemoveAll(reference => !reference.TryGetTarget(out _));
        foreach (var reference in s_faces)
            if (reference.TryGetTarget(out Face? face)) face.Update(animate: false);
    }

    private static TransformGroup Transforms(FrameworkElement element)
    {
        if (element.RenderTransform is TransformGroup { IsFrozen: false } group) return group;
        group = new TransformGroup();
        if (element.RenderTransform is { } existing && !existing.Value.IsIdentity) group.Children.Add(existing);
        element.RenderTransform = group;
        return group;
    }

    private static TranslateTransform Shift(FrameworkElement element)
    {
        if (element.GetValue(ShiftProperty) is TranslateTransform existing) return existing;
        var shift = new TranslateTransform();
        Transforms(element).Children.Add(shift);
        element.SetValue(ShiftProperty, shift);
        return shift;
    }

    private static ScaleTransform Scale(FrameworkElement element)
    {
        if (element.GetValue(ScaleProperty) is ScaleTransform existing) return existing;
        var scale = new ScaleTransform();
        // Scale before translation, otherwise the corner's offset also changes with scale.
        Transforms(element).Children.Insert(0, scale);
        if (element is not TextBlock) element.RenderTransformOrigin = new Point(0.5, 0.5);
        element.SetValue(ScaleProperty, scale);
        return scale;
    }

    private static void PrepareIcon(FrameworkElement icon, double size, bool preserveStroke)
    {
        if (icon is SymbolIcon symbol)
        {
            if (preserveStroke) symbol.StrokeThickness *= symbol.Size / size;
            symbol.Size = size;
        }
        else
        {
            if (preserveStroke && icon is Viewbox { Child: Canvas canvas } viewbox)
                foreach (Shape path in canvas.Children.OfType<Shape>()) path.StrokeThickness *= viewbox.Width / size;
            icon.Width = icon.Height = size;
        }
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        icon.VerticalAlignment = VerticalAlignment.Center;
    }

    private sealed class Face(TextBlock caption)
    {
        internal TranslateTransform? PrimaryShift;
        internal ScaleTransform? IconScale;
        internal ScaleTransform? CaptionScale;
        internal bool Corner;
        private readonly TranslateTransform _captionShift = Shift(caption);

        internal void Update(bool animate)
        {
            bool revealed = ShortcutPresentation.IsRevealed(caption);
            animate &= caption.IsLoaded && !SettleImmediatelyForTest && ShortcutPresentation.ReadEnabled?.Invoke() != true;
            // Faces inside collapsed groups (hidden properties, the inactive command mode) settle directly.
            animate &= caption.IsVisible;
            string profile = ProfileForTest ?? Profile;
            bool reduced = profile == "C" || !FluidMotion.AnimationsEnabled;
            int move = !animate || reduced ? 0 : profile == "B"
                ? revealed ? BEntranceMilliseconds : BExitMilliseconds
                : revealed ? AEntranceMilliseconds : AExitMilliseconds;
            Point c1 = profile == "B" ? BControl1 : AControl1;
            Point c2 = profile == "B" ? BControl2 : AControl2;
            if (PrimaryShift is not null)
            {
                Animate(PrimaryShift, TranslateTransform.XProperty, Corner && revealed ? CornerIconOffset : 0, move, c1, c2);
                Animate(PrimaryShift, TranslateTransform.YProperty, Corner ? revealed ? CornerIconOffset : 0 : revealed ? 0 : PrimaryRestOffset, move, c1, c2);
            }
            if (IconScale is not null)
            {
                double scale = revealed ? Corner ? CornerIconScale : CommandIconScale : 1;
                Animate(IconScale, ScaleTransform.ScaleXProperty, scale, move, c1, c2);
                Animate(IconScale, ScaleTransform.ScaleYProperty, scale, move, c1, c2);
            }
            if (CaptionScale is not null)
            {
                Animate(CaptionScale, ScaleTransform.ScaleXProperty, revealed ? 1 : CornerCaptionScale, move, c1, c2);
                Animate(CaptionScale, ScaleTransform.ScaleYProperty, revealed ? 1 : CornerCaptionScale, move, c1, c2);
            }
            // A fresh entrance starts at +4. A reversal retains its currently rendered value.
            if (animate && !reduced && !Corner && revealed && caption.Opacity == 0
                && (!_captionShift.HasAnimatedProperties || _captionShift.Y == CaptionExitOffset))
                Animate(_captionShift, TranslateTransform.YProperty, CaptionEntranceOffset, 0);
            double captionOffset = Corner || reduced || revealed ? 0 : animate ? CaptionExitOffset : CaptionEntranceOffset;
            Animate(_captionShift, TranslateTransform.YProperty, captionOffset, move, c1, c2);
            int fade = !animate ? 0 : reduced ? ReducedFadeMilliseconds : profile == "B"
                ? revealed ? BOpacityInMilliseconds : BOpacityOutMilliseconds
                : revealed ? AOpacityInMilliseconds : AOpacityOutMilliseconds;
            bool splineOpacity = profile == "B" && revealed && !reduced;
            Animate(caption, UIElement.OpacityProperty, revealed ? 1 : 0, fade,
                splineOpacity ? BOpacityControl1 : null, splineOpacity ? BOpacityControl2 : null,
                animate && splineOpacity ? BOpacityDelayMilliseconds : 0);
        }
    }

    internal static void Animate(IAnimatable target, DependencyProperty property, double to, int milliseconds,
        Point? control1 = null, Point? control2 = null, int delay = 0)
    {
        var owner = (DependencyObject)target;
        double from = (double)owner.GetValue(property);
        if (milliseconds == 0 || from.Equals(to))
        {
            target.BeginAnimation(property, null);
            owner.SetValue(property, to);
            return;
        }
        // Preserve the sampled value before the new clock's first render tick and during B's
        // delay. Replacing the existing clock directly also preserves WPF's handoff snapshot.
        owner.SetValue(property, from);
        var animation = new DoubleAnimationUsingKeyFrames
        {
            BeginTime = TimeSpan.FromMilliseconds(delay),
            FillBehavior = FillBehavior.HoldEnd,
        };
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        var end = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(milliseconds));
        animation.KeyFrames.Add(control1 is { } first && control2 is { } second
            ? new SplineDoubleKeyFrame(to, end, new KeySpline(first, second))
            : new LinearDoubleKeyFrame(to, end));
        target.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
    }
}
