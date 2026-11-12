using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Diagnostics;
using MyCapture.Core.Storage;
using MyCapture.Ocr.Translation;
using Xunit;
using Xunit.Abstractions;

namespace MyCapture.App.Tests;

/// <summary>Real factory argument validation, using only small owned synthetic files.</summary>
public sealed class TranslationNativeLoadPathBoundaryTests(ITestOutputHelper output)
{
    [ModelDownloadBoundaryFact]
    public Task DefaultFactoryRejectsRootJunctionBeforeNativeLoad() => Run(false, Boundary.Root);

    [ModelDownloadBoundaryFact]
    public Task DirectionFactoryRejectsRootJunctionBeforeNativeLoad() => Run(true, Boundary.Root);

    [ModelDownloadBoundaryFact]
    public Task DefaultFactoryRejectsModelFolderJunctionBeforeNativeLoad() => Run(false, Boundary.Folder);

    [ModelDownloadBoundaryFact]
    public Task DirectionFactoryRejectsModelFolderJunctionBeforeNativeLoad() => Run(true, Boundary.Folder);

    [ModelDownloadBoundaryFact(fileSymlink: true)]
    public Task DefaultFactoryRejectsCatalogLeafSymlinkBeforeNativeLoad() => Run(false, Boundary.Leaf);

    [ModelDownloadBoundaryFact(fileSymlink: true)]
    public Task DirectionFactoryRejectsCatalogLeafSymlinkBeforeNativeLoad() => Run(true, Boundary.Leaf);

    [Fact]
    public void RegularCatalogPathsReturnTheDirectoryWithoutChangingFiles()
    {
        string root = CreateRoot("regular");
        output.WriteLine("Preserved native-load path evidence: {0}", root);
        AppPaths paths = AppPaths.CreateForRoot(DiagnosticOutputPaths.Create(DiagnosticOutputPaths.Child(root, "profile")));
        TranslationModelSpec spec = TranslationModelCatalog.EnglishToKorean;
        TranslationModelFile[] files = SyntheticFiles(spec);
        string directory = DiagnosticOutputPaths.Create(Path.Combine(paths.TranslationModelsRoot, spec.FolderName));
        WriteModels(directory, files);
        FileEvidence[] before = Snapshot(directory);
        using var http = new HttpClient(new RejectingHandler());
        var store = CreateStore(paths, http, spec, files);

        string actual = store.GetValidatedModelDirectoryForLoad(spec.ModelFiles);

        FileEvidence[] after = Snapshot(directory);
        WriteJson(root, "receipt.json", new { scenario = "regular-catalog", expected = directory, actual, before, after });
        Assert.Equal(Path.GetFullPath(directory), actual);
        Assert.Equal(before, after);
        Assert.Equal(files.Length, after.Length);
    }

    [Fact]
    public async Task InjectedFakeFactoryStillWorksWithoutModelFiles()
    {
        string root = CreateRoot("fake-missing");
        output.WriteLine("Preserved fake-factory evidence: {0}", root);
        AppPaths paths = AppPaths.CreateForRoot(DiagnosticOutputPaths.Create(DiagnosticOutputPaths.Child(root, "profile")));
        TranslationModelSpec spec = TranslationModelCatalog.EnglishToKorean;
        var handler = new RejectingHandler();
        using var http = new HttpClient(handler);
        var store = CreateStore(paths, http, spec, SyntheticFiles(spec));
        int loads = 0;
        var fake = new TextTranslationServiceTests.FakeEngine(text => "FAKE " + text);
        using var service = new TextTranslationService(store, () => { loads++; return fake; }, NullLogger.Instance,
            TimeSpan.FromMinutes(5));
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Assert.False(Directory.Exists(store.ModelDirectory));

        IReadOnlyList<TranslatedText> result = await service.TranslateAsync(["hello"], null, lifetime.Token);

        Assert.False(lifetime.IsCancellationRequested);
        Assert.Equal("FAKE hello", Assert.Single(result).Text);
        Assert.Equal(1, loads);
        Assert.True(service.IsEngineLoaded);
        Assert.Equal("hello", Assert.Single(Assert.Single(fake.Batches)));
        Assert.False(Directory.Exists(store.ModelDirectory));
        Assert.Equal(0, handler.Calls);
        service.Dispose();
        Assert.True(fake.Disposed);
        WriteJson(root, "receipt.json", new { scenario = "injected-fake-missing-model", loads,
            httpCalls = handler.Calls, modelDirectoryCreated = Directory.Exists(store.ModelDirectory),
            result = result.Select(value => value.Text).ToArray(), fake.Disposed });
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(false, 3)]
    [InlineData(false, 4)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    [InlineData(true, 3)]
    [InlineData(true, 4)]
    public Task RealFactoryRejectsEveryUncataloguedEntryPointBeforeNativeLoad(bool directionFactory, int role) =>
        RunModelFileContract(directionFactory, role, unsafeKind: null);

    [Theory]
    [InlineData(0, "parent")]
    [InlineData(1, "parent")]
    [InlineData(2, "parent")]
    [InlineData(3, "parent")]
    [InlineData(4, "parent")]
    [InlineData(0, "rooted")]
    [InlineData(1, "rooted")]
    [InlineData(2, "rooted")]
    [InlineData(3, "rooted")]
    [InlineData(4, "rooted")]
    public Task DirectionFactoryRejectsEveryUnsafeEntryPointBeforeNativeLoad(int role, string unsafeKind) =>
        RunModelFileContract(directionFactory: true, role, unsafeKind);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BuiltInModelEntryPointsAreExactCatalogSubsets(bool koreanToEnglish)
    {
        TranslationModelSpec spec = koreanToEnglish
            ? TranslationModelCatalog.KoreanToEnglish : TranslationModelCatalog.EnglishToKorean;
        string[] names = ModelEntryPoints(spec.ModelFiles);
        Assert.Equal(5, names.Length);
        Assert.Equal(5, names.Distinct(StringComparer.Ordinal).Count());
        foreach (string name in names)
            Assert.Single(spec.Files, file => string.Equals(file.FileName, name, StringComparison.Ordinal));

        string root = CreateRoot($"catalog-subset-{koreanToEnglish}");
        output.WriteLine("Preserved catalog-subset evidence: {0}", root);
        AppPaths paths = AppPaths.CreateForRoot(DiagnosticOutputPaths.Create(DiagnosticOutputPaths.Child(root, "profile")));
        string directory = DiagnosticOutputPaths.Create(Path.Combine(paths.TranslationModelsRoot, spec.FolderName));
        TranslationModelFile[] files = SyntheticFiles(spec);
        WriteModels(directory, files);
        FileEvidence[] before = Snapshot(root);
        var handler = new RejectingHandler();
        using var http = new HttpClient(handler);
        var store = CreateStore(paths, http, spec, files);

        string actual = store.GetValidatedModelDirectoryForLoad(spec.ModelFiles);

        FileEvidence[] after = Snapshot(root);
        WriteJson(root, "receipt.json", new { scenario = "catalog-subset", koreanToEnglish, names,
            catalog = spec.Files.Select(file => file.FileName).ToArray(), actual, before, after, handler.Calls });
        Assert.Equal(Path.GetFullPath(directory), actual);
        Assert.Equal(before, after);
        Assert.Equal(0, handler.Calls);
    }

    private async Task RunModelFileContract(bool directionFactory, int role, string? unsafeKind)
    {
        string root = CreateRoot($"model-contract-{directionFactory}-{role}-{unsafeKind ?? "missing"}");
        output.WriteLine("Preserved actual-model-name evidence: {0}", root);
        AppPaths paths = AppPaths.CreateForRoot(DiagnosticOutputPaths.Create(DiagnosticOutputPaths.Child(root, "profile")));
        TranslationModelSpec spec = directionFactory
            ? TranslationModelCatalog.KoreanToEnglish : TranslationModelCatalog.EnglishToKorean;
        TranslationModelFile[] allFiles = SyntheticFiles(spec);
        string directory = DiagnosticOutputPaths.Create(Path.Combine(paths.TranslationModelsRoot, spec.FolderName));
        WriteModels(directory, allFiles);
        string[] names = ModelEntryPoints(spec.ModelFiles);
        string originalName = names[role];
        TranslationModelFile[] catalog;
        if (unsafeKind is null)
        {
            // Keep the actual file present; only its catalog membership is missing.
            catalog = allFiles.Where(file => file.FileName != originalName).ToArray();
            Assert.Equal(allFiles.Length - 1, catalog.Length);
            Assert.True(File.Exists(Path.Combine(directory, originalName)));
        }
        else
        {
            catalog = allFiles;
            names[role] = unsafeKind switch
            {
                "parent" => "../outside-model.bin",
                "rooted" => Path.GetFullPath(Path.Combine(root, "outside-model.bin")),
                _ => throw new ArgumentOutOfRangeException(nameof(unsafeKind)),
            };
            if (unsafeKind == "rooted") Assert.True(Path.IsPathRooted(names[role]));
        }
        var actualFiles = new MarianModelFiles(names[0], names[1], names[2], names[3], names[4]);
        FileEvidence[] before = Snapshot(root);
        var handler = new RejectingHandler();
        using var http = new HttpClient(handler);
        var store = CreateStore(paths, http, spec, catalog);
        using var service = directionFactory
            ? new TextTranslationService(store, NullLogger<TextTranslationService>.Instance, actualFiles)
            : new TextTranslationService(store, NullLogger<TextTranslationService>.Instance);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Exception? error = await Record.ExceptionAsync(() => service.TranslateAsync(["hello"], null, lifetime.Token));
        StackFrame[] trace = error is null ? [] : new StackTrace(error, false).GetFrames();
        string[] frames = trace.Select(frame => frame.GetMethod()).Select(method =>
            $"{method?.DeclaringType?.FullName}.{method?.Name}").ToArray();
        bool validationFrame = trace.Any(frame => frame.GetMethod()?.DeclaringType == typeof(TranslationModelStore)
            && frame.GetMethod()?.Name is "GetValidatedModelDirectoryForLoad" or "ValidateFileName");
        bool nativeFrame = trace.Any(frame => frame.GetMethod()?.DeclaringType == typeof(MarianTranslator)
            || frame.GetMethod()?.DeclaringType?.Namespace?.StartsWith("Microsoft.ML.OnnxRuntime", StringComparison.Ordinal) == true);
        FileEvidence[] after = Snapshot(root);
        WriteJson(root, "receipt.json", new { scenario = "actual-model-file-contract", directionFactory, role, unsafeKind,
            names, catalog = catalog.Select(file => file.FileName).ToArray(), error = Error(error), frames,
            validationFrame, nativeFrame, service.IsEngineLoaded, handler.Calls,
            lifetime.IsCancellationRequested, before, after });

        Assert.False(lifetime.IsCancellationRequested);
        ArgumentException argument = Assert.IsType<ArgumentException>(error);
        Assert.Equal(unsafeKind is null ? "files" : "fileName", argument.ParamName);
        Assert.True(validationFrame);
        Assert.False(nativeFrame);
        Assert.False(service.IsEngineLoaded);
        Assert.Equal(0, handler.Calls);
        Assert.Equal(before, after);
    }

    private static string[] ModelEntryPoints(MarianModelFiles files) =>
        [files.Encoder, files.Decoder, files.DecoderWithPast, files.SourceVocabulary, files.TargetVocabulary];

    private async Task Run(bool directionFactory, Boundary boundary)
    {
        string root = CreateRoot($"{(directionFactory ? "direction" : "default")}-{boundary}");
        output.WriteLine("Preserved real-factory boundary evidence: {0}", root);
        string profile = DiagnosticOutputPaths.Create(DiagnosticOutputPaths.Child(root, "profile"));
        string outside = DiagnosticOutputPaths.Create(DiagnosticOutputPaths.Child(root, "outside"));
        AppPaths paths = AppPaths.CreateForRoot(profile);
        TranslationModelSpec spec = directionFactory ? TranslationModelCatalog.KoreanToEnglish : TranslationModelCatalog.EnglishToKorean;
        TranslationModelFile[] files = SyntheticFiles(spec);
        string modelDirectory = Path.Combine(paths.TranslationModelsRoot, spec.FolderName);
        string targetDirectory = DiagnosticOutputPaths.Create(DiagnosticOutputPaths.Child(outside, spec.FolderName));
        WriteModels(targetDirectory, files);
        WriteNew(DiagnosticOutputPaths.Child(outside, "unrelated-sentinel.bin"), [0x39, 0x72, 0xA7]);
        string target = boundary == Boundary.Root ? outside : boundary == Boundary.Folder ? targetDirectory
            : Path.Combine(targetDirectory, spec.ModelFiles.SourceVocabulary);
        string link = boundary == Boundary.Root ? paths.TranslationModelsRoot : boundary == Boundary.Folder ? modelDirectory
            : Path.Combine(modelDirectory, spec.ModelFiles.SourceVocabulary);
        if (boundary == Boundary.Root)
            DiagnosticOutputPaths.Create(Path.GetDirectoryName(link)!);
        else if (boundary == Boundary.Folder)
            DiagnosticOutputPaths.Create(paths.TranslationModelsRoot);
        else
        {
            DiagnosticOutputPaths.Create(modelDirectory);
            WriteModels(modelDirectory, files.Where(file => file.FileName != spec.ModelFiles.SourceVocabulary));
        }
        FileEvidence[] before = Snapshot(outside);
        WriteJson(root, "before.json", new { directionFactory, boundary = boundary.ToString(), spec.FolderName,
            catalogNames = files.Select(file => file.FileName).ToArray(), link, target, before,
            syntheticPayload = "Small repeated bytes are intentionally not valid Marian/ONNX model content." });
        var handler = new RejectingHandler();
        using var http = new HttpClient(handler);
        var store = CreateStore(paths, http, spec, files);
        Exception? operationError = null;
        Exception? observationError = null;
        FileEvidence[]? afterOperation = null;
        FileEvidence[]? afterCleanup = null;
        LinkCleanup? cleanup = null;
        bool setupComplete = false, returned = false, engineLoaded = false;
        string[] frames = [];
        bool validationFrame = false, serviceFrame = false, nativeFrame = false;
        try
        {
            if (boundary == Boundary.Leaf) File.CreateSymbolicLink(link, target);
            else UpdatePathsTests.CreateJunction(link, target);
            VerifyLink(root, link, target, boundary != Boundary.Leaf);
            setupComplete = true;
            // Use the actual two Marian-producing constructors. Neither supplies a fake factory.
            using var service = directionFactory
                ? new TextTranslationService(store, NullLogger<TextTranslationService>.Instance, spec.ModelFiles)
                : new TextTranslationService(store, NullLogger<TextTranslationService>.Instance);
            using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await service.TranslateAsync(["hello"], null, lifetime.Token);
                returned = true;
            }
            catch (Exception error)
            {
                operationError = error;
                StackFrame[] trace = new StackTrace(error, false).GetFrames();
                frames = trace.Select(frame => frame.GetMethod()).Select(method =>
                    $"{method?.DeclaringType?.FullName}.{method?.Name}").ToArray();
                validationFrame = trace.Any(frame => frame.GetMethod()?.DeclaringType == typeof(ValidatedFilePaths)
                    || (frame.GetMethod()?.DeclaringType == typeof(TranslationModelStore)
                        && frame.GetMethod()?.Name == "GetValidatedModelDirectoryForLoad"));
                serviceFrame = trace.Any(frame => frame.GetMethod()?.DeclaringType == typeof(TextTranslationService)
                    || frame.GetMethod()?.DeclaringType?.DeclaringType == typeof(TextTranslationService));
                nativeFrame = trace.Any(frame => frame.GetMethod()?.DeclaringType == typeof(MarianTranslator)
                    || frame.GetMethod()?.DeclaringType?.Namespace?.StartsWith("Microsoft.ML.OnnxRuntime", StringComparison.Ordinal) == true);
            }
            engineLoaded = service.IsEngineLoaded;
            if (lifetime.IsCancellationRequested) throw new IOException("The caller watchdog ended the operation; path rejection was not observed.");
            VerifyLink(root, link, target, boundary != Boundary.Leaf);
            afterOperation = Snapshot(outside);
        }
        catch (Exception error) { observationError = error; }
        finally
        {
            cleanup = RecycleLink(root, link, target, boundary != Boundary.Leaf);
            try { afterCleanup = Snapshot(outside); }
            catch (Exception error) { observationError ??= error; }
            WriteJson(root, "receipt.json", new { directionFactory, boundary = boundary.ToString(), setupComplete,
                returned, engineLoaded, httpCalls = handler.Calls, operationError = Error(operationError),
                observationError = Error(observationError), validationFrame, serviceFrame, nativeFrame, frames,
                before, afterOperation, afterCleanup, cleanup });
        }

        Assert.True(setupComplete, $"Fixture setup did not complete; see {root}");
        Assert.Null(observationError);
        Assert.False(returned);
        Assert.NotNull(operationError);
        // Require the exact path-rejection type and provenance. A model parse failure,
        // DllNotFoundException or a different I/O subtype is not a successful boundary test.
        Assert.True(operationError.GetType() == typeof(IOException) || operationError.GetType() == typeof(UnauthorizedAccessException),
            $"Expected an exact path rejection, observed {operationError}; see {root}");
        Assert.True(validationFrame, $"The actual exception did not originate in the path validator; see {root}");
        Assert.True(serviceFrame, $"The actual exception did not pass through the real service factory; see {root}");
        Assert.False(nativeFrame);
        Assert.False(engineLoaded);
        Assert.Equal(0, handler.Calls);
        Assert.NotNull(afterOperation);
        Assert.NotNull(afterCleanup);
        Assert.Equal(before, afterOperation);
        Assert.Equal(before, afterCleanup);
        Assert.NotNull(cleanup);
        Assert.Null(cleanup.Error);
        Assert.Equal("Windows Recycle Bin move confirmed", cleanup.Status);
        Assert.NotNull(cleanup.Receipt);
    }

    private static TranslationModelStore CreateStore(AppPaths paths, HttpClient http, TranslationModelSpec spec,
        IReadOnlyList<TranslationModelFile> files) =>
        new(paths, http, NullLogger.Instance, files, TimeSpan.FromSeconds(5), _ => long.MaxValue, spec.FolderName);

    private static readonly byte[] Payload = Enumerable.Repeat((byte)0xA7, 256).ToArray();
    private static TranslationModelFile[] SyntheticFiles(TranslationModelSpec spec) =>
        spec.Files.Select(file => new TranslationModelFile(file.FileName, "https://example.test/" + file.FileName,
            Convert.ToHexString(SHA256.HashData(Payload)), Payload.Length)).ToArray();

    private static void WriteModels(string directory, IEnumerable<TranslationModelFile> files)
    {
        foreach (TranslationModelFile file in files) WriteNew(DiagnosticOutputPaths.Child(directory, file.FileName), Payload);
    }

    private static LinkCleanup RecycleLink(string root, string link, string target, bool directory)
    {
        try
        {
            VerifyLink(root, link, target, directory);
            var removal = new RecoverableFileRemoval();
            CleanupReceipt receipt = directory ? removal.RecycleDirectory(link) : removal.RecycleFile(link);
            if (File.Exists(link) || Directory.Exists(link)) throw new IOException("Link remains after recycling.");
            if (directory ? !Directory.Exists(target) : !File.Exists(target)) throw new IOException("The owned target disappeared.");
            return new("Windows Recycle Bin move confirmed", receipt, null);
        }
        catch (Exception error) { return new("Cleanup not confirmed; original entry preserved without fallback", null, Error(error)); }
    }

    private static void VerifyLink(string root, string link, string target, bool directory)
    {
        string prefix = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(link).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFullPath(target).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Fixture paths escaped the owned artifact root.");
        if ((File.GetAttributes(link) & FileAttributes.ReparsePoint) == 0) throw new IOException("Fixture link was replaced; preserve it.");
        FileSystemInfo? actual = directory ? Directory.ResolveLinkTarget(link, false) : File.ResolveLinkTarget(link, false);
        if (actual is null || !string.Equals(Path.GetFullPath(actual.FullName), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Fixture link target changed; preserve it.");
    }

    private static FileEvidence[] Snapshot(string root)
    {
        var results = new List<FileEvidence>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out string? directory))
        {
            if (directory is null || !Directory.Exists(directory) || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Expected an ordinary owned snapshot directory.");
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Do not follow links in the target snapshot.");
                bool isDirectory = (attributes & FileAttributes.Directory) != 0;
                results.Add(new(Path.GetRelativePath(root, entry), isDirectory, isDirectory ? 0 : new FileInfo(entry).Length,
                    isDirectory ? null : Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(entry))),
                    isDirectory ? null : File.GetLastWriteTimeUtc(entry)));
                if (isDirectory) pending.Push(entry);
            }
        }
        return results.OrderBy(item => item.Path, StringComparer.Ordinal).ToArray();
    }

    private static string CreateRoot(string scenario)
    {
        DirectoryInfo? repo = new(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "MyCapture.slnx"))) repo = repo.Parent;
        if (repo is null) throw new IOException("Cannot locate the repository for owned artifacts.");
        string parent = DiagnosticOutputPaths.Create(Path.Combine(repo.FullName, "artifacts", "translation-native-load-boundary"));
        string root = DiagnosticOutputPaths.Child(parent, $"{scenario}-{Guid.NewGuid():N}");
        if (Directory.Exists(root) || File.Exists(root)) throw new IOException("Evidence directory already exists.");
        return DiagnosticOutputPaths.Create(root);
    }

    private static void WriteNew(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        stream.Write(bytes);
    }

    private static void WriteJson(string root, string name, object value) => WriteNew(DiagnosticOutputPaths.Child(root, name),
        JsonSerializer.SerializeToUtf8Bytes(value, new JsonSerializerOptions { WriteIndented = true }));
    private static object? Error(Exception? error) => error is null ? null : new { type = error.GetType().FullName,
        error.HResult, error.Message, error.StackTrace };
    private enum Boundary { Root, Folder, Leaf }
    private sealed record FileEvidence(string Path, bool IsDirectory, long Bytes, string? Sha256, DateTime? LastWriteUtc);
    private sealed record LinkCleanup(string Status, CleanupReceipt? Receipt, object? Error);
    private sealed class RejectingHandler : HttpMessageHandler
    {
        internal int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            throw new InvalidOperationException("Native-load boundary tests must not send HTTP requests.");
        }
    }
}
