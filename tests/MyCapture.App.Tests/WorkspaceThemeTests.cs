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
            Assert.False(cardBrush.IsFrozen);
            WorkspaceTheme.Apply(gallery, WorkspaceRole.Gallery, AppTheme.HighContrast);
            Assert.Equal(Colors.Black, galleryBrush.Color);
            Assert.Equal(Color.FromRgb(0x12, 0x12, 0x12), cardBrush.Color);
            Assert.Equal((Color)ColorConverter.ConvertFromString("#141b22"), editorBrush.Color);
            Assert.Equal(0.52, ((Brush)gallery["Overlay.Dimmer"]).Opacity, 2);
            WorkspaceTheme.Apply(gallery, WorkspaceRole.Gallery, AppTheme.Daylight);
            Assert.Equal(ThemeCatalog.ColorsFor(AppTheme.Daylight)["Surface.Base"].R, galleryBrush.Color.R);
            WorkspaceTheme.Apply(gallery, WorkspaceRole.Gallery, AppTheme.Workspace);
            Assert.Equal(Colors.White, cardBrush.Color);
        });
    }
}
