using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.App.Editing;
using MyCapture.Core.Annotations;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using MyCapture.Platform.Imaging;
using Xunit;
using static MyCapture.App.Tests.EditorKeysTests;

namespace MyCapture.App.Tests;

/// <summary>
/// The reusable stamp: one command places a registered logo as an ordinary image annotation at
/// the remembered placement, registering a copy of the picture the first time, and remembers
/// where the user left it.
/// </summary>
public sealed class EditorStampTests
{
    // Canvas 640x360 (EditorWindowHost default); a 400x200 logo is 15 % of 640 = 96 wide, 48 high.
    private static readonly RectD DefaultRect = StampLayout.Place(
        StampAnchor.BottomRight, 16, 16, 0.15, 640, 360, 400, 200);

    // ---- Placing ---------------------------------------------------------------------

    [Fact]
    public void PlacingAddsOneImageAtTheBottomRightWithTheRememberedWidthAndUndoRemovesIt() => StaTestHost.Run(() =>
    {
        using var context = new DispatcherContextScope();
        string root = OwnedTestDirectory.Create("mc-stamp-place-");
        try
        {
            using var stamp = new StampScope(Path.Combine(root, "stamp"));
            stamp.Register(root, 400, 200);
            using var host = EditorWindowHost.Create();
            AnnotationEditorControl editor = host.Editor;
            AnnotationEditorController controller = Controller(editor);

            Run(editor.PlaceStampAsync());

            Assert.Equal(new RectD(528, 296, 96, 48), DefaultRect);
            ImageAnnotation item = Assert.IsType<ImageAnnotation>(Assert.Single(controller.Document.Items));
            Assert.Equal(DefaultRect, item.Rect);
            Assert.Equal(400, item.SourceWidth);
            Assert.Equal(200, item.SourceHeight);
            Assert.Equal(1.0, item.Opacity);
            Assert.Equal(0, item.RotationDegrees);
            Assert.Same(item, controller.Selected);
            Assert.Equal(EditorTool.Select, controller.Tool);
            Assert.Equal(1, controller.Undo.UndoCount);
            Assert.Equal([item.Id], editor.PlacedStampIds);
            Assert.False(editor.IsEditingOperationInProgress);

            Assert.True(controller.PerformUndo());
            Assert.Empty(controller.Document.Items);
            Assert.True(controller.PerformRedo());
            Assert.Same(item, Assert.Single(controller.Document.Items));
        }
        finally { OwnedTestDirectory.Delete(root); }
    });

    [Fact]
    public void ThePlacedStampIsAnOrdinaryImageThatMovesResizesAndDeletes() => StaTestHost.Run(() =>
    {
        using var context = new DispatcherContextScope();
        string root = OwnedTestDirectory.Create("mc-stamp-ordinary-");
        try
        {
            using var stamp = new StampScope(Path.Combine(root, "stamp"));
            stamp.Register(root, 400, 200);
            using var host = EditorWindowHost.Create();
            AnnotationEditorController controller = Controller(host.Editor);
            Run(host.Editor.PlaceStampAsync());
            ImageAnnotation item = Assert.IsType<ImageAnnotation>(controller.Selected);

            Assert.True(item.SupportsResize);
            controller.NudgeSelected(-10, 0);
            Assert.Equal(DefaultRect.X - 10, item.Rect.X, 6);
            item.SetBounds(new RectD(item.Rect.X, item.Rect.Y, 120, 60));
            Assert.Equal(120, item.Rect.Width, 6);

            host.Editor.ShortcutActions["Delete"]();
            Assert.Empty(controller.Document.Items);
            Assert.True(controller.PerformUndo());
            Assert.Same(item, Assert.Single(controller.Document.Items));
        }
        finally { OwnedTestDirectory.Delete(root); }
    });

    [Fact]
    public void ARotatedCanvasGetsTheStampAtTheCornerOfTheRotatedCanvas() => StaTestHost.Run(() =>
    {
        using var context = new DispatcherContextScope();
        string root = OwnedTestDirectory.Create("mc-stamp-rotated-");
        try
        {
            using var stamp = new StampScope(Path.Combine(root, "stamp"));
            stamp.Register(root, 400, 200);
            using var host = EditorWindowHost.Create();
            AnnotationEditorControl editor = host.Editor;
            Assert.True(Press(editor, Key.W, ime: false));
            Assert.Equal(1, Controller(editor).RotationTurns);

            Run(editor.PlaceStampAsync());

            // 360 wide, 640 high after a quarter turn: 15 % of 360 is 54.
            ImageAnnotation item = Assert.IsType<ImageAnnotation>(Assert.Single(Controller(editor).Document.Items));
            Assert.Equal(StampLayout.Place(StampAnchor.BottomRight, 16, 16, 0.15, 360, 640, 400, 200), item.Rect);
            Assert.Equal(54, item.Rect.Width, 6);
            Assert.Equal(0, item.RotationDegrees);
        }
        finally { OwnedTestDirectory.Delete(root); }
    });

    [Fact]
    public void ARememberedPlacementAppliesToTheNextCaptureOfAnotherSize() => StaTestHost.Run(() =>
    {
        using var context = new DispatcherContextScope();
        string root = OwnedTestDirectory.Create("mc-stamp-sizes-");
        try
        {
            var defaults = new AnnotationDefaults
            {
                Stamp = new StampSettings
                {
                    Anchor = StampAnchor.TopLeft, MarginX = 28, MarginY = 20, WidthFraction = 0.25, OpacityPercent = 70,
                },
            };
            using var stamp = new StampScope(Path.Combine(root, "stamp"), defaults);
            stamp.Register(root, 400, 200);
            using var host = EditorWindowHost.Create(EditorKeysTests.Solid(1280, 720, 0x30));

            Run(host.Editor.PlaceStampAsync());

            ImageAnnotation item = Assert.IsType<ImageAnnotation>(Assert.Single(Controller(host.Editor).Document.Items));
            // 25 % of 1280 is 320, below the logo's own 400 pixels, so the fraction decides.
            Assert.Equal(new RectD(28, 20, 320, 160), item.Rect);
            Assert.Equal(0.7, item.Opacity, 6);
        }
        finally { OwnedTestDirectory.Delete(root); }
    });

    // ---- Registering -----------------------------------------------------------------

    [Fact]
    public void CropThenStampThenCloseWaitsOnlyForThePicture() => StaTestHost.Run(() =>
    {
        using var context = new DispatcherContextScope();
        string root = OwnedTestDirectory.Create("mc-stamp-crop-close-");
        try
        {
            using var stamp = new StampScope(Path.Combine(root, "stamp"));
            stamp.Register(root, 400, 200);
            using var host = EditorWindowHost.Create();
            AnnotationEditorControl editor = host.Editor;
            AnnotationEditorController controller = Controller(editor);
            AnnotationImageStore.DecodedFile decoded = AnnotationImageStore.Decode(StampStore.PathIn(stamp.Folder))!;
            var gate = new TaskCompletionSource<AnnotationImageStore.DecodedFile?>(TaskCreationOptions.RunContinuationsAsynchronously);
            editor.DecodeImageForInsert = _ => gate.Task;
            int prompts = 0;
            int picturesWhenAsked = -1;
            host.Window.ConfirmClose = () =>
            {
                prompts++;
                picturesWhenAsked = controller.Document.Items.OfType<ImageAnnotation>().Count();
                return EditorCloseChoice.DontSave;
            };

            try
            {
                Assert.True(Press(editor, Key.X, ime: false));
                Assert.Equal(EditorTool.Crop, controller.Tool);
                Assert.True(Press(editor, Key.S, ime: false));
                Assert.Equal(EditorTool.Select, controller.Tool);
                Assert.True(editor.IsOnlyReadingPictures);
                host.Window.RequestClose();
                Assert.True(host.Window.IsVisible);
                Assert.Equal(0, prompts);

                gate.SetResult(decoded);
                PumpUntil(() => !host.Window.IsVisible);

                Assert.False(editor.IsEditingOperationInProgress);
                Assert.Equal(1, prompts);
                Assert.Equal(1, picturesWhenAsked);
                Assert.Equal(DefaultRect, Assert.IsType<ImageAnnotation>(Assert.Single(controller.Document.Items)).Rect);
            }
            finally
            {
                // Release the held operation even when a regression fails an assertion.
                if (controller.Tool == EditorTool.Crop) editor.ShortcutActions["Select"]();
                gate.TrySetResult(decoded);
                Run(editor.ImageInsertsSettledAsync());
            }
        }
        finally { OwnedTestDirectory.Delete(root); }
    });

    [Fact]
    public void FirstUseAsksForAPictureStoresACopyAndPlacesItThenNeverAsksAgain() => StaTestHost.Run(() =>
    {
        using var context = new DispatcherContextScope();
        string root = OwnedTestDirectory.Create("mc-stamp-first-");
        try
        {
            string stampFolder = Path.Combine(root, "data", "stamp");
            using var stamp = new StampScope(stampFolder);
            string original = Path.Combine(root, "logo.png");
            ImageCodec.SavePng(Solid(400, 200, 0x80), original);
            using var host = EditorWindowHost.Create();
            AnnotationEditorControl editor = host.Editor;
            AnnotationEditorController controller = Controller(editor);
            AnnotationImageStore store = Field<AnnotationImageStore>(editor, "_imageStore");
            int pickerCalls = 0;
            editor.PickStampImagePath = () => { pickerCalls++; return original; };
            Assert.False(File.Exists(Path.Combine(stampFolder, "stamp.png")));

            Run(editor.PlaceStampAsync());

            Assert.Equal(1, pickerCalls);
            Assert.True(File.Exists(Path.Combine(stampFolder, "stamp.png")));
            ImageAnnotation first = Assert.IsType<ImageAnnotation>(Assert.Single(controller.Document.Items));
            Assert.Equal(DefaultRect, first.Rect);
            // The placed picture came from the stored copy, never from the file that was picked.
            Assert.Equal(
                Path.GetFullPath(Path.Combine(stampFolder, "stamp.png")),
                store.SourcesFor([first.AssetFileName])[first.AssetFileName]);

            File.Delete(original);
            Run(editor.PlaceStampAsync());

            Assert.Equal(1, pickerCalls);
            Assert.Equal(2, controller.Document.Items.OfType<ImageAnnotation>().Count());
        }
        finally { OwnedTestDirectory.Delete(root); }
    });

    [Theory]
    [InlineData(3000, 1500, 1024, 512)]
    [InlineData(500, 2000, 256, 1024)]
    [InlineData(1024, 700, 1024, 700)]
    [InlineData(300, 120, 300, 120)]
    public void TheStoredPictureIsCappedToTheLongestSideAndASmallOneIsKept(int width, int height, int storedWidth, int storedHeight) => StaTestHost.Run(() =>
    {
        using var context = new DispatcherContextScope();
        string root = OwnedTestDirectory.Create("mc-stamp-cap-");
        try
        {
            string stampFolder = Path.Combine(root, "stamp");
            using var stamp = new StampScope(stampFolder);
            string original = Path.Combine(root, "logo.png");
            ImageCodec.SavePng(Solid(width, height, 0x80), original);
            using var host = EditorWindowHost.Create();
            host.Editor.PickStampImagePath = () => original;

            Run(host.Editor.PlaceStampAsync());

            BitmapSource stored = ImageCodec.TryLoad(Path.Combine(stampFolder, "stamp.png"))!;
            Assert.Equal(storedWidth, stored.PixelWidth);
            Assert.Equal(storedHeight, stored.PixelHeight);
            ImageAnnotation item = Assert.IsType<ImageAnnotation>(Assert.Single(Controller(host.Editor).Document.Items));
            Assert.Equal(storedWidth, item.SourceWidth);
            Assert.Equal(storedHeight, item.SourceHeight);
            Assert.True(item.Rect.Width <= storedWidth + 1e-9, "the stamp is never drawn larger than its stored picture");
            // Nothing but the one picture is kept in the folder.
            Assert.Equal(new[] { "stamp.png" }, Directory.GetFiles(stampFolder).Select(file => Path.GetFileName(file)!).ToArray());
        }
        finally { OwnedTestDirectory.Delete(root); }
    });

    [Fact]
    public void CancellingThePickerPlacesAndStoresNothing() => StaTestHost.Run(() =>
    {
        using var context = new DispatcherContextScope();
        string root = OwnedTestDirectory.Create("mc-stamp-cancel-");
        try
        {
            string stampFolder = Path.Combine(root, "stamp");
            using var stamp = new StampScope(stampFolder);
            using var host = EditorWindowHost.Create();
            host.Editor.PickStampImagePath = () => null;

            Run(host.Editor.PlaceStampAsync());

            Assert.Empty(Controller(host.Editor).Document.Items);
            Assert.False(Directory.Exists(stampFolder));
            Assert.False(host.Editor.IsEditingOperationInProgress);
        }
        finally { OwnedTestDirectory.Delete(root); }
    });

    [Fact]
    public void AFileThatIsNotAnImageIsReportedAndNothingIsStored() => StaTestHost.Run(() =>
    {
        using var context = new DispatcherContextScope();
        string root = OwnedTestDirectory.Create("mc-stamp-unreadable-");
        try
        {
            string stampFolder = Path.Combine(root, "stamp");
            using var stamp = new StampScope(stampFolder);
            string notAnImage = Path.Combine(root, "logo.png");
            File.WriteAllText(notAnImage, "not an image");
            using var host = EditorWindowHost.Create();
            int reports = 0;
            host.Editor.ReportUnreadableImage = () => reports++;
            host.Editor.PickStampImagePath = () => notAnImage;

            Run(host.Editor.PlaceStampAsync());

            Assert.Equal(1, reports);
            Assert.Empty(Controller(host.Editor).Document.Items);
            Assert.Null(StampStore.RegisteredPath(stampFolder));
        }
        finally { OwnedTestDirectory.Delete(root); }
    });

    [Fact]
    public void ADamagedStoredStampIsForgottenSoTheNextUseAsksForANewPicture() => StaTestHost.Run(() =>
    {
        using var context = new DispatcherContextScope();
        using var language = UiText.UseLanguage("ko-KR");
        string root = OwnedTestDirectory.Create("mc-stamp-damaged-");
        try
        {
            string stampFolder = Path.Combine(root, "stamp");
            using var stamp = new StampScope(stampFolder);
            Directory.CreateDirectory(stampFolder);
            File.WriteAllText(Path.Combine(stampFolder, "stamp.png"), "damaged");
            using var host = EditorWindowHost.Create();
            int reports = 0;
            host.Editor.ReportUnreadableImage = () => reports++;

            Run(host.Editor.PlaceStampAsync());

            Assert.Equal(0, reports);
            Assert.Empty(Controller(host.Editor).Document.Items);
            Assert.Null(StampStore.RegisteredPath(stampFolder));
            Assert.Equal("도장 이미지를 읽을 수 없습니다. 다시 등록해 주십시오.", host.Editor.StatusText);
        }
        finally { OwnedTestDirectory.Delete(root); }
    });

    [Fact]
    public void WithoutAStampFolderTheCommandSaysItIsUnavailable() => StaTestHost.Run(() =>
    {
        using var context = new DispatcherContextScope();
        using var language = UiText.UseLanguage("ko-KR");
        string root = OwnedTestDirectory.Create("mc-stamp-none-");
        try
        {
            using var stamp = new StampScope(Path.Combine(root, "stamp"));
            AnnotationEditorPreferences.StampFolder = null;
            using var host = EditorWindowHost.Create();

            Run(host.Editor.PlaceStampAsync());

            Assert.Empty(Controller(host.Editor).Document.Items);
            Assert.Equal("이 화면에서는 도장을 사용할 수 없습니다.", host.Editor.StatusText);
        }
        finally { OwnedTestDirectory.Delete(root); }
    });

    // ---- Menu ------------------------------------------------------------------------

    [Fact]
    public void TheFirstContextMenuRequestOpensTheMenuAndPopulatesItLazily() => StaTestHost.Run(() =>
    {
        using var context = new DispatcherContextScope();
        string root = OwnedTestDirectory.Create("mc-stamp-first-menu-");
        try
        {
            using var stamp = new StampScope(Path.Combine(root, "stamp"));
            int calls = stamp.FolderCalls;
            using var host = EditorWindowHost.Create();
            ToggleButton button = host.Editor.StampButton;
            Assert.Equal(calls, stamp.FolderCalls);
            Assert.Null(host.Editor.StampMenu);
            Assert.NotNull(button.ContextMenu);
            Assert.Empty(button.ContextMenu.Items);
            bool openingRaised = false;
            button.ContextMenuOpening += (_, _) => openingRaised = true;
            button.Focus();

            // InputManager runs WPF's first-open service; calling EnsureStampMenu or raising
            // Opened ourselves would hide a missing initial ContextMenu on the button.
            InputManager.Current.ProcessInput(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(host.Window), 0, Key.Apps)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent, Source = button });
            InputManager.Current.ProcessInput(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(host.Window), 0, Key.Apps)
                { RoutedEvent = Keyboard.KeyUpEvent, Source = button });

            try
            {
                PumpUntil(() => button.ContextMenu.IsOpen);
                Assert.True(openingRaised);
                Assert.Same(button, button.ContextMenu.PlacementTarget);
                Assert.Same(host.Editor.StampMenu, button.ContextMenu);
                Assert.Equal(4, button.ContextMenu.Items.OfType<MenuItem>().Count());
            }
            finally { button.ContextMenu.IsOpen = false; }
        }
        finally { OwnedTestDirectory.Delete(root); }
    });

    [Theory]
    [InlineData("ko-KR", "도장 찍기", "도장 이미지 바꾸기…", "불투명도", "도장 지우기", "100%", "70%", "50%", "30%")]
    [InlineData("en-US", "Place stamp", "Change stamp image…", "Opacity", "Remove stamp", "100%", "70%", "50%", "30%")]
    public void TheMenuOffersPlaceChangeOpacityAndRemoveInBothLanguages(
        string language, string place, string change, string opacity, string remove, string o100, string o70, string o50, string o30) => StaTestHost.Run(() =>
    {
        using var scope = UiText.UseLanguage(language);
        string root = OwnedTestDirectory.Create("mc-stamp-menu-");
        try
        {
            using var stamp = new StampScope(Path.Combine(root, "stamp"));
            using var host = EditorWindowHost.Create();
            Assert.Null(host.Editor.StampMenu);

            ContextMenu menu = host.Editor.EnsureStampMenu();

            MenuItem[] items = menu.Items.OfType<MenuItem>().ToArray();
            Assert.Equal([place, change, opacity, remove], items.Select(item => item.Header).ToArray());
            Assert.Equal("S", items[0].InputGestureText);
            Assert.Equal([o100, o70, o50, o30], items[2].Items.OfType<MenuItem>().Select(item => (string)item.Header).ToArray());
            Assert.Same(menu, host.Editor.EnsureStampMenu());
        }
        finally { OwnedTestDirectory.Delete(root); }
    });

    [Fact]
    public void TheMenuMarksTheCurrentOpacityAndOnlyOffersRemoveWhileAStampIsRegistered() => StaTestHost.Run(() =>
    {
        string root = OwnedTestDirectory.Create("mc-stamp-menu-state-");
        try
        {
            var defaults = new AnnotationDefaults { Stamp = new StampSettings { OpacityPercent = 50 } };
            using var stamp = new StampScope(Path.Combine(root, "stamp"), defaults);
            using var host = EditorWindowHost.Create();
            ContextMenu menu = host.Editor.EnsureStampMenu();
            MenuItem remove = menu.Items.OfType<MenuItem>().Last();
            MenuItem[] opacities = menu.Items.OfType<MenuItem>().ElementAt(2).Items.OfType<MenuItem>().ToArray();

            menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
            Assert.False(remove.IsEnabled);
            Assert.Equal([false, false, true, false], opacities.Select(item => item.IsChecked).ToArray());

            stamp.Register(root, 100, 50);
            stamp.Defaults.Stamp.OpacityPercent = 100;
            menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
            Assert.True(remove.IsEnabled);
            Assert.Equal([true, false, false, false], opacities.Select(item => item.IsChecked).ToArray());
        }
        finally { OwnedTestDirectory.Delete(root); }
    });

    [Fact]
    public void ChangingTheImageReplacesTheStoredPictureAndPlacesNothing() => StaTestHost.Run(() =>
    {
        using var context = new DispatcherContextScope();
        using var language = UiText.UseLanguage("ko-KR");
        string root = OwnedTestDirectory.Create("mc-stamp-change-");
        try
        {
            string stampFolder = Path.Combine(root, "stamp");
            using var stamp = new StampScope(stampFolder);
            stamp.Register(root, 400, 200);
            string other = Path.Combine(root, "other.png");
            ImageCodec.SavePng(Solid(200, 100, 0x10), other);
            using var host = EditorWindowHost.Create();
            host.Editor.PickStampImagePath = () => other;

            ClickMenu(host.Editor, 1);
            PumpUntil(() => host.Editor.StatusText == "도장 이미지 변경됨" && !host.Editor.IsEditingOperationInProgress);
            Pump(TimeSpan.FromMilliseconds(50));
            Assert.Equal(200, ImageCodec.TryLoad(Path.Combine(stampFolder, "stamp.png"))!.PixelWidth);

            Assert.Empty(Controller(host.Editor).Document.Items);
            Assert.Equal("도장 이미지 변경됨", host.Editor.StatusText);
            // Placing now uses the new picture at the same placement.
            Run(host.Editor.PlaceStampAsync());
            ImageAnnotation item = Assert.IsType<ImageAnnotation>(Assert.Single(Controller(host.Editor).Document.Items));
            Assert.Equal(200, item.SourceWidth);
        }
        finally { OwnedTestDirectory.Delete(root); }
    });

    [Fact]
    public void RemovingDeletesTheCopyKeepsPlacedStampsAndMakesTheNextUseAskAgain() => StaTestHost.Run(() =>
    {
        using var context = new DispatcherContextScope();
        using var language = UiText.UseLanguage("ko-KR");
        string root = OwnedTestDirectory.Create("mc-stamp-remove-");
        try
        {
            string stampFolder = Path.Combine(root, "stamp");
            var defaults = new AnnotationDefaults { Stamp = new StampSettings { Anchor = StampAnchor.TopLeft, OpacityPercent = 30 } };
            using var stamp = new StampScope(stampFolder, defaults);
            stamp.Register(root, 400, 200);
            using var host = EditorWindowHost.Create();
            AnnotationEditorController controller = Controller(host.Editor);
            Run(host.Editor.PlaceStampAsync());
            int pickerCalls = 0;
            host.Editor.PickStampImagePath = () => { pickerCalls++; return null; };

            ClickMenu(host.Editor, 3);

            Assert.Null(StampStore.RegisteredPath(stampFolder));
            Assert.Single(controller.Document.Items);
            Assert.Empty(host.Editor.PlacedStampIds);
            Assert.Equal(StampAnchor.BottomRight, stamp.Defaults.Stamp.Anchor);
            Assert.Equal(100, stamp.Defaults.Stamp.OpacityPercent);
            Assert.Equal("도장 이미지 삭제됨 · 이미 넣은 도장은 그대로 유지됨", host.Editor.StatusText);

            Run(host.Editor.PlaceStampAsync());
            Assert.Equal(1, pickerCalls);
            Assert.Single(controller.Document.Items);
        }
        finally { OwnedTestDirectory.Delete(root); }
    });

    [Fact]
    public void RemoveIsDisabledAndItsHandlerDoesNothingWhileAReplacementIsDecoding() => StaTestHost.Run(() =>
    {
        using var context = new DispatcherContextScope();
        string root = OwnedTestDirectory.Create("mc-stamp-remove-pending-");
        try
        {
            using var stamp = new StampScope(Path.Combine(root, "stamp"), new AnnotationDefaults
            {
                Stamp = new StampSettings { Anchor = StampAnchor.TopLeft, OpacityPercent = 30 },
            });
            stamp.Register(root, 400, 200);
            string other = Path.Combine(root, "other.png");
            ImageCodec.SavePng(Solid(200, 100, 0x10), other);
            AnnotationImageStore.DecodedFile decoded = AnnotationImageStore.Decode(other)!;
            using var host = EditorWindowHost.Create();
            AnnotationEditorControl editor = host.Editor;
            ContextMenu menu = editor.EnsureStampMenu();
            MenuItem remove = menu.Items.OfType<MenuItem>().Last();
            menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
            Assert.True(remove.IsEnabled);
            editor.PickStampImagePath = () => other;
            var gate = new TaskCompletionSource<AnnotationImageStore.DecodedFile?>(TaskCreationOptions.RunContinuationsAsynchronously);
            editor.DecodeImageForInsert = _ => gate.Task;

            Task change = editor.ChangeStampImageAsync();
            try
            {
                Assert.False(remove.IsEnabled);
                menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
                Assert.False(remove.IsEnabled);
                // Bypass the disabled control to verify the handler also rejects stale input.
                remove.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Assert.Equal(400, ImageCodec.TryLoad(StampStore.PathIn(stamp.Folder))!.PixelWidth);
                Assert.Equal(StampAnchor.TopLeft, stamp.Defaults.Stamp.Anchor);
                Assert.Equal(30, stamp.Defaults.Stamp.OpacityPercent);
            }
            finally
            {
                gate.TrySetResult(decoded);
                Run(change);
            }

            Assert.True(remove.IsEnabled);
            Assert.Equal(200, ImageCodec.TryLoad(StampStore.PathIn(stamp.Folder))!.PixelWidth);
            Assert.Equal(StampAnchor.TopLeft, stamp.Defaults.Stamp.Anchor);
            Assert.Empty(Controller(editor).Document.Items);
        }
        finally { OwnedTestDirectory.Delete(root); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedRecyclingKeepsTheRegisteredFileAndSettings(bool unreadable) => StaTestHost.Run(() =>
    {
        using var context = new DispatcherContextScope();
        string root = OwnedTestDirectory.Create("mc-stamp-recycle-failed-");
        try
        {
            using var stamp = new StampScope(Path.Combine(root, "stamp"), new AnnotationDefaults
            {
                Stamp = new StampSettings { Anchor = StampAnchor.TopLeft, MarginX = 28, OpacityPercent = 30 },
            });
            string path = StampStore.PathIn(stamp.Folder);
            if (unreadable)
            {
                Directory.CreateDirectory(stamp.Folder);
                File.WriteAllText(path, "damaged");
            }
            else stamp.Register(root, 400, 200);
            byte[] before = File.ReadAllBytes(path);
            using var host = EditorWindowHost.Create();
            // Permit decoding but prevent a move to the Recycle Bin. No destructive fallback
            // may remove this file, and a failed removal must not reset the stamp preferences.
            using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (unreadable) Run(host.Editor.PlaceStampAsync());
                else ClickMenu(host.Editor, 3);

                Assert.Equal(path, StampStore.RegisteredPath(stamp.Folder));
                Assert.Equal(before, File.ReadAllBytes(path));
                Assert.Equal(StampAnchor.TopLeft, stamp.Defaults.Stamp.Anchor);
                Assert.Equal(28, stamp.Defaults.Stamp.MarginX);
                Assert.Equal(30, stamp.Defaults.Stamp.OpacityPercent);
            }
        }
        finally { OwnedTestDirectory.Delete(root); }
    });

    // ---- Opacity ---------------------------------------------------------------------

    [Fact]
    public void OpacityAppliesToTheSelectedStampAsOneUndoStepAndToTheStampsThatFollow() => StaTestHost.Run(() =>
    {
        using var context = new DispatcherContextScope();
        string root = OwnedTestDirectory.Create("mc-stamp-opacity-");
        try
        {
            using var stamp = new StampScope(Path.Combine(root, "stamp"));
            stamp.Register(root, 400, 200);
            using var host = EditorWindowHost.Create();
            AnnotationEditorController controller = Controller(host.Editor);
            Run(host.Editor.PlaceStampAsync());
            ImageAnnotation first = Assert.IsType<ImageAnnotation>(controller.Selected);
            int undoBefore = controller.Undo.UndoCount;

            ClickMenu(host.Editor, 2, subItem: 2);

            Assert.Equal(0.5, first.Opacity, 6);
            Assert.Equal(50, stamp.Defaults.Stamp.OpacityPercent);
            Assert.Equal(undoBefore + 1, controller.Undo.UndoCount);
            Assert.True(controller.PerformUndo());
            Assert.Equal(1.0, first.Opacity, 6);
            Assert.True(controller.PerformRedo());
            Assert.Equal(0.5, first.Opacity, 6);

            Run(host.Editor.PlaceStampAsync());
            ImageAnnotation second = controller.Document.Items.OfType<ImageAnnotation>().Last();
            Assert.NotSame(first, second);
            Assert.Equal(0.5, second.Opacity, 6);
        }
        finally { OwnedTestDirectory.Delete(root); }
    });

    [Fact]
    public void OpacityOfAPlainInsertedImageIsNotTouchedByTheStampMenu() => StaTestHost.Run(() =>
    {
        using var context = new DispatcherContextScope();
        string root = OwnedTestDirectory.Create("mc-stamp-opacity-plain-");
        try
        {
            using var stamp = new StampScope(Path.Combine(root, "stamp"));
            stamp.Register(root, 400, 200);
            string plain = Path.Combine(root, "plain.png");
            ImageCodec.SavePng(Solid(100, 100, 0x40), plain);
            using var host = EditorWindowHost.Create();
            AnnotationEditorController controller = Controller(host.Editor);
            Run(host.Editor.InsertImageFileAsync(plain, new PointD(100, 100)));
            ImageAnnotation image = Assert.IsType<ImageAnnotation>(controller.Selected);

            ClickMenu(host.Editor, 2, subItem: 3);

            Assert.Equal(1.0, image.Opacity);
            Assert.Equal(30, stamp.Defaults.Stamp.OpacityPercent);
        }
        finally { OwnedTestDirectory.Delete(root); }
    });

    [Fact]
    public void TheSavedAndExportedImageHonoursTheStampsOpacity() => StaTestHost.Run(() =>
    {
        using var context = new DispatcherContextScope();
        string root = OwnedTestDirectory.Create("mc-stamp-export-");
        try
        {
            var defaults = new AnnotationDefaults { Stamp = new StampSettings { OpacityPercent = 50 } };
            using var stamp = new StampScope(Path.Combine(root, "stamp"), defaults);
            stamp.Register(root, 400, 200, value: 0x00);
            using var host = EditorWindowHost.Create(Solid(640, 360, 0xFF));
            AnnotationEditorControl editor = host.Editor;
            AnnotationEditingResult? result = null;
            editor.CommitRequested = r => { result = r; return Task.FromResult(true); };
            Run(editor.PlaceStampAsync());

            editor.ShortcutActions["Done"]();

            Assert.NotNull(result);
            ImageAnnotation saved = Assert.IsType<ImageAnnotation>(Assert.Single(result!.Document.Items));
            Assert.Equal(0.5, saved.Opacity, 6);
            Assert.Contains("\"opacity\":0.5", result.Document.ToJson(), StringComparison.Ordinal);
            Assert.True(result.ImageAssetBitmaps.ContainsKey(saved.AssetFileName));

            var renderer = new AnnotationRenderer(
                AnnotationImageStore.FromDecoded(result.ImageAssetBitmaps), new AnnotationBlurCache(Dispatcher.CurrentDispatcher));
            BitmapSource flat = AnnotationFlattener.Flatten(result.SelectedBitmap, result.Document, renderer);
            byte[] inside = new byte[4];
            flat.CopyPixels(new Int32Rect(576, 320, 1, 1), inside, 4, 0);
            byte[] outside = new byte[4];
            flat.CopyPixels(new Int32Rect(100, 100, 1, 1), outside, 4, 0);
            // A black logo at 50 % over white is mid grey; the rest of the capture is untouched white.
            Assert.InRange(inside[0], 120, 135);
            Assert.Equal(0xFF, outside[0]);
        }
        finally { OwnedTestDirectory.Delete(root); }
    });

    // ---- Remembering -----------------------------------------------------------------

    [Fact]
    public void AMovedStampIsRememberedWhenTheEditorClosesAndTheNextStampLandsThere() => StaTestHost.Run(() =>
    {
        using var context = new DispatcherContextScope();
        string root = OwnedTestDirectory.Create("mc-stamp-remember-");
        try
        {
            using var stamp = new StampScope(Path.Combine(root, "stamp"));
            stamp.Register(root, 400, 200);
            using (var host = EditorWindowHost.Create())
            {
                Run(host.Editor.PlaceStampAsync());
                // To the top-left: 500 left and 280 up (Shift+arrow moves ten pixels).
                for (int step = 0; step < 50; step++) host.Editor.ShortcutActions["LeftFast"]();
                for (int step = 0; step < 28; step++) host.Editor.ShortcutActions["UpFast"]();
                ImageAnnotation moved = Assert.IsType<ImageAnnotation>(Controller(host.Editor).Selected);
                Assert.Equal(new RectD(28, 16, 96, 48), moved.Rect);
                // Nothing is written until the editor saves or closes.
                Assert.Equal(StampAnchor.BottomRight, stamp.Defaults.Stamp.Anchor);
            }
            // The editor reports its final state when it unloads, which the dispatcher runs after Close.
            Pump(TimeSpan.FromMilliseconds(50));

            Assert.Equal(StampAnchor.TopLeft, stamp.Defaults.Stamp.Anchor);
            Assert.Equal(28, stamp.Defaults.Stamp.MarginX);
            Assert.Equal(16, stamp.Defaults.Stamp.MarginY);
            Assert.Equal(0.15, stamp.Defaults.Stamp.WidthFraction, 6);

            using var next = EditorWindowHost.Create(Solid(1280, 720, 0x30));
            Run(next.Editor.PlaceStampAsync());
            ImageAnnotation placed = Assert.IsType<ImageAnnotation>(Assert.Single(Controller(next.Editor).Document.Items));
            Assert.Equal(new RectD(28, 16, 192, 96), placed.Rect);
        }
        finally { OwnedTestDirectory.Delete(root); }
    });

    [Fact]
    public void AResizedStampIsRememberedByItsWidthOnTheCanvasWhenTheEditorSaves() => StaTestHost.Run(() =>
    {
        using var context = new DispatcherContextScope();
        string root = OwnedTestDirectory.Create("mc-stamp-save-");
        try
        {
            using var stamp = new StampScope(Path.Combine(root, "stamp"));
            stamp.Register(root, 400, 200);
            using var host = EditorWindowHost.Create();
            host.Editor.CommitRequested = _ => Task.FromResult(true);
            Run(host.Editor.PlaceStampAsync());
            ImageAnnotation item = Assert.IsType<ImageAnnotation>(Controller(host.Editor).Selected);
            // Twice as wide, still touching the bottom-right margins.
            item.SetBounds(new RectD(640 - 16 - 192, 360 - 16 - 96, 192, 96));

            host.Editor.ShortcutActions["Done"]();

            Assert.Equal(StampAnchor.BottomRight, stamp.Defaults.Stamp.Anchor);
            Assert.Equal(16, stamp.Defaults.Stamp.MarginX);
            Assert.Equal(16, stamp.Defaults.Stamp.MarginY);
            Assert.Equal(0.3, stamp.Defaults.Stamp.WidthFraction, 6);
        }
        finally { OwnedTestDirectory.Delete(root); }
    });

    [Fact]
    public void AStampLeftWhereItWasPlacedChangesNothing() => StaTestHost.Run(() =>
    {
        using var context = new DispatcherContextScope();
        string root = OwnedTestDirectory.Create("mc-stamp-untouched-");
        try
        {
            var defaults = new AnnotationDefaults { Stamp = new StampSettings { WidthFraction = 0.1234 } };
            using var stamp = new StampScope(Path.Combine(root, "stamp"), defaults);
            stamp.Register(root, 40, 20);
            using (var host = EditorWindowHost.Create())
            {
                Run(host.Editor.PlaceStampAsync());
                // The 40-pixel logo is not scaled up to the 12 % the setting asks for.
                Assert.Equal(40, Assert.IsType<ImageAnnotation>(Controller(host.Editor).Selected).Rect.Width, 6);
            }
            // The editor reports its final state when it unloads, which the dispatcher runs after Close.
            Pump(TimeSpan.FromMilliseconds(50));

            Assert.Equal(0.1234, stamp.Defaults.Stamp.WidthFraction, 6);
            Assert.Equal(StampAnchor.BottomRight, stamp.Defaults.Stamp.Anchor);
        }
        finally { OwnedTestDirectory.Delete(root); }
    });

    [Fact]
    public void OnlyStampsFromTheCommandAreRememberedNotOtherPictures() => StaTestHost.Run(() =>
    {
        using var context = new DispatcherContextScope();
        string root = OwnedTestDirectory.Create("mc-stamp-other-");
        try
        {
            using var stamp = new StampScope(Path.Combine(root, "stamp"));
            stamp.Register(root, 400, 200);
            string plain = Path.Combine(root, "plain.png");
            ImageCodec.SavePng(Solid(100, 100, 0x40), plain);
            using (var host = EditorWindowHost.Create())
            {
                Run(host.Editor.InsertImageFileAsync(plain, new PointD(100, 100)));
                for (int step = 0; step < 5; step++) host.Editor.ShortcutActions["LeftFast"]();
            }
            // The editor reports its final state when it unloads, which the dispatcher runs after Close.
            Pump(TimeSpan.FromMilliseconds(50));

            Assert.Equal(StampAnchor.BottomRight, stamp.Defaults.Stamp.Anchor);
            Assert.Equal(0.15, stamp.Defaults.Stamp.WidthFraction, 6);
        }
        finally { OwnedTestDirectory.Delete(root); }
    });

    [Fact]
    public void AStampWhoseCanvasWasRotatedAfterwardsIsNotRemembered() => StaTestHost.Run(() =>
    {
        using var context = new DispatcherContextScope();
        string root = OwnedTestDirectory.Create("mc-stamp-turned-");
        try
        {
            using var stamp = new StampScope(Path.Combine(root, "stamp"));
            stamp.Register(root, 400, 200);
            using (var host = EditorWindowHost.Create())
            {
                Run(host.Editor.PlaceStampAsync());
                for (int step = 0; step < 5; step++) host.Editor.ShortcutActions["LeftFast"]();
                Assert.True(Press(host.Editor, Key.W, ime: false));
            }
            // The editor reports its final state when it unloads, which the dispatcher runs after Close.
            Pump(TimeSpan.FromMilliseconds(50));

            Assert.Equal(StampAnchor.BottomRight, stamp.Defaults.Stamp.Anchor);
            Assert.Equal(16, stamp.Defaults.Stamp.MarginX);
        }
        finally { OwnedTestDirectory.Delete(root); }
    });

    [Fact]
    public void AnUndoneStampIsNotRemembered() => StaTestHost.Run(() =>
    {
        using var context = new DispatcherContextScope();
        string root = OwnedTestDirectory.Create("mc-stamp-undone-");
        try
        {
            using var stamp = new StampScope(Path.Combine(root, "stamp"));
            stamp.Register(root, 400, 200);
            using (var host = EditorWindowHost.Create())
            {
                Run(host.Editor.PlaceStampAsync());
                for (int step = 0; step < 5; step++) host.Editor.ShortcutActions["LeftFast"]();
                host.Editor.ShortcutActions["Delete"]();
            }
            // The editor reports its final state when it unloads, which the dispatcher runs after Close.
            Pump(TimeSpan.FromMilliseconds(50));

            Assert.Equal(StampAnchor.BottomRight, stamp.Defaults.Stamp.Anchor);
            Assert.Equal(16, stamp.Defaults.Stamp.MarginX);
        }
        finally { OwnedTestDirectory.Delete(root); }
    });

    // ---- Keyboard, rail and opening --------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheSKeyPlacesTheStampAlsoFromAnImeProcessedKey(bool ime) => StaTestHost.Run(() =>
    {
        using var context = new DispatcherContextScope();
        string root = OwnedTestDirectory.Create("mc-stamp-key-");
        try
        {
            using var stamp = new StampScope(Path.Combine(root, "stamp"));
            stamp.Register(root, 400, 200);
            using var host = EditorWindowHost.Create();

            Assert.Equal("Stamp", ShortcutCatalog.Match(ShortcutScope.Annotation, Key.S, ModifierKeys.None));
            Assert.Equal("Save", ShortcutCatalog.Match(ShortcutScope.Annotation, Key.S, ModifierKeys.Control));
            Assert.True(Press(host.Editor, Key.S, ime));
            PumpUntil(() => Controller(host.Editor).Document.Items.Count == 1);

            Assert.IsType<ImageAnnotation>(Assert.Single(Controller(host.Editor).Document.Items));
        }
        finally { OwnedTestDirectory.Delete(root); }
    });

    [Fact]
    public void TheCommandSitsBesideTheImageToolAsAnUncheckedRailButtonWithItsKey() => StaTestHost.Run(() =>
    {
        using var context = new DispatcherContextScope();
        using var language = UiText.UseLanguage("ko-KR");
        string root = OwnedTestDirectory.Create("mc-stamp-rail-");
        try
        {
            using var stamp = new StampScope(Path.Combine(root, "stamp"));
            stamp.Register(root, 400, 200);
            using var host = EditorWindowHost.Create();
            ToggleButton button = host.Editor.StampButton;
            ToggleButton image = host.Editor.ToolButton(EditorTool.Image);

            Assert.Same(VisualTreeHelperParent(image), VisualTreeHelperParent(button));
            Assert.Equal(image.Width, button.Width);
            Assert.Equal(image.Height, button.Height);
            Assert.Same(image.Style, button.Style);
            Assert.Equal("S", System.Windows.Automation.AutomationProperties.GetAcceleratorKey(button));
            Assert.Equal("도장 찍기 (S)", System.Windows.Automation.AutomationProperties.GetName(button));
            Assert.Equal("도장 찍기 — 오른쪽 버튼으로 설정 (S)", Assert.IsType<string>(button.ToolTip));
            Assert.Equal([EditorTool.Image], host.Editor.ToolGroups.Last());

            // A real click goes through OnClick, which would toggle an ordinary ToggleButton.
            typeof(ButtonBase).GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(button, null);
            PumpUntil(() => Controller(host.Editor).Document.Items.Count == 1);

            Assert.False(button.IsChecked == true);
            Assert.False(image.IsChecked == true);
        }
        finally { OwnedTestDirectory.Delete(root); }
    });

    [Fact]
    public void OpeningTheEditorNeverReadsTheStampOrBuildsItsMenu() => StaTestHost.Run(() =>
    {
        string root = OwnedTestDirectory.Create("mc-stamp-open-");
        try
        {
            using var stamp = new StampScope(Path.Combine(root, "stamp"));
            stamp.Register(root, 400, 200);
            int calls = stamp.FolderCalls;
            using (var host = EditorWindowHost.Create())
            {
                Assert.Equal(calls, stamp.FolderCalls);
                Assert.Null(host.Editor.StampMenu);
                Assert.Empty(host.Editor.PlacedStampIds);
                Assert.Empty(Controller(host.Editor).Document.Items);
            }
            // The editor reports its final state when it unloads, which the dispatcher runs after Close.
            Pump(TimeSpan.FromMilliseconds(50));

            Assert.Equal(calls, stamp.FolderCalls);
        }
        finally { OwnedTestDirectory.Delete(root); }
    });

    // ---- Helpers ---------------------------------------------------------------------

    private static DependencyObject? VisualTreeHelperParent(DependencyObject element) =>
        System.Windows.Media.VisualTreeHelper.GetParent(element);

    /// <summary>
    /// Clicks a stamp menu item by position (0 place, 1 change, 2 opacity, 3 remove), or one
    /// opacity choice by <paramref name="subItem"/>.
    /// </summary>
    private static void ClickMenu(AnnotationEditorControl editor, int index, int? subItem = null)
    {
        ContextMenu menu = editor.EnsureStampMenu();
        MenuItem item = menu.Items.OfType<MenuItem>().ElementAt(index);
        if (subItem is int sub) item = item.Items.OfType<MenuItem>().ElementAt(sub);
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        // A menu item that starts an asynchronous step finishes through the dispatcher.
        Pump(TimeSpan.FromMilliseconds(30));
    }

    private static void Run(Task task)
    {
        PumpUntil(() => task.IsCompleted);
        task.GetAwaiter().GetResult();
    }

    private static void PumpUntil(Func<bool> predicate)
    {
        var timer = Stopwatch.StartNew();
        while (!predicate())
        {
            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(10), "Timed out waiting for the dispatcher.");
            PumpOnce();
        }
    }

    private static void Pump(TimeSpan duration)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < duration) PumpOnce();
    }

    private static void PumpOnce()
    {
        var frame = new DispatcherFrame();
        var resume = new DispatcherTimer(TimeSpan.FromMilliseconds(5), DispatcherPriority.Background,
            (sender, _) => { ((DispatcherTimer)sender!).Stop(); frame.Continue = false; }, Dispatcher.CurrentDispatcher);
        resume.Start();
        Dispatcher.PushFrame(frame);
    }

    /// <summary>Runs async continuations on this test thread's dispatcher, as in the app.</summary>
    private sealed class DispatcherContextScope : IDisposable
    {
        private readonly SynchronizationContext? _previous = SynchronizationContext.Current;

        internal DispatcherContextScope() =>
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));

        public void Dispose() => SynchronizationContext.SetSynchronizationContext(_previous);
    }

    /// <summary>
    /// Points the editor's app-owned hooks at an isolated stamp folder and an in-memory copy of the
    /// annotation defaults, and restores the previous hooks afterwards. Never touches the profile.
    /// </summary>
    private sealed class StampScope : IDisposable
    {
        private readonly Func<AnnotationDefaults>? _read = AnnotationEditorPreferences.Read;
        private readonly Action<AnnotationDefaults>? _write = AnnotationEditorPreferences.Write;
        private readonly Func<bool>? _sourceTitle = AnnotationEditorPreferences.ReadShowSourceWindowTitle;
        private readonly Func<string?>? _folder = AnnotationEditorPreferences.StampFolder;

        internal StampScope(string folder, AnnotationDefaults? defaults = null)
        {
            Folder = folder;
            Defaults = defaults ?? new AnnotationDefaults();
            AnnotationEditorPreferences.Read = () => Defaults;
            AnnotationEditorPreferences.Write = value => Defaults = value;
            AnnotationEditorPreferences.ReadShowSourceWindowTitle = () => false;
            AnnotationEditorPreferences.StampFolder = () => { FolderCalls++; return Folder; };
        }

        internal string Folder { get; }

        internal AnnotationDefaults Defaults { get; private set; }

        internal int FolderCalls { get; private set; }

        /// <summary>Stores a solid picture as the registered stamp, as an earlier use of the command would have.</summary>
        internal void Register(string scratch, int width, int height, byte value = 0x80)
        {
            string source = Path.Combine(scratch, $"seed-{width}x{height}.png");
            ImageCodec.SavePng(Solid(width, height, value), source);
            StampStore.Save(ImageCodec.TryLoad(source)!, Folder);
            File.Delete(source);
        }

        public void Dispose()
        {
            AnnotationEditorPreferences.Read = _read;
            AnnotationEditorPreferences.Write = _write;
            AnnotationEditorPreferences.ReadShowSourceWindowTitle = _sourceTitle;
            AnnotationEditorPreferences.StampFolder = _folder;
        }
    }
}
