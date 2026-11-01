using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Recording;
using MyCapture.Core.Annotations;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using MyCapture.Core.Recording;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class RecordingWatermarkTests
{
    [Fact]
    public void SnapshotOwnsFrozenPixelsEvenIfRegisteredImageChanges() => StaTestHost.Run(() =>
    {
        var image = new WriteableBitmap(4, 2, 96, 96, PixelFormats.Bgra32, null);
        byte[] red = Enumerable.Range(0, 8).SelectMany(_ => new byte[] { 0, 0, 255, 255 }).ToArray();
        image.WritePixels(new Int32Rect(0, 0, 4, 2), red, 16, 0);
        RecordingWatermarkSnapshot snapshot = RecordingWatermarkSnapshot.Create(new() { Enabled = true }, image);
        image.WritePixels(new Int32Rect(0, 0, 4, 2), new byte[32], 16, 0);
        var stored = Assert.IsAssignableFrom<BitmapSource>(snapshot.Content);
        byte[] pixels = new byte[32]; stored.CopyPixels(pixels, 16, 0);
        Assert.Equal(red, pixels); Assert.True(stored.IsFrozen); Assert.NotSame(image, stored);
    });

    [Fact]
    public void PreparationRereadsAtStartThenLocksImageAndCommandsIncludingPause() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        int reads = 0;
        var settings = new RecordingWatermarkSettings { Enabled = true };
        var controls = Control(new RecordingSettings());
        controls.LoadWatermarkImage = () => { reads++; return Image(reads == 1 ? Colors.Red : Colors.Blue); };
        int writes = 0;
        controls.WatermarkSettingsChanged += _ => writes++;
        try
        {
            Assert.True(controls.ApplyWatermarkSettings(settings));
            Assert.Equal(1, reads); Assert.Equal(1, writes);
            RecordingWatermarkSnapshot preview = controls.WatermarkSnapshotForTest!;
            Assert.True(controls.PrepareWatermarkForStart());
            Assert.Equal(2, reads);
            RecordingWatermarkSnapshot frozen = controls.WatermarkSnapshotForTest!;
            Assert.NotSame(preview, frozen);
            Assert.False(controls.ApplyWatermarkSettings(settings with { Enabled = false }));
            Assert.False(controls.CanEditWatermark);
            controls.PreviewRecordingForTest(paused: true, TimeSpan.FromSeconds(2));
            MenuItem[] commands = controls.CreateMoreMenu().Items.OfType<MenuItem>().ToArray();
            Assert.Equal(3, commands.Length);
            Assert.Equal("워터마크 표시", commands[1].Header);
            Assert.Equal("워터마크 설정…", commands[2].Header);
            Assert.False(commands[1].IsEnabled); Assert.False(commands[2].IsEnabled);
            Assert.Same(frozen, controls.WatermarkSnapshotForTest);
            Assert.Equal(2, reads); Assert.Equal(1, writes);
        }
        finally { controls.Close(); }
        Assert.Null(controls.WatermarkSnapshotForTest);
    });

    [Fact]
    public void MissingImageAndBlankTextBlockStartWithoutRecorderOrProfileAccess() => StaTestHost.Run(() =>
    {
        var controls = Control(new RecordingSettings { Watermark = new() { Enabled = true } });
        try
        {
            Assert.False(controls.PrepareWatermarkForStart());
            Assert.True(controls.CanEditWatermark);
            Assert.Null(controls.WatermarkSnapshotForTest);
            controls.LoadWatermarkImage = () => throw new System.IO.FileFormatException("Synthetic invalid PNG.");
            Assert.False(controls.PrepareWatermarkForStart());
            Assert.True(controls.ApplyWatermarkSettings(new() { Enabled = true, Source = RecordingWatermarkSource.Text, Text = "  " }));
            Assert.False(controls.PrepareWatermarkForStart());
            Assert.True(controls.ApplyWatermarkSettings(new() { Enabled = false }));
            Assert.True(controls.PrepareWatermarkForStart());
        }
        finally { controls.Close(); }
    });

    [Fact]
    public void OverlayUsesPhysicalCoordinatesAtMixedDpiAndNegativeMonitorOrigin() => StaTestHost.Run(() =>
    {
        var settings = new RecordingWatermarkSettings { Enabled = true, Anchor = StampAnchor.BottomRight };
        RecordingWatermarkSnapshot snapshot = RecordingWatermarkSnapshot.Create(settings, Image(Colors.Blue, 500, 250));
        var region = new RectD(-1600, -400, 1000, 600);
        var window = new RecordingWatermarkWindow(region, 1.5, snapshot);
        try
        {
            RectD before = window.PhysicalBounds;
            Assert.Equal(new RectD(-766, 109, 150, 75), before);
            Assert.Equal(100, window.Width, 6);
            Assert.True(window.AllowsTransparency); Assert.False(window.ShowActivated); Assert.False(window.IsHitTestVisible);
            Assert.Equal(0.7, window.ImageForTest.Opacity);
            window.UpdateRecordingRegion(region, 2);
            Assert.Equal(before, window.PhysicalBounds);
            Assert.Equal(75, window.Width, 6);
            window.UpdateRecordingRegion(new RectD(-10, -20, 8, 4), 1.25);
            RectD bounds = window.PhysicalBounds;
            Assert.True(bounds.Left >= -10 && bounds.Top >= -20 && bounds.Right <= -2 && bounds.Bottom <= -16);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void TextSnapshotIsVectorAndSettingsDialogDoesNotMutateInput() => StaTestHost.Run(() =>
    {
        var settings = new RecordingWatermarkSettings { Enabled = true, Source = RecordingWatermarkSource.Text, Text = "Example", TextColor = "#00AAFF" };
        RecordingWatermarkSnapshot snapshot = RecordingWatermarkSnapshot.Create(settings, null);
        Assert.IsType<DrawingImage>(snapshot.Content); Assert.True(snapshot.Content.IsFrozen);
        Assert.Equal(settings, snapshot.Settings);
        var dialog = new RecordingWatermarkSettingsDialog(settings, imageFolder: null);
        try { Assert.Null(dialog.Result); Assert.Equal("Example", settings.Text); }
        finally { dialog.Close(); }
    });

    [Fact]
    public void SettingsRefreshUpdatesPreparationWithoutSavingAndLockedDialogRejectsSave() => StaTestHost.Run(() =>
    {
        var controls = Control(new RecordingSettings());
        int writes = 0;
        controls.WatermarkSettingsChanged += _ => writes++;
        var initial = new RecordingWatermarkSettings { Enabled = true, Source = RecordingWatermarkSource.Text, Text = "First" };
        try
        {
            Assert.True(controls.RefreshWatermarkSettings(initial));
            Assert.Equal("First", controls.WatermarkSnapshotForTest!.Settings.Text);
            Assert.True(controls.RefreshWatermarkSettings(initial with { Text = "Second", Anchor = StampAnchor.TopLeft }));
            Assert.Equal("Second", controls.WatermarkSnapshotForTest!.Settings.Text);
            Assert.Equal(StampAnchor.TopLeft, controls.WatermarkSnapshotForTest.Settings.Anchor);
            Assert.Equal(0, writes);
            Assert.True(controls.PrepareWatermarkForStart());
            Assert.False(controls.RefreshWatermarkSettings(initial));
            Assert.Equal("Second", controls.WatermarkSnapshotForTest!.Settings.Text);
        }
        finally { controls.Close(); }
        var dialog = new RecordingWatermarkSettingsDialog(initial, null, isLocked: () => true);
        try
        {
            var scroll = Assert.IsType<ScrollViewer>(dialog.Content);
            var panel = Assert.IsType<StackPanel>(scroll.Content);
            var actions = Assert.IsType<StackPanel>(panel.Children[panel.Children.Count - 1]);
            Button save = actions.Children.OfType<Button>().Single(button => button.IsDefault);
            save.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.Null(dialog.Result);
        }
        finally { dialog.Close(); }
    });

    internal static BitmapSource Image(Color color, int width = 20, int height = 10)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int index = 0; index < pixels.Length; index += 4)
        { pixels[index] = color.B; pixels[index + 1] = color.G; pixels[index + 2] = color.R; pixels[index + 3] = color.A; }
        BitmapSource image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        image.Freeze(); return image;
    }

    private static RecordingControlWindow Control(RecordingSettings settings) => new(new RectD(100, 100, 800, 450), settings,
        () => throw new InvalidOperationException("Recorder must not be created in watermark unit tests."),
        () => throw new InvalidOperationException("Output must not be created."), NullLogger.Instance);
}
