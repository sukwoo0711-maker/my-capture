using System.Security.Cryptography;
using System.Text.Json;
using MyCapture.Core.Serialization;

namespace MyCapture.Core.Storage;

public sealed record EditDraft(Guid SessionId, long Revision, string Kind, string Document,
    Dictionary<string, string> Metadata, Dictionary<string, string> Files, DateTimeOffset SavedUtc);

/// <summary>
/// Immutable revisions become recoverable only after their durable ready marker exists.
/// Drafts are app-owned working copies: a retired session is deleted from disk, never moved to
/// the user's Recycle Bin. A video draft references its immutable library original by path and
/// hash instead of copying it.
/// </summary>
public sealed class EditDraftStore
{
    private readonly string _root;
    private readonly Action<string> _deleteSessionDirectory;

    /// <param name="deleteSessionDirectory">Test seam that replaces the direct deletion of a
    /// retired session directory. Production omits it.</param>
    public EditDraftStore(string root, Action<string>? deleteSessionDirectory = null)
    {
        _root = Path.GetFullPath(root);
        _deleteSessionDirectory = deleteSessionDirectory ?? DeleteSessionDirectory;
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
        string blobs = Path.Combine(parent, "blobs");
        AssertNoLinks(blobs);
        Directory.CreateDirectory(blobs);
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, bytes) in files)
        {
            ValidateName(name);
            string hash = Convert.ToHexString(SHA256.HashData(bytes));
            string destination = Path.Combine(blobs, hash + ".blob");
            if (!File.Exists(destination))
            {
                string pending = Path.Combine(blobs, Guid.NewGuid().ToString("N") + ".pending");
                await DurableWriteAsync(pending, bytes, token).ConfigureAwait(false);
                File.Move(pending, destination);
            }
            hashes[name] = hash;
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
        // Small revision markers stay until this session is retired. This avoids one delete
        // per debounce; immutable pixel blobs are stored only once.
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
            foreach (string directory in Directory.EnumerateDirectories(sessionPath)
                .OrderByDescending(Path.GetFileName, StringComparer.Ordinal))
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
                    var verified = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var (name, hash) in draft.Files)
                    {
                        ValidateName(name);
                        string file = ResolveFile(draft, name);
                        if (verified.Add(hash) && Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))) != hash)
                            throw new IOException("Draft content does not match its durable marker.");
                    }
                    if (!Path.GetFileName(directory).StartsWith($"{draft.Revision:D20}-", StringComparison.Ordinal)) continue;
                    newest = (draft, directory);
                    break;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { }
            }
            if (newest is { } value) result.Add(value);
        }
        return result;
    }

    /// <summary>SHA-256 of a referenced file, read once without copying it.</summary>
    public static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20,
            FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    public async Task RetireAsync(Guid session)
    {
        string directory = SessionPath(session);
        AssertNoLinks(directory);
        Directory.CreateDirectory(directory);
        string marker = Path.Combine(directory, "retired");
        if (!File.Exists(marker)) await DurableWriteAsync(marker, [1], CancellationToken.None).ConfigureAwait(false);
        // A failed deletion leaves the durable tombstone: completed work cannot reappear.
        AssertNoLinks(directory);
        _deleteSessionDirectory(directory);
    }

    /// <summary>
    /// Deletes a retired session. Revisions and blobs go first and the tombstone last, so an
    /// interrupted deletion can never make a retired revision recoverable again. A link inside
    /// the session is removed itself; its target is never entered.
    /// </summary>
    private static void DeleteSessionDirectory(string directory)
    {
        string tombstone = Path.Combine(directory, "retired");
        foreach (string child in Directory.GetDirectories(directory))
        {
            if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) Directory.Delete(child);
            else Directory.Delete(child, recursive: true);
        }
        foreach (string file in Directory.GetFiles(directory))
        {
            if (!string.Equals(file, tombstone, StringComparison.OrdinalIgnoreCase)) File.Delete(file);
        }
        File.Delete(tombstone);
        Directory.Delete(directory);
    }

    public bool IsRetired(Guid session) => File.Exists(Path.Combine(SessionPath(session), "retired"));

    public string ResolveFile(EditDraft draft, string name)
    {
        ValidateName(name);
        if (!draft.Files.TryGetValue(name, out string? hash) || string.IsNullOrEmpty(hash) || hash.Length != 64
            || hash.Any(c => !char.IsAsciiHexDigit(c))) throw new IOException("Invalid draft blob reference.");
        string path = Path.Combine(SessionPath(draft.SessionId), "blobs", hash + ".blob");
        AssertNoLinks(Path.GetDirectoryName(path)!);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Draft blobs cannot be links.");
        return path;
    }

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
