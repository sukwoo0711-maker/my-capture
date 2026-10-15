using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Editing;
using MyCapture.App.Threading;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Imaging;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class EditorSaveAsExportOptionsTests
{
    private const string SourceUrl = "https://example.com/capture";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SelectedOptionsApplyToThePublishedCopyAndAreRememberedOnlyAfterSuccess(bool includeUrl) => await StaThreadTask.RunAsync(() =>
    {
        var (commit, settings, store, root) = CreateService();
        ImageExportOptions previous = settings.Export.LastImageExport;
        var selected = new ImageExportOptions { LongEdge = 800, IncludeSourceUrl = includeUrl };
        string destination = Path.Combine(root, "published.png");
        commit.ExportOptionsPrompt = _ => selected;
        commit.SaveAsPrompt = _ =>
        {
            Assert.Equal(previous, settings.Export.LastImageExport);
            Assert.Equal(previous, store.Load().Export.LastImageExport);
            return destination;
        };
        int remembered = 0;
        commit.PersistExportSettings = () =>
        {
            Assert.True(File.Exists(destination));
            store.Save(settings);
            remembered++;
        };

        bool saved = commit.CommitAsync(null, CreateResult(), namingRecord: new CaptureRecord { SourcePageUrl = SourceUrl })
            .GetAwaiter().GetResult();

        Assert.True(saved);
        Assert.Equal(1, remembered);
        Assert.Equal(selected, settings.Export.LastImageExport);
        Assert.Equal(selected, store.Load().Export.LastImageExport);
        byte[] bytes = File.ReadAllBytes(destination);
        using var stream = new MemoryStream(bytes);
        BitmapSource published = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        Assert.Equal((800, 400), (published.PixelWidth, published.PixelHeight));
        Assert.Equal(includeUrl ? SourceUrl : string.Empty, PngSourceUrlMetadata.TryReadSourceUrl(bytes));
        return true;
    });

    [Theory]
    [InlineData("options-cancelled")]
    [InlineData("destination-cancelled")]
    [InlineData("invalid-extension")]
    [InlineData("publication-failed")]
    public async Task UnpublishedExportLeavesLiveAndStoredOptionsUnchanged(string outcome) => await StaThreadTask.RunAsync(() =>
    {
        var (commit, settings, store, root) = CreateService();
        ImageExportOptions previous = settings.Export.LastImageExport;
        var selected = new ImageExportOptions { LongEdge = 800, IncludeSourceUrl = true };
        string blocker = Path.Combine(root, "existing-file");
        File.WriteAllText(blocker, "A file cannot be an export directory.");
        commit.ExportOptionsPrompt = _ => outcome == "options-cancelled" ? null : selected;
        commit.SaveAsPrompt = _ => outcome switch
        {
            "options-cancelled" => throw new InvalidOperationException("The destination must not be requested after cancelling options."),
            "destination-cancelled" => null,
            "invalid-extension" => Path.Combine(root, "capture.jpg"),
            _ => Path.Combine(blocker, "capture.png"),
        };
        int remembered = 0;
        commit.PersistExportSettings = () => { remembered++; store.Save(settings); };

        bool saved = commit.CommitAsync(null, CreateResult()).GetAwaiter().GetResult();

        Assert.False(saved);
        Assert.Equal(0, remembered);
        Assert.Equal(previous, settings.Export.LastImageExport);
        Assert.Equal(previous, store.Load().Export.LastImageExport);
        Assert.False(File.Exists(Path.Combine(root, "capture.jpg")));
        return true;
    });

    private static (CaptureCommitService Commit, AppSettings Settings, SettingsStore Store, string Root) CreateService()
    {
        string root = ExportTestDirectory.Create();
        AppPaths paths = AppPaths.CreateForRoot(root);
        AppSettings settings = NewInstallDefaults.Create();
        var store = new SettingsStore(paths, NullLogger<SettingsStore>.Instance);
        store.Save(settings);
        var queue = new CaptureQueue(paths, settings.Queue, NullLogger<CaptureQueue>.Instance);
        var persistence = new CapturePersistenceService(queue, paths, () => settings.Queue, NullLogger<CapturePersistenceService>.Instance);
        var commit = new CaptureCommitService(persistence, () => settings, () => paths,
            NullLogger<CaptureCommitService>.Instance, (_, _) => Task.FromResult(true));
        return (commit, settings, store, root);
    }

    private static AnnotationEditingResult CreateResult()
    {
        const int width = 1000, height = 500;
        BitmapSource image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null,
            new byte[width * height * 4], width * 4);
        image.Freeze();
        var region = new RectD(0, 0, width, height);
        var frame = new FrozenFrame(image, region, null, 0);
        return new AnnotationEditingResult(frame, region, image, AnnotationDocument.CreateFor(width, height),
            EditorCommitAction.SaveAs, new Dictionary<string, BitmapSource>(), new Dictionary<string, string>());
    }
}
