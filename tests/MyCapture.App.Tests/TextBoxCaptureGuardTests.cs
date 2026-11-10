using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MyCapture.App.Ocr;
using MyCapture.Ocr;
using Xunit;
using static MyCapture.App.Tests.EditorKeysTests;

namespace MyCapture.App.Tests;

/// <summary>Queued releases keep text-editor capture local to one completed gesture.</summary>
public sealed class TextBoxCaptureGuardTests
{
    private const string OcrText = "한글 ABC 결과\n두 번째 줄 XYZ";

    [Fact]
    public void OcrResult_PastEndReleaseLetsTheNextClickReachTheHeader() => Ocr410Fixture.RunSta(() =>
    {
        OcrResultWindow window = CreateOcrWindow();
        try
        {
            window.ShowResult(OcrResult.Success(OcrText, "ko-KR", [], TimeSpan.Zero), "Synthetic");
            window.UpdateLayout();
            TextBox box = Field<TextBox>(window, "_textBox");
            using var mouse = new QueuedMouse(window, box);
            Assert.NotEmpty(box.Text);
            Assert.Equal(box.Text.Length, box.SelectionLength);
            // Eight pixels past the final character, centered on the last line, in PART_ContentHost.
            Point point = PastEndPoint(box);
            mouse.Click(box, point);
            Assert.Equal(0, box.SelectionLength);
            Assert.False(mouse.ViewValidAtRelease);
            AssertReleasedAndFocused(mouse, box);

            // The header's painted margin has no action and is outside all text editors.
            var root = Assert.IsType<Grid>(window.Content);
            var header = Assert.IsType<Border>(root.Children[0]);
            AssertNextClickReaches(mouse, window, box, header, new Point(12, 12));
        }
        finally { window.CloseForExit(); }
    });

    [Fact]
    public void OcrResult_PressOnSelectionKeepsWpfDeferredCaretPlacement() => Ocr410Fixture.RunSta(() =>
    {
        OcrResultWindow window = CreateOcrWindow();
        try
        {
            window.ShowResult(OcrResult.Success(OcrText, "ko-KR", [], TimeSpan.Zero), "Synthetic");
            window.UpdateLayout();
            TextBox box = Field<TextBox>(window, "_textBox");
            using var mouse = new QueuedMouse(window, box);
            Assert.NotEmpty(box.Text);
            Assert.Equal(box.Text.Length, box.SelectionLength);
            // Two pixels into character 2, centered on its line, inside the selected text.
            Rect character = box.GetRectFromCharacterIndex(2);
            Assert.False(character.IsEmpty);
            var point = new Point(character.X + 2, character.Y + character.Height / 2);
            AssertPointInTextArea(box, point);
            mouse.Click(box, point);
            Assert.Equal(0, box.SelectionLength);
            Assert.InRange(box.CaretIndex, 2, 3);
            AssertReleasedAndFocused(mouse, box);
        }
        finally { window.CloseForExit(); }
    });

    [Fact]
    public void PasswordBox_PastEndReleaseLetsTheNextClickReachTheBorder() => Ocr410Fixture.RunSta(() =>
    {
        var box = new PasswordBox
        {
            Password = "test-only-value", Width = 280, Height = 40, Padding = new Thickness(8),
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        var target = new Border { Width = 280, Height = 48, Background = Brushes.SlateGray };
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(box);
        panel.Children.Add(target);
        var window = new Window
        {
            Content = panel, Width = 360, Height = 220,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -4000, Top = -4000,
            ShowActivated = false, ShowInTaskbar = false,
        };
        try
        {
            window.Show();
            window.UpdateLayout();
            Assert.True(box.Focus());
            box.SelectAll();
            Assert.NotEmpty(box.Password);
            using var mouse = new QueuedMouse(window, box);
            mouse.Drain();
            var host = Assert.IsType<ScrollViewer>(box.Template.FindName("PART_ContentHost", box));
            var dots = new FormattedText(new string(box.PasswordChar, box.Password.Length),
                CultureInfo.InvariantCulture, box.FlowDirection,
                new Typeface(box.FontFamily, box.FontStyle, box.FontWeight, box.FontStretch),
                box.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(box).PixelsPerDip);
            // Eight pixels past the measured password dots, vertically centered in PART_ContentHost.
            Point point = host.TranslatePoint(new Point(dots.WidthIncludingTrailingWhitespace + 8,
                host.ActualHeight / 2), box);
            AssertPointInTextArea(box, point);
            mouse.Click(box, point);
            Assert.False(mouse.ViewValidAtRelease);
            AssertReleasedAndFocused(mouse, box);
            AssertNextClickReaches(mouse, window, box, target);
        }
        finally { window.Close(); }
    });

    private static OcrResultWindow CreateOcrWindow()
    {
        var window = new OcrResultWindow
        {
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -4000, Top = -4000,
            ShowActivated = false, ShowInTaskbar = false,
        };
        Ocr410Fixture.LoadTheme(window);
        return window;
    }

    internal static Point PastEndPoint(TextBox box)
    {
        Rect end = box.GetRectFromCharacterIndex(box.Text.Length);
        Assert.False(end.IsEmpty);
        var point = new Point(end.Right + 8, end.Y + end.Height / 2);
        AssertPointInTextArea(box, point);
        return point;
    }

    internal static void AssertPointInTextArea(Control box, Point point)
    {
        var host = Assert.IsType<ScrollViewer>(box.Template.FindName("PART_ContentHost", box));
        Point inside = box.TranslatePoint(point, host);
        Assert.True(inside.X > 0 && inside.X < host.ActualWidth
            && inside.Y > 0 && inside.Y < host.ActualHeight, $"Press {point} must be inside PART_ContentHost.");
        var hit = Assert.IsAssignableFrom<DependencyObject>(box.InputHitTest(point));
        Assert.True(ReferenceEquals(hit, host) || host.IsAncestorOf(hit));
    }

    internal static void AssertReleasedAndFocused(QueuedMouse mouse, Control box)
    {
        Assert.Null(Mouse.Captured);
        Assert.False(mouse.NativeCaptureAfterGesture);
        Assert.Same(box, Keyboard.FocusedElement);
    }

    internal static void AssertNextClickReaches(QueuedMouse mouse, Window window, Control box,
        FrameworkElement target, Point? at = null)
    {
        // Check the box's completed gesture before the helper takes native capture for the
        // next click; a passive Border does not take or release capture itself.
        Assert.False(mouse.NativeCaptureAfterGesture);
        Point point = at ?? new Point(target.ActualWidth / 2, target.ActualHeight / 2);
        var hit = Assert.IsAssignableFrom<DependencyObject>(window.InputHitTest(target.TranslatePoint(point, window)));
        Assert.True(ReferenceEquals(hit, target) || target.IsAncestorOf(hit));
        int presses = 0;
        object? originalSource = null;
        MouseButtonEventHandler handler = (_, e) => { presses++; originalSource = e.OriginalSource; };
        target.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, handler, handledEventsToo: true);
        try
        {
            mouse.Click(target, point);
            Assert.Equal(1, presses);
            var source = Assert.IsAssignableFrom<DependencyObject>(originalSource);
            Assert.True(ReferenceEquals(source, target) || target.IsAncestorOf(source));
            Assert.False(ReferenceEquals(source, box) || box.IsAncestorOf(source));
            Assert.Null(Mouse.Captured);
        }
        finally { target.RemoveHandler(UIElement.PreviewMouseLeftButtonDownEvent, handler); }
    }
}
