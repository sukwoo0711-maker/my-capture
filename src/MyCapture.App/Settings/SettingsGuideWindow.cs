using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.App.Themes;
using MyCapture.Platform.Display;

namespace MyCapture.App.Settings;

/// <summary>Read-only local illustrations. No settings, capture, clipboard or OCR services are invoked.</summary>
internal static class SettingsGuide
{
    internal static Action<SettingsGuideWindow>? BeforeShowForTest { get; set; }
    internal static SettingsGuideWindow Show(string guideId, Window owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var window = new SettingsGuideWindow(SettingsGuideCatalog.Get(guideId));
        window.AttachOwner(owner);
        BeforeShowForTest?.Invoke(window);
        window.Show();
        return window;
    }
}

internal sealed class SettingsGuideWindow : Window
{
    private readonly string _language = UiText.Culture.TwoLetterISOLanguageName == "ko" ? "ko" : "en";
    private readonly string _assetDirectory;
    private readonly Func<bool> _reducedMotion;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(850) };
    private readonly Image _before = new() { Stretch = Stretch.Uniform };
    private readonly Image _after = new() { Stretch = Stretch.Uniform };
    private readonly TextBlock _beforeValue = Label();
    private readonly TextBlock _afterValue = Label();
    private readonly TextBlock _description = Label();
    private readonly TextBlock _beforeMissing = Label();
    private readonly TextBlock _afterMissing = Label();
    private IInputElement? _returnFocus;
    private Window? _owner;
    private int _frame;
    private bool _closed;

    internal SettingsGuideDefinition Guide { get; }
    internal ComboBox ComparisonSelector { get; } = new() { MinHeight = 34, Margin = new Thickness(0, 0, 0, 12), DisplayMemberPath = "Title" };
    internal Button CloseButton { get; } = new() { MinWidth = 96, IsCancel = true };
    internal Button ReplayButton { get; } = new() { MinWidth = 96, Margin = new Thickness(0, 0, 8, 0) };
    internal bool IsPlaying => _timer.IsEnabled;
    internal SettingsGuideComparison Comparison => (SettingsGuideComparison)ComparisonSelector.SelectedItem;
    internal int Frame => _frame;
    internal bool HasBothImages => _before.Source is not null && _after.Source is not null;
    internal ScrollViewer Scroller { get; }

    internal SettingsGuideWindow(SettingsGuideDefinition guide, string? assetDirectory = null, Func<bool>? reducedMotion = null)
    {
        Guide = guide;
        _assetDirectory = assetDirectory ?? SettingsGuideCatalog.AssetDirectory;
        _reducedMotion = reducedMotion ?? (() => !FluidMotion.AnimationsEnabled);
        Title = guide.Title;
        Width = 1040; Height = 650; MinWidth = 540; MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        StandardWindowTheme.Apply(this);
        SetResourceReference(BackgroundProperty, "Surface.Base");
        SetResourceReference(ForegroundProperty, "Text.Primary");
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.Cycle);
        var root = new DockPanel { Margin = new Thickness(24, 18, 24, 18) };
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        ReplayButton.Content = UiText.Get("SettingsGuide.Replay");
        CloseButton.Content = UiText.Get("Dialog.Close");
        foreach (var button in new[] { ReplayButton, CloseButton }) button.SetResourceReference(StyleProperty, "Button.Secondary");
        ReplayButton.Click += (_, _) => Replay();
        CloseButton.Click += (_, _) => Close();
        footer.Children.Add(ReplayButton); footer.Children.Add(CloseButton);
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);

        var body = new StackPanel();
        var heading = Label(); heading.Text = guide.Title; heading.FontSize = 22; heading.FontWeight = FontWeights.SemiBold;
        heading.Margin = new Thickness(0, 0, 0, 16); body.Children.Add(heading);
        AutomationProperties.SetName(ComparisonSelector, UiText.Get("SettingsGuide.Comparison"));
        ComparisonSelector.ItemsSource = guide.Comparisons;
        ComparisonSelector.Visibility = guide.Comparisons.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        body.Children.Add(ComparisonSelector);
        var panels = new Grid();
        panels.ColumnDefinitions.Add(new ColumnDefinition()); panels.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) }); panels.ColumnDefinitions.Add(new ColumnDefinition());
        Border before = Panel(UiText.Get("SettingsGuide.Before"), _beforeValue, _before, _beforeMissing);
        Border after = Panel(UiText.Get("SettingsGuide.After"), _afterValue, _after, _afterMissing);
        Grid.SetColumn(after, 2); panels.Children.Add(before); panels.Children.Add(after); body.Children.Add(panels);
        _description.Margin = new Thickness(0, 14, 0, 0); _description.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary"); body.Children.Add(_description);
        Scroller = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        root.Children.Add(Scroller);
        var surface = new Border { Child = root };
        surface.SetResourceReference(Border.BackgroundProperty, "Surface.Base");
        Content = surface;
        _timer.Tick += OnTick;
        ComparisonSelector.SelectionChanged += (_, _) => SelectComparison();
        ComparisonSelector.SelectedIndex = 0;
        Loaded += (_, _) => { SystemParameters.StaticPropertyChanged += OnMotionChanged; CloseButton.Focus(); };
        Deactivated += (_, _) => StopAtEnd();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
        Closed += (_, _) =>
        {
            _closed = true; _timer.Stop(); _timer.Tick -= OnTick;
            SystemParameters.StaticPropertyChanged -= OnMotionChanged;
            if (_owner is not null)
            {
                _owner.Closed -= OwnerClosed;
                if (_owner.IsVisible && _returnFocus is DependencyObject target && _owner.IsAncestorOf(target))
                {
                    FocusManager.SetFocusedElement(_owner, _returnFocus);
                    if (_owner.IsActive) Keyboard.Focus(_returnFocus);
                }
            }
        };
    }

    internal void AttachOwner(Window owner)
    {
        _owner = owner;
        _returnFocus = FocusManager.GetFocusedElement(owner) ?? Keyboard.FocusedElement;
        Owner = owner;
        owner.Closed += OwnerClosed;
        // Fit to the owner's display in DIPs; avoid an oversized child on small work areas.
        var area = SystemParameters.WorkArea;
        IntPtr handle = new WindowInteropHelper(owner).Handle;
        if (handle != IntPtr.Zero)
        {
            var monitor = MonitorEnumerator.GetFromWindow(handle);
            double scale = Math.Max(1, monitor.Dpi) / 96.0;
            area = new Rect(0, 0, monitor.WorkArea.Width / scale, monitor.WorkArea.Height / scale);
        }
        Width = Math.Min(Width, area.Width); Height = Math.Min(Height, area.Height);
        MinWidth = Math.Min(MinWidth, Width); MinHeight = Math.Min(MinHeight, Height);
    }

    private void OwnerClosed(object? sender, EventArgs e) { if (!_closed) Close(); }
    private void SelectComparison()
    {
        if (ComparisonSelector.SelectedItem is not SettingsGuideComparison comparison) return;
        using var language = UiText.UseLanguage(_language);
        _timer.Stop(); _frame = comparison.MotionFrames;
        _beforeValue.Text = comparison.Before; _afterValue.Text = comparison.After; _description.Text = comparison.Description;
        SetImage(_before, _beforeMissing, comparison.AssetFile(_language, "before"));
        SetImage(_after, _afterMissing, comparison.AssetFile(_language, "after"));
        AutomationProperties.SetName(_before, comparison.Before);
        AutomationProperties.SetName(_after, comparison.After);
        AutomationProperties.SetHelpText(_after, comparison.Description);
        RefreshMotionPreference();
    }

    internal void RefreshMotionPreference()
    {
        bool motion = Comparison.MotionFrames > 0 && !_reducedMotion();
        ReplayButton.Visibility = motion ? Visibility.Visible : Visibility.Collapsed;
        ReplayButton.IsEnabled = motion && HasBothImages && Enumerable.Range(0, Comparison.MotionFrames)
            .All(index => File.Exists(Path.Combine(_assetDirectory, Comparison.AssetFile(_language, $"motion{index}"))));
        if (!motion) StopAtEnd();
    }
    private void OnMotionChanged(object? sender, PropertyChangedEventArgs e) => RefreshMotionPreference();
    internal void Replay()
    {
        if (_closed || !ReplayButton.IsEnabled || Comparison.MotionFrames == 0 || _reducedMotion()) return;
        _frame = 0; ShowMotionFrame(); if (_after.Source is not null) _timer.Start();
    }
    private void OnTick(object? sender, EventArgs e) => AdvanceFrame();
    internal void AdvanceFrame()
    {
        if (!_timer.IsEnabled) return;
        if (_reducedMotion() || ++_frame >= Comparison.MotionFrames) { StopAtEnd(); return; }
        ShowMotionFrame();
    }
    private void ShowMotionFrame()
    {
        SetImage(_after, _afterMissing, Comparison.AssetFile(_language, $"motion{_frame}"));
        if (_after.Source is null) _timer.Stop();
    }
    private void StopAtEnd()
    {
        _timer.Stop();
        if (ComparisonSelector.SelectedItem is not SettingsGuideComparison comparison) return;
        _frame = comparison.MotionFrames;
        SetImage(_after, _afterMissing, comparison.AssetFile(_language, "after"));
    }
    private void SetImage(Image target, TextBlock missing, string name)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(Path.Combine(_assetDirectory, name), UriKind.Absolute);
            image.EndInit(); image.Freeze(); target.Source = image;
            missing.Visibility = Visibility.Collapsed;
        }
        catch (Exception error) when (error is IOException or NotSupportedException)
        {
            target.Source = null; missing.Visibility = Visibility.Visible;
        }
    }
    private static TextBlock Label() => new() { TextWrapping = TextWrapping.Wrap, FontSize = 14 };
    private static Border Panel(string title, TextBlock value, Image image, TextBlock missing)
    {
        var content = new StackPanel();
        var caption = Label(); caption.Text = title; caption.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
        value.FontWeight = FontWeights.SemiBold; value.MinHeight = 42; value.Margin = new Thickness(0, 6, 0, 8);
        image.Height = 300;
        missing.Text = UiText.Get("SettingsGuide.ImageUnavailable");
        missing.HorizontalAlignment = HorizontalAlignment.Center; missing.VerticalAlignment = VerticalAlignment.Center;
        var preview = new Grid(); preview.Children.Add(image); preview.Children.Add(missing);
        content.Children.Add(caption); content.Children.Add(value); content.Children.Add(preview);
        var border = new Border { Padding = new Thickness(14), BorderThickness = new Thickness(1), Child = content };
        border.SetResourceReference(Border.BorderBrushProperty, "Border.Subtle"); border.SetResourceReference(Border.BackgroundProperty, "Surface.Raised");
        return border;
    }
}
