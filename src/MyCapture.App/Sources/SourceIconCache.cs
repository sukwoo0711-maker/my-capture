using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.Core.Capture;
using MyCapture.Core.Storage;
using MyCapture.Platform.Shell;

namespace MyCapture.App.Sources;

/// <summary>
/// Source application icons, read once per application. Controls ask <see cref="TryGet"/>, which
/// only looks in memory, and otherwise show their glyph until <see cref="Request"/> delivers the
/// icon. Reads run on one background STA thread: first the disk cache, then the shell. A read that
/// outlives <see cref="ReadDeadline"/> is abandoned with its thread, and that application keeps its
/// glyph for the session. Icons are frozen before they leave the thread. Nothing per capture is
/// stored: the disk cache holds one small PNG per application, and the icons it outgrows go to the
/// Recycle Bin.
/// </summary>
internal sealed class SourceIconCache : IDisposable
{
    internal const int MemoryCapacity = 128;
    internal const int DiskFileLimit = 300;
    internal const long DiskByteLimit = 4L * 1024 * 1024;
    /// <summary>How many icons one pruning pass tries to move; a new icon puts the cache one over.</summary>
    internal const int PruneMoveLimit = 4;
    internal static readonly TimeSpan ReadDeadline = TimeSpan.FromSeconds(2);
    private static readonly RecoverableFileRemoval Removal = new();

    internal readonly record struct FileStamp(long WriteTicksUtc, long Length);

    private sealed record Entry(string Identity, ImageSource? Image);

    private sealed record Work(string Identity, string AppPath, string AppUserModelId);

    private readonly string? _diskRoot;
    private readonly Func<string, BitmapSource?> _readExecutable;
    private readonly Func<string, BitmapSource?> _readPackagedApp;
    private readonly Func<string, FileStamp?> _stamp;
    private readonly Func<long> _now;
    private readonly Func<string, CleanupReceipt>? _recycle;
    private readonly object _gate = new();
    private readonly Dictionary<string, LinkedListNode<Entry>> _memory = new(StringComparer.Ordinal);
    private readonly LinkedList<Entry> _order = new();
    private readonly Dictionary<string, List<(Dispatcher Dispatcher, Action<ImageSource> Ready)>> _waiting = new(StringComparer.Ordinal);
    private readonly Queue<Work> _urgent = new();
    private readonly Queue<Work> _background = new();
    private Thread? _worker;
    private Work? _current;
    private long _currentStarted;
    private bool _disposed;
    private volatile bool _pruneDue;

    internal static SourceIconCache Shared { get; private set; } = new(diskRoot: null);

    /// <summary>Gives the shared cache its disk folder (<c>AppPaths.SourceIconsRoot</c>). Tests leave it memory-only.</summary>
    internal static void Configure(string? diskRoot)
    {
        SourceIconCache previous = Shared;
        Shared = new SourceIconCache(diskRoot);
        previous.Dispose();
    }

    internal SourceIconCache(string? diskRoot, Func<string, BitmapSource?>? readExecutable = null,
        Func<string, BitmapSource?>? readPackagedApp = null, Func<string, FileStamp?>? stamp = null, Func<long>? now = null,
        Func<string, CleanupReceipt>? recycle = null)
    {
        _diskRoot = ValidatedRoot(diskRoot);
        _readExecutable = readExecutable ?? SourceAppIconReader.ReadExecutable;
        _readPackagedApp = readPackagedApp ?? SourceAppIconReader.ReadPackagedApp;
        _stamp = stamp ?? ReadStamp;
        _now = now ?? (static () => Environment.TickCount64);
        _recycle = recycle;
    }

    /// <summary>The application a source's icon belongs to; empty when none is known.</summary>
    internal static string Identity(CaptureProvenance source) =>
        source.AppUserModelId.Length > 0 ? "aumid|" + source.AppUserModelId
        : source.AppExecutablePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? "exe|" + source.AppExecutablePath.ToUpperInvariant() : string.Empty;

    /// <summary>
    /// The disk key: a packaged application by its ID, an executable by its path, write time and size,
    /// so an updated application gets its new icon. Null when the executable cannot be read.
    /// </summary>
    internal static string? DiskKey(string appPath, string appUserModelId, FileStamp? stamp) =>
        appUserModelId.Length > 0 ? "aumid|" + appUserModelId
        : stamp is { } file ? $"exe|{appPath.ToUpperInvariant()}|{file.WriteTicksUtc}|{file.Length}" : null;

    internal static string DiskFileName(string key) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32].ToLowerInvariant() + ".png";

    internal int MemoryCount { get { lock (_gate) return _memory.Count; } }

    /// <summary>Memory only; true when the application was read, with a null icon when it has none.</summary>
    internal bool TryGet(CaptureProvenance source, out ImageSource? image)
    {
        image = null;
        string identity = Identity(source);
        if (identity.Length == 0) return false;
        lock (_gate)
        {
            if (!_memory.TryGetValue(identity, out LinkedListNode<Entry>? node)) return false;
            _order.Remove(node);
            _order.AddFirst(node);
            image = node.Value.Image;
            return true;
        }
    }

    /// <summary>
    /// Queues a read ahead of any prewarming; <paramref name="ready"/> runs later on the calling
    /// thread's dispatcher, only when an icon was found.
    /// </summary>
    internal void Request(CaptureProvenance source, Action<ImageSource> ready)
    {
        ArgumentNullException.ThrowIfNull(ready);
        string identity = Identity(source);
        if (identity.Length == 0) return;
        ImageSource? known = null;
        lock (_gate)
        {
            if (_disposed) return;
            if (_memory.TryGetValue(identity, out LinkedListNode<Entry>? node))
            {
                known = node.Value.Image;
                if (known is null) return;
            }
            else
            {
                if (!_waiting.TryGetValue(identity, out var list)) _waiting[identity] = list = [];
                list.Add((Dispatcher.CurrentDispatcher, ready));
                Enqueue(_urgent, new Work(identity, source.AppExecutablePath, source.AppUserModelId));
                return;
            }
        }
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, ready, known);
    }

    /// <summary>Reads each distinct application once, after every pending request.</summary>
    internal void Prewarm(IEnumerable<CaptureProvenance> sources)
    {
        lock (_gate)
        {
            if (_disposed) return;
            foreach (CaptureProvenance source in sources)
            {
                string identity = Identity(source);
                if (identity.Length == 0 || _memory.ContainsKey(identity)) continue;
                Enqueue(_background, new Work(identity, source.AppExecutablePath, source.AppUserModelId));
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _urgent.Clear();
            _background.Clear();
            _waiting.Clear();
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>Under <see cref="_gate"/>: queues work once and keeps a live worker.</summary>
    private void Enqueue(Queue<Work> queue, Work work)
    {
        if (_current?.Identity == work.Identity || _urgent.Any(w => w.Identity == work.Identity)) return;
        if (queue == _background && _background.Any(w => w.Identity == work.Identity)) return;
        queue.Enqueue(work);
        if (_worker is not null && _current is { } stuck && _now() - _currentStarted > ReadDeadline.TotalMilliseconds)
        {
            // A shell read that hangs owns its thread; leave it and remember the application as iconless.
            Remember(stuck.Identity, null);
            _waiting.Remove(stuck.Identity);
            _worker = null;
            _current = null;
        }
        if (_worker is null)
        {
            var worker = new Thread(Run) { IsBackground = true, Name = "MyCapture source icons", Priority = ThreadPriority.BelowNormal };
            worker.SetApartmentState(ApartmentState.STA);
            _worker = worker;
            worker.Start();
        }
        Monitor.PulseAll(_gate);
    }

    private void Run()
    {
        Thread self = Thread.CurrentThread;
        while (true)
        {
            Work work;
            lock (_gate)
            {
                while (!_disposed && ReferenceEquals(_worker, self) && _urgent.Count == 0 && _background.Count == 0)
                    Monitor.Wait(_gate);
                if (_disposed || !ReferenceEquals(_worker, self)) return;
                work = _urgent.Count > 0 ? _urgent.Dequeue() : _background.Dequeue();
                if (_memory.ContainsKey(work.Identity)) continue;
                _current = work;
                _currentStarted = _now();
            }

            ImageSource? image = null;
            try { image = Read(work); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { image = null; }

            List<(Dispatcher Dispatcher, Action<ImageSource> Ready)>? waiting;
            lock (_gate)
            {
                // An abandoned thread's late result is dropped; the application already counts as iconless.
                if (!ReferenceEquals(_worker, self) || _disposed) return;
                _current = null;
                Remember(work.Identity, image);
                _waiting.Remove(work.Identity, out waiting);
            }
            if (image is not null && waiting is not null)
            {
                foreach (var (dispatcher, ready) in waiting)
                {
                    if (dispatcher.HasShutdownStarted) continue;
                    dispatcher.BeginInvoke(DispatcherPriority.Background, ready, image);
                }
            }
            PruneAfterStore();
        }
    }

    /// <summary>
    /// Runs once the icon is on its way, outside the read that <see cref="ReadDeadline"/> times: a move
    /// to the Recycle Bin is a Shell operation, and a slow one must not cost the application its icon.
    /// </summary>
    private void PruneAfterStore()
    {
        if (!_pruneDue || _diskRoot is null) return;
        _pruneDue = false;
        try { Prune(_diskRoot, DiskFileLimit, DiskByteLimit, _recycle); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Upkeep only: what is still over a limit is tried again after the next new icon.
        }
    }

    /// <summary>Under <see cref="_gate"/>.</summary>
    private void Remember(string identity, ImageSource? image)
    {
        if (_memory.TryGetValue(identity, out LinkedListNode<Entry>? existing)) _order.Remove(existing);
        var node = _order.AddFirst(new Entry(identity, image));
        _memory[identity] = node;
        while (_memory.Count > MemoryCapacity && _order.Last is { } oldest)
        {
            _order.RemoveLast();
            _memory.Remove(oldest.Value.Identity);
        }
    }

    private BitmapSource? Read(Work work)
    {
        FileStamp? stamp = work.AppUserModelId.Length == 0 ? _stamp(work.AppPath) : null;
        string? key = DiskKey(work.AppPath, work.AppUserModelId, stamp);
        if (key is null) return null;
        string? file = CacheFile(DiskFileName(key), ".png");
        if (file is not null && LoadPng(file) is { } cached) return cached;

        BitmapSource? icon = work.AppUserModelId.Length > 0 ? _readPackagedApp(work.AppUserModelId) : null;
        if (icon is null && work.AppPath.Length > 0 && (stamp ?? _stamp(work.AppPath)) is not null)
            icon = _readExecutable(work.AppPath);
        if (icon is null) return null;
        if (!icon.IsFrozen) icon.Freeze();
        if (file is not null) Store(file, icon);
        return icon;
    }

    internal static BitmapSource? LoadPng(string file)
    {
        try
        {
            if (!File.Exists(file)) return null;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.UriSource = new Uri(file, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            // The last use orders pruning; a failure to mark it only makes the icon older.
            try { File.SetLastWriteTimeUtc(file, DateTime.UtcNow); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return image;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
            or ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    private void Store(string file, BitmapSource icon)
    {
        try
        {
            ValidatedFilePaths.CreateDirectory(_diskRoot!);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(icon));
            if (CacheFile(Path.GetFileName(file) + ".tmp", ".tmp") is not { } temporary) return;
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                encoder.Save(stream);
            File.Move(temporary, file, overwrite: true);
            _pruneDue = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
            or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            // The icon stays in memory for this session.
        }
    }

    /// <summary>
    /// Moves the least recently used icons beyond the file count or total size to the Recycle Bin, the
    /// oldest first, and returns what was moved. Nothing is deleted outright: an icon Windows cannot
    /// recycle stays in the folder. One pass tries at most <see cref="PruneMoveLimit"/> icons, so a
    /// Recycle Bin that refuses them costs each new icon no more than that many attempts.
    /// </summary>
    internal static IReadOnlyList<CleanupReceipt> Prune(string root, int fileLimit, long byteLimit,
        Func<string, CleanupReceipt>? recycle = null)
    {
        recycle ??= Removal.RecycleFile;
        var files = new DirectoryInfo(ValidatedFilePaths.DirectoryRoot(root)).GetFiles("*.png").OrderByDescending(f => f.LastWriteTimeUtc).ToList();
        // Newest first: from the first icon over a limit to the end, every icon is over it.
        int kept = 0;
        long total = 0;
        for (; kept < files.Count && kept < fileLimit; kept++)
        {
            total += files[kept].Length;
            if (total > byteLimit) break;
        }
        var moved = new List<CleanupReceipt>();
        for (int index = files.Count - 1, tried = 0; index >= kept && tried < PruneMoveLimit; index--, tried++)
        {
            try { moved.Add(recycle(files[index].FullName)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return moved;
    }

    /// <summary>The cache folder only when it is a plain local path; otherwise the cache stays in memory.</summary>
    private static string? ValidatedRoot(string? diskRoot)
    {
        if (string.IsNullOrWhiteSpace(diskRoot)) return null;
        try { return ValidatedFilePaths.DirectoryRoot(diskRoot); }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>A file named by the cache's own hash, confined to the cache folder.</summary>
    private string? CacheFile(string name, string extension)
    {
        if (_diskRoot is null) return null;
        try { return ValidatedFilePaths.FileWithinRoot(_diskRoot, name, extension); }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    private static FileStamp? ReadStamp(string path)
    {
        try
        {
            var file = new FileInfo(path);
            return file.Exists ? new FileStamp(file.LastWriteTimeUtc.Ticks, file.Length) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
            or ArgumentException or System.Security.SecurityException)
        {
            return null;
        }
    }
}
