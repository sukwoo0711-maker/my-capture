using System.IO;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.App.Editing;
using MyCapture.App.Threading;
using MyCapture.Core.Annotations;
using MyCapture.Core.Storage;

namespace MyCapture.App;

internal sealed record DecodedDraftImage(BitmapSource Current, BitmapSource Original,
    AnnotationDocument Document, IReadOnlyDictionary<string, BitmapSource> Assets);

internal static class EditRecoveryPresentation
{
    /// <summary>Decoded thumbnails are this many pixels wide: the 96px frame at 200% scaling.</summary>
    internal const int ThumbnailPixelWidth = 192;

    /// <summary>
    /// What the recovery dialog shows for a draft: its size and a small picture. An image draft is
    /// read from its own current pixels; a video draft from the recording it edits and that
    /// recording's library thumbnail. Anything unreadable leaves a placeholder, never an error.
    /// </summary>
    internal static async Task<DraftRecoveryItem> DescribeAsync(EditDraftStore store, EditDraft draft,
        Func<EditDraft, string?> videoThumbnailPath)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(videoThumbnailPath);
        bool video = draft.Kind != "image";
        int width = 0, height = 0;
        string? picture = null;
        try
        {
            if (video)
            {
                if (draft.Metadata.TryGetValue("recording", out string? recording)
                    && System.Text.Json.JsonSerializer.Deserialize<MyCapture.Platform.Recording.RecordingResult>(
                        recording, MyCapture.Core.Serialization.JsonDefaults.Compact) is { } result)
                    (width, height) = (result.Width, result.Height);
                picture = videoThumbnailPath(draft);
            }
            else
            {
                picture = store.ResolveFile(draft, "base.png");
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
            or System.Text.Json.JsonException or KeyNotFoundException) { }

        (BitmapSource? thumbnail, int pixelWidth, int pixelHeight) = picture is null || !File.Exists(picture)
            ? (null, 0, 0)
            : await StaThreadTask.RunAsync(() => DecodeThumbnail(picture), "MyCapture draft thumbnail decoder");
        if (!video) (width, height) = (pixelWidth, pixelHeight);
        return new DraftRecoveryItem(draft.SessionId, video, draft.SavedUtc, width, height, thumbnail);
    }

    /// <summary>A frozen thumbnail and the full pixel size of the picture it was decoded from.</summary>
    private static (BitmapSource? Thumbnail, int Width, int Height) DecodeThumbnail(string path)
    {
        try
        {
            int width, height;
            using (FileStream header = File.OpenRead(path))
            {
                BitmapFrame frame = BitmapDecoder.Create(header, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
                (width, height) = (frame.PixelWidth, frame.PixelHeight);
            }
            using FileStream stream = File.OpenRead(path);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = Math.Min(ThumbnailPixelWidth, Math.Max(1, width));
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return (image, width, height);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException
            or InvalidOperationException or ArgumentException or System.Runtime.InteropServices.ExternalException)
        {
            return (null, 0, 0);
        }
    }

    // One awaited worker per recovered image; only detached, frozen pixels reach the UI.
    internal static Task<DecodedDraftImage> DecodeImageAsync(EditDraftStore store, EditDraft draft) =>
        StaThreadTask.RunAsync(() =>
        {
            var cache = new Dictionary<string, BitmapSource>(StringComparer.OrdinalIgnoreCase);
            BitmapSource Load(string name)
            {
                string path = store.ResolveFile(draft, name);
                if (cache.TryGetValue(path, out var previous)) return previous;
                using var stream = File.OpenRead(path);
                var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = stream; image.EndInit(); image.Freeze();
                cache.Add(path, image);
                return image;
            }
            var document = AnnotationDocument.TryFromJson(draft.Document) ?? throw new IOException("Invalid draft document.");
            var assets = draft.Metadata.Where(x => x.Key.StartsWith("asset:", StringComparison.Ordinal))
                .ToDictionary(x => x.Key[6..], x => Load(x.Value));
            return new DecodedDraftImage(Load("base.png"), Load("original.png"), document, assets);
        }, "MyCapture draft image decoder");

    // SessionEnding is synchronous. Drain dispatcher continuations before returning to WPF
    // shutdown, but never cancel an OS logout or extend this beyond the bounded budget.
    internal static bool DrainSessionEnding(Dispatcher dispatcher, Func<Task> flush, TimeSpan budget, Action<string> log)
    {
        dispatcher.VerifyAccess();
        Task pending;
        try { pending = flush(); }
        catch (Exception error) { log(error.Message); return false; }
        if (!pending.IsCompleted)
        {
            var deadline = System.Diagnostics.Stopwatch.StartNew();
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.Send, dispatcher) { Interval = TimeSpan.FromMilliseconds(10) };
            timer.Tick += (_, _) =>
            {
                if (pending.IsCompleted || deadline.Elapsed >= budget) frame.Continue = false;
            };
            timer.Start();
            try { Dispatcher.PushFrame(frame); }
            catch (Exception error) { log(error.Message); return false; }
            finally { timer.Stop(); }
        }
        if (!pending.IsCompleted)
        {
            _ = pending.ContinueWith(done => { _ = done.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            log("Deadline reached; the last durable revision remains available."); return false;
        }
        try { pending.GetAwaiter().GetResult(); return true; }
        catch (Exception error) { log(error.Message); return false; }
    }
}
