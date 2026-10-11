using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MyCapture.App.Themes;
using MyCapture.Core.Themes;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class WorkspaceThemeTests
{
    [Fact]
    public void RoleBrushesAndSealedStylesStayIsolatedAndSupportHighContrastSwitch()
    {
        StaTestHost.Run(() =>
        {
            ResourceDictionary gallery = WorkspaceTheme.Create(WorkspaceRole.Gallery);
            ResourceDictionary editor = WorkspaceTheme.Create(WorkspaceRole.VideoEditor);
            WorkspaceTheme.Apply(gallery, WorkspaceRole.Gallery, AppTheme.Workspace);
            WorkspaceTheme.Apply(editor, WorkspaceRole.VideoEditor, AppTheme.Workspace);
            var galleryBrush = Assert.IsType<SolidColorBrush>(gallery["Surface.Base"]);
            var editorBrush = Assert.IsType<SolidColorBrush>(editor["Surface.Base"]);
            Assert.NotSame(galleryBrush, editorBrush);
            Assert.Equal((Color)ColorConverter.ConvertFromString("#edf3f2"), galleryBrush.Color);
            Assert.Equal((Color)ColorConverter.ConvertFromString("#141b22"), editorBrush.Color);
            var card = new Border { Resources = gallery, Style = (Style)gallery["Card"] };
            card.Measure(new Size(240, 200));
            var cardBrush = Assert.IsType<SolidColorBrush>(card.Background);
            Assert.Equal(Colors.White, cardBrush.Color);
            // Palette brushes are frozen and swapped on a theme change, never recoloured in
            // place (ThemeBrushes); the sealed style follows through its resource reference.
            Assert.True(cardBrush.IsFrozen);
            WorkspaceTheme.Apply(gallery, WorkspaceRole.Gallery, AppTheme.HighContrast);
            Assert.Equal(Colors.Black, Brush(gallery, "Surface.Base").Color);
            Assert.Equal(Color.FromRgb(0x12, 0x12, 0x12), Assert.IsType<SolidColorBrush>(card.Background).Color);
            Assert.Equal((Color)ColorConverter.ConvertFromString("#edf3f2"), galleryBrush.Color);
            Assert.Equal(Colors.White, cardBrush.Color);
            Assert.Same(editorBrush, editor["Surface.Base"]);
            Assert.Equal((Color)ColorConverter.ConvertFromString("#141b22"), editorBrush.Color);
            Assert.Equal(0.52, ((Brush)gallery["Overlay.Dimmer"]).Opacity, 2);
            WorkspaceTheme.Apply(gallery, WorkspaceRole.Gallery, AppTheme.Daylight);
            Assert.Equal(ThemeCatalog.ColorsFor(AppTheme.Daylight)["Surface.Base"].R, Brush(gallery, "Surface.Base").Color.R);
            WorkspaceTheme.Apply(gallery, WorkspaceRole.Gallery, AppTheme.Glass);
            Assert.Equal(ThemeCatalog.ColorsFor(AppTheme.Glass)["Surface.Base"].R, Brush(gallery, "Surface.Base").Color.R);
            Assert.Equal(ThemeCatalog.ColorsFor(AppTheme.Glass)["Surface.Base"].A, Brush(gallery, "Surface.Base").Color.A);
            WorkspaceTheme.Apply(gallery, WorkspaceRole.Gallery, AppTheme.Workspace);
            Assert.Equal(Colors.White, Assert.IsType<SolidColorBrush>(card.Background).Color);
        });
    }

    private static SolidColorBrush Brush(ResourceDictionary resources, string key) =>
        Assert.IsType<SolidColorBrush>(resources[key]);
}
