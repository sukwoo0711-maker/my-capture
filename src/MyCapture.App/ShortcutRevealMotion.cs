using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using MyCapture.App.Themes;

namespace MyCapture.App;

/// <summary>
/// Alt feedback commits inner render transforms and opacity together. An icon-only command's width
/// follows it, within the space its command bar already holds for the revealed caption.
/// </summary>
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
    // Keep existing diagnostic profile selections readable. Alt feedback now commits one
    // complete face immediately for every profile, including reduced-motion preferences.
    internal static string Profile { get; } = ParseProfile(Environment.GetEnvironmentVariable("MYCAPTURE_ALT_MOTION"));
    internal static string ParseProfile(string? value) => value?.ToUpperInvariant() is "B" or "C" ? value.ToUpperInvariant() : "A";
    internal static void Initialize() => _ = Profile;
    // Existing geometry test scopes use this switch; production feedback is also synchronous.
    [ThreadStatic] internal static bool SettleImmediatelyForTest;
    [ThreadStatic] internal static string? ProfileForTest;
    [ThreadStatic] private static List<WeakReference<Face>>? s_faces;
    [ThreadStatic] private static int s_registrationsSincePrune;

    private static readonly DependencyProperty FaceProperty = DependencyProperty.RegisterAttached(
        "Face", typeof(Face), typeof(ShortcutRevealMotion));
    private static readonly DependencyProperty ShiftProperty = DependencyProperty.RegisterAttached(
        "Shift", typeof(TranslateTransform), typeof(ShortcutRevealMotion));
    private static readonly DependencyProperty ScaleProperty = DependencyProperty.RegisterAttached(
        "Scale", typeof(ScaleTransform), typeof(ShortcutRevealMotion));
    // Follows the caption's reveal; an icon-only command widens with it (CommandButtonPresentation).
    private static readonly DependencyProperty WidthProgressProperty = DependencyProperty.RegisterAttached(
        "WidthProgress", typeof(double), typeof(ShortcutRevealMotion),
        new PropertyMetadata(0d, (target, _) => CommandButtonPresentation.FitCaption((TextBlock)target)));

    internal static double WidthProgress(TextBlock caption) => (double)caption.GetValue(WidthProgressProperty);

    internal static void Register(TextBlock hint)
    {
        var face = new Face(hint);
        hint.SetValue(FaceProperty, face);
        s_faces ??= [];
        if (++s_registrationsSincePrune >= 64)
        {
            s_faces.RemoveAll(reference => !reference.TryGetTarget(out _));
            s_registrationsSincePrune = 0;
        }
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
        Settle(Shift(primary), TranslateTransform.YProperty, PrimaryRestOffset);
        FrameworkElement? icon = primary is StackPanel row
            ? row.Children.OfType<FrameworkElement>().FirstOrDefault(child => child is not TextBlock)
            : primary is TextBlock ? null : primary;
        if (icon?.GetValue(ScaleProperty) is ScaleTransform scale)
        {
            Settle(scale, ScaleTransform.ScaleXProperty, 1);
            Settle(scale, ScaleTransform.ScaleYProperty, 1);
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
            // A held key is a current state, not a transition to wait through. Commit the
            // icon, available caption width and opacity together before the next render.
            // This also prevents a short tap or reversal from leaving a partly open face.
            if (PrimaryShift is not null)
            {
                Settle(PrimaryShift, TranslateTransform.XProperty, Corner && revealed ? CornerIconOffset : 0);
                Settle(PrimaryShift, TranslateTransform.YProperty, Corner ? revealed ? CornerIconOffset : 0 : revealed ? 0 : PrimaryRestOffset);
            }
            if (IconScale is not null)
            {
                double scale = revealed ? Corner ? CornerIconScale : CommandIconScale : 1;
                Settle(IconScale, ScaleTransform.ScaleXProperty, scale);
                Settle(IconScale, ScaleTransform.ScaleYProperty, scale);
            }
            if (CaptionScale is not null)
            {
                Settle(CaptionScale, ScaleTransform.ScaleXProperty, revealed ? 1 : CornerCaptionScale);
                Settle(CaptionScale, ScaleTransform.ScaleYProperty, revealed ? 1 : CornerCaptionScale);
            }
            if (!Corner) Settle(caption, WidthProgressProperty, revealed ? 1 : 0);
            Settle(_captionShift, TranslateTransform.YProperty, Corner || revealed ? 0 : CaptionEntranceOffset);
            Settle(caption, UIElement.OpacityProperty, revealed ? 1 : 0);
        }
    }

    private static void Settle(IAnimatable target, DependencyProperty property, double value)
    {
        var owner = (DependencyObject)target;
        if (((double)owner.GetValue(property)).Equals(value) && !target.HasAnimatedProperties) return;
        target.BeginAnimation(property, null);
        owner.SetValue(property, value);
    }
}
