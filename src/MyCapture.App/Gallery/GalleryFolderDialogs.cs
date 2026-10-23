using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using MyCapture.App.Themes;
using MyCapture.Core.Queue;

namespace MyCapture.App.Gallery;

internal sealed class GalleryFolderEditDialog : Window
{
    private bool _saving;
    internal Control Input { get; }
    internal Button SaveButton { get; }
    internal Button CancelButton { get; }

    private GalleryFolderEditDialog(string titleKey, string labelKey, Control input,
        string actionKey, string invalidKey, Func<Task> save, string? hintKey = null)
    {
        Input = input;
        Title = UiText.Get(titleKey);
        Width = 480; MinWidth = 360; SizeToContent = SizeToContent.Height;
        MaxHeight = 640; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        SetResourceReference(StyleProperty, "Window.Standard");
        SetResourceReference(BackgroundProperty, "Surface.Base");
        SetResourceReference(ForegroundProperty, "Text.Primary");
        var content = new StackPanel { Margin = new Thickness(24) };
        content.Children.Add(new TextBlock { Text = UiText.Get(labelKey), Margin = new Thickness(0, 0, 0, 8) });
        AutomationProperties.SetName(input, UiText.Get(labelKey));
        content.Children.Add(input);
        if (hintKey is not null) content.Children.Add(KoreanWordWrap.Show(new TextBlock { TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0) }, UiText.Get(hintKey)));
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
        AutomationProperties.SetLiveSetting(error, AutomationLiveSetting.Polite);
        content.Children.Add(error);
        // The action first, then cancel, like every other dialog in the app.
        SaveButton = ThemedDialog.CreateActionButton(UiText.Get(actionKey), primary: true);
        SaveButton.IsDefault = true;
        CancelButton = ThemedDialog.CreateActionButton(UiText.Get("Library.Action.Cancel"), primary: false);
        CancelButton.IsCancel = true;
        WrapPanel buttons = ThemedDialog.CreateActionRow(SaveButton, CancelButton);
        buttons.Margin = new Thickness(0, 16, 0, 0);
        content.Children.Add(buttons);
        Content = content;
        Loaded += (_, _) => { input.Focus(); if (input is TextBox text) text.SelectAll(); };
        Closing += (_, e) => e.Cancel = _saving;
        SaveButton.Click += async (_, _) =>
        {
            if (_saving) return;
            _saving = true; input.IsEnabled = SaveButton.IsEnabled = CancelButton.IsEnabled = false;
            bool saved = false;
            try { await save(); saved = true; }
            catch (ArgumentException) { error.Text = UiText.Get(invalidKey); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                or AggregateException)
            { error.Text = UiText.Get("Folder410.OperationFailed"); }
            finally { _saving = false; input.IsEnabled = SaveButton.IsEnabled = CancelButton.IsEnabled = true; }
            if (saved) DialogResult = true;
        };
    }

    internal static GalleryFolderEditDialog ForName(string initial, bool create, Func<string, Task> save)
    {
        var input = new TextBox { Text = initial, MaxLength = CaptureFolderNaming.MaximumNameLength, MinHeight = 36 };
        return new(create ? "Folder410.NewFolder" : "Folder410.Rename", "Folder410.Name", input,
            create ? "Folder410.Create" : "Library.Action.Save", "Folder410.NameInvalid", () => save(input.Text));
    }

    internal static GalleryFolderEditDialog ForMove(CaptureQueue queue, Guid? sourceFolder, Guid? initial,
        Func<Guid?, Task> save)
    {
        HashSet<Guid> excluded = sourceFolder is { } id
            ? queue.FoldersInScope(CaptureFolderScope.ForFolder(id)).Select(folder => folder.Id).ToHashSet() : [];
        var choices = new List<FolderDestination>
        {
            new(null, UiText.Get(sourceFolder is null ? "Folder410.Unassigned" : "Folder410.RootLocation")),
        };
        choices.AddRange(queue.Folders.Where(folder => !excluded.Contains(folder.Id))
            .Select(folder => new FolderDestination(folder.Id, queue.GetFolderDate(folder.Id).ToString("yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture) + " / " + queue.GetFolderDisplayPath(folder.Id)))
            .OrderBy(choice => choice.Name, StringComparer.CurrentCultureIgnoreCase));
        var input = new ListBox { ItemsSource = choices, DisplayMemberPath = nameof(FolderDestination.Name),
            MinHeight = 120, MaxHeight = 330, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        input.SelectedItem = choices.FirstOrDefault(choice => choice.Id == initial) ?? choices[0];
        return new(sourceFolder is null ? "Folder410.MoveCaptures" : "Folder410.MoveFolder",
            "Folder410.Destination", input, sourceFolder is null ? "Folder410.MoveCaptures" : "Folder410.MoveFolder",
            "Folder410.InvalidMove", () => save((input.SelectedItem as FolderDestination)?.Id),
            sourceFolder is null ? "Folder410.RetentionRestarted" : null);
    }

    private sealed record FolderDestination(Guid? Id, string Name);
}

internal sealed class GalleryFolderDeletionDialog : Window
{
    internal Button CancelButton { get; }
    internal Button DeleteButton { get; }
    internal GalleryFolderDeletionDialog(string name, CaptureFolderDeletionPreview preview, bool isDate = false)
    {
        Title = UiText.Get("Folder410.DeleteTitle");
        Width = 470; MinWidth = 360; SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(StyleProperty, "Window.Standard");
        SetResourceReference(BackgroundProperty, "Surface.Base");
        SetResourceReference(ForegroundProperty, "Text.Primary");
        var content = new StackPanel { Margin = new Thickness(24) };
        content.Children.Add(KoreanWordWrap.Show(new TextBlock { TextWrapping = TextWrapping.Wrap }, UiText.Format("Folder410.DeleteConfirm", name,
            preview.SubfolderCount, preview.ImageCount, preview.VideoCount)));
        if (isDate) content.Children.Add(KoreanWordWrap.Show(new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) },
            UiText.Get("Folder410.DeleteDateIncludes")));
        content.Children.Add(KoreanWordWrap.Show(new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) },
            UiText.Get("Folder410.DeleteIrreversible")));
        // Delete comes first like any confirming action, but Enter and Esc both cancel: a
        // destructive command is never the default. As in ThemedDialog's destructive
        // confirmations, that default stays bordered: the red fill would mark Cancel as the danger.
        DeleteButton = ThemedDialog.CreateDangerButton(UiText.Get("Folder410.Delete"));
        DeleteButton.Click += (_, _) => DialogResult = true;
        CancelButton = ThemedDialog.CreateActionButton(UiText.Get("Library.Action.Cancel"), primary: false);
        CancelButton.IsCancel = true;
        CancelButton.IsDefault = true;
        WrapPanel buttons = ThemedDialog.CreateActionRow(DeleteButton, CancelButton);
        buttons.Margin = new Thickness(0, 20, 0, 0);
        content.Children.Add(buttons);
        Content = content;
        Loaded += (_, _) => CancelButton.Focus();
    }
}
