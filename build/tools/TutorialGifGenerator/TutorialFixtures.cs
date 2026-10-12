using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App;
using MyCapture.App.Themes;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using static TutorialDrawing;

/// <summary>Offscreen visual fixtures. No events that execute product transactions are raised.</summary>
internal static class TutorialFixtures
{
    internal const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Set(object target, string field, object value) => target.GetType().GetField(field, Private)!.SetValue(target, value);
    internal static T Field<T>(object target, string field) => (T)target.GetType().GetField(field, Private)!.GetValue(target)!;
    internal static void Invoke(object target, string method, params object[] args) => target.GetType().GetMethod(method, Private)!.Invoke(target, args);
    internal static BitmapSource Snapshot(FrameworkElement element, double width, double? height = null)
    {
        // Measuring content directly never attaches a PresentationSource or raises Window.Loaded.
        TextElement.SetForeground(element, Brush("Text.Primary"));
        TextElement.SetFontFamily(element, (FontFamily)Application.Current.Resources["Font.Ui"]);
        element.Measure(new Size(width, height ?? double.PositiveInfinity));
        double h = height ?? Math.Ceiling(element.DesiredSize.Height);
        element.Arrange(new Rect(0, 0, width, h)); element.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width), Math.Max(1, (int)Math.Ceiling(h)), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element); bitmap.Freeze(); return bitmap;
    }
    internal static BitmapSource WindowContent(Window window, double width, double? height = null)
    {
        var bitmap = Snapshot((FrameworkElement)window.Content, width, height);
        window.Content = null; window.Close(); return bitmap;
    }
    internal static string L(bool ko, string korean, string english) => ko ? korean : english;
    internal static Button Button(string text, bool primary = false)
    {
        var button = new Button { Content = text, MinHeight = 32, Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(3) };
        button.SetResourceReference(FrameworkElement.StyleProperty, primary ? "Button.Primary" : "Button.Secondary"); return button;
    }
    internal static BitmapSource Desktop(bool ko) => DrawScene(dc =>
    {
        dc.DrawRectangle(Brush("Surface.Base"), null, new Rect(0, 0, Width, SceneHeight));
        dc.DrawRoundedRectangle(Brush("Surface.Raised"), new Pen(Brush("Border.Control"), 1), new Rect(42, 72, 552, 216), 8, 8);
        Text(dc, L(ko, "개발 기록", "Development notes"), 62, 85, 17, Brush("Text.Primary"));
        dc.DrawLine(new Pen(Brush("Border.Subtle"), 1), new Point(42, 118), new Point(594, 118));
        Text(dc, "npm run build", 65, 140, 22, Brush("Text.Primary"), true);
        Text(dc, "Build completed", 65, 178, 22, new SolidColorBrush(Color.FromRgb(154, 225, 177)), true);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(124, 108, 255)), null, new Rect(450, 237, 112, 26), 5, 5);
        Text(dc, "#7C6CFF", 462, 241, 15, Brushes.White, true);
    });
    internal static BitmapSource OnDesktop(BitmapSource desktop, BitmapSource card, Rect target, string title = "") => DrawScene(dc =>
    {
        dc.DrawImage(desktop, new Rect(0, 0, Width, SceneHeight));
        if (title.Length > 0) Text(dc, title, target.X, Math.Max(0, target.Y - 25), 17, Brush("Text.Primary"));
        dc.DrawImage(card, target);
    });
    internal static BitmapSource Tray(bool ko, string action, bool more = false)
    {
        Action noop = () => { };
        var entries = TrayMenuLayout.Build(new TrayMenuCommands(noop, noop, noop, noop, noop, noop, noop, noop, noop, noop, noop, noop, noop), new HotkeySettings(), true, false);
        if (more) entries = entries[2].Children!;
        var panel = new StackPanel();
        foreach (var entry in entries.Where(entry => !entry.IsSeparator))
        {
            var button = Button(entry.Label, entry.Label == action);
            button.HorizontalContentAlignment = HorizontalAlignment.Left; panel.Children.Add(button);
        }
        var surface = new Border { Child = panel, Background = Brush("Surface.Floating"), BorderBrush = Brush("Border.Subtle"), BorderThickness = new Thickness(1), Padding = new Thickness(5), CornerRadius = new CornerRadius(8) };
        return Snapshot(surface, 275);
    }
    internal static TutorialStoryboard Story(PointD target, PointD end, PointD follow) => new TutorialStoryboard()
        .Hold("open", 100, new PointD(550, 278))
        .Motion("approach", 35, new PointD(550, 278), target)
        .Motion("click", 8, target, target, true)
        .Hold("input", 100, target)
        .Motion("action", 50, target, end, true)
        .Hold("result", 100, end)
        .Motion("follow", 35, end, follow)
        .Motion("finish", 8, follow, new PointD(follow.X + 30, follow.Y))
        .Hold("done", 276, new PointD(follow.X + 30, follow.Y));
    internal static IReadOnlyList<int> KeyFrames { get; } = new[] { 0, 44, 94, 95, 139 };
    internal static bool Result(TutorialFramePlan frame) => frame.Segment is "result" or "follow" or "finish" or "done";
    internal static bool Input(TutorialFramePlan frame) => frame.Segment is "click" or "input" or "action" || Result(frame);
    internal static BitmapSource Frame(BitmapSource scene, TutorialFramePlan frame, string caption, string key = "") => Compose(scene, caption, key, frame.Cursor,
        frame.Cursor, frame.Segment == "click" ? frame.Progress : 0);
}
