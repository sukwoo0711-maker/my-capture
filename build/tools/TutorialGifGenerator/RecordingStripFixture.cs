using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Themes;
using MyCapture.Core.Primitives;
using static TutorialDrawing;
using static TutorialFixtures;

/// <summary>Pure child controls matching RecordingControlWindow.BuildControlStrip; no Window/recorder constructor.</summary>
internal sealed class RecordingStripFixture
{
    internal BitmapSource Bitmap { get; }
    internal PointD PrimaryCenter { get; }
    internal PointD PauseCenter { get; }
    internal RecordingStripFixture(string state, string elapsed)
    {
        bool ready = state == "ready", paused = state == "paused";
        var panel = new Grid { Margin = new Thickness(10, 8, 10, 8) };
        foreach (GridLength width in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto, GridLength.Auto }) panel.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        var primary = new Button { MinWidth = 108, VerticalAlignment = VerticalAlignment.Center };
        primary.SetResourceReference(FrameworkElement.StyleProperty, ready ? "Button.Primary" : "Button.Danger");
        primary.Content = Face(ready ? "M10,4 A6,6 0 1 1 9.99,4" : "M4.5,4.5 L15.5,4.5 L15.5,15.5 L4.5,15.5 Z", UiText.Get(ready ? "Text_255C28FD0AC2" : "Onboard_RecordStop"));
        panel.Children.Add(primary);
        var pause = new Button { MinWidth = 84, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Visibility = ready ? Visibility.Collapsed : Visibility.Visible };
        pause.SetResourceReference(FrameworkElement.StyleProperty, "Button.Secondary");
        pause.Content = Face(paused ? "M6,3.75 L16,10 L6,16.25 Z" : "M5,4 L8.5,4 L8.5,16 L5,16 Z M11.5,4 L15,4 L15,16 L11.5,16 Z", UiText.Get(paused ? "RecordingPause_Resume" : "RecordingPause_Pause"));
        Grid.SetColumn(pause, 1); panel.Children.Add(pause);
        var status = new TextBlock { Text = ready ? "" : UiText.Get(paused ? "RecordingPause_Status" : "Text_38B12BA58C48"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 12, 0), Foreground = Brush(paused ? "Text.Primary" : "State.Danger"), FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(status, 2); panel.Children.Add(status);
        var timer = new TextBlock { Text = elapsed, FontSize = 15, FontFamily = (FontFamily)Application.Current.Resources["Font.Mono"], VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        Grid.SetColumn(timer, 3); panel.Children.Add(timer);
        var more = new Button { Content = "···", MinWidth = 40, Padding = new Thickness(6), Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
        more.SetResourceReference(FrameworkElement.StyleProperty, "Button.Ghost"); Grid.SetColumn(more, 4); panel.Children.Add(more);
        var cancel = new Button { Content = UiText.Get("Text_BE876433993A"), MinWidth = 64, VerticalAlignment = VerticalAlignment.Center, Visibility = ready ? Visibility.Visible : Visibility.Collapsed };
        cancel.SetResourceReference(FrameworkElement.StyleProperty, "Button.Ghost"); Grid.SetColumn(cancel, 5); panel.Children.Add(cancel);
        var surface = new Border { Background = Brush("Surface.Floating"), BorderBrush = Brush("Border.Subtle"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Child = panel };
        Bitmap = Snapshot(surface, 560, 56);
        PrimaryCenter = Center(primary, surface);
        PauseCenter = ready ? default : Center(pause, surface);
    }
    private static PointD Center(Button button, FrameworkElement root)
    {
        Point center = button.TranslatePoint(new Point(button.ActualWidth / 2, button.ActualHeight / 2), root);
        if (button.ActualWidth <= 0 || button.ActualHeight <= 0) throw new InvalidOperationException("Recording button was not laid out.");
        return new PointD(center.X + 40, center.Y + 242);
    }
    private static StackPanel Face(string geometry, string label)
    {
        var face = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        face.Children.Add(new SymbolIcon { Size = 20, StrokeThickness = 0, Data = Geometry.Parse(geometry), Fill = Brush("Text.Primary") });
        face.Children.Add(new TextBlock { Text = label, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center }); return face;
    }
}
