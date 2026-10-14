using System.Globalization;
using System.IO;
using System.IO.Compression;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;

namespace MyCapture.App.Gallery;

internal sealed record GalleryFolderExportEntry(CaptureRecord Record, long Revision, string Source, string RelativePath);
internal sealed record GalleryFolderExportPlan(
    IReadOnlyList<GalleryFolderExportEntry> Entries, IReadOnlyList<string> Directories, IReadOnlyList<string> RootNames);

/// <summary>Media-only snapshots of virtual trees, shared by shell drag, folder export and ZIP.</summary>
internal sealed class GalleryFolderExportService
{
    private readonly CaptureQueue _queue;
    private readonly string _stagingRoot;
    private readonly Func<DateTimeOffset> _clock;
    internal Action<int>? StagedFileForTest { get; set; }

    internal GalleryFolderExportService(CaptureQueue queue, string? stagingRoot = null,
        Func<DateTimeOffset>? clock = null)
    {
        _queue = queue;
        _stagingRoot = stagingRoot ?? GalleryExportFiles.Child(
            GalleryExportFiles.Child(Path.GetTempPath(), "MyCapture"), "DragExports");
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    internal GalleryFolderExportPlan BuildPlan(CaptureFolderScope scope)
    {
        if (scope.FolderId is { } id && _queue.FindFolder(id) is null)
            throw new InvalidOperationException("The selected folder no longer exists.");
        return Build(_queue.RecordsInScope(scope), _queue.FoldersInScope(scope), tree: true);
    }

    internal GalleryFolderExportPlan BuildCapturePlan(IReadOnlyList<CaptureRecord> records, bool tree = false) =>
        Build(records, [], tree);

    private GalleryFolderExportPlan Build(IReadOnlyList<CaptureRecord> records,
        IReadOnlyList<CaptureFolder> folders, bool tree)
    {
        CaptureRecord[] orderedRecords = records.DistinctBy(record => record.Id)
            .OrderBy(record => record.CreatedAt).ThenBy(record => record.FolderSequence).ThenBy(record => record.Id).ToArray();
        string ParentPath(CaptureRecord record) => !tree ? string.Empty : record.FolderId is { } folderId
            ? FolderPath(folderId)
            : DateOnly.FromDateTime(record.CreatedAt.DateTime).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (CaptureFolder folder in folders) AddParents(FolderPath(folder.Id), directories);
        foreach (CaptureRecord record in orderedRecords)
        {
            if (_queue.Find(record.Id) != record) throw new InvalidOperationException("A capture is no longer available.");
            string parent = ParentPath(record);
            if (parent.Length > 0) AddParents(parent, directories);
        }
        // Reserve every directory before allocating file names, including selection-only exports.
        var used = new HashSet<string>(directories, StringComparer.OrdinalIgnoreCase);
        var entries = new List<GalleryFolderExportEntry>();
        foreach (CaptureRecord record in orderedRecords)
        {
            string parent = ParentPath(record);
            string fileName = _queue.GetExportFileName(record);
            string relative;
            int suffix = 1;
            do
            {
                string name = CaptureFolderNaming.WithCollisionSuffix(fileName, suffix++);
                relative = parent.Length == 0 ? name : parent + "/" + name;
            } while (!used.Add(relative));
            // The plan carries rendered PNG/MP4 paths, never separate originals or metadata files.
            entries.Add(new(record, record.ContentRevision, ResolveSource(record), relative));
        }
        string[] orderedDirectories = directories.OrderBy(path => path.Count(c => c == '/'))
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        string[] roots = orderedDirectories.Concat(entries.Select(entry => entry.RelativePath))
            .Select(path => path.Split('/')[0]).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return new(entries, orderedDirectories, roots);
    }

    private string FolderPath(Guid id)
    {
        IReadOnlyList<CaptureFolder> path = _queue.GetFolderPath(id);
        if (path.Count == 0) throw new InvalidOperationException("The folder no longer exists.");
        return path[0].CreatedDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            + "/" + string.Join("/", path.Select(folder => folder.Name));
    }

    private static void AddParents(string relative, HashSet<string> directories)
    {
        string current = string.Empty;
        foreach (string segment in relative.Split('/'))
        {
            current = current.Length == 0 ? segment : current + "/" + segment;
            directories.Add(current);
        }
    }

    private string ResolveSource(CaptureRecord record)
    {
        if (!record.IsVideo) return _queue.GetFilePath(record, CaptureFileNames.Rendered);
        string rendered = _queue.GetFilePath(record, CaptureFileNames.VideoRendered);
        return File.Exists(rendered) ? rendered : _queue.GetFilePath(record, CaptureFileNames.VideoSource);
    }

    internal async Task<PreparedFolderExport> PrepareAsync(GalleryFolderExportPlan plan,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (plan.RootNames.Count == 0) throw new InvalidOperationException("There is no folder or capture to export.");
        plan = FitStagingPlan(plan);
        var leases = new List<IDisposable>();
        string? batch = null;
        try
        {
            foreach (GalleryFolderExportEntry entry in plan.Entries)
            {
                if (_queue.Find(entry.Record.Id) != entry.Record || entry.Record.ContentRevision != entry.Revision)
                    throw new InvalidOperationException("A capture changed before export.");
                leases.Add(_queue.AcquireEvictionLease(entry.Record.Id));
            }
            batch = await Task.Run(() => Stage(plan, cancellationToken), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (plan.Entries.Any(entry => _queue.Find(entry.Record.Id) != entry.Record
                || entry.Record.ContentRevision != entry.Revision))
                throw new InvalidOperationException("A capture changed during export.");
            return new PreparedFolderExport(batch, plan, leases);
        }
        catch
        {
            if (batch is not null) await Task.Run(() => GalleryExportFiles.DeleteFolderBatchBestEffort(batch));
            foreach (IDisposable lease in leases) lease.Dispose();
            throw;
        }
    }

    internal string PrepareSingle(CaptureRecord record)
    {
        using IDisposable lease = _queue.AcquireEvictionLease(record.Id);
        GalleryFolderExportPlan plan = FitStagingPlan(BuildCapturePlan([record]));
        string batch = Stage(plan, CancellationToken.None);
        return GalleryExportFiles.RelativeChild(batch, plan.Entries[0].RelativePath);
    }

    private GalleryFolderExportPlan FitStagingPlan(GalleryFolderExportPlan plan) =>
        GalleryExportPathBudget.Fit(plan, Path.Combine(_stagingRoot,
            GalleryExportFiles.FolderBatchPrefix + new string('0', 32)).Length);

    private string Stage(GalleryFolderExportPlan plan, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        GalleryExportFiles.CleanupFolderBatches(_stagingRoot, _clock() - GalleryDragExportService.DefaultRetention);
        string batch = GalleryExportFiles.Child(_stagingRoot, GalleryExportFiles.FolderBatchPrefix + Guid.NewGuid().ToString("N"));
        if (!GalleryExportFiles.TryCreateNewDirectory(batch)) throw new IOException("Export namespace collision.");
        try
        {
            foreach (string directory in plan.Directories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                GalleryExportFiles.EnsureDirectory(GalleryExportFiles.RelativeChild(batch, directory));
            }
            for (int index = 0; index < plan.Entries.Count; index++)
            {
                GalleryFolderExportEntry entry = plan.Entries[index];
                GalleryExportFiles.Copy(entry.Source, GalleryExportFiles.RelativeChild(batch, entry.RelativePath), cancellationToken);
                StagedFileForTest?.Invoke(index + 1);
            }
            cancellationToken.ThrowIfCancellationRequested();
            return batch;
        }
        catch
        {
            GalleryExportFiles.DeleteFolderBatchBestEffort(batch);
            throw;
        }
    }

    internal static Task ExportZipAsync(PreparedFolderExport prepared, string destination,
        IProgress<int>? progress = null, CancellationToken cancellationToken = default,
        ImageExportOptions? options = null) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Path.GetFullPath(destination).Length > GalleryExportPathBudget.MaximumPathLength)
            throw new PathTooLongException("Choose a shorter archive path.");
        GalleryFolderExportPlan outputPlan = GalleryExportPathBudget.Fit(prepared.Plan,
            Path.Combine(Path.GetDirectoryName(Path.GetFullPath(destination))!, Path.GetFileNameWithoutExtension(destination)).Length);
        var sources = prepared.Plan.Entries.ToDictionary(e => e.Record.Id, e => e.RelativePath);
        GalleryExportFiles.WriteStreamNew(destination, stream =>
        {
            using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
            foreach (string directory in outputPlan.Directories)
            {
                _ = GalleryExportFiles.RelativeChild(prepared.BatchRoot, directory);
                archive.CreateEntry(directory + "/", CompressionLevel.NoCompression);
            }
            int done = 0;
            foreach (GalleryFolderExportEntry entry in outputPlan.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string source = GalleryExportFiles.RelativeChild(prepared.BatchRoot, sources[entry.Record.Id]);
                using Stream output = archive.CreateEntry(entry.RelativePath, CompressionLevel.NoCompression).Open();
                GalleryImageExport.CopyTo(source, output, options ?? new(), cancellationToken);
                progress?.Report(++done);
            }
            cancellationToken.ThrowIfCancellationRequested();
        }, cancellationToken);
    }, cancellationToken);

    internal static Task<IReadOnlyList<string>> ExportDirectoryAsync(PreparedFolderExport prepared, string destination,
        IProgress<int>? progress = null, CancellationToken cancellationToken = default,
        ImageExportOptions? options = null) => Task.Run<IReadOnlyList<string>>(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        using GalleryExportFiles.DirectoryLease destinationLease = GalleryExportFiles.DirectoryLease.Open(destination, create: false);
        // Root collision suffixes consume up to six extra characters (-9999).
        GalleryFolderExportPlan outputPlan = GalleryExportPathBudget.Fit(prepared.Plan, Path.GetFullPath(destination).Length + 6);
        var sources = prepared.Plan.Entries.ToDictionary(e => e.Record.Id, e => e.RelativePath);
        var roots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var outputs = new List<string>();
        foreach (string root in outputPlan.RootNames.Where(root => outputPlan.Directories.Contains(root)))
        {
            string allocated = GalleryExportFiles.CreateUniqueDirectory(destination, root);
            roots.Add(root, allocated);
            outputs.Add(allocated);
        }
        string Target(string relative)
        {
            string[] parts = relative.Split('/');
            string path = roots[parts[0]];
            foreach (string part in parts.Skip(1)) path = GalleryExportFiles.Child(path, part);
            return path;
        }
        foreach (string directory in outputPlan.Directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GalleryExportFiles.EnsureDirectory(Target(directory));
        }
        int done = 0;
        foreach (GalleryFolderExportEntry entry in outputPlan.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string source = GalleryExportFiles.RelativeChild(prepared.BatchRoot, sources[entry.Record.Id]);
            if (entry.RelativePath.Contains('/'))
                GalleryImageExport.Copy(source, Target(entry.RelativePath), options ?? new(), cancellationToken);
            else
            {
                string? allocated = null;
                for (int suffix = 1; suffix <= 10000; suffix++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string target = GalleryExportFiles.Child(destination,
                        CaptureFolderNaming.WithCollisionSuffix(entry.RelativePath, suffix));
                    try { GalleryImageExport.Copy(source, target, options ?? new(), cancellationToken); allocated = target; break; }
                    catch (IOException) when (File.Exists(target) || Directory.Exists(target)) { }
                }
                if (allocated is null) throw new IOException("No available export file name.");
                outputs.Add(allocated);
            }
            progress?.Report(++done);
        }
        return outputs;
    }, cancellationToken);

    internal sealed class PreparedFolderExport : IDisposable
    {
        private readonly List<IDisposable> _leases;
        private bool _disposed;
        internal string BatchRoot { get; }
        internal GalleryFolderExportPlan Plan { get; }
        internal IReadOnlyList<string> Paths { get; }

        internal PreparedFolderExport(string batchRoot, GalleryFolderExportPlan plan, List<IDisposable> leases)
        {
            BatchRoot = batchRoot; Plan = plan; _leases = leases;
            Paths = plan.RootNames.Select(name => GalleryExportFiles.RelativeChild(batchRoot, name)).ToArray();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            GalleryExportFiles.DeleteFolderBatchBestEffort(BatchRoot);
            foreach (IDisposable lease in _leases) lease.Dispose();
            _leases.Clear();
        }
    }
}
