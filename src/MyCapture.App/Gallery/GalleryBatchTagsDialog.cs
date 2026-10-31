using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using MyCapture.App.Themes;
using MyCapture.Core.Queue;

namespace MyCapture.App.Gallery;

/// <summary>Mixed rows leave existing membership alone until the user explicitly checks or clears them.</summary>
internal sealed class GalleryBatchTagsDialog : Window
{
    private readonly Dictionary<string, CheckBox> _rows = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CaptureTagChange> _changes = new(StringComparer.OrdinalIgnoreCase);
    private readonly StackPanel _tagRows = new();
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private bool _saving;

    internal TextBox NewTagInput { get; } = new() { MinHeight = 36 };
    internal Button ApplyButton { get; }
    internal CaptureBatchTagResult? Result { get; private set; }
    internal IReadOnlyList<CaptureTagChange> Changes => _changes.Values.ToArray();
    internal IReadOnlyDictionary<string, CheckBox> Rows => _rows;

    internal GalleryBatchTagsDialog(IReadOnlyList<CaptureRecord> records,
        Func<IReadOnlyList<CaptureTagChange>, Task<CaptureBatchTagResult>> save)
    {
        Title = UiText.Format("Library.Tags.DialogTitle", records.Count);
        Width = 420; Height = 440; MinWidth = 360; MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(StyleProperty, "Window.Standard");
        SetResourceReference(BackgroundProperty, "Surface.Base");
        SetResourceReference(ForegroundProperty, "Text.Primary");
        foreach (CaptureTagSelection tag in CaptureOrganization.SelectionTags(records.Select(record => record.Tags)))
            AddRow(tag.Name, tag.IsChecked);

        var content = new DockPanel { Margin = new Thickness(24) };
        var actions = new StackPanel();
        var newTagLabel = new Label { Content = UiText.Get("Library.Tags.New"), Target = NewTagInput, Padding = new Thickness(0, 8, 0, 6) };
        newTagLabel.SetResourceReference(Control.ForegroundProperty, "Text.Primary");
        actions.Children.Add(newTagLabel);
        AutomationProperties.SetName(NewTagInput, UiText.Get("Library.Tags.New"));
        actions.Children.Add(NewTagInput);
        AutomationProperties.SetLiveSetting(_error, AutomationLiveSetting.Polite);
        actions.Children.Add(_error);
        ApplyButton = ThemedDialog.CreateActionButton(UiText.Get("Library.Tags.Apply"), primary: true);
        var cancel = ThemedDialog.CreateActionButton(UiText.Get("Library.Action.Cancel"), primary: false);
        cancel.IsCancel = true;
        actions.Children.Add(ThemedDialog.CreateActionRow(ApplyButton, cancel));
        DockPanel.SetDock(actions, Dock.Bottom);
        content.Children.Add(actions);
        content.Children.Add(new ScrollViewer { Content = _tagRows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        Content = content;
        NewTagInput.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            AddNewTag();
        };
        Loaded += (_, _) => NewTagInput.Focus();
        Closing += (_, e) => e.Cancel = _saving;
        ApplyButton.Click += async (_, _) =>
        {
            if (_saving || (!string.IsNullOrWhiteSpace(NewTagInput.Text) && !AddNewTag())) return;
            _saving = true; content.IsEnabled = false;
            try { Result = await save(Changes); }
            catch (ArgumentException) { KoreanWordWrap.Show(_error, UiText.Get("Library.Tags.Invalid")); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or AggregateException)
            { KoreanWordWrap.Show(_error, UiText.Get("Library.Organize.Failed")); }
            finally { _saving = false; content.IsEnabled = true; }
            if (Result is not null) DialogResult = true;
        };
    }

    private void AddRow(string name, bool? state)
    {
        var row = new TagCheckBox { Content = name, IsThreeState = true, IsChecked = state,
            Margin = new Thickness(0, 4, 0, 4), MinHeight = 28 };
        row.SetResourceReference(StyleProperty, typeof(CheckBox));
        AutomationProperties.SetName(row, name);
        void RecordIntent(object sender, RoutedEventArgs e)
        {
            if (row.IsChecked is bool add) _changes[name] = new CaptureTagChange(name, add);
            else _changes.Remove(name);
        }
        row.Checked += RecordIntent;
        row.Unchecked += RecordIntent;
        row.Indeterminate += RecordIntent;
        _rows.Add(name, row);
        _tagRows.Children.Add(row);
    }

    internal bool AddNewTag()
    {
        try
        {
            CaptureTagChange change = CaptureOrganization.NormalizeTagChanges([new(NewTagInput.Text, true)])[0];
            if (!_rows.TryGetValue(change.Name, out CheckBox? row)) AddRow(change.Name, true);
            else row.IsChecked = true;
            _changes[change.Name] = change;
            NewTagInput.Clear();
            KoreanWordWrap.Show(_error, string.Empty);
            return true;
        }
        catch (ArgumentException) { KoreanWordWrap.Show(_error, UiText.Get("Library.Tags.Invalid")); return false; }
    }

    private sealed class TagCheckBox : CheckBox
    {
        // Mixed is the initial summary only. A click always expresses an add or remove intent.
        protected override void OnToggle() => IsChecked = IsChecked != true;
    }
}
