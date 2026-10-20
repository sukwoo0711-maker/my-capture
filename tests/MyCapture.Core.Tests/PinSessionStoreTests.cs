using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
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
    public void SaveThenLoad_RoundTripsRenderingMetadataAndRemainsReadableByLegacySchema()
    {
        using var workspace = new TempWorkspace();
        PinSessionStore store = CreateStore(workspace);
        PinSessionSnapshot code = CodeSnapshot();
        PinSessionSnapshot plain = Snapshot(PinSessionContentKind.Text, "plain text", true);
        plain = plain with { Pin = plain.Pin with { TextStyle = PinTextStyle.PlainText } };
        PinSessionSnapshot grid = Snapshot(PinSessionContentKind.Table, "a\tb\r\n1\t2", true);
        grid = grid with { Pin = grid.Pin with { TextStyle = PinTextStyle.Grid } };

        Assert.True(store.Save([plain, grid, code]));
        IReadOnlyList<PinSessionSnapshot> loaded = CreateStore(workspace).Load();

        Assert.Equal(new PinTextStyle?[] { PinTextStyle.PlainText, PinTextStyle.Grid, PinTextStyle.Code },
            loaded.Select(item => item.Pin.TextStyle));
        PinCodeDocument restored = Assert.IsType<PinCodeDocument>(loaded[2].Pin.CodeDocument);
        Assert.Equal(code.Pin.CodeDocument!.DisplayText, restored.DisplayText);
        Assert.Equal(code.Pin.CodeDocument.Background, restored.Background);
        Assert.Equal(code.Pin.CodeDocument.Foreground, restored.Foreground);
        Assert.Equal(code.Pin.CodeDocument.Runs, restored.Runs);
        Assert.Equal(code.Pin.OriginalText, loaded[2].Pin.OriginalText);
        Assert.All(loaded, item => Assert.Equal(Png, item.PngBytes));

        var legacyOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            IgnoreReadOnlyProperties = true,
            Converters = { new JsonStringEnumConverter<PinSessionContentKind>() }
        };
        LegacySessionManifest legacy = JsonSerializer.Deserialize<LegacySessionManifest>(
            File.ReadAllText(ManifestPath(workspace)), legacyOptions)!;
        Assert.Equal(1, legacy.SchemaVersion);
        Assert.Equal(3, legacy.Pins.Count);
        Assert.Equal(code.Pin.OriginalText, legacy.Pins[2].OriginalText);
        Assert.Equal(code.Pin.Id, legacy.Pins[2].Id);
        Assert.True(legacy.Pins[2].UsesDeviceIndependentPixels);
    }

    [Fact]
    public void Load_LegacyManifestHasNoRenderingMetadata()
    {
        using var workspace = new TempWorkspace();
        PinSessionStore store = CreateStore(workspace);
        PinSessionSnapshot legacy = Snapshot(PinSessionContentKind.Text, "legacy code or text", true);
        Assert.True(store.Save([legacy]));
        JsonNode manifest = JsonNode.Parse(File.ReadAllText(ManifestPath(workspace)))!;
        JsonObject record = manifest["pins"]![0]!.AsObject();
        record.Remove("textStyle");
        record.Remove("codeDocument");
        File.WriteAllText(ManifestPath(workspace), manifest.ToJsonString());

        PinSessionSnapshot restored = Assert.Single(CreateStore(workspace).Load());

        Assert.Equal(legacy.Pin, restored.Pin);
        Assert.Null(restored.Pin.TextStyle);
        Assert.Null(restored.Pin.CodeDocument);
        Assert.Equal(Png, restored.PngBytes);
    }

    [Theory]
    [InlineData("unknown-style")]
    [InlineData("undefined-style")]
    [InlineData("wrong-style-type")]
    [InlineData("missing-style")]
    [InlineData("missing-document")]
    [InlineData("plain-with-document")]
    [InlineData("grid-with-text-kind")]
    [InlineData("image-with-code-style")]
    [InlineData("picture-with-code-style")]
    [InlineData("missing-source")]
    [InlineData("different-source")]
    [InlineData("oversized-source")]
    [InlineData("missing-display")]
    [InlineData("empty-display")]
    [InlineData("oversized-display")]
    [InlineData("bad-background")]
    [InlineData("bad-foreground")]
    [InlineData("bad-run-color")]
    [InlineData("missing-runs")]
    [InlineData("null-run")]
    [InlineData("negative-start")]
    [InlineData("zero-length")]
    [InlineData("outside-text")]
    [InlineData("overflow-length")]
    [InlineData("overlapping-runs")]
    [InlineData("oversized-runs")]
    public void Load_InvalidRenderingMetadataSkipsOnlyThatRecord(string corruption)
    {
        using var workspace = new TempWorkspace();
        PinSessionStore store = CreateStore(workspace);
        PinSessionSnapshot code = CodeSnapshot();
        PinSessionSnapshot kept = Snapshot();
        Assert.True(store.Save([code, kept]));
        JsonNode manifest = JsonNode.Parse(File.ReadAllText(ManifestPath(workspace)))!;
        JsonObject record = manifest["pins"]![0]!.AsObject();
        JsonObject document = record["codeDocument"]!.AsObject();
        JsonArray runs = document["runs"]!.AsArray();
        switch (corruption)
        {
            case "unknown-style": record["textStyle"] = "FutureCode"; break;
            case "undefined-style": record["textStyle"] = 99; break;
            case "wrong-style-type": record["textStyle"] = new JsonObject(); break;
            case "missing-style": record.Remove("textStyle"); break;
            case "missing-document": record["codeDocument"] = null; break;
            case "plain-with-document": record["textStyle"] = "PlainText"; break;
            case "grid-with-text-kind":
                record["textStyle"] = "Grid";
                record["codeDocument"] = null;
                break;
            case "image-with-code-style": record["kind"] = "Image"; break;
            case "picture-with-code-style": record["usesDeviceIndependentPixels"] = false; break;
            case "missing-source": record["originalText"] = null; break;
            case "different-source": record["originalText"] = "different code"; break;
            case "oversized-source": record["originalText"] = new string(' ', PinCodeDocument.MaxOriginalTextLength + 1); break;
            case "missing-display": document["displayText"] = null; break;
            case "empty-display": document["displayText"] = ""; break;
            case "oversized-display": document["displayText"] = new string(' ', PinCodeDocument.MaxDisplayTextLength + 1); break;
            case "bad-background": document["background"] = "#12345G"; break;
            case "bad-foreground": document["foreground"] = "red"; break;
            case "bad-run-color": runs[0]!["color"] = "#FF123456"; break;
            case "missing-runs": document["runs"] = null; break;
            case "null-run": runs[0] = null; break;
            case "negative-start": runs[0]!["start"] = -1; break;
            case "zero-length": runs[0]!["length"] = 0; break;
            case "outside-text": runs[0]!["start"] = 100; break;
            case "overflow-length": runs[0]!["length"] = int.MaxValue; break;
            case "overlapping-runs": runs[1]!["start"] = 2; break;
            case "oversized-runs":
                while (runs.Count <= PinCodeDocument.MaxRunCount) runs.Add(runs[0]!.DeepClone());
                break;
        }
        File.WriteAllText(ManifestPath(workspace), manifest.ToJsonString());

        PinSessionLoadResult loaded = CreateStore(workspace).LoadDetailed();

        Assert.False(loaded.ManifestUnreadable);
        Assert.Equal(kept.Pin, Assert.Single(loaded.Pins).Pin);
    }

    [Fact]
    public void Save_InvalidRenderingMetadataDoesNotWriteItsImage()
    {
        using var workspace = new TempWorkspace();
        PinSessionStore store = CreateStore(workspace);
        PinSessionSnapshot code = CodeSnapshot();
        PinSessionSnapshot invalid = code with
        {
            Pin = code.Pin with { CodeDocument = code.Pin.CodeDocument! with { Background = "invalid" } }
        };
        PinSessionSnapshot kept = Snapshot();

        Assert.True(store.Save([invalid, kept]));

        Assert.Equal(kept.Pin, Assert.Single(store.Load()).Pin);
        Assert.False(File.Exists(ImagePath(workspace, invalid.Pin.Id)));
    }

    [Fact]
    public void CodeDocument_DefensivelyCopiesRunsAndComparesWhitespaceWithoutChangingSource()
    {
        PinCodeRun[] runs = [new(0, 3, "#569CD6")];
        var document = new PinCodeDocument("int x;", runs, "#1E1E1E", "#DCDCDC");
        runs[0] = new PinCodeRun(0, 3, "#FFFFFF");
        Assert.Equal("#569CD6", document.Runs[0].Color);
        Assert.True(document.IsValid());
        Assert.True(document.MatchesOriginalText("\tint   x;\r\n"));
        Assert.False(document.MatchesOriginalText("int y;"));

        PinCodeRun[] replacement = [new(4, 1, "#123ABC")];
        PinCodeDocument copied = document with { Runs = replacement };
        replacement[0] = new PinCodeRun(4, 1, "#FFFFFF");
        Assert.Equal("#123ABC", copied.Runs[0].Color);
        Assert.Throws<NotSupportedException>(() => ((IList<PinCodeRun>)copied.Runs).Clear());
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
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                Assert.False(store.Save([Snapshot()]));
                Assert.Equal(ImagePath(workspace, oldPin.Pin.Id),
                    Assert.Single(Directory.GetFiles(workspace.Paths.PinSessionRoot, "*.png")));
            }
        }

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
    public void LoadDetailed_UnreadableManifestBlocksOverwriteUntilSuccessfulRetry()
    {
        using var workspace = new TempWorkspace();
        PinSessionSnapshot original = Snapshot();
        CreateStore(workspace).Save([original]);
        PinSessionStore store = CreateStore(workspace);
        byte[] manifest = File.ReadAllBytes(ManifestPath(workspace));
        using (var locked = new FileStream(ManifestPath(workspace), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            PinSessionLoadResult loaded = store.LoadDetailed();
            Assert.True(loaded.ManifestUnreadable);
            Assert.Empty(loaded.Pins);
            Assert.False(store.Save([]));
            Assert.False(store.Save([Snapshot()]));
            Assert.Single(Directory.GetFiles(workspace.Paths.PinSessionRoot, "*.png"));
        }

        Assert.False(store.Save([]));
        Assert.Equal(manifest, File.ReadAllBytes(ManifestPath(workspace)));
        PinSessionLoadResult retry = store.LoadDetailed();
        Assert.False(retry.ManifestUnreadable);
        Assert.Equal(original.Pin, Assert.Single(retry.Pins).Pin);
        PinSessionSnapshot added = Snapshot();
        Assert.True(store.Save([retry.Pins[0] with { PngBytes = null }, added]));
        Assert.Equal(new[] { original.Pin, added.Pin }, store.Load().Select(pin => pin.Pin));
        Assert.True(File.Exists(ImagePath(workspace, original.Pin.Id)));
    }

    [Fact]
    public void Save_FirstAttemptWithUnreadableManifestDoesNotCreateOrDeleteImages()
    {
        using var workspace = new TempWorkspace();
        PinSessionSnapshot original = Snapshot();
        CreateStore(workspace).Save([original]);
        PinSessionStore store = CreateStore(workspace);
        using var locked = new FileStream(ManifestPath(workspace), FileMode.Open, FileAccess.Read, FileShare.None);

        Assert.False(store.Save([Snapshot()]));
        Assert.Equal(ImagePath(workspace, original.Pin.Id),
            Assert.Single(Directory.GetFiles(workspace.Paths.PinSessionRoot, "*.png")));
    }

    [Fact]
    public void LoadDetailed_CorruptAndMissingManifestsAllowLaterSave()
    {
        using var workspace = new TempWorkspace();
        PinSessionStore store = CreateStore(workspace);
        Assert.False(store.LoadDetailed().ManifestUnreadable);
        Directory.CreateDirectory(workspace.Paths.PinSessionRoot);
        File.WriteAllText(ManifestPath(workspace), "{invalid");

        PinSessionLoadResult loaded = store.LoadDetailed();

        Assert.False(loaded.ManifestUnreadable);
        Assert.Empty(loaded.Pins);
        Assert.True(store.Save([Snapshot()]));
        Assert.Single(store.Load());
    }

    [Fact]
    public void LoadDetailed_UnreadablePngCarriesItsRecordThroughSaveAndRelease()
    {
        using var workspace = new TempWorkspace();
        PinSessionSnapshot[] pins = [Snapshot(), Snapshot(), Snapshot()];
        CreateStore(workspace).Save(pins);
        PinSessionStore store = CreateStore(workspace);
        string lockedPath = ImagePath(workspace, pins[1].Pin.Id);
        using (var locked = new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            PinSessionLoadResult loaded = store.LoadDetailed();
            Assert.False(loaded.ManifestUnreadable);
            Assert.Equal(pins.Select(pin => pin.Pin), loaded.Pins.Select(pin => pin.Pin));
            Assert.Equal(Png, loaded.Pins[0].PngBytes);
            Assert.Null(loaded.Pins[1].PngBytes);
            Assert.Equal(Png, loaded.Pins[2].PngBytes);
            Assert.True(store.TryGetImageLength(pins[1].Pin.Id, out long length));
            Assert.Equal(Png.LongLength, length);
            Assert.True(store.Save(loaded.Pins.Select(pin => pin with { PngBytes = null }).ToList()));
            Assert.True(File.Exists(lockedPath));
            Assert.Equal(3, JsonNode.Parse(File.ReadAllText(ManifestPath(workspace)))!["pins"]!.AsArray().Count);
        }

        Assert.True(store.Save(pins.Select(pin => pin with { PngBytes = null }).ToList()));
        Assert.Equal(pins.Select(pin => pin.Pin), store.Load().Select(pin => pin.Pin));
        Assert.Equal(Png, File.ReadAllBytes(lockedPath));
    }

    [Fact]
    public void Save_FreshBytesForCommittedLockedImagePreservesItsRecordAndFile()
    {
        using var workspace = new TempWorkspace();
        PinSessionSnapshot pin = Snapshot();
        CreateStore(workspace).Save([pin]);
        PinSessionStore store = CreateStore(workspace);
        string path = ImagePath(workspace, pin.Pin.Id);
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.True(store.Save([pin]));
            Assert.True(File.Exists(path));
            Assert.Single(JsonNode.Parse(File.ReadAllText(ManifestPath(workspace)))!["pins"]!.AsArray());
        }

        Assert.Equal(pin.Pin, Assert.Single(store.Load()).Pin);
        Assert.Equal(Png, File.ReadAllBytes(path));
    }

    [Fact]
    public void LoadDetailed_UnmeasurableNewestPinDoesNotExcludeOlderCommittedPins()
    {
        using var workspace = new TempWorkspace();
        PinSessionSnapshot[] pins = [Snapshot(), Snapshot(), Snapshot()];
        CreateStore(workspace).Save(pins);
        var store = new PinSessionStore(workspace.Paths.PinSessionRoot, NullLogger.Instance,
            maxPngBytes: 2L * Png.Length)
        {
            ReadImageLengthForTest = id => id == pins[2].Pin.Id
                ? throw new IOException("Transient metadata failure") : Png.LongLength
        };

        PinSessionLoadResult loaded = store.LoadDetailed();

        Assert.False(loaded.ManifestUnreadable);
        Assert.Equal(pins.Select(pin => pin.Pin), loaded.Pins.Select(pin => pin.Pin));
        Assert.Equal(Png, loaded.Pins[0].PngBytes);
        Assert.Equal(Png, loaded.Pins[1].PngBytes);
        Assert.Null(loaded.Pins[2].PngBytes);
        Assert.False(store.TryGetImageLength(pins[2].Pin.Id, out _));
    }

    [Fact]
    public void Save_UnmeasurableCommittedPinDoesNotConsumeBudgetForOlderPins()
    {
        using var workspace = new TempWorkspace();
        PinSessionSnapshot[] pins = [Snapshot(), Snapshot(), Snapshot()];
        CreateStore(workspace).Save(pins);
        var store = new PinSessionStore(workspace.Paths.PinSessionRoot, NullLogger.Instance,
            maxPngBytes: 2L * Png.Length)
        {
            ReadImageLengthForTest = id => id == pins[2].Pin.Id
                ? throw new IOException("Transient metadata failure") : Png.LongLength
        };
        List<PinSessionSnapshot> pending = pins.Select(pin => pin with { PngBytes = null }).ToList();

        Assert.True(store.Save(pending));

        Assert.Equal(pins.Select(pin => pin.Pin), CreateStore(workspace).Load().Select(pin => pin.Pin));
        Assert.Equal(3, Directory.GetFiles(workspace.Paths.PinSessionRoot, "*.png").Length);
        // Once metadata is readable, the ordinary byte limit applies again.
        store.ReadImageLengthForTest = null;
        Assert.True(store.Save(pending));
        Assert.Equal(pins.Skip(1).Select(pin => pin.Pin), store.Load().Select(pin => pin.Pin));
        Assert.Equal(2, Directory.GetFiles(workspace.Paths.PinSessionRoot, "*.png").Length);
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

        Assert.True(store.Clear());
        Assert.True(store.Clear());

        Assert.Empty(Directory.EnumerateFileSystemEntries(workspace.Paths.PinSessionRoot));
        Assert.Empty(store.Load());
        Assert.False(store.HasImage(pin.Pin.Id));
    }

    [Fact]
    public void Clear_ReportsLockedFileFailureAndSucceedsOnRetry()
    {
        using var workspace = new TempWorkspace();
        PinSessionStore store = CreateStore(workspace);
        PinSessionSnapshot pin = Snapshot();
        store.Save([pin]);
        string path = ImagePath(workspace, pin.Pin.Id);
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.False(store.Clear());
            Assert.True(File.Exists(path));
        }

        Assert.True(store.Clear());
        Assert.Empty(Directory.EnumerateFileSystemEntries(workspace.Paths.PinSessionRoot));
        Assert.False(store.HasImage(pin.Pin.Id));
    }

    [Fact]
    public void Save_RejectedGenerationDoesNotCreateFiles()
    {
        using var workspace = new TempWorkspace();
        PinSessionStore store = CreateStore(workspace);

        Assert.False(store.Save([Snapshot()], canCommit: () => false));

        Assert.False(Directory.Exists(workspace.Paths.PinSessionRoot));
    }

    [Fact]
    public void Save_GenerationRejectedAfterImageWriteKeepsCommittedSessionAndRemovesNewImage()
    {
        using var workspace = new TempWorkspace();
        PinSessionStore store = CreateStore(workspace);
        PinSessionSnapshot original = Snapshot();
        PinSessionSnapshot stale = Snapshot();
        store.Save([original]);
        byte[] manifest = File.ReadAllBytes(ManifestPath(workspace));
        string stalePath = ImagePath(workspace, stale.Pin.Id);

        Assert.False(store.Save([stale], canCommit: () => !File.Exists(stalePath)));

        Assert.Equal(manifest, File.ReadAllBytes(ManifestPath(workspace)));
        Assert.False(File.Exists(stalePath));
        Assert.Equal(original.Pin, Assert.Single(store.Load()).Pin);
    }

    [Fact]
    public void Save_CanceledAfterManifestTempFlushPreservesCommittedSessionAndRemovesAttemptFiles()
    {
        using var workspace = new TempWorkspace();
        PinSessionStore store = CreateStore(workspace);
        PinSessionSnapshot original = Snapshot();
        PinSessionSnapshot stale = Snapshot();
        store.Save([original]);
        byte[] manifest = File.ReadAllBytes(ManifestPath(workspace));
        bool current = true;
        int finalChecks = 0;
        store.BeforeManifestCommitForTest = () =>
        {
            finalChecks++;
            string staged = Assert.Single(Directory.GetFiles(workspace.Paths.PinSessionRoot, "session.json.*.tmp"));
            Assert.NotEmpty(File.ReadAllBytes(staged));
            Assert.Equal(manifest, File.ReadAllBytes(ManifestPath(workspace)));
            Assert.True(File.Exists(ImagePath(workspace, stale.Pin.Id)));
            current = false;
        };

        Assert.False(store.Save([stale], canCommit: () => current));

        Assert.Equal(1, finalChecks);
        Assert.Equal(manifest, File.ReadAllBytes(ManifestPath(workspace)));
        Assert.Equal(ImagePath(workspace, original.Pin.Id),
            Assert.Single(Directory.GetFiles(workspace.Paths.PinSessionRoot, "*.png")));
        Assert.Empty(Directory.GetFiles(workspace.Paths.PinSessionRoot, "*.tmp"));
        Assert.Equal(original.Pin, Assert.Single(store.Load()).Pin);
    }

    [Fact]
    public async Task Clear_WaitsForSaveGateAndRemovesCommittedFilesAfterward()
    {
        using var workspace = new TempWorkspace();
        PinSessionStore store = CreateStore(workspace);
        using var saveInsideGate = new ManualResetEventSlim();
        using var releaseSave = new ManualResetEventSlim();
        using var clearStarted = new ManualResetEventSlim();
        Task<bool> save = Task.Run(() => store.Save([Snapshot()], canCommit: () =>
        {
            saveInsideGate.Set();
            return releaseSave.Wait(TimeSpan.FromSeconds(5));
        }));
        Assert.True(saveInsideGate.Wait(TimeSpan.FromSeconds(5)));
        Task<bool> clear = Task.Run(() =>
        {
            clearStarted.Set();
            return store.Clear();
        });
        try
        {
            Assert.True(clearStarted.Wait(TimeSpan.FromSeconds(5)));
            Assert.False(clear.IsCompleted);
        }
        finally
        {
            releaseSave.Set();
        }

        Assert.True(await save);
        Assert.True(await clear);
        Assert.Empty(Directory.EnumerateFileSystemEntries(workspace.Paths.PinSessionRoot));
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

    private static PinSessionSnapshot CodeSnapshot()
    {
        var document = new PinCodeDocument("int x = 1;",
            [new PinCodeRun(0, 3, "#569CD6"), new PinCodeRun(4, 1, "#9CDCFE")], "#1E1E1E", "#DCDCDC");
        PinSessionSnapshot snapshot = Snapshot(PinSessionContentKind.Text, "int\tx = 1;\r\n", true);
        return snapshot with { Pin = snapshot.Pin with { TextStyle = PinTextStyle.Code, CodeDocument = document } };
    }

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

    private sealed record LegacySessionManifest(int SchemaVersion, List<LegacySessionPin> Pins);
    private sealed record LegacySessionPin(Guid Id, PinSessionContentKind Kind, string? OriginalText,
        string SourcePageUrl, bool UsesDeviceIndependentPixels, double ImageWidthDip, double ImageHeightDip,
        double Zoom, double Opacity, PinClosedPlacement? Placement, long OpenedOrder = 0);

    private sealed class RecordingLogger : ILogger
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}
