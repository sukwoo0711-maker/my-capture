using System.Reflection;
using System.Windows.Controls;
using MyCapture.App.Gallery;
using MyCapture.Core.Queue;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// Guards the gallery overflow contract: OCR and Delete live in a context menu whose items are
/// outside the tile's visual tree, so they must resolve their target tile from the item's
/// <see cref="MenuItem.CommandParameter"/> (the robust route) rather than an inherited
/// DataContext. A regression here would silently apply a destructive Delete to the wrong capture.
/// </summary>
public sealed class GalleryOverflowTargetingTests
{
    [Fact]
    public void OverflowMenuItemResolvesTileFromCommandParameter_NotAmbientDataContext()
    {
        StaTestHost.Run(() =>
        {
            GalleryItemViewModel intended = Tile(Guid.NewGuid());
            GalleryItemViewModel ambient = Tile(Guid.NewGuid());

            // A menu item that carries the intended tile as its command parameter but has a
            // *different* tile as its ambient DataContext must resolve the command-parameter tile.
            var item = new MenuItem
            {
                DataContext = ambient,
                CommandParameter = intended,
            };

            GalleryItemViewModel? resolved = InvokeResolveTileFromCommand(item);

            Assert.Same(intended, resolved);
            Assert.NotSame(ambient, resolved);
        });
    }

    [Fact]
    public void OverflowMenuItemFallsBackToDataContextWhenNoCommandParameter()
    {
        StaTestHost.Run(() =>
        {
            GalleryItemViewModel fallback = Tile(Guid.NewGuid());
            var item = new MenuItem { DataContext = fallback };

            GalleryItemViewModel? resolved = InvokeResolveTileFromCommand(item);

            Assert.Same(fallback, resolved);
        });
    }

    private static GalleryItemViewModel? InvokeResolveTileFromCommand(object source)
    {
        MethodInfo method = typeof(GalleryWindow).GetMethod(
            "ResolveTileFromCommand",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ResolveTileFromCommand not found.");

        return (GalleryItemViewModel?)method.Invoke(null, [source]);
    }

    private static GalleryItemViewModel Tile(Guid id) =>
        new(new CaptureRecord { Id = id, Width = 10, Height = 10 }, _ => "missing.jpg", 320);
}
