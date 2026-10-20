using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Core.Pin;
using MyCapture.Core.Primitives;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class PinSessionStoreTests
{
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+ip1sAAAAASUVORK5CYII=");

    [Fact]
    public void SaveThenLoad_RoundTripsEveryFieldAndOriginalTextExactlyInZOrder()
    {
        using var workspace = new TempWorkspace();
        PinSessionStore store = CreateStore(workspace);
        const string text = "\t한글 열\tvalue  \r\n다음 줄\r\n  끝  \t";
        PinSessionSnapshot[] expected =
        [
            Snapshot(PinSessionContentKind.Image, null, false, null),
            Snapshot(PinSessionContentKind.Text, text, true,
                new PinClosedPlacement(new RectD(-1200, 20, 200, 150), @"\\.\DISPLAY2", new RectD(-1920, 0, 1920, 1080))),
            Snapshot(PinSessionContentKind.Table, text + "\r\n", true,
                new PinClosedPlacement(new RectD(50, 100, 333.5, 250), @"\\.\DISPLAY1", new RectD(0, 0, 2560, 1440)))
        ];

        store.Save(expected);
        IReadOnlyList<PinSessionSnapshot> actual = CreateStore(workspace).Load();

        Assert.Equal(expected.Select(item => item.Pin), actual.Select(item => item.Pin));
        for (int i = 0; i < expected.Length; i++) Assert.Equal(Png, actual[i].PngBytes);
        Assert.Equal(Encoding.UTF8.GetBytes(text), Encoding.UTF8.GetBytes(actual[1].Pin.OriginalText!));
        Assert.Equal(Path.Combine(workspace.Paths.DataRoot, "pins"), workspace.Paths.PinSessionRoot);
        JsonNode manifest = JsonNode.Parse(File.ReadAllText(ManifestPath(workspace)))!;
        Assert.Equal(1, manifest["schemaVersion"]!.GetValue<int>());
        Assert.Equal("Text", manifest["pins"]![1]!["kind"]!.GetValue<string>());
        Assert.DoesNotContain("isHidden", manifest.ToJsonString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("isClickThrough", manifest.ToJsonString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3, Directory.GetFiles(workspace.Paths.PinSessionRoot, "*.png").Length);
        Assert.All(Directory.GetFiles(workspace.Paths.PinSessionRoot, "*.png"),
            path => Assert.True(Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out _)));
    }

    [Fact]
    public void Save_CountBudgetKeepsNewestPinsAndLogsSkippedCount()
    {
        using var workspace = new TempWorkspace();
        var logger = new RecordingLogger();
        var store = new PinSessionStore(workspace.Paths.PinSessionRoot, logger, maxPins: 2);
        PinSessionSnapshot[] pins = [Snapshot(), Snapshot(), Snapshot(), Snapshot()];

        store.Save(pins);

        Assert.Equal(pins.Skip(2).Select(pin => pin.Pin), store.Load().Select(pin => pin.Pin));
        Assert.Equal(2, Directory.GetFiles(workspace.Paths.PinSessionRoot, "*.png").Length);
        Assert.Contains(logger.Messages, message => message.Contains("Skipped 2 older pins", StringComparison.Ordinal));
        Assert.Equal(2, store.MaxPins);
        Assert.Equal(PinSessionStore.DefaultMaxPngBytes, store.MaxPngBytes);
    }

    [Fact]
    public void Save_ByteBudgetKeepsNewestPins()
    {
        using var workspace = new TempWorkspace();
        var logger = new RecordingLogger();
        var store = new PinSessionStore(workspace.Paths.PinSessionRoot, logger, maxPngBytes: 2L * Png.Length);
        PinSessionSnapshot[] pins = [Snapshot(), Snapshot(), Snapshot()];

        store.Save(pins);

        Assert.Equal(pins.Skip(1).Select(pin => pin.Pin), store.Load().Select(pin => pin.Pin));
        Assert.Equal(2L * Png.Length, Directory.GetFiles(workspace.Paths.PinSessionRoot, "*.png")
            .Sum(path => new FileInfo(path).Length));
        Assert.Contains(logger.Messages, message => message.Contains("Skipped 1 older pins", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Save_BudgetUsesOpeningAgeAndPreservesRetainedStackingOrder(bool limitBytes)
    {
        using var workspace = new TempWorkspace();
        PinSessionSnapshot[] pins = [WithOpeningOrder(3), WithOpeningOrder(1), WithOpeningOrder(2)];
        var store = new PinSessionStore(workspace.Paths.PinSessionRoot, NullLogger.Instance,
            maxPins: limitBytes ? 50 : 2,
            maxPngBytes: limitBytes ? 2L * Png.Length : PinSessionStore.DefaultMaxPngBytes);

        store.Save(pins);

        Assert.Equal(new[] { pins[0].Pin, pins[2].Pin }, store.Load().Select(pin => pin.Pin));
        Assert.False(File.Exists(ImagePath(workspace, pins[1].Pin.Id)));
        JsonNode manifest = JsonNode.Parse(File.ReadAllText(ManifestPath(workspace)))!;
        Assert.Equal(3, manifest["pins"]![0]!["openedOrder"]!.GetValue<long>());
        Assert.Equal(2, manifest["pins"]![1]!["openedOrder"]!.GetValue<long>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Load_BudgetUsesOpeningAgeAndPreservesRetainedStackingOrder(bool limitBytes)
    {
        using var workspace = new TempWorkspace();
        PinSessionSnapshot[] pins = [WithOpeningOrder(3), WithOpeningOrder(1), WithOpeningOrder(2)];
        CreateStore(workspace).Save(pins);
        var store = new PinSessionStore(workspace.Paths.PinSessionRoot, NullLogger.Instance,
            maxPins: limitBytes ? 50 : 2,
            maxPngBytes: limitBytes ? 2L * Png.Length : PinSessionStore.DefaultMaxPngBytes);

        Assert.Equal(new[] { pins[0].Pin, pins[2].Pin }, store.Load().Select(pin => pin.Pin));
    }

    [Fact]
    public void Save_SharedContentPreservesEveryWindowAndCountsPngOnlyOnce()
    {
        using var workspace = new TempWorkspace();
        var store = new PinSessionStore(workspace.Paths.PinSessionRoot, NullLogger.Instance, maxPngBytes: Png.Length);
        PinSessionSnapshot first = Snapshot();
        PinSessionSnapshot second = first with { Pin = first.Pin with { Zoom = 2.25, Opacity = 0.5 } };

        store.Save([first, second]);

        Assert.Equal(new[] { first.Pin, second.Pin }, store.Load().Select(pin => pin.Pin));
        Assert.Single(Directory.GetFiles(workspace.Paths.PinSessionRoot, "*.png"));
    }

    [Fact]
    public void Save_ReusesExistingImageWithoutRewritingAndUpdatesManifestAtomically()
    {
        using var workspace = new TempWorkspace();
        PinSessionStore store = CreateStore(workspace);
        PinSessionSnapshot first = Snapshot();
        store.Save([first]);
        string imagePath = ImagePath(workspace, first.Pin.Id);
        DateTime timestamp = new(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(imagePath, timestamp);
        PinSessionSnapshot updated = first with { Pin = first.Pin with { Zoom = 2.5 }, PngBytes = null };

        Assert.True(store.HasImage(first.Pin.Id));
        store.Save([updated]);

        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(imagePath));
        Assert.Equal(updated.Pin, Assert.Single(store.Load()).Pin);
        Assert.Equal(Png, File.ReadAllBytes(imagePath));
        Assert.Empty(Directory.GetFiles(workspace.Paths.PinSessionRoot, "*.tmp"));
        Assert.Empty(Directory.GetFiles(workspace.Paths.PinSessionRoot, "*.bak"));
    }

    [Fact]
    public void Save_FailedAtomicManifestReplacementPreservesPreviousSession()
    {
        using var workspace = new TempWorkspace();
        var logger = new RecordingLogger();
        var store = new PinSessionStore(workspace.Paths.PinSessionRoot, logger);
        PinSessionSnapshot oldPin = Snapshot();
        store.Save([oldPin]);
        byte[] manifest = File.ReadAllBytes(ManifestPath(workspace));
        using (var heldManifest = new FileStream(ManifestPath(workspace), FileMode.Open, FileAccess.Read, FileShare.Read))
            store.Save([Snapshot()]);

        Assert.Equal(manifest, File.ReadAllBytes(ManifestPath(workspace)));
        Assert.Equal(oldPin.Pin, Assert.Single(store.Load()).Pin);
        Assert.True(File.Exists(ImagePath(workspace, oldPin.Pin.Id)));
        Assert.Contains(logger.Messages, message => message.Contains("Could not save the open-pin session", StringComparison.Ordinal));
        Assert.Empty(Directory.GetFiles(workspace.Paths.PinSessionRoot, "*.tmp"));
    }

    [Fact]
    public void Save_RemovesOrphanPngAndOwnedTempsButPreservesUnrelatedFiles()
    {
        using var workspace = new TempWorkspace();
        PinSessionStore store = CreateStore(workspace);
        PinSessionSnapshot oldPin = Snapshot();
        PinSessionSnapshot keptPin = Snapshot();
        store.Save([oldPin, keptPin]);
        string orphan = ImagePath(workspace, Guid.NewGuid());
        string staleTemp = Path.Combine(workspace.Paths.PinSessionRoot, $"session.json.{Guid.NewGuid():N}.tmp");
        string unrelated = Path.Combine(workspace.Paths.PinSessionRoot, "reference.png");
        File.WriteAllBytes(orphan, Png);
        File.WriteAllText(staleTemp, "interrupted session");
        File.WriteAllText(unrelated, "keep");

        store.Save([keptPin with { PngBytes = null }]);

        Assert.False(File.Exists(orphan));
        Assert.False(File.Exists(staleTemp));
        Assert.False(File.Exists(ImagePath(workspace, oldPin.Pin.Id)));
        Assert.True(File.Exists(ImagePath(workspace, keptPin.Pin.Id)));
        Assert.Equal("keep", File.ReadAllText(unrelated));
        Assert.Equal(keptPin.Pin, Assert.Single(store.Load()).Pin);
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("null")]
    [InlineData("{\"schemaVersion\":99,\"pins\":[]}")]
    [InlineData("{\"schemaVersion\":1,\"pins\":null}")]
    public void Load_CorruptManifestReturnsEmptyLogsAndNextSaveReplacesIt(string manifest)
    {
        using var workspace = new TempWorkspace();
        var logger = new RecordingLogger();
        var store = new PinSessionStore(workspace.Paths.PinSessionRoot, logger);
        Directory.CreateDirectory(workspace.Paths.PinSessionRoot);
        File.WriteAllText(ManifestPath(workspace), manifest);

        Assert.Empty(store.Load());
        Assert.Contains(logger.Messages, message => message.Contains("Could not read the saved pin session", StringComparison.Ordinal));
        PinSessionSnapshot pin = Snapshot();
        store.Save([pin]);
        Assert.Equal(pin.Pin, Assert.Single(store.Load()).Pin);
    }

    [Fact]
    public void Load_UnreadableManifestReturnsEmptyAndLogs()
    {
        using var workspace = new TempWorkspace();
        var logger = new RecordingLogger();
        var store = new PinSessionStore(workspace.Paths.PinSessionRoot, logger);
        store.Save([Snapshot()]);
        using var locked = new FileStream(ManifestPath(workspace), FileMode.Open, FileAccess.Read, FileShare.None);

        Assert.Empty(store.Load());
        Assert.Contains(logger.Messages, message => message.Contains("Could not read the saved pin session", StringComparison.Ordinal));
    }

    [Fact]
    public void Load_MissingPngSkipsOnlyThatPinAndLogs()
    {
        using var workspace = new TempWorkspace();
        var logger = new RecordingLogger();
        var store = new PinSessionStore(workspace.Paths.PinSessionRoot, logger);
        PinSessionSnapshot[] pins = [Snapshot(), Snapshot(), Snapshot()];
        store.Save(pins);
        File.Move(ImagePath(workspace, pins[1].Pin.Id), Path.Combine(workspace.Root, "removed.png"));

        Assert.Equal(new[] { pins[0].Pin, pins[2].Pin }, store.Load().Select(pin => pin.Pin));
        Assert.False(store.HasImage(pins[1].Pin.Id));
        Assert.Contains(logger.Messages, message => message.Contains("Skipped missing or corrupt saved pin", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Load_CorruptPngSkipsOnlyThatPinAndCanBeReplaced(bool corruptChunk)
    {
        using var workspace = new TempWorkspace();
        PinSessionStore store = CreateStore(workspace);
        PinSessionSnapshot[] pins = [Snapshot(), Snapshot(), Snapshot()];
        store.Save(pins);
        byte[] damaged = corruptChunk ? Png.ToArray() : [1, 2, 3];
        if (corruptChunk) damaged[45] ^= 1;
        string path = ImagePath(workspace, pins[1].Pin.Id);
        File.WriteAllBytes(path, damaged);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));

        Assert.Equal(new[] { pins[0].Pin, pins[2].Pin }, store.Load().Select(pin => pin.Pin));
        Assert.False(store.HasImage(pins[1].Pin.Id));
        store.Save(pins);
        Assert.Equal(pins.Select(pin => pin.Pin), store.Load().Select(pin => pin.Pin));
    }

    [Fact]
    public void Load_EnforcesBudgetsOnAnExistingLargerSession()
    {
        using var workspace = new TempWorkspace();
        PinSessionSnapshot[] pins = [Snapshot(), Snapshot(), Snapshot()];
        CreateStore(workspace).Save(pins);

        var countStore = new PinSessionStore(workspace.Paths.PinSessionRoot, NullLogger.Instance, maxPins: 2);
        var byteStore = new PinSessionStore(workspace.Paths.PinSessionRoot, NullLogger.Instance, maxPngBytes: Png.Length);

        Assert.Equal(pins.Skip(1).Select(pin => pin.Pin), countStore.Load().Select(pin => pin.Pin));
        Assert.Equal(pins[2].Pin, Assert.Single(byteStore.Load()).Pin);
    }

    [Fact]
    public void Clear_RemovesAllSavedFilesAndIsIdempotent()
    {
        using var workspace = new TempWorkspace();
        PinSessionStore store = CreateStore(workspace);
        PinSessionSnapshot pin = Snapshot();
        store.Save([pin, Snapshot()]);
        File.WriteAllText(Path.Combine(workspace.Paths.PinSessionRoot, $"session.json.{Guid.NewGuid():N}.tmp"), "pending");

        store.Clear();
        store.Clear();

        Assert.Empty(Directory.EnumerateFileSystemEntries(workspace.Paths.PinSessionRoot));
        Assert.Empty(store.Load());
        Assert.False(store.HasImage(pin.Pin.Id));
    }

    [Fact]
    public void MissingSession_DoesNotCreateStorageUntilFirstSave()
    {
        using var workspace = new TempWorkspace();
        PinSessionStore store = CreateStore(workspace);

        Assert.Empty(store.Load());
        Assert.False(store.HasImage(Guid.NewGuid()));
        store.Clear();

        Assert.False(Directory.Exists(workspace.Paths.PinSessionRoot));
        Assert.Equal(50, store.MaxPins);
        Assert.Equal(256L * 1024 * 1024, store.MaxPngBytes);
    }

    [Fact]
    public void ManifestCannotSupplyAFilePathOutsideTheRoot()
    {
        using var workspace = new TempWorkspace();
        PinSessionStore store = CreateStore(workspace);
        string outside = Path.Combine(workspace.Root, "outside.png");
        File.WriteAllText(outside, "outside sentinel");
        store.Save([Snapshot()]);
        JsonNode manifest = JsonNode.Parse(File.ReadAllText(ManifestPath(workspace)))!;
        manifest["pins"]![0]!["id"] = "../outside";
        manifest["pins"]![0]!["fileName"] = outside;
        File.WriteAllText(ManifestPath(workspace), manifest.ToJsonString());

        Assert.Empty(store.Load());
        store.Clear();

        Assert.Equal("outside sentinel", File.ReadAllText(outside));
        Assert.Empty(Directory.EnumerateFileSystemEntries(workspace.Paths.PinSessionRoot));
    }

    [Fact]
    public void ConstructorRejectsAmbiguousOrFileRootBeforeWriting()
    {
        using var workspace = new TempWorkspace();
        string file = Path.Combine(workspace.Root, "sentinel");
        File.WriteAllText(file, "keep");

        Assert.Throws<IOException>(() => new PinSessionStore(file, NullLogger.Instance));
        Assert.Throws<IOException>(() => new PinSessionStore(Path.Combine(workspace.Root, "pins."), NullLogger.Instance));
        Assert.Equal("keep", File.ReadAllText(file));
        Assert.Single(Directory.EnumerateFileSystemEntries(workspace.Root));
    }

    [Fact]
    public void Save_InvalidRecordsAndMissingReuseImagesDoNotBlockOtherPins()
    {
        using var workspace = new TempWorkspace();
        PinSessionStore store = CreateStore(workspace);
        PinSessionSnapshot good = Snapshot();
        PinSessionSnapshot invalid = Snapshot() with { Pin = good.Pin with { Id = Guid.Empty } };
        PinSessionSnapshot noImage = Snapshot() with { PngBytes = null };

        store.Save([invalid, good, noImage]);

        Assert.Equal(good.Pin, Assert.Single(store.Load()).Pin);
        Assert.False(store.HasImage(Guid.Empty));
    }

    private static PinSessionStore CreateStore(TempWorkspace workspace) =>
        new(workspace.Paths.PinSessionRoot, NullLogger.Instance);

    private static PinSessionSnapshot Snapshot(PinSessionContentKind kind = PinSessionContentKind.Image,
        string? text = null, bool usesDip = false, PinClosedPlacement? placement = null) =>
        new(new PinSessionPin(Guid.NewGuid(), kind, text, "https://example.test/spec?tab=핀", usesDip,
            160.5, 80.25, 1.75, 0.65, placement), Png);

    private static PinSessionSnapshot WithOpeningOrder(long order)
    {
        PinSessionSnapshot snapshot = Snapshot();
        return snapshot with { Pin = snapshot.Pin with { OpenedOrder = order } };
    }

    private static string ManifestPath(TempWorkspace workspace) =>
        Path.Combine(workspace.Paths.PinSessionRoot, "session.json");

    private static string ImagePath(TempWorkspace workspace, Guid id) =>
        Path.Combine(workspace.Paths.PinSessionRoot, $"{id:N}.png");

    private sealed class RecordingLogger : ILogger
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}
