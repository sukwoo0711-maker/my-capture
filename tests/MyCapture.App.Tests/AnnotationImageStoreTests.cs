using System.IO;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Editing;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// The inserted-image store must decode a file without holding a lock on it, since the
/// user must be free to move or delete the file they just inserted.
/// </summary>
/// <remarks>
/// WPF imaging (encoding a fixture PNG, decoding through <see cref="BitmapImage"/>) is run
/// on a dedicated STA thread so the tests behave the same as they would under the UI
/// thread, independent of the runner's apartment state.
/// </remarks>
public sealed class AnnotationImageStoreTests
{
    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException($"STA body threw: {failure}");
        }
    }

    private static string WriteTempPng(int width, int height)
    {
        string path = Path.Combine(Path.GetTempPath(), $"mycapture-test-{Guid.NewGuid():N}.png");
        var bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        byte[] pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = 0x80;
        }

        bitmap.WritePixels(new System.Windows.Int32Rect(0, 0, width, height), pixels, width * 4, 0);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
        {
            encoder.Save(stream);
        }

        return path;
    }

    [Fact]
    public void LoadFromFile_DecodesAndDoesNotLockSource() => RunSta(() =>
    {
        string path = WriteTempPng(32, 16);
        try
        {
            Assert.True(new FileInfo(path).Length > 0, "Fixture PNG was empty.");

            var store = new AnnotationImageStore();
            (BitmapSource Bitmap, string AssetFileName)? loaded = store.LoadFromFile(path);

            Assert.NotNull(loaded);
            Assert.Equal(32, loaded!.Value.Bitmap.PixelWidth);
            Assert.Equal(16, loaded.Value.Bitmap.PixelHeight);
            Assert.EndsWith(".png", loaded.Value.AssetFileName);

            // The store must not hold the file open: deleting immediately must succeed.
            File.Delete(path);
            Assert.False(File.Exists(path));

            // The decoded pixels survive the source file being gone.
            Assert.NotNull(store.Get(loaded.Value.AssetFileName));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    });

    [Fact]
    public void LoadFromFile_ReturnsNullForNonImage() => RunSta(() =>
    {
        string path = Path.Combine(Path.GetTempPath(), $"mycapture-test-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, "not an image");
        try
        {
            var store = new AnnotationImageStore();
            Assert.Null(store.LoadFromFile(path));
        }
        finally
        {
            File.Delete(path);
        }
    });

    [Fact]
    public void SourcesFor_ReportsOnlyUsedAssets() => RunSta(() =>
    {
        string a = WriteTempPng(10, 10);
        string b = WriteTempPng(10, 10);
        try
        {
            var store = new AnnotationImageStore();
            string usedName = store.LoadFromFile(a)!.Value.AssetFileName;
            string unusedName = store.LoadFromFile(b)!.Value.AssetFileName;

            IReadOnlyDictionary<string, string> sources = store.SourcesFor([usedName]);

            Assert.True(sources.ContainsKey(usedName));
            Assert.False(sources.ContainsKey(unusedName));
            Assert.Equal(Path.GetFullPath(a), sources[usedName]);
        }
        finally
        {
            File.Delete(a);
            File.Delete(b);
        }
    });
}
