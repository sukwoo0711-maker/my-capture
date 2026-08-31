using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Editing;
using MyCapture.App.Pinning;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Platform.Capture;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class PinImageSaveServiceTests
{
    [Fact]
    public async Task SaveAs_Cancelled_DoesNotCreateAFile()
    {
        using var temp = new TempDirectory();
        PinImageSaveService service = CreateService(temp.Path);
        service.SaveAsPrompt = (_, _) => null;

        PinSaveResult result = await service.SaveAsAsync(AlphaImage(), owner: null);

        Assert.Equal(PinSaveStatus.Cancelled, result.Status);
        Assert.False(Directory.Exists(AppPaths.CreateForRoot(temp.Path).QuickSaveRoot));
        Assert.Empty(Directory.EnumerateFiles(temp.Path, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task SaveAs_ValidPng_PreservesOriginalDimensionsAndAlpha()
    {
        using var temp = new TempDirectory();
        string target = Path.Combine(temp.Path, "chosen.png");
        PinImageSaveService service = CreateService(temp.Path);
        service.SaveAsPrompt = (_, suggested) =>
        {
            Assert.Equal(".png", Path.GetExtension(suggested), ignoreCase: true);
            return target;
        };

        PinSaveResult result = await service.SaveAsAsync(AlphaImage(), owner: null);

        Assert.Equal(PinSaveStatus.Saved, result.Status);
        Assert.Equal(target, result.Path);
        BitmapSource decoded = LoadPng(target);
        Assert.Equal(2, decoded.PixelWidth);
        Assert.Equal(1, decoded.PixelHeight);

        byte[] pixels = CopyAsBgra32(decoded);
        Assert.Equal(0x40, pixels[3]);
        Assert.Equal(0xFF, pixels[7]);
    }

    [Fact]
    public async Task SaveAs_NonPngName_IsRejectedWithoutWriting()
    {
        using var temp = new TempDirectory();
        string target = Path.Combine(temp.Path, "wrong.jpg");
        PinImageSaveService service = CreateService(temp.Path);
        service.SaveAsPrompt = (_, _) => target;

        PinSaveResult result = await service.SaveAsAsync(AlphaImage(), owner: null);

        Assert.Equal(PinSaveStatus.Failed, result.Status);
        Assert.Contains("PNG", result.ErrorMessage, StringComparison.Ordinal);
        Assert.False(File.Exists(target));
        Assert.Empty(Directory.EnumerateFiles(temp.Path, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task QuickSave_ResolvesCollisionsInsteadOfOverwriting()
    {
        using var temp = new TempDirectory();
        PinImageSaveService service = CreateService(temp.Path, pattern: "pin");

        PinSaveResult first = await service.QuickSaveAsync(AlphaImage());
        PinSaveResult second = await service.QuickSaveAsync(AlphaImage());

        Assert.Equal(PinSaveStatus.Saved, first.Status);
        Assert.Equal(PinSaveStatus.Saved, second.Status);
        Assert.Equal("pin.png", Path.GetFileName(first.Path));
        Assert.Equal("pin-2.png", Path.GetFileName(second.Path));
        Assert.True(File.Exists(first.Path));
        Assert.True(File.Exists(second.Path));
    }

    [Fact]
    public async Task SaveAs_OverwriteIsAtomicWithoutLeavingRecoveryBackup()
    {
        using var temp = new TempDirectory();
        string target = Path.Combine(temp.Path, "same.png");
        PinImageSaveService service = CreateService(temp.Path);
        service.SaveAsPrompt = (_, _) => target;

        PinSaveResult first = await service.SaveAsAsync(SolidImage(1, 1, Colors.Red), owner: null);
        PinSaveResult second = await service.SaveAsAsync(SolidImage(3, 2, Colors.Blue), owner: null);

        Assert.Equal(PinSaveStatus.Saved, first.Status);
        Assert.Equal(PinSaveStatus.Saved, second.Status);
        Assert.False(File.Exists(target + AtomicFile.BackupSuffix));
        BitmapSource decoded = LoadPng(target);
        Assert.Equal(3, decoded.PixelWidth);
        Assert.Equal(2, decoded.PixelHeight);
    }

    [Fact]
    public async Task SaveAs_OverwritePreservesAnUnrelatedExistingBakFile()
    {
        using var temp = new TempDirectory();
        string target = Path.Combine(temp.Path, "same.png");
        string sentinelBackup = target + AtomicFile.BackupSuffix;
        await File.WriteAllTextAsync(sentinelBackup, "user-owned sentinel");
        PinImageSaveService service = CreateService(temp.Path);
        service.SaveAsPrompt = (_, _) => target;

        PinSaveResult result = await service.SaveAsAsync(AlphaImage(), owner: null);

        Assert.Equal(PinSaveStatus.Saved, result.Status);
        Assert.Equal("user-owned sentinel", await File.ReadAllTextAsync(sentinelBackup));
    }

    [Fact]
    public async Task QuickSave_ConcurrentRequestsReserveDistinctNamesWithoutLoss()
    {
        using var temp = new TempDirectory();
        PinImageSaveService service = CreateService(temp.Path, pattern: "pin");

        Task<PinSaveResult>[] saves = Enumerable.Range(0, 8)
            .Select(_ => service.QuickSaveAsync(AlphaImage()))
            .ToArray();
        PinSaveResult[] results = await Task.WhenAll(saves);

        Assert.All(results, result => Assert.Equal(PinSaveStatus.Saved, result.Status));
        Assert.Equal(8, results.Select(result => result.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(8, Directory.EnumerateFiles(temp.Path, "*.png", SearchOption.AllDirectories).Count());
    }

    [Fact]
    public async Task QuickSave_PreparationIOException_ReturnsFailureInsteadOfThrowing()
    {
        using var temp = new TempDirectory();
        var service = new PinImageSaveService(
            settings: static () => throw new IOException("settings unavailable"),
            paths: () => AppPaths.CreateForRoot(temp.Path),
            NullLogger<PinImageSaveService>.Instance);

        PinSaveResult result = await service.QuickSaveAsync(AlphaImage());

        Assert.Equal(PinSaveStatus.Failed, result.Status);
        Assert.Contains("settings unavailable", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PinAndEditorSaveAs_ShareOneDestinationSelectionTransaction()
    {
        using var temp = new TempDirectory();
        using var releasePinPrompt = new ManualResetEventSlim(initialState: false);
        var pinPromptEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var editorPromptEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var settings = new AppSettings();
        AppPaths paths = AppPaths.CreateForRoot(temp.Path);
        var queue = new CaptureQueue(paths, settings.Queue, NullLogger<CaptureQueue>.Instance);
        var persistence = new CapturePersistenceService(
            queue,
            paths,
            () => settings.Queue,
            NullLogger<CapturePersistenceService>.Instance);
        var editor = new CaptureCommitService(
            persistence,
            () => settings,
            () => paths,
            NullLogger<CaptureCommitService>.Instance);
        PinImageSaveService pin = CreateService(temp.Path);
        string pinPath = Path.Combine(temp.Path, "pin.png");
        string editorPath = Path.Combine(temp.Path, "editor.png");

        pin.SaveAsPrompt = (_, _) =>
        {
            pinPromptEntered.TrySetResult(true);
            if (!releasePinPrompt.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException("Test did not release the first Save As transaction.");
            }

            return pinPath;
        };
        editor.SaveAsPrompt = _ =>
        {
            editorPromptEntered.TrySetResult(true);
            return editorPath;
        };

        Task<PinSaveResult> pinSave = Task.Run(() => pin.SaveAsAsync(AlphaImage(), owner: null));
        await pinPromptEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Task<bool> editorSave = editor.CommitAsync(
            record: null,
            MakeSaveAsResult(AlphaImage()));

        try
        {
            Task early = await Task.WhenAny(
                editorPromptEntered.Task,
                Task.Delay(TimeSpan.FromMilliseconds(200)));
            Assert.NotSame(editorPromptEntered.Task, early);
        }
        finally
        {
            releasePinPrompt.Set();
        }

        PinSaveResult pinResult = await pinSave.WaitAsync(TimeSpan.FromSeconds(10));
        await editorPromptEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        bool editorResult = await editorSave.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(PinSaveStatus.Saved, pinResult.Status);
        Assert.True(editorResult);
        Assert.True(File.Exists(pinPath));
        Assert.True(File.Exists(editorPath));
    }

    private static PinImageSaveService CreateService(string root, string pattern = "pin")
    {
        var settings = new AppSettings();
        settings.Export.FileNamePattern = pattern;
        AppPaths paths = AppPaths.CreateForRoot(root);
        return new PinImageSaveService(
            () => settings,
            () => paths,
            NullLogger<PinImageSaveService>.Instance);
    }

    private static AnnotationEditingResult MakeSaveAsResult(BitmapSource selected)
    {
        var bounds = new RectD(0, 0, selected.PixelWidth, selected.PixelHeight);
        var frame = new FrozenFrame(selected, bounds, null, 0);
        return new AnnotationEditingResult(
            frame,
            bounds,
            selected,
            AnnotationDocument.CreateFor(selected.PixelWidth, selected.PixelHeight),
            EditorCommitAction.SaveAs,
            new Dictionary<string, BitmapSource>(),
            new Dictionary<string, string>());
    }

    private static BitmapSource AlphaImage()
    {
        byte[] pixels =
        [
            0x10, 0x20, 0x30, 0x40,
            0x50, 0x60, 0x70, 0xFF,
        ];
        BitmapSource image = BitmapSource.Create(
            2,
            1,
            96,
            96,
            PixelFormats.Bgra32,
            palette: null,
            pixels,
            stride: 8);
        image.Freeze();
        return image;
    }

    private static BitmapSource SolidImage(int width, int height, Color color)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int index = 0; index < pixels.Length; index += 4)
        {
            pixels[index] = color.B;
            pixels[index + 1] = color.G;
            pixels[index + 2] = color.R;
            pixels[index + 3] = color.A;
        }

        BitmapSource image = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            palette: null,
            pixels,
            stride: width * 4);
        image.Freeze();
        return image;
    }

    private static BitmapSource LoadPng(string path)
    {
        using var stream = File.OpenRead(path);
        var decoder = new PngBitmapDecoder(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        BitmapSource frame = decoder.Frames[0];
        frame.Freeze();
        return frame;
    }

    private static byte[] CopyAsBgra32(BitmapSource source)
    {
        BitmapSource converted = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int stride = converted.PixelWidth * 4;
        byte[] pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    private sealed class TempDirectory : IDisposable
    {
        internal TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "mycapture-pin-save-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup; a failing assertion must remain the primary failure.
            }
            catch (UnauthorizedAccessException)
            {
                // Best-effort cleanup on scanners briefly retaining a generated PNG.
            }
        }
    }
}
