using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using MyCapture.App.Gallery;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Queue;
using MyCapture.Tests;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// The library's microinteractions on the real window (feel bench 4-B and the in-button copy
/// check): a thumbnail that arrives fades in, the tile actions fade with the card, a copy shows a
/// check in its button or, from a menu, on the status line, and a delete lets the cards leave,
/// slides the rest into the gap and raises its result. With motion off a delete is today's.
/// Copies go to the test clipboard (RecordingClipboard), never the user's.
/// </summary>
public sealed partial class SettingsThemePreviewTests
{
    [Fact]
    public Task LibraryFeel_CardsArriveCopyConfirmsAndDeleteClosesTheGap() =>
        RunIsolated(nameof(LibraryFeel_CardsArriveCopyConfirmsAndDeleteClosesTheGap), () =>
        {
            using var language = UiText.UseLanguage("ko-KR");
            Application application = CreateLibraryApplication();
            bool? previous = FluidMotion.AnimationsEnabledOverrideForTest;
            FluidMotion.AnimationsEnabledOverrideForTest = true;
            try
            {
                VerifyLibraryFeelWithMotion();
                FluidMotion.AnimationsEnabledOverrideForTest = false;
                VerifyLibraryDeleteWithoutMotion();
            }
            finally
            {
                FluidMotion.AnimationsEnabledOverrideForTest = previous;
                application.Shutdown();
            }
        });

    private static void VerifyLibraryFeelWithMotion()
    {
        EvidenceLibrary library = CreateEvidenceLibrary(9, "mc-library-feel-");
        GalleryWindow window = library.Window;
        try
        {
            window.Show();
            SettleFeelLibrary(window);
            var status = (TextBlock)window.FindName("LibraryActionStatus");

            // A thumbnail decoded after its card is on screen fades in.
            ListBoxItem first = FeelCard(window, library.Records[0].Id);
            GalleryThumbnailImage picture = FeelPicture(first);
            bool? fadingOnArrival = null;
            DependencyPropertyDescriptor source = DependencyPropertyDescriptor.FromProperty(Image.SourceProperty, typeof(Image));
            EventHandler arrived = (_, _) =>
            {
                if (picture.Source is not null)
                    window.Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() => fadingOnArrival ??= picture.IsFading));
            };
            source.AddValueChanged(picture, arrived);
            try
            {
                library.ViewModel.FindTile(library.Records[0].Id)!.RefreshThumbnail();
                PumpLibraryUntil(window, () => fadingOnArrival is not null, "feel: thumbnail arrival");
            }
            finally { source.RemoveValueChanged(picture, arrived); }
            Assert.True(fadingOnArrival, "a thumbnail that arrives on a shown card fades in");
            PumpLibraryUntil(window, () => !picture.IsFading, "feel: thumbnail settled");

            Assert.DoesNotContain(Descendants(first).OfType<Border>(), border => border.Name == "TileActions");
            CaptureRecord copied = library.Records[0];
            File.Copy(library.Queue.GetFilePath(copied, CaptureFileNames.Original),
                library.Queue.GetFilePath(copied, CaptureFileNames.Rendered), overwrite: true);
            string copyName = UiText.Get("Text_42A667BC71B0");
            long writes = RecordingClipboard.WriteCount;
            string statusBefore = status.Text;
            window.SetDetailsPaneOpen(true);

            // The details pane's copy button confirms the same way, until it shows no capture.
            library.ViewModel.Select(copied.Id);
            SettleGalleryLayout(window);
            var detailsCopy = (Button)window.FindName("DetailsCopyButton");
            PumpUiTask(window, () => { detailsCopy.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); return Task.FromResult(true); });
            PumpLibraryUntil(window, () => GalleryCopyFeedback.IsConfirming(detailsCopy), "feel: details copy check");
            Assert.Equal(writes + 1, RecordingClipboard.WriteCount);
            Assert.Equal("복사됨", AutomationProperties.GetName(detailsCopy));
            Assert.Equal("복사됨", detailsCopy.ToolTip);
            Assert.Equal(statusBefore, status.Text);
            Assert.Empty(Application.Current.Windows.OfType<ThemedDialog>());
            library.ViewModel.ClearSelection();
            SettleGalleryLayout(window);
            Assert.False(GalleryCopyFeedback.IsConfirming(detailsCopy));
            Assert.Equal(copyName, AutomationProperties.GetName(detailsCopy));
            Assert.Equal(UiText.Get("Text_59D43391538F"), detailsCopy.ToolTip);
            Assert.Equal(statusBefore, status.Text);

            // A copy without a button (the card's menu) confirms on the status line instead.
            first = FeelCard(window, copied.Id);
            ContextMenu menu = window.TileMenu;
            menu.PlacementTarget = first;
            SettleGalleryLayout(window);
            MenuItem menuCopy = menu.Items.OfType<MenuItem>().Single(item => item.Header as string == copyName);
            Assert.Same(first.DataContext, menuCopy.CommandParameter);
            PumpUiTask(window, () => { menuCopy.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); return Task.FromResult(true); });
            PumpLibraryUntil(window, () => KoreanWordWrap.Strip(status.Text) == "이미지 복사됨", "feel: menu copy status");
            Assert.Equal(writes + 2, RecordingClipboard.WriteCount);
            Assert.Equal(FeedbackKind.Confirmation, window.ActionStatusKindForTest);
            Assert.Empty(Application.Current.Windows.OfType<ThemedDialog>());
            menu.ClearValue(ContextMenu.PlacementTargetProperty);

            // Delete: the card leaves while its capture moves away; the view waits for both.
            window.ConfirmDelete = (_, _) => true;
            Guid deleted = library.Records[1].Id, follower = library.Records[2].Id, leader = library.Records[0].Id;
            ListBoxItem leaving = FeelCard(window, deleted);
            Task delete = StartUiTask(window, () => window.DeleteCapturesAsync([deleted]));
            Assert.True(leaving.HasAnimatedProperties);
            var shrink = Assert.IsType<ScaleTransform>(leaving.RenderTransform);
            Assert.True(shrink.HasAnimatedProperties);
            Assert.NotNull(FeelPicture(leaving).Source); // the picture stays while it fades
            Assert.NotEqual("1개를 휴지통으로 옮김", KoreanWordWrap.Strip(status.Text));

            (string Status, bool Rising, Guid[] Moving, bool Fading, int Count, bool UndoReady)? end = null;
            delete.ContinueWith(_ => end = (KoreanWordWrap.Strip(status.Text), GalleryDeleteMotion.IsMoving(status),
                    window.RealizedCards().Where(GalleryDeleteMotion.IsMoving).Select(card => ((GalleryItemViewModel)card.DataContext).Id).ToArray(),
                    window.RealizedCards().Any(card => Descendants(card).OfType<GalleryThumbnailImage>().Any(image => image.IsFading)),
                    library.Queue.Count, library.Queue.LibraryUndo.CanUndo),
                TaskContinuationOptions.ExecuteSynchronously);
            PumpLibraryUntil(window, () => delete.IsCompleted, "feel: delete");
            Assert.True(delete.IsCompletedSuccessfully, delete.Exception?.ToString());
            Assert.NotNull(end);
            Assert.Equal("1개를 휴지통으로 옮김", end.Value.Status);
            Assert.True(end.Value.Rising, "the result line rises in");
            Assert.Equal(8, end.Value.Count);
            Assert.True(end.Value.UndoReady);
            Assert.Contains(follower, end.Value.Moving);
            Assert.DoesNotContain(leader, end.Value.Moving);
            Assert.DoesNotContain(deleted, end.Value.Moving);
            // Cards already decoded keep their pictures through the rebuild: no shimmer.
            Assert.False(end.Value.Fading);
            Assert.DoesNotContain(window.RealizedCards(), card => ((GalleryItemViewModel)card.DataContext).Id == deleted);
            Assert.Equal(UiText.Get("LibraryUndo_Action"), window.StatusActionLabelForTest);

            PumpLibraryUntil(window, () => !GalleryDeleteMotion.IsMoving(status)
                && !window.RealizedCards().Any(GalleryDeleteMotion.IsMoving), "feel: reflow settled");
            Assert.All(window.RealizedCards(), card =>
            {
                Assert.Same(Transform.Identity, card.RenderTransform);
                Assert.Equal(1, card.Opacity);
            });

            // Undo brings the card back; it arrives like any new card.
            PumpUiTask(window, async () => { await window.InvokeStatusActionForTest(); return true; });
            PumpLibraryUntil(window, () => window.RealizedCards().Any(card => ((GalleryItemViewModel)card.DataContext).Id == deleted)
                && KoreanWordWrap.Strip(status.Text) == "라이브러리에 1개 복원됨", "feel: undo");
            Assert.Equal(9, library.Queue.Count);
        }
        finally
        {
            window.CloseForExit();
        }
    }

    private static void VerifyLibraryDeleteWithoutMotion()
    {
        EvidenceLibrary library = CreateEvidenceLibrary(6, "mc-library-feel-still-");
        GalleryWindow window = library.Window;
        try
        {
            window.Show();
            SettleFeelLibrary(window);
            var status = (TextBlock)window.FindName("LibraryActionStatus");
            window.ConfirmDelete = (_, _) => true;
            Guid deleted = library.Records[1].Id;
            ListBoxItem leaving = FeelCard(window, deleted);

            // Today's delete: nothing animates, the view is rebuilt as soon as the move ends.
            Task delete = StartUiTask(window, () => window.DeleteCapturesAsync([deleted]));
            Assert.False(leaving.HasAnimatedProperties);
            Assert.Same(Transform.Identity, leaving.RenderTransform);
            bool? moving = null;
            delete.ContinueWith(_ => moving = GalleryDeleteMotion.IsMoving(status)
                    || window.RealizedCards().Any(GalleryDeleteMotion.IsMoving), TaskContinuationOptions.ExecuteSynchronously);
            PumpLibraryUntil(window, () => delete.IsCompleted, "still: delete");
            Assert.True(delete.IsCompletedSuccessfully, delete.Exception?.ToString());
            Assert.False(moving);
            Assert.Equal("1개를 휴지통으로 옮김", KoreanWordWrap.Strip(status.Text));
            Assert.Same(Transform.Identity, status.RenderTransform);
            Assert.Equal(5, library.Queue.Count);
            Assert.All(window.RealizedCards(), card => Assert.Same(Transform.Identity, card.RenderTransform));
        }
        finally
        {
            window.CloseForExit();
        }
    }

    private static void SettleFeelLibrary(GalleryWindow window) =>
        PumpLibraryUntil(window, () => window.RealizedCards().Count() >= 4
            && window.RealizedCards().All(card => FeelPicture(card).Source is not null && !FeelPicture(card).IsFading), "feel: thumbnails");

    private static ListBoxItem FeelCard(GalleryWindow window, Guid id) =>
        window.RealizedCards().Single(card => ((GalleryItemViewModel)card.DataContext).Id == id);

    private static GalleryThumbnailImage FeelPicture(DependencyObject card) =>
        Descendants(card).OfType<GalleryThumbnailImage>().Single();

    /// <summary>Starts a window task with WPF's synchronization context and returns it unfinished.</summary>
    private static Task StartUiTask(Window window, Func<Task> start)
    {
        SynchronizationContext? previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(window.Dispatcher));
        try { return start(); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }
}
