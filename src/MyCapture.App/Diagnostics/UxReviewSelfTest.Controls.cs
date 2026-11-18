using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using MyCapture.App.Pinning;
using MyCapture.App.Themes;
using MyCapture.Core.Settings;
using MyCapture.Platform.Shell;

namespace MyCapture.App.Diagnostics;

internal static partial class UxReviewSelfTest
{
    private static Window CreateControlLanguageFixture()
    {
        var window = new Window { Title = "Control comparison", Width = 440, Height = 470,
            WindowStartupLocation = WindowStartupLocation.Manual };
        var stack = new StackPanel { Margin = new Thickness(24), HorizontalAlignment = HorizontalAlignment.Left };
        stack.Children.Add(new TextBlock { Text = "입력 · Input", Margin = new Thickness(0, 0, 0, 8) });
        stack.Children.Add(new TextBox { Name = "ReviewRestInput", Text = "Sample", FontSize = 14, Width = 160 });
        stack.Children.Add(new TextBox { Name = "ReviewFocusedInput", Text = "Sample", FontSize = 14, Width = 160, Margin = new Thickness(0, 8, 0, 8) });
        var combo = new ComboBox { Name = "ReviewCombo", FontSize = 14, Width = 160 };
        combo.Items.Add("Sample"); combo.SelectedIndex = 0;
        stack.Children.Add(combo);
        var checks = new StackPanel { Margin = new Thickness(0, 12, 0, 12) };
        checks.Children.Add(new CheckBox { Name = "ReviewUnchecked", Content = "Sample", FontSize = 14 });
        checks.Children.Add(new CheckBox { Name = "ReviewChecked", Content = "Sample", FontSize = 14, IsChecked = true });
        checks.Children.Add(new CheckBox { Content = "일부 선택 · Mixed", IsThreeState = true, IsChecked = null, FontSize = 14 });
        checks.Children.Add(new RadioButton { Content = "선택 · Selected", IsChecked = true, FontSize = 14 });
        stack.Children.Add(checks);
        stack.Children.Add(new ScrollBar { Name = "ReviewScroll", Width = 16, Height = 120, Maximum = 100, ViewportSize = 10, Value = 20,
            HorizontalAlignment = HorizontalAlignment.Left });
        window.Content = stack;
        return window;
    }

    private static void WriteControlMeasurements(Window window, string path)
    {
        var measured = Descendants(window).OfType<FrameworkElement>()
            .Where(element => element.Name is "ReviewRestInput" or "ReviewCombo" or "ReviewUnchecked" or "ReviewScroll")
            .Select(element => new { element.Name, Kind = element.GetType().Name,
                DesiredWidth = element.DesiredSize.Width, DesiredHeight = element.DesiredSize.Height,
                ActualWidth = element.ActualWidth, ActualHeight = element.ActualHeight }).ToArray();
        File.WriteAllText(path, JsonSerializer.Serialize(measured, new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed class ReviewHotkeyRegistrar : IHotkeyRegistrar
    {
        public bool TryRegister(int id, Hotkey hotkey, out int errorCode, out string errorMessage)
        { errorCode = 0; errorMessage = string.Empty; return true; }
        public void Unregister(int id) { }
    }

    private sealed class ReviewPinInput : IPinDragInput
    {
        public ModifierKeys Modifiers => ModifierKeys.None;
        public (int X, int Y) CursorPosition => (40, 40);
        public Point LocalAnchor(MouseButtonEventArgs args, PinWindow owner) => new(0, 0);
        public bool LeftButtonPressed(MouseEventArgs args) => false;
        public bool TryCapture(PinWindow owner) => false;
        public bool HasCapture(PinWindow owner) => false;
        public void Release(PinWindow owner) { }
    }
}
