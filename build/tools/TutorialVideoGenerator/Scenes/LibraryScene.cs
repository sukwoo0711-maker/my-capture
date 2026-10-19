using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using MyCapture.App;
using MyCapture.App.Gallery;
using MyCapture.App.Themes;
using MyCapture.Core.Primitives;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;

namespace MyCapture.TutorialGenerator.Scenes;

/// <summary>
/// The library clips: the real GalleryWindow over a fixture queue, the real tray menu, folder and
/// confirmation dialogs and the real image editor. Selection, folder navigation, dialog buttons and
/// deletion run through the product's own handlers and queue operations.
/// </summary>
/// <remarks>
/// The library's key handlers (Ctrl+Shift+N, F2, M, Enter) open their dialogs with ShowDialog,
/// which would block the generator and activate a window. For those keys the scene builds the
/// same dialog the handler builds (GalleryFolderEditDialog.ForName/ForMove, GalleryEditorWindow)
/// with the same arguments, shows it off screen, and after the dialog closes does what the handler
/// does next (refresh, navigate, status line). Del runs the product's own
/// GalleryWindow.DeleteCapturesAsync; only its confirmation, the dialog
/// LibraryPermanentDeletion.CreateRecycleConfirmation builds, is shown off screen and answered
/// with its own button first.
/// </remarks>
internal sealed class LibraryScene : TutorialScene
{
    internal static readonly string[] Features = ["library-open", "folder-create", "folder-rename", "library-move", "library-delete"];

    // The library at its minimum height (MinHeight 560 = 521 DIP client) and a little wider than its
    // minimum width, drawn at 86% so the window fits the scene and its text stays readable.
    internal const double ClientWidth = 1093, ClientHeight = 521, Scale = 448.0 / ClientHeight;
    private static readonly Point ClientOrigin = new(10, 4 + Stage.TitleBarHeight);

    private readonly string _feature;
    private readonly LibraryFixture _library;
    private readonly Shown _gallery;
    private readonly List<OffscreenWindow> _hosts = [];
    private Shown? _dialog, _editor;
    private TextBox? _nameBox;
    private string _typedName = string.Empty, _initialName = string.Empty;
    private int _typedCount;
    // The callbacks passed to the dialog (what its Save button awaits), and what follows Save.
    private Func<string, Task>? _nameSave;
    private Func<Guid?, Task>? _moveSave;
    private Func<Task>? _save;
    // Pointer targets planned before the library changed (create, rename, move, delete): when the
    // pointer starts towards one, the target must still be that control in the changed library.
    private readonly Dictionary<string, (PointD Planned, Func<PointD> Actual)> _pointerChecks = [];
    private CaptureRecord? _target;
    private CaptureFolder? _destination;
    private bool _libraryOpen = true, _deleted;

    // Tray menu (library-open).
    private ContextMenu? _menu;
    private MenuItem? _libraryItem;
    private Point _menuOrigin;
    private bool _menuOpen, _trayPressed;

    internal LibraryScene(string id, string language) : base(id, language)
    {
        _feature = id;
        _library = new LibraryFixture(Korean);
        var host = new OffscreenWindow(_library.Window, ClientWidth, ClientHeight);
        _hosts.Add(host);
        _gallery = new Shown(host, ClientOrigin, closeOnly: false);
        Settle();
        Story = _feature switch
        {
            "library-open" => BuildOpen(),
            "folder-create" => BuildCreate(),
            "folder-rename" => BuildRename(),
            "library-move" => BuildMove(),
            _ => BuildDelete(),
        };
    }

    internal override string Provenance =>
        "Real GalleryWindow over a fixture CaptureQueue (temporary AppPaths under the output folder, synthetic captures), "
        + "real tray ContextMenu from ThemedShellPresenter.CreateMenu(TrayMenuLayout.Build) with every command App wires, "
        + "real GalleryFolderEditDialog, LibraryPermanentDeletion.CreateRecycleConfirmation and GalleryEditorWindow; "
        + "selection, folder navigation, folder create/rename and capture move run through the product's view model, "
        + "handlers and queue, and deletion through GalleryWindow.DeleteCapturesAsync with a real RecoverableLibraryService "
        + "(status line result with Undo) whose trash is a folder inside the fixture, never the Recycle Bin. "
        + "Taskbar and desktop are synthetic.";

    private GalleryWindow Gallery => _library.Window;

    private GalleryViewModel ViewModel => _library.ViewModel;

    private static void Settle(int milliseconds = 120) => Stage.Pump(milliseconds);

    // ---- Geometry ---------------------------------------------------------------------------

    /// <summary>A product window drawn at <see cref="Scale"/> with its client's top-left at <see cref="Origin"/>.</summary>
    private sealed class Shown(OffscreenWindow host, Point origin, bool closeOnly)
    {
        internal OffscreenWindow Host { get; } = host;

        internal Point Origin { get; } = origin;

        internal bool Visible { get; set; } = true;

        internal PointD Of(FrameworkElement element) => At(element, new Point(element.ActualWidth / 2, element.ActualHeight / 2));

        internal PointD At(FrameworkElement element, Point local)
        {
            PointD point = Host.PointOf(element, local);
            return new PointD(Origin.X + point.X * Scale, Origin.Y + point.Y * Scale);
        }

        internal Point Local(PointD stage) => new((stage.X - Origin.X) / Scale, (stage.Y - Origin.Y) / Scale);

        internal bool Contains(PointD stage)
        {
            Point local = Local(stage);
            Size size = Host.ClientSize;
            return local.X >= 0 && local.Y >= 0 && local.X < size.Width && local.Y < size.Height;
        }

        internal void Draw(DrawingContext dc) =>
            Stage.DrawWindow(dc, Host.Snapshot(Scale), Origin, Host.Window.Title, closeOnly: closeOnly);
    }

    /// <summary>
    /// Shows a dialog off screen at its own width and natural height and places it as CenterOwner
    /// does: its whole window (caption included) centred over the library's.
    /// </summary>
    private Shown OpenDialog(Window dialog)
    {
        double width = dialog.Width;
        var host = new OffscreenWindow(dialog, width, 300, show: false);
        _hosts.Add(host);
        dialog.SizeToContent = SizeToContent.Height;
        dialog.Show();
        Settle(150);
        Shown shown = Placed(host);
        shown.Visible = false;
        return shown;
    }

    private void CloseDialog(Shown? shown)
    {
        if (shown is null) return;
        shown.Visible = false;
        try { shown.Host.Window.Close(); }
        catch (InvalidOperationException) { }
        Settle(60);
    }

    private ListBoxItem TileItem(CaptureRecord record)
    {
        GalleryItemViewModel tile = ViewModel.FindTile(record.Id) ?? throw new InvalidOperationException($"No tile for {record.Title}.");
        return _gallery.Host.Find<ListBoxItem>().First(item => item.IsVisible && ReferenceEquals(item.DataContext, tile));
    }

    private FrameworkElement TreeRow(CaptureFolder folder)
    {
        TreeViewItem item = _gallery.Host.Find<TreeViewItem>().First(candidate => candidate.IsVisible
            && candidate.DataContext is GalleryFolderNode node && node.FolderId == folder.Id);
        return item.Template.FindName("Row", item) as FrameworkElement ?? item;
    }

    /// <summary>
    /// A point on a folder row's name, measured from the row's left edge. Adding a row can bring
    /// in the folder list's scroll bar, which narrows every row but keeps their left edges.
    /// </summary>
    private PointD RowName(FrameworkElement row) =>
        _gallery.At(row, new Point(Math.Min(64, row.ActualWidth / 2), row.ActualHeight / 2));

    private GalleryFolderNode Node(Guid folderId) =>
        Flatten(ViewModel.FolderRoots).First(node => node.FolderId == folderId);

    private static IEnumerable<GalleryFolderNode> Flatten(IEnumerable<GalleryFolderNode> nodes) =>
        nodes.SelectMany(node => new[] { node }.Concat(Flatten(node.Children)));

    /// <summary>
    /// A point on the card clear of its buttons: on the preview, or on the title line so a dialog
    /// that opens next is not under the pointer.
    /// </summary>
    private PointD TilePoint(CaptureRecord record, bool onTitle = false)
    {
        ListBoxItem item = TileItem(record);
        Point local = onTitle ? new Point(item.ActualWidth * 0.3, item.ActualHeight - 62) : new Point(item.ActualWidth * 0.45, Math.Min(item.ActualHeight * 0.45, 120));
        PointD point = _gallery.Host.PointOf(item, local);
        return new PointD(ClientOrigin.X + point.X * Scale, ClientOrigin.Y + point.Y * Scale);
    }

    /// <summary>Where the pointer rests: over the details pane, clear of the dialogs.</summary>
    private static PointD Rest => new(860, 330);

    // ---- Storyboards ------------------------------------------------------------------------

    private TutorialStoryboard BuildOpen()
    {
        _libraryOpen = false;
        _target = _library.Records["dashboard"];
        // The tray menu exactly as the tray builds it; rendered detached (a popup would land on screen).
        _menu = new ThemedShellPresenter(() => true).CreateMenu(TrayMenu.Entries(library: () => _libraryOpen = true));
        LayoutMenu();
        _libraryItem = _menu.Items.OfType<MenuItem>().First(item => Equals(item.Header, UiText.Get("Text_8739F6684453")));
        PointD tray = Taskbar.TrayIcon;
        _menuOrigin = new Point(Math.Min(tray.X, Stage.Width - _menu.ActualWidth - 6), tray.Y - _menu.ActualHeight);
        Point item = _libraryItem.TranslatePoint(new Point(_libraryItem.ActualWidth * 0.4, _libraryItem.ActualHeight / 2), _menu);

        var story = new TutorialStoryboard(new PointD(560, 250));
        story.Hold("desktop", 0.8);
        story.Move("tray-move", tray).Click("tray-click").Hold("menu", 0.5);
        story.Move("item-move", new PointD(_menuOrigin.X + item.X, _menuOrigin.Y + item.Y)).Hold("item-hover", 0.3).Click("item-click");
        story.Hold("library", 0.7);
        story.Move("tile-move", TilePoint(_target)).Click("tile-click").Hold("selected", 1.0);
        story.Hold("enter", 0.5);
        story.Hold("editor", 0.4);
        return story.Finish("editor", 11.4);
    }

    private TutorialStoryboard BuildCreate()
    {
        var story = new TutorialStoryboard(Rest);
        CaptureFolderScope scope = ViewModel.SelectedScope;
        CaptureFolder? created = null;
        string name = L("회의 자료", "Meeting notes");
        // OnNewFolderClick: the name dialog creates the folder in the shown location.
        Func<string, Task> create = async text => { created = await _library.Queue.CreateFolderInScopeAsync(text, scope); };
        var dialog = GalleryFolderEditDialog.ForName(string.Empty, true, create);
        _nameSave = create;
        _dialog = OpenDialog(dialog);
        _nameBox = (TextBox)dialog.Input;
        _typedName = name;
        _save = async () =>
        {
            await SaveFromDialog(dialog);
            // After the dialog: refresh, then open the new folder (SelectFolderScopeAsync).
            ViewModel.Refresh();
            if (created is not null) ViewModel.NavigateTo(CaptureFolderScope.ForFolder(created.Id));
        };

        story.Hold("open", 1.0).Hold("key", 0.5).Hold("dialog", 0.5);
        Typing(story, name);
        story.Move("save-move", _dialog.Of(dialog.SaveButton)).Click("save").Hold("created", 0.7);
        // The new folder's row: the folder list sorts by name (CurrentCultureIgnoreCase), so it
        // takes that place among the existing rows; checked against the real row when drawn.
        GalleryFolderNode[] roots = [.. ViewModel.FolderRoots];
        int index = roots.Select(node => node.Name).Append(name).OrderBy(text => text, StringComparer.CurrentCultureIgnoreCase)
            .ToList().IndexOf(name);
        PointD first = RowName(TreeRow(_library.Queue.FindFolder(roots[0].FolderId!.Value)!));
        PointD second = RowName(TreeRow(_library.Queue.FindFolder(roots[1].FolderId!.Value)!));
        story.Move("created-move", new PointD(first.X, first.Y + index * (second.Y - first.Y)));
        _pointerChecks["created-move"] = (story.End, () => RowName(TreeRow(_library.Queue.FindFolder(created!.Id)!)));
        return story.Finish("result", 11);
    }

    private TutorialStoryboard BuildRename()
    {
        CaptureFolder folder = _library.ReferenceFolder;
        var story = new TutorialStoryboard(Rest);
        story.Hold("open", 0.8);
        story.Move("tree-move", _gallery.Of(TreeRow(folder))).Click("tree-click").Hold("selected", 0.8);
        // OnRenameFolderClick: the same dialog over the folder's current name.
        Func<string, Task> rename = async text => { await _library.Queue.RenameFolderAsync(folder.Id, text); };
        var dialog = GalleryFolderEditDialog.ForName(folder.Name, false, rename);
        _nameSave = rename;
        _dialog = OpenDialog(dialog);
        _nameBox = (TextBox)dialog.Input;
        _initialName = folder.Name;
        // A name that keeps the folder's place in the sorted list, so the row the pointer
        // returns to is still this folder (checked when the frame is drawn).
        _typedName = L("디자인 참고", "UI references");
        _save = async () => { await SaveFromDialog(dialog); ViewModel.Refresh(); };
        story.Hold("key", 0.5).Hold("dialog", 0.8);
        Typing(story, _typedName);
        story.Move("save-move", _dialog.Of(dialog.SaveButton)).Click("save").Hold("renamed", 0.6);
        story.Move("renamed-move", _gallery.Of(TreeRow(folder)));
        _pointerChecks["renamed-move"] = (story.End, () => _gallery.Of(TreeRow(folder)));
        return story.Finish("result", 11);
    }

    private TutorialStoryboard BuildMove()
    {
        _target = _library.Records["dashboard"];
        _destination = _library.ReferenceFolder;
        var story = new TutorialStoryboard(Rest);
        story.Hold("open", 0.8);
        story.Move("tile-move", TilePoint(_target)).Click("tile-click").Hold("selected", 0.6);
        // OnMoveCapturesClick: the destination dialog for the selection, starting at the shown location.
        Guid[] ids = [_target.Id];
        Func<Guid?, Task> move = async destination =>
        {
            int moved = await _library.Queue.MoveCapturesAsync(ids, destination);
            Gallery.LibraryActionStatus.Text = UiText.Format("Folder410.MoveDone", moved)
                + (destination is null ? " " + UiText.Get("Folder410.RetentionRestarted") : string.Empty);
        };
        var dialog = GalleryFolderEditDialog.ForMove(_library.Queue, null, ViewModel.SelectedScope.FolderId, move);
        _moveSave = move;
        _dialog = OpenDialog(dialog);
        _save = async () => { await SaveFromDialog(dialog); ViewModel.Refresh(); };
        story.Hold("key", 0.5).Hold("dialog", 0.6);
        story.Move("choose-move", _dialog.Of(DestinationItem(_destination))).Click("choose").Hold("chosen", 0.5);
        story.Move("save-move", _dialog.Of(dialog.SaveButton)).Click("save").Hold("moved", 1.2);
        story.Move("folder-move", _gallery.Of(TreeRow(_destination))).Click("folder-click");
        CaptureFolder destination = _destination;
        _pointerChecks["folder-move"] = (story.End, () => _gallery.Of(TreeRow(destination)));
        return story.Finish("result", 11.2);
    }

    private TutorialStoryboard BuildDelete()
    {
        _target = _library.Records["meeting"];
        var story = new TutorialStoryboard(Rest);
        story.Hold("open", 0.8);
        story.Move("tile-move", TilePoint(_target, onTitle: true)).Click("tile-click").Hold("selected", 0.6);
        // Del → DeleteCapturesAsync asks LibraryPermanentDeletion.ConfirmRecycle first: the dialog it
        // builds ([Move to Recycle Bin] in the danger style, Cancel as the default), shown off screen.
        // It asks about the selection, which the click makes this one capture (checked when Del runs).
        _dialog = OpenDialog(LibraryPermanentDeletion.CreateRecycleConfirmation(1, entireLibrary: false));
        story.Hold("key", 0.5).Hold("dialog", 1.0);
        story.Move("ok-move", _dialog.Of(DialogButton(_dialog, "LibrarySelection_MoveToRecycleBin"))).Click("ok").Hold("removed", 1.4);
        // The result goes to the status line with Undo beside it; the pointer goes to Undo.
        PointD undo = MeasureUndo();
        story.Move("undo-move", undo).Hold("undo", 1.8);
        _pointerChecks["undo-move"] = (undo, () => _gallery.Of(Gallery.LibraryStatusAction));
        return story.Finish("result", 11);
    }

    /// <summary>
    /// Where Undo lands on the status line once DeleteCapturesAsync reports one recycled capture:
    /// the line is laid out with the same words (GalleryWindow.DeleteStatus) and hidden again; the
    /// pointer check compares it with the real line when the clip reaches it.
    /// </summary>
    private PointD MeasureUndo()
    {
        Gallery.LibraryActionStatus.Text = GalleryWindow.DeleteStatus(1, new CaptureBatchRemovalResult(1, 0), null);
        Gallery.LibraryStatusAction.Content = UiText.Get("LibraryUndo_Action");
        Gallery.LibraryStatusActionPanel.Visibility = Visibility.Visible;
        Settle(60);
        PointD point = _gallery.Of(Gallery.LibraryStatusAction);
        Gallery.LibraryStatusActionPanel.Visibility = Visibility.Collapsed;
        Gallery.LibraryStatusAction.Content = null;
        Gallery.LibraryActionStatus.Text = string.Empty;
        Settle(60);
        return point;
    }

    private Shown Placed(OffscreenWindow host)
    {
        Size client = host.ClientSize;
        double ownerTop = ClientOrigin.Y - Stage.TitleBarHeight, ownerHeight = ClientHeight * Scale + Stage.TitleBarHeight;
        double outerHeight = client.Height * Scale + Stage.TitleBarHeight;
        return new Shown(host, new Point(Math.Round(ClientOrigin.X + (ClientWidth - client.Width) * Scale / 2),
            Math.Round(ownerTop + (ownerHeight - outerHeight) / 2 + Stage.TitleBarHeight)), closeOnly: true);
    }

    private static Button DialogButton(Shown dialog, string key) => dialog.Host.Find<Button>()
        .First(button => button.IsVisible && Equals(button.Content, UiText.Get(key)));

    private ListBoxItem DestinationItem(CaptureFolder folder)
    {
        string path = _library.Queue.GetFolderDisplayPath(folder.Id);
        return _dialog!.Host.Find<ListBoxItem>().First(item => item.IsVisible && item.Content is { } choice
            && ((string?)choice.GetType().GetProperty("Name")?.GetValue(choice))?.EndsWith(" / " + path, StringComparison.Ordinal) == true);
    }

    /// <summary>Typing: about four characters a second, one frame plan per character.</summary>
    private static void Typing(TutorialStoryboard story, string text)
    {
        foreach (char _ in text) story.Hold("typing", 0.2);
        story.Hold("typed", 0.5);
    }

    /// <summary>
    /// What the dialog's Save handler does: awaits its save callback, which runs the queue
    /// operation. The handler then sets DialogResult, which only a modal dialog accepts, so the
    /// scene closes the dialog itself.
    /// </summary>
    private async Task SaveFromDialog(GalleryFolderEditDialog dialog)
    {
        if (dialog.Input is TextBox box) await _nameSave!(box.Text);
        else
        {
            // ForMove saves the selected destination's Id (FolderDestination is private to the dialog).
            object? choice = ((ListBox)dialog.Input).SelectedItem;
            await _moveSave!((Guid?)choice?.GetType().GetProperty("Id")?.GetValue(choice));
        }

        CloseDialog(_dialog);
    }

    // ---- Frames -----------------------------------------------------------------------------

    internal override void Draw(DrawingContext dc, TutorialFramePlan frame)
    {
        Apply(frame);
        Stage.DrawDesktop(dc);
        if (_feature == "library-open") Taskbar.Draw(dc, Korean, LibraryFixture.Now, _trayPressed);
        if (_libraryOpen) _gallery.Draw(dc);
        if (_editor is { Visible: true }) _editor.Draw(dc);
        if (_dialog is { Visible: true }) { _dialog.Draw(dc); DrawCaret(dc); }
        if (_menuOpen && _menu is not null) DrawMenu(dc);
        (string caption, string key) = Caption(frame.Segment);
        if (frame.Pressed) Stage.DrawClick(dc, frame.Cursor, frame.Progress);
        Stage.DrawPointer(dc, frame.Cursor, PointerAt(frame.Cursor));
        Stage.DrawCaption(dc, caption, key);
    }

    private void Apply(TutorialFramePlan frame)
    {
        if (frame.IsFirst && _pointerChecks.TryGetValue(frame.Segment, out var check))
        {
            PointD actual = check.Actual();
            if (Math.Abs(actual.X - check.Planned.X) > 2 || Math.Abs(actual.Y - check.Planned.Y) > 2)
                throw new InvalidOperationException($"'{frame.Segment}' points at ({check.Planned.X:0},{check.Planned.Y:0}) but its target is now at ({actual.X:0},{actual.Y:0}).");
        }

        switch (frame.Segment)
        {
            case "tray-click" when frame.IsFirst:
                _trayPressed = true;
                break;
            case "tray-click" when frame.IsLast:
                _trayPressed = false;
                _menuOpen = true;
                break;
            case "item-move" or "item-hover":
                SetHighlighted(_libraryItem!, MenuItemContains(_libraryItem!, frame.Cursor));
                break;
            case "item-click" when frame.IsLast:
                // The menu item's own Click handler closes the menu and runs the Library command.
                _libraryItem!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, _libraryItem));
                _menuOpen = false;
                ViewModel.Refresh();
                break;
            case "tile-click" when frame.IsLast:
                // What a click on a card does (OnTileMouseLeftButtonDown), without capturing the mouse.
                ViewModel.Select(_target!.Id);
                break;
            case "editor" when _editor is null:
                OpenEditor();
                break;
            case "tree-click" when frame.IsLast:
            case "folder-click" when frame.IsLast:
                // Selecting the tree row runs the tree's own SelectedItemChanged handler.
                Node((_feature == "folder-rename" ? _library.ReferenceFolder : _destination!).Id).IsSelected = true;
                break;
            case "dialog" when _dialog is { Visible: false } && !_deleted:
                ShowDialogNow();
                break;
            case "typing":
                _typedCount++;
                SetName(_typedName[..Math.Min(_typedName.Length, _typedCount)]);
                break;
            case "typed":
                SetName(_typedName);
                break;
            case "choose" when frame.IsLast:
                ((ListBox)((GalleryFolderEditDialog)_dialog!.Host.Window).Input).SelectedItem = DestinationItem(_destination!).Content;
                break;
            case "save" when frame.IsLast && _save is not null:
                Func<Task> save = _save;
                _save = null;
                LibraryFixture.Wait(save());
                break;
            case "ok" when frame.IsLast:
                Delete();
                break;
        }

        Stage.Pump(15);
    }

    private void ShowDialogNow()
    {
        _dialog!.Visible = true;
        if (_nameBox is not null)
        {
            // The dialog focuses its name box on load; FocusLook shows that focus off screen.
            FocusLook.Apply(_nameBox);
            SetName(_initialName);
            if (_initialName.Length > 0) _nameBox.SelectAll();
        }
    }

    private void SetName(string text)
    {
        if (_nameBox is null || _nameBox.Text == text) return;
        _nameBox.Text = text;
        _nameBox.CaretIndex = text.Length;
    }

    /// <summary>The text caret at the end of the typed name (the focused box shows it).</summary>
    private void DrawCaret(DrawingContext dc)
    {
        if (_nameBox is null || _dialog is null || _nameBox.SelectionLength > 0) return;
        _nameBox.UpdateLayout();
        Rect caret = _nameBox.GetRectFromCharacterIndex(_nameBox.CaretIndex);
        if (caret.IsEmpty) return;
        PointD top = _dialog.Host.PointOf(_nameBox, caret.TopLeft);
        double x = _dialog.Origin.X + top.X * Scale, y = _dialog.Origin.Y + top.Y * Scale;
        dc.DrawRectangle(Stage.Res("Text.Primary"), null, new Rect(x, y, 1.2, caret.Height * Scale));
    }

    private void OpenEditor()
    {
        // OpenReedit: load the capture, then the gallery editor over the library (commits intercepted).
        CaptureRecord record = _target!;
        GalleryReeditContext context = _library.ReeditLoader.TryLoad(record, out GalleryReeditLoader.LoadFailure failure)
            ?? throw new InvalidOperationException($"The fixture capture did not load: {failure}.");
        var editor = new GalleryEditorWindow(context, _library.Privacy, record,
            () => ViewModel.FindTile(record.Id)?.RetentionHours() ?? CaptureRetention.DefaultImageRetentionHours)
        {
            CommitRequested = _ => Task.FromResult(false),
        };
        // Below the library's caption, so both windows read as separate windows.
        var host = new OffscreenWindow(editor, 1050, 480);
        _hosts.Add(host);
        Settle(250);
        _editor = new Shown(host, new Point(40, ClientOrigin.Y + Stage.TitleBarHeight), closeOnly: false) { Visible = true };
    }

    private void Delete()
    {
        // The danger button records OK and closes the dialog, as it does over the library.
        var confirm = (ThemedDialog)_dialog!.Host.Window;
        DialogButton(_dialog, "LibrarySelection_MoveToRecycleBin").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        _dialog.Visible = false;
        if (confirm.Result != MessageBoxResult.OK) throw new InvalidOperationException("The confirmation did not return OK.");
        Settle(60);
        // Del runs DeleteCapturesAsync over the selection (ConfirmDeleteMany); its confirmation is
        // the dialog just answered, asked about exactly this selection.
        Guid[] selected = [.. ViewModel.Selection.SelectedIds];
        if (selected.Length != 1 || selected[0] != _target!.Id)
            throw new InvalidOperationException("The click did not select exactly the capture the confirmation asked about.");
        Gallery.ConfirmDelete = (count, entireLibrary) => count == 1 && !entireLibrary && confirm.Result == MessageBoxResult.OK;
        LibraryFixture.Wait(Gallery.DeleteCapturesAsync(selected));
        _deleted = true;
        if (ViewModel.FindTile(_target.Id) is not null || Gallery.StatusActionLabelForTest != UiText.Get("LibraryUndo_Action"))
            throw new InvalidOperationException("The delete did not leave the list with Undo on the status line.");
    }

    // ---- Tray menu ----------------------------------------------------------------------------

    private void LayoutMenu() => Detached.Layout(_menu!);

    private bool MenuItemContains(MenuItem item, PointD stage)
    {
        Point local = new(stage.X - _menuOrigin.X, stage.Y - _menuOrigin.Y);
        Point topLeft = item.TranslatePoint(new Point(), _menu);
        return new Rect(topLeft, new Size(item.ActualWidth, item.ActualHeight)).Contains(local);
    }

    private static readonly PropertyInfo HighlightedProperty = typeof(MenuItem).GetProperty(nameof(MenuItem.IsHighlighted))!;

    /// <summary>The pointer's hover: MenuItem.IsHighlighted, which the template's hover layer follows.</summary>
    private void SetHighlighted(MenuItem item, bool highlighted)
    {
        if (item.IsHighlighted == highlighted) return;
        HighlightedProperty.SetValue(item, highlighted);
        LayoutMenu();
    }

    private void DrawMenu(DrawingContext dc)
    {
        LayoutMenu();
        Detached.Draw(dc, _menu!, _menuOrigin);
    }

    // ---- Pointer ------------------------------------------------------------------------------

    /// <summary>The cursor the topmost window under the pointer asks for (hand on menu items and links).</summary>
    private Stage.PointerKind PointerAt(PointD stage)
    {
        if (_menuOpen && _libraryItem is not null && MenuItemContains(_libraryItem, stage)) return Stage.PointerKind.Hand;
        foreach (Shown? window in new[] { _dialog, _editor, _libraryOpen ? _gallery : null })
        {
            if (window is not { Visible: true } || !window.Contains(stage)) continue;
            if (window.Host.ClientRoot.InputHitTest(window.Local(stage)) is not DependencyObject hit) return Stage.PointerKind.Arrow;
            for (DependencyObject? current = hit; current is not null;
                 current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
            {
                if (current is TextBoxBase) return Stage.PointerKind.Beam;
                if (current is FrameworkElement { Cursor: { } cursor })
                    return cursor == Cursors.Hand ? Stage.PointerKind.Hand : cursor == Cursors.IBeam ? Stage.PointerKind.Beam : Stage.PointerKind.Arrow;
            }

            return Stage.PointerKind.Arrow;
        }

        return Stage.PointerKind.Arrow;
    }

    // ---- Captions -----------------------------------------------------------------------------

    private (string Caption, string Key) Caption(string segment)
    {
        switch (_feature)
        {
            case "library-open":
                string library = Ui("Text_8739F6684453");
                return segment switch
                {
                    "desktop" or "tray-move" or "tray-click" or "menu" =>
                        (L("알림 영역의 MyCapture 아이콘을 마우스 오른쪽 단추로 클릭", "Right-click the MyCapture icon in the notification area"), ""),
                    "item-move" or "item-hover" or "item-click" => (L($"트레이 메뉴에서 {library} 선택", $"Choose {library} in the tray menu"), ""),
                    "library" or "tile-move" or "tile-click" or "selected" => (L("확인할 이미지나 동영상 선택", "Select an image or video"), ""),
                    "enter" => (L("Enter: 이미지 편집기에서 열기", "Enter opens the image in the editor"), "Enter"),
                    _ => (L("이미지는 편집기, 동영상은 재생 창에서 열림", "Images open in the editor, videos in the player"), ""),
                };
            case "folder-create":
                string create = Label(DialogSave);
                return segment switch
                {
                    "open" => (L("라이브러리 열기", "Open the library"), ""),
                    "key" or "dialog" => ($"{FeatureKeys}: {Ui("Folder410.NewFolder")}", FeatureKeys),
                    "typing" or "typed" => (L("새 폴더 이름 입력", "Type the new folder's name"), ""),
                    "save-move" or "save" => (L($"{create} 선택 또는 Enter", $"Click {create} or press Enter"), "Enter"),
                    _ => (L("폴더 목록에 새 폴더가 나타나고 열림", "The new folder appears in the folder list and opens"), ""),
                };
            case "folder-rename":
                string save = Label(DialogSave);
                return segment switch
                {
                    "open" or "tree-move" or "tree-click" or "selected" => (L("폴더 목록에서 이름을 바꿀 폴더 선택", "Select the folder in the folder list"), ""),
                    "key" or "dialog" => ($"{FeatureKeys}: {Ui("Folder410.Rename")}", FeatureKeys),
                    "typing" or "typed" => (L("새 이름 입력", "Type the new name"), ""),
                    "save-move" or "save" => (L($"{save} 선택 또는 Enter", $"Click {save} or press Enter"), "Enter"),
                    _ => (L("바뀐 폴더 이름 확인", "Check the new folder name"), ""),
                };
            case "library-move":
                string move = Label(DialogSave), destination = Ui("Folder410.Destination");
                return segment switch
                {
                    "open" or "tile-move" or "tile-click" or "selected" => (L("이동할 캡처 선택", "Select the capture to move"), ""),
                    "key" or "dialog" => ($"{FeatureKeys}: {Ui("Folder410.MoveCaptures")}", FeatureKeys),
                    "choose-move" or "choose" or "chosen" => (L($"{destination}에서 대상 폴더 선택", $"Choose the folder under {destination}"), ""),
                    "save-move" or "save" => (L($"{move} 선택", $"Click {move}"), ""),
                    "moved" => (L("캡처가 대상 폴더로 이동됨", "The capture moves to the folder"), ""),
                    _ => (L("대상 폴더에서 캡처 확인", "Find the capture in that folder"), ""),
                };
            default:
                string recycle = Ui("LibrarySelection_MoveToRecycleBin"), undo = Ui("LibraryUndo_Action");
                return segment switch
                {
                    "open" or "tile-move" or "tile-click" or "selected" => (L("삭제할 항목 선택", "Select the item to delete"), ""),
                    "key" or "dialog" => (L($"{FeatureKeys}: {recycle} 확인", $"{FeatureKeys}: confirm {recycle}"), FeatureKeys),
                    "ok-move" or "ok" => (L($"{recycle} 선택", $"Click {recycle}"), ""),
                    "removed" => (L("목록에서 바로 사라지고 상태 줄에 결과 표시", "It leaves the list and the status line shows the result"), ""),
                    _ => (L($"상태 줄의 {undo} 또는 Ctrl+Z로 복원", $"{undo} on the status line or Ctrl+Z brings it back"), ""),
                };
        }
    }

    /// <summary>The open folder dialog's confirming button (Create, Save or Move to folder).</summary>
    private Button DialogSave => ((GalleryFolderEditDialog)(_dialog ?? throw new InvalidOperationException("No folder dialog.")).Host.Window).SaveButton;

    public override void Dispose()
    {
        foreach (OffscreenWindow host in Enumerable.Reverse(_hosts))
        {
            if (ReferenceEquals(host.Window, Gallery)) continue;
            host.Dispose();
        }

        _library.Dispose();
        _gallery.Host.Dispose();
    }
}
