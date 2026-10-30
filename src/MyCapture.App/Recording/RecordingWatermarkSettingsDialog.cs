using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using MyCapture.App.Capture;
using MyCapture.App.Editing;
using MyCapture.App.Themes;
using MyCapture.Core.Annotations;
using MyCapture.Core.Recording;

namespace MyCapture.App.Recording;

internal sealed class RecordingWatermarkSettingsDialog : Window
{
    private readonly string? _imageFolder;
    private readonly Action<RecordingWatermarkSettings>? _preview;
    private readonly Func<bool> _isLocked;
    private readonly DispatcherTimer _lockTimer;
    private readonly CheckBox _enabled;
    private readonly ComboBox _source;
    private readonly TextBox _text;
    private readonly TextBox _width;
    private readonly TextBox _margin;
    private readonly TextBox _opacity;
    private readonly TextBox _color;
    private readonly TextBlock _error;
    private readonly Button _choose;
    private StampAnchor _anchor;
    private bool _initialized;

    internal RecordingWatermarkSettingsDialog(RecordingWatermarkSettings settings, string? imageFolder,
        Action<RecordingWatermarkSettings>? preview = null, Func<bool>? isLocked = null)
    {
        _imageFolder = imageFolder;
        _preview = preview;
        _isLocked = isLocked ?? (static () => false);
        settings = settings.Normalize();
        _anchor = settings.Anchor;
        StandardWindowTheme.Apply(this);
        Title = UiText.Get("RecordingWatermark_Title");
        Width = 520; Height = 650; MinWidth = 460; MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        var panel = new StackPanel { Margin = new Thickness(24) };
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        _enabled = new CheckBox { Content = UiText.Get("RecordingWatermark_Show"), IsChecked = settings.Enabled, Margin = new Thickness(0, 0, 0, 14) };
        panel.Children.Add(_enabled);
        _source = new ComboBox { SelectedValuePath = "Tag", MinWidth = 200, HorizontalAlignment = HorizontalAlignment.Left };
        _source.Items.Add(new ComboBoxItem { Tag = RecordingWatermarkSource.Image, Content = UiText.Get("RecordingWatermark_Image") });
        _source.Items.Add(new ComboBoxItem { Tag = RecordingWatermarkSource.Text, Content = UiText.Get("RecordingWatermark_Text") });
        _source.SelectedValue = settings.Source;
        AddField(panel, "RecordingWatermark_Source", _source);
        _choose = ThemedDialog.CreateActionButton(UiText.Get("RecordingWatermark_ChooseImage"), primary: false);
        _choose.HorizontalAlignment = HorizontalAlignment.Left;
        _choose.Click += (_, _) => ChooseImage();
        panel.Children.Add(_choose);
        _text = Box(settings.Text); _text.MaxLength = 80; _text.AcceptsReturn = false;
        AddField(panel, "RecordingWatermark_Text", _text);
        var anchors = new UniformGrid { Rows = 3, Columns = 3 };
        foreach (StampAnchor anchor in Enum.GetValues<StampAnchor>())
        {
            var button = new RadioButton
            {
                Content = UiText.Get($"RecordingWatermark_Anchor{anchor}"),
                IsChecked = anchor == _anchor, GroupName = "RecordingWatermarkAnchor",
                Margin = new Thickness(0, 4, 8, 4), MinHeight = 28,
            };
            AutomationProperties.SetName(button, (string)button.Content);
            button.Checked += (_, _) => { _anchor = anchor; RefreshPreview(); };
            anchors.Children.Add(button);
        }
        AddField(panel, "RecordingWatermark_Position", anchors);
        _width = Box((settings.WidthFraction * 100).ToString("0.##", CultureInfo.CurrentCulture));
        _margin = Box(settings.Margin.ToString("0.##", CultureInfo.CurrentCulture));
        _opacity = Box((settings.Opacity * 100).ToString("0.##", CultureInfo.CurrentCulture));
        _color = Box(settings.TextColor);
        foreach ((string key, TextBox box) in new[] { ("RecordingWatermark_Width", _width), ("RecordingWatermark_Margin", _margin),
                     ("RecordingWatermark_Opacity", _opacity), ("RecordingWatermark_TextColor", _color) })
        {
            box.Width = 150; box.HorizontalAlignment = HorizontalAlignment.Left; AddField(panel, key, box);
        }
        _error = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) };
        _error.SetResourceReference(TextBlock.ForegroundProperty, "State.Danger");
        panel.Children.Add(_error);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        var cancel = ThemedDialog.CreateActionButton(UiText.Get("Dialog.Cancel"), primary: false); cancel.IsCancel = true;
        var save = ThemedDialog.CreateActionButton(UiText.Get("Library.Action.Save"), primary: true); save.IsDefault = true;
        save.Click += (_, _) =>
        {
            if (_isLocked()) return;
            if (!TryRead(out RecordingWatermarkSettings candidate)) { _error.Text = UiText.Get("RecordingWatermark_InvalidValues"); return; }
            if (candidate.Enabled && !ValidateAsset(candidate)) return;
            Result = candidate; DialogResult = true;
        };
        actions.Children.Add(cancel); actions.Children.Add(save); panel.Children.Add(actions);
        _lockTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(150) };
        _lockTimer.Tick += (_, _) =>
        {
            bool unlocked = !_isLocked();
            foreach (UIElement child in panel.Children)
                if (!ReferenceEquals(child, actions)) child.IsEnabled = unlocked;
            save.IsEnabled = unlocked;
            if (unlocked) RefreshSourceAvailability();
        };
        Loaded += (_, _) => _lockTimer.Start();
        Closed += (_, _) => _lockTimer.Stop();
        foreach (TextBox box in new[] { _text, _width, _margin, _opacity, _color }) box.TextChanged += (_, _) => RefreshPreview();
        _enabled.Checked += (_, _) => RefreshPreview(); _enabled.Unchecked += (_, _) => RefreshPreview();
        _source.SelectionChanged += (_, _) => RefreshPreview();
        SourceInitialized += (_, _) => CaptureWindowExclusion.TryApply(this);
        _initialized = true;
        RefreshPreview();
    }

    internal RecordingWatermarkSettings? Result { get; private set; }

    private static TextBox Box(string text) => new() { Text = text, MinHeight = 30 };

    private static void AddField(Panel panel, string key, FrameworkElement field)
    {
        panel.Children.Add(new TextBlock { Text = UiText.Get(key), Margin = new Thickness(0, 10, 0, 4) });
        AutomationProperties.SetName(field, UiText.Get(key));
        panel.Children.Add(field);
    }

    private bool TryRead(out RecordingWatermarkSettings settings)
    {
        settings = new();
        if (!double.TryParse(_width.Text, out double width) || !double.IsFinite(width) || width is < 2 or > 100
            || !double.TryParse(_margin.Text, out double margin) || !double.IsFinite(margin) || margin is < 0 or > 4096
            || !double.TryParse(_opacity.Text, out double opacity) || !double.IsFinite(opacity) || opacity is < 10 or > 100
            || !RecordingWatermarkSettings.IsValidColor(_color.Text)) return false;
        settings = new RecordingWatermarkSettings
        {
            Enabled = _enabled.IsChecked == true,
            Source = _source.SelectedValue is RecordingWatermarkSource source ? source : RecordingWatermarkSource.Image,
            Text = _text.Text, TextColor = _color.Text, Anchor = _anchor,
            WidthFraction = width / 100, Margin = margin, Opacity = opacity / 100,
        }.Normalize();
        return true;
    }

    private bool ValidateAsset(RecordingWatermarkSettings settings)
    {
        try
        {
            _ = RecordingWatermarkSnapshot.Create(settings, settings.Source == RecordingWatermarkSource.Image
                ? RecordingWatermarkAssets.Load(_imageFolder) : null);
            _error.Text = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException
            or ArgumentException or FormatException or System.Runtime.InteropServices.ExternalException or OverflowException)
        {
            _error.Text = UiText.Get(settings.Source == RecordingWatermarkSource.Image ? "RecordingWatermark_ImageError" : "RecordingWatermark_TextError");
            return false;
        }
    }

    private void RefreshPreview()
    {
        if (!_initialized || _isLocked()) return;
        RefreshSourceAvailability();
        if (!TryRead(out RecordingWatermarkSettings candidate)) return;
        if (candidate.Enabled && !ValidateAsset(candidate)) return;
        _error.Text = string.Empty;
        _preview?.Invoke(candidate);
    }

    private void RefreshSourceAvailability()
    {
        bool isText = _source.SelectedValue is RecordingWatermarkSource.Text;
        _text.IsEnabled = _color.IsEnabled = isText;
        _choose.IsEnabled = !isText && !string.IsNullOrWhiteSpace(_imageFolder);
    }

    private void ChooseImage()
    {
        if (_isLocked() || string.IsNullOrWhiteSpace(_imageFolder)) return;
        var picker = new OpenFileDialog { Title = UiText.Get("RecordingWatermark_ChooseImage"), Filter = UiText.Get("Text_DC9F5ED04461"), CheckFileExists = true };
        if (picker.ShowDialog(this) != true) return;
        if (_isLocked()) return;
        try
        {
            using var stream = new FileStream(picker.FileName, FileMode.Open, FileAccess.Read, FileShare.Read);
            BitmapSource image = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            image.Freeze();
            StampStore.Save(image, _imageFolder);
            RefreshPreview();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException
            or ArgumentException or FormatException or System.Runtime.InteropServices.ExternalException or OverflowException)
        { _error.Text = UiText.Get("RecordingWatermark_ImageError"); }
    }
}
