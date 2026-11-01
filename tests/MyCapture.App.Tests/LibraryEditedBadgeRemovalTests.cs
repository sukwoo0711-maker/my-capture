using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MyCapture.App.Gallery;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Queue;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class LibraryEditedBadgeRemovalTests
{
    [Fact]
    public Task EveryCardStateOmitsEditedBadgeAndAccessibleSuffix() =>
        new SettingsThemePreviewTests().LibraryVideo_EditedBadgeIsAbsentFromEveryCardState();
}

public sealed partial class SettingsThemePreviewTests
{
    [Fact]
    public Task LibraryVideo_EditedBadgeIsAbsentFromEveryCardState() =>
        RunIsolated(nameof(LibraryVideo_EditedBadgeIsAbsentFromEveryCardState), () =>
        {
            Application application = CreateLibraryMotionApplication();
            FluidMotion.AnimationsEnabledOverrideForTest = false;
            LibraryUxFixture fixture = CreateLibraryUxFixture("mc-no-edited-badge-");
            try
            {
                DataTemplate template = (DataTemplate)fixture.Window.FindResource("Gallery.TileTemplate");
                foreach (string language in new[] { "ko-KR", "en-US" })
                foreach (bool video in new[] { false, true })
                foreach (bool annotated in new[] { false, true })
                foreach (bool selected in new[] { false, true })
                foreach (bool search in new[] { false, true })
                foreach (bool compact in new[] { false, true })
                {
                    using var languageScope = UiText.UseLanguage(language);
                    var record = new CaptureRecord
                    {
                        Title = "자료 검토", HasAnnotations = annotated, IsPinned = true,
                        MediaKind = video ? CaptureMediaKind.Video : CaptureMediaKind.Image,
                        Width = 640, Height = 360, DurationMs = video ? 12000 : 0,
                    };
                    var tile = new GalleryItemViewModel(record, _ => string.Empty, 96) { IsSelected = selected };
                    tile.SetSearchMatch(search, search ? CaptureMatchField.Title : CaptureMatchField.None);
                    var card = (FrameworkElement)template.LoadContent();
                    card.DataContext = tile;
                    var host = new Border
                    {
                        Resources = fixture.Window.Resources, Width = compact ? 260 : 340,
                        Child = card, Background = (Brush)fixture.Window.FindResource("Surface.Base"),
                    };
                    GalleryCardStyle.SetIsCompact(host, compact);
                    LayoutEvidence(host);
                    Assert.DoesNotContain(Descendants(host).OfType<FrameworkElement>(), element => element.Name == "EditedBadge");
                    Assert.DoesNotContain(UiText.Get("Text_2C64DDA4D878"), tile.AccessibleName, StringComparison.Ordinal);
                    Assert.Equal(annotated, tile.HasAnnotations);
                    Assert.Equal(2, CardPart<Grid>(host, "TileStatusBadges").ColumnDefinitions.Count);
                    Assert.NotNull(CardPart<Border>(host, "KeepBadge"));
                    Assert.NotNull(CardPart<Border>(host, "FolderBadge"));
                    fixture.Window.PrepareTileMenu(fixture.Window.TileMenu, tile);
                    Assert.Contains(fixture.Window.TileMenu.Items.OfType<MenuItem>(), item => Equals(item.Tag, "Edit"));
                }
                Assert.False(fixture.Window.IsVisible);
            }
            finally
            {
                fixture.Window.CloseForExit();
                FluidMotion.AnimationsEnabledOverrideForTest = null;
                application.Shutdown();
            }
        });
}
