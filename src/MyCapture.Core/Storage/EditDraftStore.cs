using System.Security.Cryptography;
using System.Text.Json;
using MyCapture.Core.Serialization;

namespace MyCapture.Core.Storage;

public sealed record EditDraft(Guid SessionId, long Revision, string Kind, string Document,
    Dictionary<string, string> Metadata, Dictionary<string, string> Files, DateTimeOffset SavedUtc);

/// <summary>Immutable revisions become recoverable only after their durable ready marker exists.</summary>
public sealed class EditDraftStore
{
    private readonly string _root;
    private readonly Action<string> _recycleDirectory;
    public EditDraftStore(string root, Action<string> recycleDirectory)
    {
        _root = Path.GetFullPath(root);
        _recycleDirectory = recycleDirectory;
    }

    public async Task WriteAsync(Guid session, long revision, string kind, string document,
        Dictionary<string, string> metadata, IReadOnlyDictionary<string, byte[]> files,
        Func<bool> isCurrent, CancellationToken token = default, object? publicationGate = null)
    {
        string parent = SessionPath(session);
        AssertNoLinks(parent);
        Directory.CreateDirectory(parent);
        if (File.Exists(Path.Combine(parent, "retired"))) return;
        string directory = Path.Combine(parent, $"{revision:D20}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, bytes) in files)
        {
            ValidateName(name);
            await DurableWriteAsync(Path.Combine(directory, name), bytes, token).ConfigureAwait(false);
            hashes[name] = Convert.ToHexString(SHA256.HashData(bytes));
        }
        var draft = new EditDraft(session, revision, kind, document, metadata, hashes, DateTimeOffset.UtcNow);
        await DurableWriteAsync(Path.Combine(directory, "ready.pending"),
            JsonSerializer.SerializeToUtf8Bytes(draft, JsonDefaults.Compact), token).ConfigureAwait(false);
        // The caller serializes writers and synchronizes this publication with revision changes.
        lock (publicationGate ?? this)
        {
            if (!isCurrent() || File.Exists(Path.Combine(parent, "retired"))) return;
            File.Move(Path.Combine(directory, "ready.pending"), Path.Combine(directory, "ready.json"));
        }
        foreach (string old in Directory.EnumerateDirectories(parent))
        {
            if (old == directory) continue;
            try { AssertNoLinks(old); _recycleDirectory(old); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Preserve on failure. */ }
        }
    }

    public IReadOnlyList<(EditDraft Draft, string Directory)> ReadAvailable()
    {
        var result = new List<(EditDraft, string)>();
        if (!Directory.Exists(_root)) return result;
        AssertNoLinks(_root);
        foreach (string sessionPath in Directory.EnumerateDirectories(_root))
        {
            try { AssertNoLinks(sessionPath); }
            catch (IOException) { continue; }
            if (!Guid.TryParseExact(Path.GetFileName(sessionPath), "N", out Guid session)
                || File.Exists(Path.Combine(sessionPath, "retired"))) continue;
            (EditDraft Draft, string Directory)? newest = null;
            foreach (string directory in Directory.EnumerateDirectories(sessionPath))
            {
                try
                {
                    AssertNoLinks(directory);
                    string marker = Path.Combine(directory, "ready.json");
                    if (!File.Exists(marker)) continue;
                    EditDraft? draft = JsonSerializer.Deserialize<EditDraft>(File.ReadAllBytes(marker), JsonDefaults.Compact);
                    if (draft is null || draft.SessionId != session || draft.Revision < 0
                        || draft.Kind is not ("image" or "video") || draft.Files is null || draft.Metadata is null
                        || string.IsNullOrWhiteSpace(draft.Document)) continue;
                    foreach (var (name, hash) in draft.Files)
                    {
                        ValidateName(name);
                        string file = Path.Combine(directory, name);
                        if (Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))) != hash)
                            throw new IOException("Draft content does not match its durable marker.");
                    }
                    if (newest is null || draft.Revision > newest.Value.Draft.Revision) newest = (draft, directory);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { }
            }
            if (newest is { } value) result.Add(value);
        }
        return result;
    }

    public async Task<string> EnsureVideoSourceAsync(Guid session, string source)
    {
        string parent = SessionPath(session);
        AssertNoLinks(parent);
        Directory.CreateDirectory(parent);
        string destination = Path.Combine(parent, "original-video.mp4");
        if (File.Exists(destination)) return destination;
        string pending = destination + "." + Guid.NewGuid().ToString("N") + ".pending";
        await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true))
        await using (var output = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            65536, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await input.CopyToAsync(output).ConfigureAwait(false);
            await output.FlushAsync().ConfigureAwait(false);
            output.Flush(true);
        }
        File.Move(pending, destination);
        return destination;
    }

    public async Task RetireAsync(Guid session)
    {
        string directory = SessionPath(session);
        AssertNoLinks(directory);
        Directory.CreateDirectory(directory);
        string marker = Path.Combine(directory, "retired");
        if (!File.Exists(marker)) await DurableWriteAsync(marker, [1], CancellationToken.None).ConfigureAwait(false);
        // A failed recycle leaves the durable tombstone: completed work cannot reappear.
        AssertNoLinks(directory);
        _recycleDirectory(directory);
    }

    public bool IsRetired(Guid session) => File.Exists(Path.Combine(SessionPath(session), "retired"));

    private string SessionPath(Guid session) => Path.Combine(_root, session.ToString("N"));
    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || Path.GetFileName(name) != name
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains('/') || name.Contains('\\'))
            throw new IOException("Draft content must stay inside its revision directory.");
    }
    private static void AssertNoLinks(string path)
    {
        for (DirectoryInfo? current = new(path); current is not null; current = current.Parent)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Draft paths cannot traverse links.");
    }
    private static async Task DurableWriteAsync(string path, byte[] bytes, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            65536, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(bytes, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }
}
