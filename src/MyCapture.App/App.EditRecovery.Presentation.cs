using System.IO;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.App.Threading;
using MyCapture.Core.Annotations;
using MyCapture.Core.Storage;

namespace MyCapture.App;

internal sealed record DecodedDraftImage(BitmapSource Current, BitmapSource Original,
    AnnotationDocument Document, IReadOnlyDictionary<string, BitmapSource> Assets);

internal static class EditRecoveryPresentation
{
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
