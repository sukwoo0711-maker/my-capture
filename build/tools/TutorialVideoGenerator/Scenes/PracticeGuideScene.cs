using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App;
using MyCapture.App.Capture;
using MyCapture.App.Editing;
using MyCapture.App.Gallery;
using MyCapture.App.Themes;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;
using MyCapture.Platform.Imaging;

namespace MyCapture.TutorialGenerator.Scenes;

/// <summary>
/// Practice-picture workflows, rendered from current product controls. External transactions use
/// fixture callbacks; no user clipboard, capture device, profile, popup or real input is used.
/// </summary>
internal sealed class PracticeGuideScene : TutorialScene
{
    internal static readonly string[] Features =
        ["practice-image-copy", "practice-editor-open", "capture-region", "image-arrow", "image-copy", "library-open", "practice-library-find"];

    private static readonly Point EditorOrigin = new(10, 36);
    private static readonly Point LibraryOrigin = new(10, 36);
    private const double GuideScale = 1.46, GuideLeft = 24, GuideTop = 10;
    private const double GalleryScale = 940d / 1093;
    private readonly BitmapSource _picture = PracticePicture.Load();
    private readonly Guid _recordId = Guid.NewGuid();
    private readonly List<OffscreenWindow> _hosts = [];
    private WelcomeWindow? _guide;
    private OffscreenWindow? _guideHost, _editorHost, _galleryHost, _selectorHost;
    private AnnotationEditorWindow? _editor;
    private LibraryFixture? _library;
    private CaptureRecord? _record;
    private CaptureOverlayView? _selector;
    private RegionSelectionEventArgs? _selected;
    private BitmapSource? _frozen, _copied;
    private PointD _selectorCursor, _dragStart, _dragEnd;
    private Rect _sampleBounds;
    private PointD _guideAction;
    private double _guideActionOffset;
    private bool _editorShown, _editorClosed, _libraryShown, _menuShown, _copiedPicture;
    private bool _libraryDoubleClicked;
    private bool _guideActionInvoked;
    private int _guidePressFrames;
    private ContextMenu? _menu;
    private MenuItem? _libraryCommand;
    private Point _menuOrigin;
    private Rect _menuCrop;
    private const double LibraryMenuScale = 2.2;
    private TaskCompletionSource<bool>? _commit;
    private Task<BitmapSource>? _copyRendering;
    private QuickAccessWindow? _receipt;
    private Border? _receiptRoot;

    internal PracticeGuideScene(string id, string language) : base(id, language)
    {
        switch (id)
        {
            case "practice-image-copy":
            case "practice-editor-open":
                PrepareGuide(id == "practice-image-copy" ? PracticeStep.B0 : PracticeStep.A3);
                Story = GuideStory();
                break;
            case "capture-region":
                PrepareGuide(PracticeStep.A2);
                PrepareCapture();
                Story = CaptureStory();
                break;
            case "image-arrow":
            case "image-copy":
                OpenEditor(_picture);
                if (id == "image-copy") AddArrow();
                Story = EditorStory();
                break;
            case "library-open":
                PrepareLibrary();
                Story = LibraryStory();
                break;
            case "practice-library-find":
                PrepareLibrary();
                _libraryShown = true;
                Story = LibraryFindStory();
                break;
            default: throw new NotSupportedException(id);
        }
    }

    internal override string Provenance =>
        "Current WelcomeWindow, CaptureOverlayView, AnnotationEditorWindow, GalleryWindow and GalleryEditorWindow, "
        + "all rendered offscreen with RenderTargetBitmap.Render; every workflow uses Assets/practice-picture.png. "
        + "Guide buttons, selector gestures, editor tool gestures and Ctrl+C use product handlers. "
        + "Clipboard/save completion is a fixture callback, gallery records live below the output folder. "
        + "The library double-click is storyboarded; its editor uses the same loader/constructor as OpenReedit without ShowDialog. "
        + "Loaded and editor-rendered pixels are checked against the exact practice picture. "
        + "Guide camera crops enlarge the actual control surface; the desktop and post-copy note are synthetic.";

    private OffscreenWindow Host(Window window, double width, double height)
    {
        window.Focusable = false;
        window.PreviewGotKeyboardFocus += (_, e) => e.Handled = true;
        if (window is AnnotationEditorWindow editorWindow)
        {
            // The interactive editor activates itself from ContentRendered even when
            // ShowActivated is false. Omit only that input/activation hook for this renderer.
            var method = typeof(AnnotationEditorWindow).GetMethod("OnContentRendered",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly,
                binder: null, types: [typeof(object), typeof(EventArgs)], modifiers: null)
                ?? throw new MissingMethodException("The editor activation hook changed.");
            var activation = (EventHandler)method.CreateDelegate(typeof(EventHandler), editorWindow);
            editorWindow.ContentRendered -= activation;
            editorWindow.Editor.Focusable = false;
        }
        var host = new OffscreenWindow(window, width, height);
        _hosts.Add(host);
        return host;
    }

    private void PrepareGuide(PracticeStep step)
    {
        var monitor = new MonitorInfo("tutorial", new RectD(-24000, -24000, 1920, 1080),
            new RectD(-24000, -24000, 1920, 1080), 96, true);
        _guide = new WelcomeWindow(NewInstallDefaults.Create().Hotkeys, AfterCaptureAction.Editor,
            options: new WelcomeOptions
            {
                ReducedMotion = true, HintDelay = TimeSpan.FromHours(1), MonitorProvider = () => monitor,
                MediaFactory = () => new QuietMedia(), CopyPracticePicture = () => { _copiedPicture = true; return 42; },
            });
        if (step == PracticeStep.B0) _guide.Session.StartGroup(PracticeStep.B0);
        else
        {
            Guid request = _guide.Session.BeginCapture();
            Require(_guide.Notify(new(PracticeEventKind.SelectorReady, _guide.Session.Id, request)), "Selector setup was rejected.");
            if (step == PracticeStep.A3)
                Require(_guide.Notify(new(PracticeEventKind.CaptureSaved, _guide.Session.Id, request, _recordId)), "Capture setup was rejected.");
        }
        _guide.Session.ClearFeedback();
        // Lock the nested preview to the product's picture/step fallback. Otherwise a second
        // generation after installation would render a previous generation inside this clip.
        typeof(WelcomeTutorialPreview).GetProperty("AssetDirectory", System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.NonPublic)!.SetValue(_guide.Preview, Path.Combine(Program.WorkFolder, "preview-placeholder"));
        _guide.Preview.SelectFeature(Id);
        _guide.EditorRequested += (_, _) =>
        {
            OpenEditor(_picture);
            // The offscreen host omits the activation handler. Supply the shell's correlated
            // readiness notification after the real editor has loaded, so RenderStep controls
            // the guide button visibility rather than a renderer-only visual override.
            var target = new PracticeEditorTarget(_guide.Session.Id, _recordId, Guid.NewGuid());
            _editor!.Editor.PracticeTarget = target;
            Require(_guide.Notify(new(PracticeEventKind.EditorReady, target.SessionId,
                RecordId: target.RecordId, EditorId: target.EditorId)), "The guide rejected the loaded editor.");
            Require(_guide.Session.Step == PracticeStep.A4 && _guide.TryNowButton.Visibility == Visibility.Collapsed
                && _guide.ReplayButton.Visibility == Visibility.Visible, "The guide did not hide its external-action button while retaining replay.");
        };
        _guideHost = Host(_guide, 624, 720);
        Stage.Pump(50);
        _guideAction = _guideHost.CenterOf(_guide.TryNowButton);
        _guideActionOffset = Math.Max(0, _guideAction.Y - 260);
    }

    private TutorialStoryboard GuideStory()
    {
        PointD picture = new(312, 214);
        return new TutorialStoryboard(picture)
            .Hold("guide", 1.4)
            .Motion("focus-action", 34, picture, _guideAction)
            .Hold("action-ready", .7)
            .Click("guide-click", 8)
            // Cut to the result only after every pressed frame has shown the original button.
            // Moving away before the state changes also avoids pointing at a reflowed footer.
            .Hold("clicked", .45, new PointD(312, 185))
            .Finish("result", 8.4);
    }

    private void DrawGuide(DrawingContext dc, double offset)
    {
        Stage.DrawDesktop(dc);
        dc.PushClip(new RectangleGeometry(Stage.SceneBounds));
        dc.PushTransform(new TranslateTransform(GuideLeft, GuideTop - offset * GuideScale));
        dc.PushTransform(new ScaleTransform(GuideScale, GuideScale));
        BitmapSource snapshot = _guideHost!.Snapshot();
        dc.DrawImage(snapshot, new Rect(0, 0, snapshot.PixelWidth / Stage.Scale, snapshot.PixelHeight / Stage.Scale));
        dc.Pop(); dc.Pop(); dc.Pop();
    }

    private static PointD GuidePoint(PointD source, double offset) =>
        new(GuideLeft + source.X * GuideScale, GuideTop + (source.Y - offset) * GuideScale);

    private double GuideOffset(TutorialFramePlan frame) => frame.Segment switch
    {
        "focus-action" => _guideActionOffset * Stage.Ease(frame.Progress),
        "action-ready" or "guide-click" => _guideActionOffset,
        _ => 0,
    };

    private void OpenEditor(BitmapSource image)
    {
        if (_editor is not null) { _editorShown = true; return; }
        var region = new RectD(0, 0, image.PixelWidth, image.PixelHeight);
        _editor = new AnnotationEditorWindow(new FrozenFrame(image, region, null, 0), region, image,
            sourceWindowTitle: L("연습 그림", "Practice picture"));
        _editor.CommitRequested = async result =>
        {
            // The product prepares the editing result; the fixture owns persistence/clipboard.
            _commit = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _copyRendering = _editor.Editor.RenderCurrentImageAsync();
            _copied = await _copyRendering;
            return await _commit.Task;
        };
        _editorHost = Host(_editor, 940, 448);
        _editorShown = true;
        Stage.Pump(60);
    }

    private PointD EditorPoint(double x, double y)
    {
        Point local = _editor!.Editor.ViewportPointOf(new PointD(x, y));
        Point actual = _editor.Editor.PointerInputElement.TranslatePoint(local, _editorHost!.ClientRoot);
        return new PointD(EditorOrigin.X + actual.X, EditorOrigin.Y + actual.Y);
    }

    private Point EditorLocal(PointD stage) => _editorHost!.ClientRoot.TranslatePoint(
        new Point(stage.X - EditorOrigin.X, stage.Y - EditorOrigin.Y), _editor!.Editor.PointerInputElement);

    private PointD EditorControl(FrameworkElement control)
    {
        PointD p = _editorHost!.CenterOf(control);
        return new PointD(EditorOrigin.X + p.X, EditorOrigin.Y + p.Y);
    }

    private void AddArrow()
    {
        var editor = _editor!.Editor;
        editor.ToolButton(EditorTool.Arrow).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Stage.Pump(20);
        Point start = editor.ViewportPointOf(new PointD(52, 110)), end = editor.ViewportPointOf(new PointD(182, 40));
        editor.SimulatePointerDown(start);
        editor.SimulatePointerMove(end, leftPressed: true);
        editor.SimulatePointerUp(end);
        Require(editor.CreateCurrentImageSnapshot().Items.Count == 1, "The practice arrow was not committed.");
        Stage.Pump(20);
    }

    private TutorialStoryboard EditorStory()
    {
        var story = new TutorialStoryboard(EditorPoint(220, 126)).Hold("editor", 1.2);
        if (Id == "image-arrow")
        {
            PointD tool = EditorControl(_editor!.Editor.ToolButton(EditorTool.Arrow));
            // Canvas coordinates are resolved again after tool selection changes the property row.
            _dragStart = new PointD(52, 110); _dragEnd = new PointD(182, 40);
            story.Move("tool-move", tool, 20).Click("tool", 8).Hold("tool-ready", .65)
                .Move("arrow-approach", EditorPoint(_dragStart.X, _dragStart.Y), 16)
                .Motion("arrow-drag", 38, _dragStart, _dragEnd, pressed: true)
                .Hold("arrow-result", .7, _dragEnd).Finish("arrow-result", 8.4);
        }
        else
        {
            var copy = _editorHost!.Named<Button>(Ui("EditorFlow_CopyAndClose"));
            story.Move("copy-focus", EditorControl(copy), 20).Hold("copy-key", .55)
                .Hold("copy-wait", .55).Hold("copy-closed", .8)
                .Move("note-focus", new PointD(310, 235), 18).Hold("paste-key", .4).Finish("pasted", 8.4);
        }
        return story;
    }

    private void PrepareCapture()
    {
        _frozen = Stage.Picture(Stage.Width, Stage.SceneHeight, dc => DrawGuide(dc, 0));
        // WelcomeWindow has no exposed element property or automation ID for this picture.
        // Use its control identity instead of localized accessible text; the nested preview
        // can contain the same bitmap, so a visual-tree pixel search is ambiguous.
        Image sample = typeof(WelcomeWindow).GetField("_practiceTable",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(_guide) as Image
            ?? throw new MissingFieldException(nameof(WelcomeWindow), "_practiceTable (Image)");
        Require(_guide!.PracticeTableShown && sample.IsVisible, "The guide's capture practice picture is hidden.");
        Require(sample.Source is BitmapSource, "The guide's capture practice picture has no bitmap.");
        RequireSamePicture((BitmapSource)sample.Source, "The guide's capture practice picture changed.");
        Rect bounds = _guideHost!.BoundsOf(sample);
        double width = Math.Min(bounds.Width, bounds.Height * _picture.PixelWidth / _picture.PixelHeight);
        double height = width * _picture.PixelHeight / _picture.PixelWidth;
        PointD top = GuidePoint(new PointD(bounds.X + (bounds.Width - width) / 2, bounds.Y + (bounds.Height - height) / 2), 0);
        _sampleBounds = new Rect(Math.Round(top.X), Math.Round(top.Y), Math.Round(width * GuideScale), Math.Round(height * GuideScale));
        _dragStart = new PointD(_sampleBounds.X, _sampleBounds.Y);
        _dragEnd = new PointD(_sampleBounds.Right, _sampleBounds.Bottom);
    }

    private TutorialStoryboard CaptureStory() => new TutorialStoryboard(new PointD(820, 350))
        .Hold("capture-start", 1.0).Hold("capture-key", .55).Hold("selector", .65)
        .Move("capture-approach", _dragStart, 20)
        .Motion("capture-drag", 38, _dragStart, _dragEnd, pressed: true)
        .Hold("capture-selected", .4).Finish("capture-result", 8.4);

    private void EnsureSelector()
    {
        if (_selector is not null) return;
        var bounds = new RectD(0, 0, _frozen!.PixelWidth, _frozen.PixelHeight);
        var monitor = new MonitorInfo("tutorial", bounds, bounds, (uint)Math.Round(96 * Stage.Scale), true);
        _selector = new CaptureOverlayView(new FrozenFrame(_frozen, bounds, monitor, 0), showMagnifier: true, showSelectionGrid: true)
        {
            ReadCursor = () => new PointD(_selectorCursor.X * Stage.Scale, _selectorCursor.Y * Stage.Scale),
            PlaceCursor = _ => false, ReadMonitors = () => [monitor], SimulatedPointerInside = true,
            OffersOtherActions = true, ShowModeBar = true, DetectWindows = false,
            Focusable = false,
        };
        _selector.SelectionConfirmed += (_, selected) => _selected = selected;
        _selectorHost = Host(new Window { Content = _selector, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize },
            Stage.Width, Stage.SceneHeight);
        _selector.InitializePointer(); _selector.FlushFeedbackForTest();
    }

    private void DrawCaptureResult(DrawingContext dc)
    {
        Require(_selected is not null, "The practice selection did not confirm.");
        RectD selected = _selected!.BitmapRegion;
        Require(Math.Abs(selected.Width / Stage.Scale - _sampleBounds.Width) <= 2
            && Math.Abs(selected.Height / Stage.Scale - _sampleBounds.Height) <= 2, "The selector missed the practice picture.");
        if (_receipt is null)
        {
            var image = new CroppedBitmap(_frozen!, new Int32Rect((int)Math.Round(selected.X), (int)Math.Round(selected.Y),
                (int)Math.Round(selected.Width), (int)Math.Round(selected.Height)));
            image.Freeze();
            _receipt = new QuickAccessWindow(image, copy: () => Task.FromResult(true), edit: () => { },
                pin: () => Task.CompletedTask, save: () => Task.FromResult(true), library: () => { },
                export: () => Task.FromResult(string.Empty), excludeFromCapture: false);
            // Loaded would register a global shortcut; only lay out and render its client content.
            var content = (UIElement)_receipt.Content; _receipt.Content = null;
            _receiptRoot = new Border { Width = _receipt.Width, Child = content };
            _receiptRoot.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "Text.Primary");
            Detached.Layout(_receiptRoot, _receipt.Width);
        }
        dc.DrawImage(_frozen!, Stage.SceneBounds);
        BitmapSource receipt = OffscreenWindow.SnapshotElement(_receiptRoot!, _receiptRoot!.ActualWidth,
            _receiptRoot.ActualHeight, opaque: false);
        double left = Math.Min(Stage.Width - _receiptRoot.ActualWidth - 18, _sampleBounds.Right + 18);
        double top = Math.Max(12, Stage.SceneHeight - _receiptRoot.ActualHeight - 12);
        dc.DrawImage(receipt, new Rect(left, top, _receiptRoot.ActualWidth, _receiptRoot.ActualHeight));
    }

    private void PrepareLibrary()
    {
        _library = new LibraryFixture(Korean);
        byte[] png = ImageCodec.EncodePng(_picture);
        _record = new CaptureRecord
        {
            Id = _recordId,
            Width = _picture.PixelWidth, Height = _picture.PixelHeight, Title = L("연습 그림", "Practice picture"),
            SourceWindowTitle = Ui("Help.Welcome"), CreatedAt = LibraryFixture.Now, UpdatedAt = LibraryFixture.Now,
            TotalBytes = png.Length * 3, OcrText = L("연습 그림", "Practice picture"),
        };
        _library.Queue.Add(_record);
        string folder = _library.Queue.GetDirectory(_record); Directory.CreateDirectory(folder);
        foreach (string name in new[] { CaptureFileNames.Original, CaptureFileNames.Rendered, CaptureFileNames.Thumbnail })
            File.WriteAllBytes(Path.Combine(folder, name), png);
        _library.ViewModel.Refresh();
        _galleryHost = Host(_library.Window, 1093, 521);
        Stage.Pump(100);
        _menu = new ThemedShellPresenter(() => true).CreateMenu(TrayMenu.Entries(library: () => _libraryShown = true));
        Detached.Layout(_menu);
        _libraryCommand = _menu.Items.OfType<MenuItem>().First(item => Equals(item.Header, Ui("Text_8739F6684453")));
        Rect choice = _libraryCommand.TransformToAncestor(_menu).TransformBounds(new Rect(_libraryCommand.RenderSize));
        Require(choice.Height > 0, "The library menu command did not lay out.");
        double height = Math.Min(164, _menu.ActualHeight);
        double top = Math.Clamp(choice.Y + choice.Height / 2 - height / 2, 0, _menu.ActualHeight - height);
        _menuCrop = new Rect(0, top, _menu.ActualWidth, height);
        _menuOrigin = new Point((Stage.Width - _menuCrop.Width * LibraryMenuScale) / 2, 55);
        Require(_menuOrigin.X >= 12 && _menuOrigin.Y + _menuCrop.Height * LibraryMenuScale <= Stage.SceneHeight - 12,
            "The enlarged library menu exceeds the scene.");
    }

    private PointD TilePoint()
    {
        var tile = _library!.ViewModel.FindTile(_record!.Id) ?? throw new InvalidOperationException("Practice tile is missing.");
        ListBoxItem element = _galleryHost!.Find<ListBoxItem>().First(item => item.IsVisible && ReferenceEquals(item.DataContext, tile));
        PointD center = _galleryHost.CenterOf(element);
        return new PointD(LibraryOrigin.X + center.X * GalleryScale, LibraryOrigin.Y + center.Y * GalleryScale);
    }

    private TutorialStoryboard LibraryStory()
    {
        Point item = _libraryCommand!.TranslatePoint(new Point(_libraryCommand.ActualWidth * .45, _libraryCommand.ActualHeight / 2), _menu);
        PointD target = new(_menuOrigin.X + (item.X - _menuCrop.X) * LibraryMenuScale,
            _menuOrigin.Y + (item.Y - _menuCrop.Y) * LibraryMenuScale);
        return new TutorialStoryboard(new PointD(610, 265)).Hold("desktop", .6)
            .Move("tray-approach", Taskbar.TrayIcon, 18).Click("tray", 8).Hold("menu", .55)
            .Move("library-approach", target, 16).Click("library", 8).Hold("library-ready", .65)
            .Move("tile-approach", TilePoint(), 20).Click("tile", 8).Hold("tile-ready", .4)
            .Hold("enter", .45).Finish("library-editor", 8.8);
    }

    private TutorialStoryboard LibraryFindStory() => new TutorialStoryboard(new PointD(820, 350))
        .Hold("library-find-ready", 1.2)
        .Move("library-find-approach", TilePoint(), 24)
        .Hold("library-find-target", 1.0)
        .Click("library-find-first-click", 4)
        .Hold("library-find-click-gap", .08)
        .Click("library-find-second-click", 4)
        .Hold("library-find-editor", .6)
        .Move("library-find-result-approach", new PointD(690, 342), 22)
        .Finish("library-find-result", 8.4);

    private void OpenLibraryEditor()
    {
        if (_editor is not null) return;
        GalleryReeditContext context = _library!.ReeditLoader.TryLoad(_record!, out GalleryReeditLoader.LoadFailure failure)
            ?? throw new InvalidOperationException($"The practice capture did not load: {failure}.");
        Require(context.Record.Id == _recordId, "The library opened a different capture record.");
        RequireSamePicture(context.OriginalBitmap, "The library loader changed the practice picture.");
        _editor = new GalleryEditorWindow(context, _library.Privacy, _record!, () => CaptureRetention.DefaultImageRetentionHours)
        { CommitRequested = _ => Task.FromResult(false) };
        _editor.Editor.SourceFilePath = _library.Queue.GetFilePath(_record!, CaptureFileNames.Original);
        _editorHost = Host(_editor, 940, 448); _editorShown = true;
        Stage.Pump(80);
        RequireSamePicture(LibraryFixture.Wait(_editor.Editor.RenderCurrentImageAsync()),
            "The reopened editor changed the practice picture.");
    }

    private void RequireSamePicture(BitmapSource image, string message)
    {
        Require(image.PixelWidth == _picture.PixelWidth && image.PixelHeight == _picture.PixelHeight, message);
        var expected = new FormatConvertedBitmap(_picture, PixelFormats.Bgra32, null, 0);
        var actual = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        int stride = expected.PixelWidth * 4;
        byte[] expectedPixels = new byte[stride * expected.PixelHeight], actualPixels = new byte[expectedPixels.Length];
        expected.CopyPixels(expectedPixels, stride, 0); actual.CopyPixels(actualPixels, stride, 0);
        Require(expectedPixels.AsSpan().SequenceEqual(actualPixels), message);
    }

    internal override void Draw(DrawingContext dc, TutorialFramePlan frame)
    {
        if (Id is "practice-image-copy" or "practice-editor-open") DrawGuideFrame(dc, frame);
        else if (Id == "capture-region") DrawCaptureFrame(dc, frame);
        else if (Id == "library-open") DrawLibraryFrame(dc, frame);
        else if (Id == "practice-library-find") DrawLibraryFindFrame(dc, frame);
        else DrawEditorFrame(dc, frame);
    }

    private void DrawGuideFrame(DrawingContext dc, TutorialFramePlan frame)
    {
        bool result = frame.Segment is "clicked" or "result";
        string actionLabel = Ui(Id == "practice-editor-open" ? "Journey.A3.Title" : "Journey.B0.Title");
        string actionCaption = L($"‘{actionLabel}’를 누릅니다.", $"Select {actionLabel}.");
        string caption = result ? Ui($"Tutorial.{Id}.Step3")
            : frame.Segment == "guide" ? Ui($"Tutorial.{Id}.Step1") : actionCaption;
        if (result && !_guideActionInvoked)
        {
            Require(!frame.Pressed && _guidePressFrames == Story.Frames.Count(plan => plan.Segment == "guide-click"),
                "The guide action ran before all of its press frames were rendered.");
            _guideActionInvoked = true;
            _guide!.TryNowButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Stage.Pump(50);
            if (Id == "practice-image-copy")
                Require(_copiedPicture && _guide.PracticeCopied && _guide.SuccessShown, "The guide did not show the copy result.");
            else Require(_editorShown, "The guide did not request its editor.");
        }
        double offset = GuideOffset(frame);
        PointD pointer = GuidePoint(frame.Cursor, offset);
        if (result && Id == "practice-editor-open")
        {
            Stage.DrawDesktop(dc); Stage.DrawWindow(dc, _editorHost!.Snapshot(), EditorOrigin, _editor!.Title);
            pointer = new PointD(750, 360);
        }
        else
        {
            RequireGuidePointer(frame, actionLabel, caption);
            DrawGuide(dc, offset);
        }
        PracticeOverlay(dc, frame with { Cursor = pointer }, caption, "");
    }

    private void RequireGuidePointer(TutorialFramePlan frame, string actionLabel, string caption)
    {
        var point = new Point(frame.Cursor.X, frame.Cursor.Y);
        foreach (Button forbidden in new[] { _guide!.SkipStepButton, _guide.SkipButton })
            Require(!forbidden.IsVisible || !_guideHost!.BoundsOf(forbidden).Contains(point),
                "The guide pointer is over Skip or End practice.");
        if (!frame.Pressed) return;

        Button button = _guide.TryNowButton;
        Require(frame.Segment == "guide-click" && !_guideActionInvoked && !_copiedPicture && !_editorShown,
            "The guide changed state during its button press.");
        // CommandButtonPresentation wraps the label in a CommandButtonFace grid on Loaded;
        // Content is then the face, not the string shown to the user. Its second child is the
        // optional Enter hint, so inspect the primary label without mixing in shortcut text.
        TextBlock? primary = button.Content is Grid { Name: "CommandButtonFace", Children.Count: > 0 } face
            ? face.Children[0] as TextBlock : null;
        string? visibleLabel = button.Content as string ?? primary?.Text;
        string contentType = button.Content?.GetType().FullName ?? "<null>";
        string automationName = AutomationProperties.GetName(button);
        string values = $"ID={Id} LANGUAGE={(Korean ? "ko" : "en")} STEP={_guide.Session.Step} "
            + $"IsVisible={button.IsVisible} Visibility={button.Visibility} IsEnabled={button.IsEnabled} "
            + $"ContentType={contentType} ContentEqualsExpected={Equals(button.Content, actionLabel)} "
            + $"PrimaryText='{visibleLabel ?? "<missing>"}' PrimaryIsVisible={primary?.IsVisible.ToString() ?? "n/a"} "
            + $"AutomationName='{automationName}' Expected='{actionLabel}'";
        if (frame.IsFirst) Console.WriteLine("GUIDE_PRESS " + values);
        Require(button.IsVisible, "The pressed guide button is not visible. " + values);
        Require(button.IsEnabled, "The pressed guide button is disabled. " + values);
        Require(button.Content is string || primary is not null,
            "The pressed guide button has an unsupported primary label structure. " + values);
        Require(primary is null || primary.IsVisible,
            "The pressed guide button's primary text is hidden. " + values);
        Require(string.Equals(visibleLabel, actionLabel, StringComparison.Ordinal),
            "The pressed guide button's visible text does not match its caption. " + values);
        Require(string.Equals(automationName, actionLabel, StringComparison.Ordinal),
            "The pressed guide button's automation name does not match its caption. " + values);
        Require(_guideHost!.BoundsOf(button).Contains(point), "The guide press missed the action button.");
        DependencyObject? hit = VisualTreeHelper.HitTest(_guideHost.ClientRoot, point)?.VisualHit;
        while (hit is not null && hit is not Button) hit = VisualTreeHelper.GetParent(hit);
        Require(ReferenceEquals(hit, button), "A different control covers the guide's pressed action button.");
        Require(caption == L($"‘{actionLabel}’를 누릅니다.", $"Select {actionLabel}."),
            "The guide press caption does not name the visible button.");
        _guidePressFrames++;
    }

    private void DrawCaptureFrame(DrawingContext dc, TutorialFramePlan frame)
    {
        _selectorCursor = frame.Cursor;
        bool selector = frame.Segment is "selector" or "capture-approach" or "capture-drag" or "capture-selected";
        if (selector)
        {
            EnsureSelector();
            if (frame.Segment == "capture-drag")
            {
                if (frame.IsFirst) { _selectorCursor = _dragStart; _selector!.SimulatePointerMove(); _selector.SimulatePointerDown(); _selectorCursor = frame.Cursor; }
                _selector!.SimulatePointerMove();
                if (frame.IsLast)
                {
                    _selector.SimulatePointerUp();
                    // PointerReleased queues ConfirmSelection at DispatcherPriority.Input.
                    // A held frame itself does not pump the dispatcher.
                    Stage.Pump(40);
                    Require(_selected is not null, "The selector did not publish its completed drag.");
                }
            }
            else _selector!.SimulatePointerMove();
            _selector!.FlushFeedbackForTest();
            dc.DrawImage(_selectorHost!.Snapshot(), Stage.SceneBounds);
        }
        else if (frame.Segment == "capture-result") DrawCaptureResult(dc);
        else dc.DrawImage(_frozen!, Stage.SceneBounds);
        string caption = frame.Segment == "capture-result" ? Ui("Journey.A2.Success")
            : frame.Segment is "capture-start" or "capture-key" ? Ui("Journey.A1.Title") : Ui("Journey.A2.Title");
        string key = frame.Segment == "capture-key" ? NewInstallDefaults.Create().Hotkeys.Capture.ToString() : "";
        PracticeOverlay(dc, frame, caption, key, selector ? Stage.PointerKind.Cross : Stage.PointerKind.Arrow);
    }

    private void DrawEditorFrame(DrawingContext dc, TutorialFramePlan frame)
    {
        PointD pointer = frame.Cursor;
        if (Id == "image-arrow")
        {
            if (frame.Segment == "tool" && frame.IsLast)
                _editor!.Editor.ToolButton(EditorTool.Arrow).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            if (frame.Segment == "arrow-approach") pointer = Stage.Lerp(
                EditorControl(_editor!.Editor.ToolButton(EditorTool.Arrow)),
                EditorPoint(_dragStart.X, _dragStart.Y), Stage.Ease(frame.Progress));
            if (frame.Segment is "arrow-drag" or "arrow-result") pointer = EditorPoint(frame.Cursor.X, frame.Cursor.Y);
            if (frame.Segment == "arrow-drag")
            {
                var editor = _editor!.Editor;
                if (frame.IsFirst) editor.SimulatePointerDown(editor.ViewportPointOf(_dragStart));
                editor.SimulatePointerMove(EditorLocal(pointer), leftPressed: true);
                if (frame.IsLast)
                {
                    editor.SimulatePointerUp(EditorLocal(pointer));
                    Require(editor.CreateCurrentImageSnapshot().Items.Count == 1, "The drawn arrow was not committed.");
                }
            }
        }
        else
        {
            if (frame.Segment == "copy-key" && _commit is null)
            {
                ShortcutDefinition shortcut = ShortcutCatalog.Get(ShortcutScope.Annotation, "Copy");
                Require(_editor!.Editor.HandleShortcut(shortcut.Key, shortcut.Modifiers), "The editor rejected Copy.");
                Stage.Pump(40); Require(_commit is not null, "The Copy command did not request a commit.");
            }
            if (frame.Segment == "copy-closed" && !_editorClosed)
            {
                Require(_copyRendering is not null, "The Copy command did not start its image renderer.");
                LibraryFixture.Wait(_copyRendering!);
                _commit!.TrySetResult(true); Stage.Pump(80);
                _editorClosed = !_editor!.IsVisible;
                Require(_editorClosed, "The editor did not close after successful copy.");
            }
        }
        Stage.Pump(10); Stage.DrawDesktop(dc);
        if (!_editorClosed) Stage.DrawWindow(dc, _editorHost!.Snapshot(), EditorOrigin, _editor!.Title);
        else DrawNote(dc, frame.Segment == "pasted");
        string caption, key = "";
        if (Id == "image-arrow")
        {
            caption = frame.Segment == "editor" ? Ui("Journey.A3.Success")
                : frame.Segment == "arrow-result" ? Ui("Journey.A5.Success")
                : frame.Segment is "arrow-approach" or "arrow-drag" ? Ui("Journey.A5.Title") : Ui("Journey.A4.Title");
        }
        else
        {
            caption = frame.Segment is "copy-key" or "copy-wait" ? Ui("EditorFlow_CopyAndClose")
                : frame.Segment is "copy-closed" or "note-focus" ? Ui("Journey.A6.Success")
                : frame.Segment is "paste-key" or "pasted" ? L("그림 붙여넣기", "Paste the picture") : Ui("Journey.A6.Title");
            key = frame.Segment is "copy-key" or "copy-wait" ? Hint(ShortcutScope.Annotation, "Copy")
                : frame.Segment is "paste-key" or "pasted" ? "Ctrl+V" : "";
        }
        PracticeOverlay(dc, frame with { Cursor = pointer }, caption, key);
    }

    private void DrawNote(DrawingContext dc, bool pasted)
    {
        var client = new Rect(140, 78, 680, 350); Stage.DrawWindowFrame(dc, client, L("메모", "Notes"));
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xFA, 0xFA, 0xF7)), null, client);
        Stage.Text(dc, L("연습 그림", "Practice picture"), 170, 101, 22, Brushes.Black);
        if (pasted) dc.DrawImage(_copied!, new Rect(170, 150, 360, 210));
        else dc.DrawLine(new Pen(Brushes.Black, 1.2), new Point(170, 160), new Point(170, 184));
    }

    private void DrawLibraryFrame(DrawingContext dc, TutorialFramePlan frame)
    {
        if (frame.Segment == "tray" && frame.IsLast) _menuShown = true;
        if (frame.Segment == "library" && frame.IsLast)
        {
            _libraryCommand!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Require(_libraryShown, "The tray command did not open the library."); _menuShown = false;
        }
        if (frame.Segment == "tile" && frame.IsLast) _library!.ViewModel.Select(_record!.Id);
        if (frame.Segment == "library-editor") OpenLibraryEditor();
        Stage.DrawDesktop(dc);
        if (_libraryShown) Stage.DrawWindow(dc, _galleryHost!.Snapshot(GalleryScale), LibraryOrigin, _library!.Window.Title);
        else Taskbar.Draw(dc, Korean, LibraryFixture.Now, _menuShown);
        if (_menuShown)
        {
            bool highlight = frame.Segment == "library" || frame.Segment == "library-approach" && frame.Progress > .8;
            typeof(MenuItem).GetProperty(nameof(MenuItem.IsHighlighted))!.SetValue(_libraryCommand, highlight);
            _menu!.UpdateLayout();
            // Render at the enlarged resolution, then crop around the real command. The
            // pointer target above uses this same source-to-scene transform.
            BitmapSource menu = OffscreenWindow.SnapshotElement(_menu, _menu.ActualWidth, _menu.ActualHeight,
                LibraryMenuScale, opaque: false);
            var target = new Rect(_menuOrigin, new Size(_menuCrop.Width * LibraryMenuScale, _menuCrop.Height * LibraryMenuScale));
            dc.PushClip(new RectangleGeometry(target));
            dc.DrawImage(menu, new Rect(_menuOrigin.X - _menuCrop.X * LibraryMenuScale,
                _menuOrigin.Y - _menuCrop.Y * LibraryMenuScale, _menu.ActualWidth * LibraryMenuScale,
                _menu.ActualHeight * LibraryMenuScale));
            dc.Pop();
        }
        if (_editorShown) Stage.DrawWindow(dc, _editorHost!.Snapshot(), EditorOrigin, _editor!.Title);
        string caption = _editorShown ? _editor!.Title : frame.Segment == "enter" ? Ui("Journey.A3.Title")
            : _libraryShown ? Ui("Journey.C2.Title") : Ui("Journey.C1.Title");
        PracticeOverlay(dc, frame, caption, frame.Segment == "enter" ? "Enter" : "");
    }

    private void DrawLibraryFindFrame(DrawingContext dc, TutorialFramePlan frame)
    {
        if (frame.Pressed)
        {
            Require(frame.Segment is "library-find-first-click" or "library-find-second-click" && !_editorShown,
                "The library click target changed before the press finished.");
            PointD target = TilePoint();
            Require(Math.Abs(frame.Cursor.X - target.X) < .5 && Math.Abs(frame.Cursor.Y - target.Y) < .5,
                "The library press missed the practice picture named by its caption.");
        }
        if (frame.Segment == "library-find-first-click" && frame.IsLast)
            _library!.ViewModel.Select(_record!.Id);
        if (frame.Segment == "library-find-second-click" && frame.IsLast) _libraryDoubleClicked = true;
        bool result = frame.Segment is "library-find-editor" or "library-find-result-approach" or "library-find-result";
        if (result)
        {
            Require(_libraryDoubleClicked, "The library editor appeared before the double-click.");
            OpenLibraryEditor();
        }
        Stage.DrawDesktop(dc);
        Stage.DrawWindow(dc, _galleryHost!.Snapshot(GalleryScale), LibraryOrigin, _library!.Window.Title);
        if (result) Stage.DrawWindow(dc, _editorHost!.Snapshot(), EditorOrigin, _editor!.Title);
        int step = result ? 3 : frame.Segment == "library-find-ready" ? 1 : 2;
        PracticeOverlay(dc, frame, Ui($"Tutorial.practice-library-find.Step{step}"), "");
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    public override void Dispose()
    {
        _commit?.TrySetResult(false);
        _selector?.ReleaseResources();
        _receipt?.Close();
        _library?.Dispose();
        foreach (OffscreenWindow host in Enumerable.Reverse(_hosts)) host.Dispose();
    }

    private sealed class QuietMedia : ITutorialMedia
    {
        public UIElement View { get; } = new Border { Visibility = Visibility.Collapsed };
        public event Action? Opened { add { } remove { } }
        public event Action<Exception?>? Failed { add { } remove { } }
        public event Action? Ended { add { } remove { } }
        public void Open(string path) { }
        public void Play() { }
        public void Pause() { }
        public void Rewind() { }
        public void Close() { }
        public void Dispose() { }
    }
}
