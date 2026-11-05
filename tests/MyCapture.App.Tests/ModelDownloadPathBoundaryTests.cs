using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Diagnostics;
using MyCapture.Core.Platform;
using MyCapture.Core.Storage;
using MyCapture.Ocr;
using MyCapture.Ocr.Translation;
using Xunit;
using Xunit.Abstractions;

namespace MyCapture.App.Tests;

/// <summary>Local reparse boundaries, with only owned synthetic targets and preserved evidence.</summary>
public sealed class ModelDownloadPathBoundaryTests(ITestOutputHelper output)
{
    private const string ModelName = "unit.bin";
    private const string TranslationFolder = "synthetic-boundary";
    private const string ModelUri = "https://example.test/unit.bin";

    [ModelDownloadBoundaryFact]
    public Task Ocr_ModelRootJunction_PreservesOutsideFiles() => Run(false, Boundary.Root);

    [ModelDownloadBoundaryFact]
    public Task Translation_ModelRootJunction_PreservesOutsideFiles() => Run(true, Boundary.Root);

    [ModelDownloadBoundaryFact(fileSymlink: true)]
    public Task Ocr_DownloadFileLink_PreservesOutsideFiles() => Run(false, Boundary.Download);

    [ModelDownloadBoundaryFact(fileSymlink: true)]
    public Task Translation_DownloadFileLink_PreservesOutsideFiles() => Run(true, Boundary.Download);

    [ModelDownloadBoundaryFact(fileSymlink: true)]
    public Task Ocr_FinalFileLink_PreservesOutsideFiles() => Run(false, Boundary.Final);

    [ModelDownloadBoundaryFact(fileSymlink: true)]
    public Task Translation_FinalFileLink_PreservesOutsideFiles() => Run(true, Boundary.Final);

    [ModelDownloadBoundaryFact]
    public Task Ocr_CachedFileThenRootJunction_RejectsCacheWithoutHashingTarget() => RunCachedRootReplacement(false);

    [ModelDownloadBoundaryFact]
    public Task Translation_CachedFileThenRootJunction_RejectsCacheWhileTargetCannotBeRead() => RunCachedRootReplacement(true);

    [ModelDownloadBoundaryFact]
    public Task Ocr_PreverificationRetiredCleanup_RootJunctionPreservesOutsideFiles() => RunRootCleanup(false);

    [ModelDownloadBoundaryFact]
    public Task Translation_ModelAndPartialCleanup_RootJunctionPreservesOutsideFiles() => RunRootCleanup(true);

    [ModelDownloadBoundaryFact]
    public async Task Translation_ModelFolderJunction_PreservesOutsideFiles()
    {
        string root = CreateEvidenceRoot(translation: true, Boundary.Root);
        output.WriteLine("Preserved translation model-folder boundary evidence: {0}", root);
        string profile = DiagnosticOutputPaths.Create(DiagnosticOutputPaths.Child(root, "profile"));
        string outside = DiagnosticOutputPaths.Create(DiagnosticOutputPaths.Child(root, "outside"));
        AppPaths paths = AppPaths.CreateForRoot(profile);
        DiagnosticOutputPaths.Create(paths.TranslationModelsRoot);
        string link = Path.Combine(paths.TranslationModelsRoot, TranslationFolder);
        string target = DiagnosticOutputPaths.Child(outside, ModelName);
        byte[] original = Enumerable.Repeat((byte)0xA7, 4096).ToArray();
        byte[] payload = Enumerable.Range(0, 4096).Select(i => (byte)i).ToArray();
        WriteNew(target, original);
        WriteNew(DiagnosticOutputPaths.Child(outside, "unrelated-sentinel.bin"), [0x39, 0x72]);
        WriteNew(DiagnosticOutputPaths.Child(root, "expected-original.bin"), original);
        WriteNew(DiagnosticOutputPaths.Child(root, "download-payload.bin"), payload);
        FileEvidence[] before = Snapshot(outside);
        WriteJson(root, "before.json", new { scenario = "translation-model-folder-junction", root,
            modelRoot = paths.TranslationModelsRoot, link, outside, before });
        var handler = new PayloadHandler(payload);
        using var http = new HttpClient(handler);
        var file = new TranslationModelFile(ModelName, ModelUri, Hash(payload), payload.LongLength);
        var store = new TranslationModelStore(paths, http, NullLogger.Instance, [file],
            TimeSpan.FromSeconds(5), _ => long.MaxValue, TranslationFolder);
        Exception? operationError = null;
        Exception? observationError = null;
        FileEvidence[]? afterOperation = null;
        FileEvidence[]? afterCleanup = null;
        LinkCleanup? cleanup = null;
        bool setupComplete = false;
        bool operationReturned = false;
        try
        {
            UpdatePathsTests.CreateJunction(link, outside);
            VerifyLink(root, link, outside, directory: true);
            setupComplete = true;
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await store.DownloadAsync(null, cancellation.Token);
                operationReturned = true;
            }
            catch (Exception error) { operationError = error; }
            afterOperation = Snapshot(outside);
        }
        catch (Exception error) { observationError = error; }
        finally
        {
            cleanup = RecycleOwnedLink(root, link, outside, directory: true);
            try { afterCleanup = Snapshot(outside); }
            catch (Exception error) { observationError ??= error; }
            WriteJson(root, "receipt.json", new { scenario = "translation-model-folder-junction",
                setupComplete, operationReturned, httpCalls = handler.Calls,
                operationError = Error(operationError), observationError = Error(observationError),
                before, afterOperation, afterCleanup, cleanup,
                targetExistsAfterCleanup = File.Exists(target), expectedOriginalSha256 = Hash(original) });
        }

        Assert.True(setupComplete, $"Model-folder junction setup did not complete. See {root}");
        Assert.Null(observationError);
        Assert.NotNull(cleanup);
        Assert.Null(cleanup.Error);
        Assert.NotNull(afterOperation);
        Assert.NotNull(afterCleanup);
        Assert.Equal(before, afterOperation);
        Assert.Equal(afterOperation, afterCleanup);
        Assert.Equal(original, File.ReadAllBytes(target));
        Assert.False(operationReturned);
        TranslationModelException rejection = Assert.IsType<TranslationModelException>(operationError);
        Assert.Equal(TranslationModelError.Disk, rejection.Error);
        Assert.Equal(0, handler.Calls);
    }

    private async Task RunCachedRootReplacement(bool translation)
    {
        string root = CreateEvidenceRoot(translation, Boundary.Root);
        output.WriteLine("Preserved cached-root boundary evidence: {0}", root);
        string profile = DiagnosticOutputPaths.Create(DiagnosticOutputPaths.Child(root, "profile"));
        string outside = DiagnosticOutputPaths.Create(DiagnosticOutputPaths.Child(root, "outside"));
        AppPaths paths = AppPaths.CreateForRoot(profile);
        string modelRoot = translation ? paths.TranslationModelsRoot : paths.OcrModelsRoot;
        string modelDirectory = translation ? Path.Combine(modelRoot, TranslationFolder) : modelRoot;
        DiagnosticOutputPaths.Create(modelDirectory);
        string originalPath = Path.Combine(modelDirectory, ModelName);
        string targetDirectory = translation
            ? DiagnosticOutputPaths.Create(DiagnosticOutputPaths.Child(outside, TranslationFolder)) : outside;
        string target = DiagnosticOutputPaths.Child(targetDirectory, ModelName);
        string archive = DiagnosticOutputPaths.Child(root, "verified-model-root-preserved");
        byte[] payload = Enumerable.Range(0, 4096).Select(i => (byte)i).ToArray();
        WriteNew(originalPath, payload);
        WriteNew(target, payload);
        WriteNew(DiagnosticOutputPaths.Child(root, "expected-original.bin"), payload);
        DateTime stamp = File.GetLastWriteTimeUtc(originalPath);
        File.SetLastWriteTimeUtc(target, stamp);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(target));
        Assert.Equal(new FileInfo(originalPath).Length, new FileInfo(target).Length);
        FileEvidence[] before = Snapshot(outside);
        int hashes = 0;
        var handler = new PayloadHandler(payload);
        using var http = new HttpClient(handler);
        var ocrFile = new OcrModelFile(ModelName, ModelUri, Hash(payload), 16, ExpectedBytes: payload.LongLength);
        var ocr = new OcrModelStore(paths, http, NullLogger.Instance, stream =>
        {
            hashes++;
            return SHA256.HashData(stream);
        });
        var translationFile = new TranslationModelFile(ModelName, ModelUri, Hash(payload), payload.LongLength);
        var translator = new TranslationModelStore(paths, http, NullLogger.Instance, [translationFile],
            TimeSpan.FromSeconds(5), _ => long.MaxValue, TranslationFolder);
        bool Query() => translation ? translator.IsInstalled : ocr.IsPresent(ocrFile);
        Assert.True(Query());
        Assert.True(Query());
        Assert.Equal(translation ? 0 : 1, hashes);
        int hashesBefore = hashes;
        WriteJson(root, "before.json", new { scenario = "cached-root-replacement", translation, modelRoot, outside,
            target, archive, stamp, hashesBefore, before });

        Exception? operationError = null;
        Exception? observationError = null;
        IOException? readBlocked = null;
        bool? first = null;
        bool? second = null;
        FileEvidence[]? afterOperation = null;
        FileEvidence[]? afterCleanup = null;
        LinkCleanup? cleanup = null;
        try
        {
            // Both resolved paths were validated as ordinary, owned directories above. Moving
            // the normal root preserves the warmed file while making room for the junction.
            _ = DiagnosticOutputPaths.Create(modelRoot);
            if (Directory.Exists(archive) || File.Exists(archive)) throw new IOException("Archive path exists.");
            Directory.Move(modelRoot, archive);
            UpdatePathsTests.CreateJunction(modelRoot, outside);
            VerifyLink(root, modelRoot, outside, directory: true);
            // The unlocked check independently detects stale cache reuse; an exclusive read
            // handle must not be able to turn a missing path guard into a passing regression.
            try { first = Query(); }
            catch (Exception error) { operationError = error; }
            using (var held = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                // Prove the read exclusion, rather than interpreting a lock request as success.
                try { using var unexpected = File.OpenRead(target); }
                catch (IOException error) { readBlocked = error; }
                if (readBlocked is null || (readBlocked.HResult & 0xffff) != 32)
                    throw new IOException("The fixture did not establish an exclusive read boundary.");
                try { second = Query(); }
                catch (Exception error) { operationError ??= error; }
            }
            afterOperation = Snapshot(outside);
        }
        catch (Exception error) { observationError = error; }
        finally
        {
            cleanup = RecycleOwnedLink(root, modelRoot, outside, directory: true);
            try { afterCleanup = Snapshot(outside); }
            catch (Exception error) { observationError ??= error; }
            WriteJson(root, "receipt.json", new { scenario = "cached-root-replacement", translation,
                first, second, hashesBefore, hashesAfter = hashes, httpCalls = handler.Calls,
                externalReadExclusion = Error(readBlocked),
                hashObservation = translation
                    ? "External target read was excluded by an independently verified sharing violation; read-open attempt count is not observed."
                    : "Injected OCR hasher counts actual hash calls; external target read also excluded.",
                operationError = Error(operationError), observationError = Error(observationError),
                before, afterOperation, afterCleanup, cleanup, targetExistsAfterCleanup = File.Exists(target) });
        }
        Assert.Null(observationError);
        Assert.Null(operationError);
        Assert.NotNull(cleanup);
        Assert.Null(cleanup.Error);
        Assert.NotNull(readBlocked);
        Assert.Equal(32, readBlocked.HResult & 0xffff);
        Assert.False(first ?? true);
        Assert.False(second ?? true);
        Assert.Equal(hashesBefore, hashes);
        Assert.Equal(0, handler.Calls);
        Assert.Equal(before, afterOperation);
        Assert.Equal(afterOperation, afterCleanup);
        Assert.Equal(payload, File.ReadAllBytes(target));
        string archivedFile = Path.Combine(archive, translation ? TranslationFolder : "", ModelName);
        Assert.Equal(payload, File.ReadAllBytes(archivedFile));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(archivedFile));
        await Task.CompletedTask;
    }

    private async Task RunRootCleanup(bool translation)
    {
        string root = CreateEvidenceRoot(translation, Boundary.Root);
        output.WriteLine("Preserved cleanup boundary evidence: {0}", root);
        string profile = DiagnosticOutputPaths.Create(DiagnosticOutputPaths.Child(root, "profile"));
        string outside = DiagnosticOutputPaths.Create(DiagnosticOutputPaths.Child(root, "outside"));
        AppPaths paths = AppPaths.CreateForRoot(profile);
        string modelRoot = translation ? paths.TranslationModelsRoot : paths.OcrModelsRoot;
        string targetDirectory = translation
            ? DiagnosticOutputPaths.Create(DiagnosticOutputPaths.Child(outside, TranslationFolder)) : outside;
        byte[] original = Enumerable.Repeat((byte)0xA7, 4096).ToArray();
        WriteNew(DiagnosticOutputPaths.Child(root, "expected-original.bin"), original);
        string[] names = translation ? [ModelName, "stale.download", "unrelated-sentinel.bin"]
            : [.. OcrModelCatalog.LegacySuperResolutionFiles, "unrelated-sentinel.bin"];
        foreach (string name in names) WriteNew(DiagnosticOutputPaths.Child(targetDirectory, name), original);
        FileEvidence[] before = Snapshot(outside);
        WriteJson(root, "before.json", new { scenario = "root-cleanup", translation, modelRoot, outside, names, before });
        Exception? operationError = null;
        Exception? observationError = null;
        FileEvidence[]? afterOperation = null;
        FileEvidence[]? afterCleanup = null;
        LinkCleanup? cleanup = null;
        int hashes = 0;
        var handler = new PayloadHandler(original);
        using var http = new HttpClient(handler);
        try
        {
            UpdatePathsTests.CreateJunction(modelRoot, outside);
            VerifyLink(root, modelRoot, outside, directory: true);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                if (translation)
                {
                    var file = new TranslationModelFile(ModelName, ModelUri, Hash(original), original.LongLength);
                    var store = new TranslationModelStore(paths, http, NullLogger.Instance, [file],
                        TimeSpan.FromSeconds(5), _ => long.MaxValue, TranslationFolder);
                    await store.DeleteAsync(cancellation.Token);
                }
                else
                {
                    var store = new OcrModelStore(paths, http, NullLogger.Instance, stream =>
                    {
                        hashes++;
                        return SHA256.HashData(stream);
                    });
                    await store.PreVerifyInstalledModelsAsync();
                }
            }
            catch (Exception error) { operationError = error; }
            afterOperation = Snapshot(outside);
        }
        catch (Exception error) { observationError = error; }
        finally
        {
            cleanup = RecycleOwnedLink(root, modelRoot, outside, directory: true);
            try { afterCleanup = Snapshot(outside); }
            catch (Exception error) { observationError ??= error; }
            WriteJson(root, "receipt.json", new { scenario = "root-cleanup", translation, hashes, httpCalls = handler.Calls,
                operationError = Error(operationError), observationError = Error(observationError),
                before, afterOperation, afterCleanup, cleanup });
        }
        Assert.Null(observationError);
        Assert.NotNull(cleanup);
        Assert.Null(cleanup.Error);
        Assert.Equal(before, afterOperation);
        Assert.Equal(afterOperation, afterCleanup);
        Assert.Equal(0, hashes);
        Assert.Equal(0, handler.Calls);
        foreach (string name in names) Assert.Equal(original, File.ReadAllBytes(Path.Combine(targetDirectory, name)));
        if (translation)
        {
            TranslationModelException error = Assert.IsType<TranslationModelException>(operationError);
            Assert.Equal(TranslationModelError.Disk, error.Error);
        }
        else Assert.Null(operationError); // Background preverification stays non-fatal.
    }

    private async Task Run(bool translation, Boundary boundary)
    {
        string root = CreateEvidenceRoot(translation, boundary);
        output.WriteLine("Preserved synthetic boundary evidence: {0}", root);
        string profile = DiagnosticOutputPaths.Create(DiagnosticOutputPaths.Child(root, "profile"));
        string outside = DiagnosticOutputPaths.Create(DiagnosticOutputPaths.Child(root, "outside"));
        AppPaths paths = AppPaths.CreateForRoot(profile);
        string modelRoot = translation ? paths.TranslationModelsRoot : paths.OcrModelsRoot;
        string modelDirectory = translation ? Path.Combine(modelRoot, TranslationFolder) : modelRoot;
        string destination = Path.Combine(modelDirectory, ModelName);
        string temp = destination + ".download";
        string targetDirectory = boundary == Boundary.Root && translation
            ? DiagnosticOutputPaths.Create(DiagnosticOutputPaths.Child(outside, TranslationFolder)) : outside;
        string target = DiagnosticOutputPaths.Child(targetDirectory, ModelName);
        string other = DiagnosticOutputPaths.Child(outside, "unrelated-sentinel.bin");
        byte[] original = Enumerable.Repeat((byte)0xA7, 4096).ToArray();
        byte[] unrelated = Enumerable.Repeat((byte)0x39, 257).ToArray();
        byte[] payload = Enumerable.Range(0, 4096).Select(i => (byte)i).ToArray();
        WriteNew(target, original);
        WriteNew(other, unrelated);
        WriteNew(DiagnosticOutputPaths.Child(root, "expected-original.bin"), original);
        WriteNew(DiagnosticOutputPaths.Child(root, "expected-unrelated.bin"), unrelated);
        WriteNew(DiagnosticOutputPaths.Child(root, "download-payload.bin"), payload);
        string link = boundary switch { Boundary.Root => modelRoot, Boundary.Download => temp, _ => destination };
        string expectedLinkTarget = boundary == Boundary.Root ? outside : target;
        FileEvidence[] before = Snapshot(outside);
        WriteJson(root, "before.json", new { translation, boundary = boundary.ToString(), root, profile, outside,
            link, expectedLinkTarget, destination, temp, before, payloadSha256 = Hash(payload) });

        Exception? operationError = null;
        Exception? observationError = null;
        FileEvidence[]? afterOperation = null;
        FileEvidence[]? afterCleanup = null;
        string? installedSha256 = null;
        var cleanup = new List<LinkCleanup>();
        var handler = new PayloadHandler(payload);
        using var http = new HttpClient(handler);
        var clock = new Stopwatch();
        bool operationStarted = false;
        bool operationReturned = false;
        bool setupComplete = false;
        try
        {
            if (boundary == Boundary.Root) UpdatePathsTests.CreateJunction(link, outside);
            else
            {
                DiagnosticOutputPaths.Create(modelDirectory);
                File.CreateSymbolicLink(link, target);
            }
            VerifyLink(root, link, expectedLinkTarget, boundary == Boundary.Root);
            setupComplete = true;
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            clock.Start();
            operationStarted = true;
            try
            {
                if (translation)
                {
                    var file = new TranslationModelFile(ModelName, ModelUri, Hash(payload), payload.LongLength);
                    var store = new TranslationModelStore(paths, http, NullLogger.Instance, [file],
                        TimeSpan.FromSeconds(5), _ => long.MaxValue, TranslationFolder);
                    await store.DownloadAsync(null, cancellation.Token);
                }
                else
                {
                    var file = new OcrModelFile(ModelName, ModelUri, Hash(payload), 16, ExpectedBytes: payload.LongLength);
                    var store = new OcrModelStore(paths, http, NullLogger.Instance);
                    await store.EnsureFileAsync(file, cancellation.Token);
                }
                operationReturned = true;
            }
            catch (Exception error) { operationError = error; }
            finally { clock.Stop(); }
            afterOperation = Snapshot(outside);
            if (File.Exists(destination)) installedSha256 = Hash(File.ReadAllBytes(destination));
        }
        catch (Exception error) { observationError = error; }
        finally
        {
            // A move can relocate a .download link to the final name. Inspect both owned names;
            // preserve any ordinary replacement and never recurse through the root junction.
            string[] possibleLinks = boundary == Boundary.Root ? [link] : [temp, destination];
            foreach (string possibleLink in possibleLinks)
                cleanup.Add(RecycleOwnedLink(root, possibleLink, expectedLinkTarget, boundary == Boundary.Root));
            try { afterCleanup = Snapshot(outside); }
            catch (Exception error) { observationError ??= error; }
            WriteJson(root, "receipt.json", new
            {
                scope = "Owned synthetic local model path boundaries; no real model or network",
                translation, boundary = boundary.ToString(), setupComplete, operationStarted, operationReturned,
                elapsedMs = clock.Elapsed.TotalMilliseconds, httpCalls = handler.Calls,
                operationError = Error(operationError), observationError = Error(observationError),
                installedSha256, before, afterOperation, afterCleanup, cleanup,
                targetExistsAfterCleanup = File.Exists(target), unrelatedExistsAfterCleanup = File.Exists(other),
                expectedOriginalSha256 = Hash(original), expectedUnrelatedSha256 = Hash(unrelated)
            });
        }

        Assert.True(setupComplete, $"Reparse setup did not complete. See {root}");
        Assert.Null(observationError);
        Assert.All(cleanup, item => Assert.Null(item.Error));
        Assert.NotNull(afterOperation);
        Assert.NotNull(afterCleanup);
        Assert.Equal(before, afterOperation);
        Assert.Equal(afterOperation, afterCleanup); // Recycling the link must not touch its target.
        Assert.True(File.Exists(target), $"Owned target disappeared. See {root}");
        Assert.Equal(original, File.ReadAllBytes(target));
        Assert.Equal(unrelated, File.ReadAllBytes(other));
        Assert.True(operationStarted);
        if (operationReturned)
        {
            Assert.Equal(1, handler.Calls);
            Assert.Equal(Hash(payload), installedSha256);
        }
        else
        {
            Assert.True(operationError is IOException or UnauthorizedAccessException ||
                operationError is TranslationModelException { Error: TranslationModelError.Disk },
                $"Expected a path/disk rejection, observed {operationError}. See {root}");
        }
    }

    private static LinkCleanup RecycleOwnedLink(string root, string link, string target, bool directory)
    {
        try
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(link); }
            catch (FileNotFoundException) { return new(link, "Absent after product operation", null, null); }
            catch (DirectoryNotFoundException) { return new(link, "Absent after product operation", null, null); }
            if ((attributes & FileAttributes.ReparsePoint) == 0)
                return new(link, "Ordinary entry preserved", null, null);
            VerifyLink(root, link, target, directory);
            var removal = new RecoverableFileRemoval();
            CleanupReceipt receipt = directory ? removal.RecycleDirectory(link) : removal.RecycleFile(link);
            if (File.Exists(link) || Directory.Exists(link)) throw new IOException("Link remains after recycling.");
            if (directory ? !Directory.Exists(target) : !File.Exists(target))
                throw new IOException("Link target disappeared during recycling.");
            return new(link, "Windows Recycle Bin move confirmed", receipt, null);
        }
        catch (Exception error) { return new(link, "Cleanup failed; no permanent-delete fallback", null, Error(error)); }
    }

    private static void VerifyLink(string root, string link, string target, bool directory)
    {
        string prefix = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(link).StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFullPath(target).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Fixture link or target escaped the owned artifact root.");
        if ((File.GetAttributes(link) & FileAttributes.ReparsePoint) == 0)
            throw new IOException("The boundary fixture is not a reparse point.");
        FileSystemInfo? actual = directory ? Directory.ResolveLinkTarget(link, false) : File.ResolveLinkTarget(link, false);
        if (actual is null || !string.Equals(Path.GetFullPath(actual.FullName), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
            throw new IOException("The fixture link target changed; preserve the entry.");
    }

    private static FileEvidence[] Snapshot(string root)
    {
        // Every target is an ordinary directory owned by this test, never the link side.
        if (!Directory.Exists(root) || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The ordinary synthetic target directory is missing or became a link.");
        var result = new List<FileEvidence>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out string? directory))
        {
            if (directory is null) throw new IOException("Unexpected empty snapshot directory.");
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
            {
                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Unexpected link in the ordinary synthetic target; do not traverse it.");
                bool isDirectory = (attributes & FileAttributes.Directory) != 0;
                result.Add(new(Path.GetRelativePath(root, entry), isDirectory,
                    isDirectory ? 0 : new FileInfo(entry).Length, isDirectory ? null : Hash(File.ReadAllBytes(entry))));
                if (isDirectory) pending.Push(entry);
            }
        }
        return result.OrderBy(item => item.Path, StringComparer.Ordinal).ToArray();
    }

    private static string CreateEvidenceRoot(bool translation, Boundary boundary)
    {
        DirectoryInfo? repo = new(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "MyCapture.slnx"))) repo = repo.Parent;
        if (repo is null) throw new IOException("Cannot locate the repository for preserved synthetic artifacts.");
        string parent = DiagnosticOutputPaths.Create(Path.Combine(repo.FullName, "artifacts", "model-download-boundary"));
        string root = DiagnosticOutputPaths.Child(parent, $"{(translation ? "translation" : "ocr")}-{boundary}-{Guid.NewGuid():N}");
        if (Directory.Exists(root) || File.Exists(root)) throw new IOException("Fresh evidence path already exists.");
        return DiagnosticOutputPaths.Create(root);
    }

    private static void WriteNew(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        stream.Write(bytes);
    }

    private static void WriteJson(string root, string name, object value) =>
        WriteNew(DiagnosticOutputPaths.Child(root, name), JsonSerializer.SerializeToUtf8Bytes(value, new JsonSerializerOptions { WriteIndented = true }));

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static ErrorEvidence? Error(Exception? error) => error is null ? null : new(error.GetType().FullName!, error.HResult, error.Message);
    private enum Boundary { Root, Download, Final }
    private sealed record FileEvidence(string Path, bool IsDirectory, long Bytes, string? Sha256);
    private sealed record ErrorEvidence(string Type, int HResult, string Message);
    private sealed record LinkCleanup(string Path, string Status, CleanupReceipt? Receipt, ErrorEvidence? Error);

    private sealed class PayloadHandler(byte[] payload) : HttpMessageHandler
    {
        internal int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.Method != HttpMethod.Get || request.RequestUri?.AbsoluteUri != ModelUri)
                throw new InvalidOperationException("Unexpected request in the synthetic-only model fixture.");
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
        }
    }
}

internal sealed class ModelDownloadBoundaryFactAttribute : FactAttribute
{
    public ModelDownloadBoundaryFactAttribute(bool fileSymlink = false)
    {
        if (!AppArchitecture.SupportsNeuralOcr)
            Skip = "Model download requires the supported x64 architecture; no path protection was exercised.";
        else if (fileSymlink)
            Skip = new WindowsFileSymlinkFactAttribute().Skip; // Preserve local 1314 vs mandatory CI behavior.
    }
}
