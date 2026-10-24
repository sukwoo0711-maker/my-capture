using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.App.Gallery;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// The library's microinteractions (feel bench 4-B and the in-button copy check): a thumbnail
/// fades in only when it arrives on a card already on screen, deleted cards fade and shrink and
/// the rest slide into the gap only when that reads as a move, and a copy turns its button's glyph
/// into a check for 1.2 s. Every part snaps when Windows animation effects are off.
/// </summary>
public sealed class GalleryFeelMotionTests
{
    // ---- Thumbnail arrival -------------------------------------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ThumbnailFadesOnlyWhenItArrivesOnACardAlreadyOnScreen(bool motion) =>
        ControlStateMotionTests.Run(motion, (_, host) =>
        {
            BitmapSource first = Bitmap(Colors.SteelBlue), second = Bitmap(Colors.Orange);
            // A cached thumbnail is bound while the card is realized: it appears whole.
            var cached = new GalleryThumbnailImage { Source = first, Width = 40, Height = 25 };
            var pending = new GalleryThumbnailImage { Width = 40, Height = 25 };
            host.Children.Add(cached);
            host.Children.Add(pending);
            ControlStateMotionTests.Drain(host);
            Assert.True(cached.IsLoaded);
            Assert.False(cached.IsFading);
            Assert.False(cached.HasAnimatedProperties);
            Assert.Equal(1, cached.Opacity);

            // The decoded bitmap replaces the placeholder of a card on screen: it fades in, and
            // only its picture moves (no transform, the base opacity is already final).
            pending.Source = first;
            Assert.Equal(motion, pending.IsFading);
            Assert.Equal(motion, pending.HasAnimatedProperties);
            Assert.Equal(1, (double)pending.GetAnimationBaseValue(UIElement.OpacityProperty));
            Assert.Same(Transform.Identity, pending.RenderTransform);
            Assert.Equal(TimeSpan.FromMilliseconds(150), GalleryThumbnailImage.FadeDuration.TimeSpan);
            PumpUntil(host, () => !pending.IsFading, TimeSpan.FromSeconds(2));
            // The finished fade releases its clock and leaves the image whole.
            Assert.False(pending.HasAnimatedProperties);
            Assert.Equal(1, pending.Opacity);

            // A refreshed picture replacing a shown one does not blink through a fade.
            pending.Source = second;
            Assert.False(pending.IsFading);
            Assert.False(pending.HasAnimatedProperties);
        });

    [Fact]
    public void ThumbnailFadeStopsWhenThePictureIsClearedOrTheCardLeaves() =>
        ControlStateMotionTests.Run(true, (_, host) =>
        {
            var image = new GalleryThumbnailImage { Width = 40, Height = 25 };
            host.Children.Add(image);
            ControlStateMotionTests.Drain(host);
            image.Source = Bitmap(Colors.SeaGreen);
            Assert.True(image.IsFading);
            image.Source = null;
            Assert.False(image.IsFading);
            Assert.False(image.HasAnimatedProperties);
            Assert.Equal(1, image.Opacity);

            image.Source = Bitmap(Colors.SeaGreen);
            Assert.True(image.IsFading);
            host.Children.Remove(image);
            ControlStateMotionTests.Drain(host);
            Assert.False(image.IsLoaded);
            Assert.False(image.IsFading);
            Assert.False(image.HasAnimatedProperties);

            // Motion is read when the bitmap arrives, not when the card was made.
            host.Children.Add(image);
            ControlStateMotionTests.Drain(host);
            image.Source = null;
            FluidMotion.AnimationsEnabledOverrideForTest = false;
            image.Source = Bitmap(Colors.SeaGreen);
            Assert.False(image.IsFading);
            Assert.False(image.HasAnimatedProperties);
        });

    // ---- In-button copy confirmation ----------------------------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CopyTurnsTheGlyphIntoACheckForTheHoldAndBack(bool motion) =>
        ControlStateMotionTests.Run(motion, (theme, host) =>
        {
            using var language = UiText.UseLanguage("ko-KR");
            (Button button, UIElement glyph, UIElement check) = CopyButton(theme);
            button.ToolTip = "편집한 이미지 복사 (Ctrl+C)";
            AutomationProperties.SetName(button, "이미지 복사");
            host.Children.Add(button);
            ControlStateMotionTests.Drain(host);
            Size rest = button.RenderSize;
            Assert.Equal(1, glyph.Opacity);
            Assert.Equal(0, check.Opacity);
            Assert.True(GalleryCopyFeedback.CanConfirm(button));

            Assert.True(GalleryCopyFeedback.Confirm(button));
            Assert.True(GalleryCopyFeedback.IsConfirming(button));
            // The state is committed at once; only the crossfade takes Motion.Fast.
            Assert.Equal(0, (double)glyph.GetAnimationBaseValue(UIElement.OpacityProperty));
            Assert.Equal(1, (double)check.GetAnimationBaseValue(UIElement.OpacityProperty));
            Assert.Equal(motion, glyph.HasAnimatedProperties);
            Assert.Equal(motion, check.HasAnimatedProperties);
            Assert.Equal(MotionSpeed.Fast, FluidMotion.GetTransition(check));
            Assert.Equal("복사됨", AutomationProperties.GetName(button));
            Assert.Equal("복사됨", button.ToolTip);
            ControlStateMotionTests.Drain(host);
            Assert.Equal(rest, button.RenderSize);

            // A second copy during the hold starts it again: 1.5 s after the first copy the check
            // that would have ended at 1.2 s stays until 1.8 s.
            PumpFor(host, TimeSpan.FromMilliseconds(600));
            Assert.True(GalleryCopyFeedback.IsConfirming(button));
            Assert.True(GalleryCopyFeedback.Confirm(button));
            PumpFor(host, TimeSpan.FromMilliseconds(900));
            Assert.True(GalleryCopyFeedback.IsConfirming(button), "the hold restarts with every copy");
            Assert.Equal("복사됨", AutomationProperties.GetName(button));

            PumpUntil(host, () => !GalleryCopyFeedback.IsConfirming(button), TimeSpan.FromSeconds(3));
            Assert.Equal(1, (double)glyph.GetAnimationBaseValue(UIElement.OpacityProperty));
            Assert.Equal(0, (double)check.GetAnimationBaseValue(UIElement.OpacityProperty));
            Assert.Equal("이미지 복사", AutomationProperties.GetName(button));
            Assert.Equal("편집한 이미지 복사 (Ctrl+C)", button.ToolTip);
            Assert.Equal(TimeSpan.FromSeconds(1.2), GalleryCopyFeedback.HoldTime);
        });

    [Fact]
    public void CopyCheckReturnsTheTemplateNameAndEndsWhenTheButtonShowsAnotherCapture() =>
        ControlStateMotionTests.Run(true, (theme, host) =>
        {
            using var language = UiText.UseLanguage("ko-KR");
            // The tile's button takes its name and tooltip from the card template, not locally.
            var template = (DataTemplate)XamlReader.Parse("""
                <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                              xmlns:gallery="clr-namespace:MyCapture.App.Gallery;assembly=MyCapture"
                              xmlns:themes="clr-namespace:MyCapture.App.Themes;assembly=MyCapture">
                    <Button ToolTip="Copy hint" AutomationProperties.Name="Copy image">
                        <Grid>
                            <themes:SymbolIcon Size="16" gallery:GalleryCopyFeedback.Part="Glyph" themes:FluidMotion.FadeTo="1" />
                            <themes:SymbolIcon Size="16" gallery:GalleryCopyFeedback.Part="Check" Opacity="0" />
                        </Grid>
                    </Button>
                </DataTemplate>
                """);
            var presenter = new ContentPresenter { Content = "first", ContentTemplate = template };
            host.Children.Add(presenter);
            ControlStateMotionTests.Drain(host);
            var button = (Button)VisualTreeHelper.GetChild(presenter, 0);
            Assert.Equal(DependencyProperty.UnsetValue, button.ReadLocalValue(FrameworkElement.ToolTipProperty));

            Assert.True(GalleryCopyFeedback.Confirm(button));
            Assert.Equal("복사됨", button.ToolTip);
            presenter.Content = "second";
            ControlStateMotionTests.Drain(host);
            Assert.False(GalleryCopyFeedback.IsConfirming(button));
            Assert.Equal("Copy hint", button.ToolTip);
            Assert.Equal("Copy image", AutomationProperties.GetName(button));
            Assert.Equal(DependencyProperty.UnsetValue, button.ReadLocalValue(FrameworkElement.ToolTipProperty));
            Assert.Equal(DependencyProperty.UnsetValue, button.ReadLocalValue(AutomationProperties.NameProperty));

            // A button without a check glyph leaves the confirmation to the status line.
            var plain = new Button { Content = "Copy" };
            host.Children.Add(plain);
            Assert.False(GalleryCopyFeedback.CanConfirm(plain));
            Assert.False(GalleryCopyFeedback.Confirm(plain));
            Assert.False(GalleryCopyFeedback.IsConfirming(plain));

            // Leaving the screen ends the hold at once.
            Assert.True(GalleryCopyFeedback.Confirm(button));
            host.Children.Remove(presenter);
            ControlStateMotionTests.Drain(host);
            Assert.False(GalleryCopyFeedback.IsConfirming(button));
            Assert.Equal("Copy hint", button.ToolTip);
        });

    // ---- Held values -------------------------------------------------------------------

    [Fact]
    public void HeldPictureOutlastsItsBindingAndHandsTheBindingBack() =>
        ControlStateMotionTests.Run(true, (_, host) =>
        {
            var template = (DataTemplate)XamlReader.Parse("""
                <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                              xmlns:gallery="clr-namespace:MyCapture.App.Gallery;assembly=MyCapture">
                    <gallery:GalleryThumbnailImage Source="{Binding Picture}" Width="40" Height="25" />
                </DataTemplate>
                """);
            BitmapSource first = Bitmap(Colors.SteelBlue), second = Bitmap(Colors.Orange);
            var model = new PictureModel { Picture = first };
            var presenter = new ContentPresenter { Content = model, ContentTemplate = template };
            host.Children.Add(presenter);
            ControlStateMotionTests.Drain(host);
            var image = (GalleryThumbnailImage)VisualTreeHelper.GetChild(presenter, 0);
            Assert.Same(first, image.Source);

            GalleryHeldValue held = Assert.IsType<GalleryHeldValue>(GalleryHeldValue.TryReplace(image, Image.SourceProperty, image.Source));
            model.Picture = null; // the tile releases its thumbnail before its files move
            Assert.Same(first, image.Source);
            Assert.False(image.IsFading);
            held.Restore();
            Assert.Null(image.Source);
            model.Picture = second;
            Assert.Same(second, image.Source);

            // A local binding comes back too.
            var local = new GalleryThumbnailImage();
            local.SetBinding(Image.SourceProperty, new System.Windows.Data.Binding(nameof(PictureModel.Picture)) { Source = model });
            GalleryHeldValue localHeld = GalleryHeldValue.Replace(local, Image.SourceProperty, first);
            Assert.Same(first, local.Source);
            localHeld.Restore();
            Assert.Same(second, local.Source);
            model.Picture = first;
            Assert.Same(first, local.Source);
        });

    // ---- Delete: exit, reflow and the result line ---------------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DeletedCardsFadeAndShrinkKeepingTheirPicturesUntilReset(bool screenOwnsTransform) =>
        ControlStateMotionTests.Run(true, (_, host) =>
        {
            BitmapSource picture = Bitmap(Colors.SteelBlue);
            var model = new PictureModel { Picture = picture };
            var image = new GalleryThumbnailImage { Width = 40, Height = 25 };
            image.SetBinding(Image.SourceProperty, new System.Windows.Data.Binding(nameof(PictureModel.Picture)) { Source = model });
            var owned = new RotateTransform(0);
            var card = new Border { Width = 60, Height = 40, Child = image };
            if (screenOwnsTransform) card.RenderTransform = owned;
            host.Children.Add(card);
            ControlStateMotionTests.Drain(host);

            GalleryCardExit exit = GalleryDeleteMotion.BeginExit([card]);
            model.Picture = null;
            Assert.Same(picture, image.Source);
            Assert.False(image.IsFading);
            // Only the picture leaves: the card's own opacity is untouched underneath the fade.
            Assert.Equal(1, (double)card.GetAnimationBaseValue(UIElement.OpacityProperty));
            Assert.True(card.HasAnimatedProperties);
            if (screenOwnsTransform)
            {
                Assert.Same(owned, card.RenderTransform);
            }
            else
            {
                var scale = Assert.IsType<ScaleTransform>(card.RenderTransform);
                Assert.True(scale.HasAnimatedProperties);
                Assert.Equal(new Point(0.5, 0.5), card.RenderTransformOrigin);
            }

            Assert.False(exit.Completion.IsCompleted);
            PumpUntil(host, () => exit.Completion.IsCompleted, TimeSpan.FromSeconds(2));
            // The card stays gone until the view is rebuilt.
            Assert.Equal(0, card.Opacity);
            if (!screenOwnsTransform)
                Assert.Equal(GalleryDeleteMotion.ExitScale, ((ScaleTransform)card.RenderTransform).ScaleX, 3);

            exit.Reset();
            Assert.False(card.HasAnimatedProperties);
            Assert.Equal(1, card.Opacity);
            Assert.Equal(screenOwnsTransform ? owned : Transform.Identity, card.RenderTransform);
            Assert.Equal(DependencyProperty.UnsetValue, card.ReadLocalValue(UIElement.RenderTransformOriginProperty));
            Assert.Null(image.Source); // the binding is back
            Assert.Equal(TimeSpan.FromMilliseconds(120), GalleryDeleteMotion.ExitDuration.TimeSpan);
        });

    [Fact]
    public void NoCardLeavingCompletesAtOnce()
    {
        GalleryCardExit exit = GalleryDeleteMotion.BeginExit([]);
        Assert.Same(GalleryCardExit.None, exit);
        Assert.True(exit.Completion.IsCompleted);
        exit.Reset();
    }

    [Fact]
    public void SlideAndRiseUseTransformsTheMotionOwnsAndRemoveThem() =>
        ControlStateMotionTests.Run(true, (_, host) =>
        {
            var card = new Border { Width = 60, Height = 40 };
            var status = new TextBlock { Text = "1개를 휴지통으로 옮김" };
            var owned = new Border { Width = 60, Height = 40, RenderTransform = new TranslateTransform(3, 0) };
            host.Children.Add(card);
            host.Children.Add(status);
            host.Children.Add(owned);
            ControlStateMotionTests.Drain(host);
            Rect slot = LayoutInformation.GetLayoutSlot(card);

            Assert.True(GalleryDeleteMotion.Slide(card, new Vector(120, 40)));
            Assert.True(GalleryDeleteMotion.IsMoving(card));
            var shift = Assert.IsType<TranslateTransform>(card.RenderTransform);
            Assert.True(shift.HasAnimatedProperties);
            Assert.Equal(0, (double)shift.GetAnimationBaseValue(TranslateTransform.XProperty));
            Assert.Equal(0, (double)shift.GetAnimationBaseValue(TranslateTransform.YProperty));
            // Layout never moves: the card's slot is final from the start.
            host.UpdateLayout();
            Assert.Equal(slot, LayoutInformation.GetLayoutSlot(card));
            Assert.False(GalleryDeleteMotion.Slide(owned, new Vector(10, 0)));
            Assert.Equal(new TranslateTransform(3, 0).Value, owned.RenderTransform.Value);

            GalleryDeleteMotion.Rise(status);
            Assert.True(GalleryDeleteMotion.IsMoving(status));
            Assert.True(status.HasAnimatedProperties);
            Assert.Equal(1, (double)status.GetAnimationBaseValue(UIElement.OpacityProperty));

            PumpUntil(host, () => !GalleryDeleteMotion.IsMoving(card) && !GalleryDeleteMotion.IsMoving(status), TimeSpan.FromSeconds(2));
            Assert.Equal(Transform.Identity, card.RenderTransform);
            Assert.Equal(Transform.Identity, status.RenderTransform);
            Assert.Equal(1, status.Opacity);

            // Settling ends a slide at once.
            Assert.True(GalleryDeleteMotion.Slide(card, new Vector(0, -40)));
            GalleryDeleteMotion.Settle(card);
            Assert.False(GalleryDeleteMotion.IsMoving(card));
            Assert.Equal(Transform.Identity, card.RenderTransform);
            Assert.Equal(TimeSpan.FromMilliseconds(200), GalleryDeleteMotion.ReflowDuration.TimeSpan);
        });

    [Fact]
    public void ReflowSlidesOnlyCardsSeenOnBothSidesOfTheRefresh()
    {
        Guid kept = Guid.NewGuid(), still = Guid.NewGuid(), fresh = Guid.NewGuid(), gone = Guid.NewGuid(), offscreen = Guid.NewGuid();
        GalleryCardLayout before = Layout(
            (gone, Cell(0, 0)), (kept, Cell(1, 0)), (still, Cell(0, 1)), (offscreen, Cell(0, 9)));
        GalleryCardLayout after = Layout(
            (kept, Cell(0, 0)), (still, Cell(0, 1)), (fresh, Cell(1, 1)), (offscreen, Cell(0, 8)));

        GalleryCardMove move = Assert.Single(GalleryDeleteMotion.PlanReflow(before, after));
        Assert.Equal(kept, move.Id);
        Assert.Equal(new Vector(200, 0), move.Offset); // from its old place to the new one

        // A card moving up from the row below slides diagonally into the gap.
        GalleryCardMove up = Assert.Single(GalleryDeleteMotion.PlanReflow(Layout((kept, Cell(0, 1))), Layout((kept, Cell(2, 0)))));
        Assert.Equal(new Vector(-400, 150), up.Offset);
    }

    [Fact]
    public void ReflowIsSkippedWhenSlidingWouldNotReadAsCardsClosingAGap()
    {
        Guid id = Guid.NewGuid();
        GalleryCardLayout before = Layout((id, Cell(1, 0)));
        GalleryCardLayout after = Layout((id, Cell(0, 0)));
        Assert.Single(GalleryDeleteMotion.PlanReflow(before, after));

        // A date heading appeared or disappeared: whole groups move.
        Assert.Empty(GalleryDeleteMotion.PlanReflow(before, after with { Headers = ["오늘"] }));
        // The list scrolled (its end came up): everything jumps together.
        Assert.Empty(GalleryDeleteMotion.PlanReflow(before, after with { ScrollOffset = 120 }));
        // The column count changed.
        Assert.Empty(GalleryDeleteMotion.PlanReflow(before, after with { Columns = 4 }));
        // A card would cross more than the visible height.
        Assert.Empty(GalleryDeleteMotion.PlanReflow(Layout((id, Cell(0, 6))), after));

        // More moving cards than read as a gap closing.
        var many = Enumerable.Range(0, GalleryDeleteMotion.MaxSlidingCards + 1).Select(_ => Guid.NewGuid()).ToArray();
        GalleryCardLayout crowdBefore = Layout(many.Select((card, index) => (card, Small(index + 1))).ToArray());
        GalleryCardLayout crowdAfter = Layout(many.Select((card, index) => (card, Small(index))).ToArray());
        Assert.Empty(GalleryDeleteMotion.PlanReflow(crowdBefore, crowdAfter));
        Assert.Equal(GalleryDeleteMotion.MaxSlidingCards,
            GalleryDeleteMotion.PlanReflow(Layout(crowdBefore.Cards.Take(GalleryDeleteMotion.MaxSlidingCards).Select(pair => (pair.Key, pair.Value)).ToArray()),
                crowdAfter).Count);
    }

    // ---- Helpers -----------------------------------------------------------------------

    private static Rect Cell(int column, int row) => new(column * 200, row * 150, 200, 150);

    // 12 columns of small cards in the 600 x 450 view.
    private static Rect Small(int index) => new(index % 12 * 50, index / 12 * 40, 50, 40);

    private static GalleryCardLayout Layout(params (Guid Id, Rect Bounds)[] cards) =>
        new(cards.ToDictionary(card => card.Id, card => card.Bounds), ["오늘", "어제"], 0, new Size(600, 450), 3);

    private static (Button Button, UIElement Glyph, UIElement Check) CopyButton(ResourceDictionary theme)
    {
        var glyph = new SymbolIcon { Size = 16, Data = (Geometry)theme["Icon.Copy"] };
        GalleryCopyFeedback.SetPart(glyph, GalleryCopyFeedbackPart.Glyph);
        FluidMotion.SetFadeTo(glyph, 1);
        var check = new SymbolIcon { Size = 16, Data = (Geometry)theme["Icon.Check"], Opacity = 0 };
        GalleryCopyFeedback.SetPart(check, GalleryCopyFeedbackPart.Check);
        var cell = new Grid();
        cell.Children.Add(glyph);
        cell.Children.Add(check);
        return (new Button { Content = cell, Width = 28, Height = 28 }, glyph, check);
    }

    private static BitmapSource Bitmap(Color color)
    {
        var bitmap = BitmapSource.Create(4, 3, 96, 96, PixelFormats.Bgra32, null,
            Enumerable.Range(0, 12).SelectMany(_ => new[] { color.B, color.G, color.R, (byte)255 }).ToArray(), 16);
        bitmap.Freeze();
        return bitmap;
    }

    private static void PumpFor(DispatcherObject owner, TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background, owner.Dispatcher) { Interval = duration };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void PumpUntil(DispatcherObject owner, Func<bool> done, TimeSpan limit)
    {
        DateTime deadline = DateTime.UtcNow + limit;
        while (!done())
        {
            Assert.True(DateTime.UtcNow < deadline, "The motion did not settle in time.");
            PumpFor(owner, TimeSpan.FromMilliseconds(20));
        }
    }

    private sealed class PictureModel : INotifyPropertyChanged
    {
        private BitmapSource? _picture;

        public event PropertyChangedEventHandler? PropertyChanged;

        public BitmapSource? Picture
        {
            get => _picture;
            set { _picture = value; Raise(); }
        }

        private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
