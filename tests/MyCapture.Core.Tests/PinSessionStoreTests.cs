using System.Text;
using System.Security.Cryptography;
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
    public void Save_ProtectsManifestImagesAndSensitiveFieldsWithoutPlaintextLeftovers()
    {
        using var workspace = new TempWorkspace();
        PinSessionSnapshot code = CodeSnapshot();
        PinSessionSnapshot text = Snapshot(PinSessionContentKind.Text, "private-token-451-value", true);
        text = text with { Pin = text.Pin with { SourcePageUrl = "https://private-host.test/private-session" } };
        PinSessionStore store = CreateStore(workspace);

        Assert.True(store.Save([code, text]));

        string manifest = ReadManifestText(workspace);
        Assert.Contains("private-token-451-value", manifest, StringComparison.Ordinal);
        Assert.Contains("private-host.test", manifest, StringComparison.Ordinal);
        Assert.Contains("int x = 1;", manifest, StringComparison.Ordinal);
        Assert.All(Directory.GetFiles(workspace.Paths.PinSessionRoot), path =>
        {
            byte[] bytes = File.ReadAllBytes(path);
            Assert.True(bytes.AsSpan().StartsWith("MCPIN\0"u8));
            Assert.False(bytes.AsSpan().IndexOf("private-token-451-value"u8) >= 0);
            Assert.False(bytes.AsSpan().IndexOf("private-host.test"u8) >= 0);
            Assert.False(bytes.AsSpan().IndexOf("int x = 1;"u8) >= 0);
            Assert.False(bytes.AsSpan().IndexOf(Png.AsSpan(0, 8)) >= 0);
        });
        Assert.Equal(3, Directory.GetFiles(workspace.Paths.PinSessionRoot).Length);
        Assert.All(store.Load(), pin => Assert.Equal(Png, pin.PngBytes));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Save_MigratesLegacyManifestAndImagesEvenWithReuseAndWarmCache(bool warmCache, bool nullBytes)
    {
        using var workspace = new TempWorkspace();
        PinSessionSnapshot original = CodeSnapshot();
        WriteLegacySession(workspace, [original]);
        PinSessionStore store = CreateStore(workspace);
        PinSessionSnapshot restored = Assert.Single((warmCache ? store : CreateStore(workspace)).Load());
        AssertSamePin(original.Pin, restored.Pin);
        Assert.Equal(Png, restored.PngBytes);
        if (warmCache) Assert.True(store.HasImage(original.Pin.Id));

        Assert.True(store.Save([restored with { PngBytes = nullBytes ? null : restored.PngBytes }]));

        AssertSamePin(original.Pin, Assert.Single(CreateStore(workspace).Load()).Pin);
        Assert.Equal(Png, ReadImageBytes(ImagePath(workspace, original.Pin.Id)));
        Assert.Contains("codeDocument", ReadManifestText(workspace), StringComparison.Ordinal);
        Assert.Equal(2, Directory.GetFiles(workspace.Paths.PinSessionRoot).Length);
        Assert.All(Directory.GetFiles(workspace.Paths.PinSessionRoot), path =>
        {
            byte[] bytes = File.ReadAllBytes(path);
            Assert.True(bytes.AsSpan().StartsWith("MCPIN\0"u8));
            Assert.False(bytes.AsSpan().IndexOf("int x = 1;"u8) >= 0);
            Assert.False(bytes.AsSpan().IndexOf(Png.AsSpan(0, 8)) >= 0);
        });
        Assert.Empty(Directory.GetFiles(workspace.Paths.PinSessionRoot, "*.bak"));
        Assert.Empty(Directory.GetFiles(workspace.Paths.PinSessionRoot, "*.tmp"));
    }

    [Theory]
    [InlineData("blob")]
    [InlineData("version")]
    [InlineData("purpose")]
    [InlineData("truncated")]
    [InlineData("foreign-entropy")]
    [InlineData("invalid-json")]
    [InlineData("magic")]
    public void Load_ProtectedManifestFailureBlocksOverwriteUntilSuccessfulRetry(string corruption)
    {
        using var workspace = new TempWorkspace();
        var logger = new RecordingLogger();
        PinSessionSnapshot original = Snapshot();
        Assert.True(CreateStore(workspace).Save([original]));
        byte[] saved = File.ReadAllBytes(ManifestPath(workspace));
        byte[] damaged = CorruptEnvelope(saved, corruption, PinSessionFilePurpose.Manifest);
        File.WriteAllBytes(ManifestPath(workspace), damaged);
        var store = new PinSessionStore(workspace.Paths.PinSessionRoot, logger);

        PinSessionLoadResult result = store.LoadDetailed();

        Assert.True(result.ManifestUnreadable);
        Assert.Empty(result.Pins);
        Assert.False(store.Save([Snapshot()]));
        Assert.False(CreateStore(workspace).Save([]));
        Assert.Equal(damaged, File.ReadAllBytes(ManifestPath(workspace)));
        Assert.True(File.Exists(ImagePath(workspace, original.Pin.Id)));
        Assert.Contains(logger.Messages, message => message.Contains("Could not read the saved pin session", StringComparison.Ordinal));
        File.WriteAllBytes(ManifestPath(workspace), saved);
        Assert.False(store.LoadDetailed().ManifestUnreadable);
        Assert.True(store.Save([original with { PngBytes = null }]));
        Assert.Equal(original.Pin, Assert.Single(store.Load()).Pin);
    }

    [Theory]
    [InlineData("blob")]
    [InlineData("version")]
    [InlineData("purpose")]
    [InlineData("truncated")]
    [InlineData("foreign-entropy")]
    public void Load_ProtectedImageFailureKeepsPendingPinAndLiveBytesRepairIt(string corruption)
    {
        using var workspace = new TempWorkspace();
        PinSessionSnapshot[] original = [Snapshot(), Snapshot(), Snapshot()];
        PinSessionStore store = CreateStore(workspace);
        Assert.True(store.Save(original));
        string path = ImagePath(workspace, original[1].Pin.Id);
        byte[] damaged = CorruptEnvelope(File.ReadAllBytes(path), corruption, PinSessionFilePurpose.Image);
        File.WriteAllBytes(path, damaged);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));

        PinSessionLoadResult loaded = store.LoadDetailed();

        Assert.False(loaded.ManifestUnreadable);
        Assert.Equal(original.Select(pin => pin.Pin), loaded.Pins.Select(pin => pin.Pin));
        Assert.Null(loaded.Pins[1].PngBytes);
        Assert.True(loaded.Pins[1].ProtectionPending);
        Assert.All(new[] { loaded.Pins[0], loaded.Pins[2] }, pin =>
        {
            Assert.Equal(Png, pin.PngBytes);
            Assert.False(pin.ProtectionPending);
        });
        Assert.Equal(damaged, File.ReadAllBytes(path));
        Assert.False(store.HasImage(original[1].Pin.Id));
        Assert.True(store.TryGetImageLength(original[1].Pin.Id, out long length));
        Assert.Equal(Png.LongLength, length);
        Assert.True(store.Save(original));
        Assert.Equal(original.Select(pin => pin.Pin), store.Load().Select(pin => pin.Pin));
        Assert.All(store.Load(), pin => Assert.False(pin.ProtectionPending));
        Assert.Equal(Png, ReadImageBytes(path));
    }

    [Theory]
    [InlineData("crc")]
    [InlineData("invalid-png")]
    public void Load_DecodedImageFailureSkipsOnlyThatPinAndLiveBytesRepairIt(string corruption)
    {
        using var workspace = new TempWorkspace();
        PinSessionSnapshot[] original = [Snapshot(), Snapshot(), Snapshot()];
        PinSessionStore store = CreateStore(workspace);
        Assert.True(store.Save(original));
        string path = ImagePath(workspace, original[1].Pin.Id);
        byte[] damaged = CorruptEnvelope(File.ReadAllBytes(path), corruption, PinSessionFilePurpose.Image);
        File.WriteAllBytes(path, damaged);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));

        PinSessionLoadResult loaded = store.LoadDetailed();

        Assert.False(loaded.ManifestUnreadable);
        Assert.Equal(new[] { original[0].Pin, original[2].Pin }, loaded.Pins.Select(pin => pin.Pin));
        Assert.False(store.HasImage(original[1].Pin.Id));
        Assert.False(store.TryGetImageLength(original[1].Pin.Id, out _));
        Assert.True(store.Save(original));
        Assert.Equal(original.Select(pin => pin.Pin), store.Load().Select(pin => pin.Pin));
        Assert.Equal(Png, ReadImageBytes(path));
    }

    [Theory]
    [InlineData("blob")]
    [InlineData("version")]
    [InlineData("purpose")]
    [InlineData("truncated")]
    [InlineData("foreign-entropy")]
    public void Save_ProtectionPendingImageSurvivesOtherPinsAndRepeatedRestarts(string corruption)
    {
        using var workspace = new TempWorkspace();
        PinSessionSnapshot original = WithOpeningOrder(1);
        Assert.True(CreateStore(workspace).Save([original]));
        string path = ImagePath(workspace, original.Pin.Id);
        byte[] saved = File.ReadAllBytes(path);
        byte[] damaged = CorruptEnvelope(saved, corruption, PinSessionFilePurpose.Image);
        File.WriteAllBytes(path, damaged);
        PinSessionStore store = CreateStore(workspace);
        PinSessionSnapshot pending = Assert.Single(store.Load());
        Assert.Equal(original.Pin, pending.Pin);
        Assert.Null(pending.PngBytes);
        Assert.True(pending.ProtectionPending);
        PinSessionSnapshot other = WithOpeningOrder(2);

        Assert.True(store.Save([pending, other]));
        Assert.Equal(damaged, File.ReadAllBytes(path));
        PinSessionStore restarted = CreateStore(workspace);
        IReadOnlyList<PinSessionSnapshot> restored = restarted.Load();
        Assert.Equal(new[] { original.Pin, other.Pin }, restored.Select(pin => pin.Pin));
        Assert.Null(restored[0].PngBytes);
        Assert.True(restored[0].ProtectionPending);
        Assert.Equal(Png, restored[1].PngBytes);
        Assert.True(restarted.TryGetImageLength(original.Pin.Id, out long length));
        Assert.Equal(Png.LongLength, length);

        // A pending marker also prevents accidental replacement by stale caller-supplied pixels.
        Assert.True(restarted.Save([restored[0] with { PngBytes = Png }, restored[1]]));
        Assert.Equal(damaged, File.ReadAllBytes(path));
        Assert.True(CreateStore(workspace).Load()[0].ProtectionPending);

        File.WriteAllBytes(path, saved);
        PinSessionSnapshot recovered = CreateStore(workspace).Load()[0];
        Assert.Equal(original.Pin, recovered.Pin);
        Assert.Equal(Png, recovered.PngBytes);
        Assert.False(recovered.ProtectionPending);
    }

    [Fact]
    public void Save_ProtectionPendingRecordsCountWithinFiftyPinLimitBeforeNewLivePins()
    {
        using var workspace = new TempWorkspace();
        PinSessionSnapshot original = WithOpeningOrder(1);
        Assert.True(CreateStore(workspace).Save([original]));
        string path = ImagePath(workspace, original.Pin.Id);
        byte[] damaged = CorruptEnvelope(File.ReadAllBytes(path), "version", PinSessionFilePurpose.Image);
        File.WriteAllBytes(path, damaged);
        PinSessionStore store = CreateStore(workspace);
        PinSessionSnapshot pending = Assert.Single(store.Load());
        PinSessionSnapshot[] live = Enumerable.Range(2, PinSessionStore.DefaultMaxPins)
            .Select(order => WithOpeningOrder(order)).ToArray();

        Assert.True(store.Save([pending, .. live]));

        IReadOnlyList<PinSessionSnapshot> restored = CreateStore(workspace).Load();
        Assert.Equal(PinSessionStore.DefaultMaxPins, restored.Count);
        Assert.Equal(original.Pin, restored[0].Pin);
        Assert.True(restored[0].ProtectionPending);
        Assert.Null(restored[0].PngBytes);
        Assert.Equal(live.Skip(1).Select(pin => pin.Pin), restored.Skip(1).Select(pin => pin.Pin));
        Assert.Equal(damaged, File.ReadAllBytes(path));
        Assert.Equal(PinSessionStore.DefaultMaxPins, Directory.GetFiles(workspace.Paths.PinSessionRoot, "*.png").Length);
    }

    [Fact]
    public void Load_ProtectionPendingFlagIsKeptForEachRecordSharingTheSameImage()
    {
        using var workspace = new TempWorkspace();
        PinSessionSnapshot original = WithOpeningOrder(1);
        PinSessionSnapshot duplicate = original with { Pin = original.Pin with { OpenedOrder = 2, Zoom = 2 } };
        Assert.True(CreateStore(workspace).Save([original, duplicate]));
        string path = ImagePath(workspace, original.Pin.Id);
        byte[] damaged = CorruptEnvelope(File.ReadAllBytes(path), "purpose", PinSessionFilePurpose.Image);
        File.WriteAllBytes(path, damaged);
        PinSessionStore store = CreateStore(workspace);

        IReadOnlyList<PinSessionSnapshot> restored = store.Load();

        Assert.Equal(new[] { original.Pin, duplicate.Pin }, restored.Select(pin => pin.Pin));
        Assert.All(restored, pin =>
        {
            Assert.True(pin.ProtectionPending);
            Assert.Null(pin.PngBytes);
        });
        Assert.True(store.Save(restored));
        Assert.Equal(damaged, File.ReadAllBytes(path));
        Assert.Equal(2, CreateStore(workspace).Load().Count);
        Assert.All(CreateStore(workspace).Load(), pin => Assert.True(pin.ProtectionPending));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void Load_ManifestTruncatedBeforeFullHeaderBlocksSaveAndPreservesImages(int length)
    {
        using var workspace = new TempWorkspace();
        PinSessionSnapshot original = Snapshot();
        Assert.True(CreateStore(workspace).Save([original]));
        byte[] image = File.ReadAllBytes(ImagePath(workspace, original.Pin.Id));
        byte[] truncated = File.ReadAllBytes(ManifestPath(workspace))[..length];
        File.WriteAllBytes(ManifestPath(workspace), truncated);
        PinSessionStore store = CreateStore(workspace);

        PinSessionLoadResult loaded = store.LoadDetailed();

        Assert.True(loaded.ManifestUnreadable);
        Assert.Empty(loaded.Pins);
        Assert.False(store.Save([Snapshot()]));
        Assert.False(CreateStore(workspace).Save([]));
        Assert.Equal(truncated, File.ReadAllBytes(ManifestPath(workspace)));
        Assert.Equal(image, File.ReadAllBytes(ImagePath(workspace, original.Pin.Id)));
        Assert.Equal(2, Directory.GetFiles(workspace.Paths.PinSessionRoot).Length);
    }

    [Theory]
    [InlineData(" \t\r\n")]
    [InlineData("\uFEFF")]
    [InlineData("\uFEFF \t\r\n")]
    public void Load_WhitespaceOnlyManifestBlocksSaveAndPreservesImages(string whitespace)
    {
        using var workspace = new TempWorkspace();
        PinSessionSnapshot original = Snapshot();
        WriteLegacySession(workspace, [original]);
        byte[] invalid = Encoding.UTF8.GetBytes(whitespace);
        File.WriteAllBytes(ManifestPath(workspace), invalid);
        PinSessionStore store = CreateStore(workspace);

        PinSessionLoadResult loaded = store.LoadDetailed();

        Assert.True(loaded.ManifestUnreadable);
        Assert.Empty(loaded.Pins);
        Assert.False(store.Save([Snapshot()]));
        Assert.False(CreateStore(workspace).Save([]));
        Assert.Equal(invalid, File.ReadAllBytes(ManifestPath(workspace)));
        Assert.Equal(Png, File.ReadAllBytes(ImagePath(workspace, original.Pin.Id)));
        Assert.Equal(2, Directory.GetFiles(workspace.Paths.PinSessionRoot).Length);
    }

    [Fact]
    public void Save_LockedLegacyImageKeepsItsPlaintextBudgetAndMigratesAfterRelease()
    {
        using var workspace = new TempWorkspace();
        PinSessionSnapshot original = Snapshot();
        WriteLegacySession(workspace, [original]);
        var store = new PinSessionStore(workspace.Paths.PinSessionRoot, NullLogger.Instance, maxPngBytes: Png.Length);
        string path = ImagePath(workspace, original.Pin.Id);
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Null(Assert.Single(store.Load()).PngBytes);
            Assert.True(store.TryGetImageLength(original.Pin.Id, out long length));
            Assert.Equal(Png.LongLength, length);
            Assert.True(store.Save([original with { PngBytes = null }]));
        }

        Assert.True(store.Save([original with { PngBytes = null }]));
        Assert.Equal(Png, ReadImageBytes(path));
        Assert.Equal(original.Pin, Assert.Single(store.Load()).Pin);
    }

    [Fact]
    public void Save_ReadableLegacyImageWithReplacementDeniedKeepsCommittedSessionUntilRetry()
    {
        using var workspace = new TempWorkspace();
        PinSessionSnapshot original = Snapshot();
        WriteLegacySession(workspace, [original]);
        string path = ImagePath(workspace, original.Pin.Id);
        byte[] manifest = File.ReadAllBytes(ManifestPath(workspace));
        PinSessionStore store = CreateStore(workspace);
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.True(store.HasImage(original.Pin.Id));
            Assert.False(store.Save([original with { PngBytes = null }]));
            Assert.Equal(manifest, File.ReadAllBytes(ManifestPath(workspace)));
            Assert.Equal(Png, File.ReadAllBytes(path));
            Assert.Equal(original.Pin, Assert.Single(store.Load()).Pin);
        }

        Assert.True(store.Save([original with { PngBytes = null }]));
        Assert.Equal(Png, ReadImageBytes(path));
        Assert.Equal(original.Pin, Assert.Single(store.Load()).Pin);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Load_OversizedImageIsRejectedBeforeReadingAndCanBeRepaired(bool protectedHeader)
    {
        using var workspace = new TempWorkspace();
        PinSessionSnapshot original = Snapshot();
        var store = new PinSessionStore(workspace.Paths.PinSessionRoot, NullLogger.Instance, maxPngBytes: Png.Length);
        Assert.True(store.Save([original]));
        string path = ImagePath(workspace, original.Pin.Id);
        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
        {
            stream.Write(protectedHeader ? new byte[] { 77, 67, 80, 73, 78, 0, 1, 2 } : Png);
            stream.SetLength(Png.LongLength + 64 * 1024 + 1);
        }
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));

        Assert.Empty(store.Load());
        Assert.False(store.HasImage(original.Pin.Id));
        Assert.False(store.TryGetImageLength(original.Pin.Id, out _));
        Assert.True(store.Save([original]));
        Assert.Equal(Png, Assert.Single(store.Load()).PngBytes);
    }

    [Fact]
    public void Failures_DoNotLogExceptionMessagesPathsOrPinContents()
    {
        using var workspace = new TempWorkspace();
        var logger = new RecordingLogger();
        const string secret = "secret-exception-pin-value";
        PinSessionSnapshot original = Snapshot(PinSessionContentKind.Text, secret, true);
        Assert.True(CreateStore(workspace).Save([original]));
        var store = new PinSessionStore(workspace.Paths.PinSessionRoot, logger)
        {
            ReadImageLengthForTest = _ => throw new IOException(workspace.Root + secret)
        };

        Assert.Single(store.Load());

        Assert.NotEmpty(logger.Messages);
        Assert.All(logger.Messages, message =>
        {
            Assert.DoesNotContain(workspace.Root, message, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, message, StringComparison.Ordinal);
        });
        Assert.All(logger.Exceptions, exception => Assert.Null(exception));
    }

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
        JsonNode manifest = JsonNode.Parse(ReadManifestText(workspace))!;
        Assert.Equal(1, manifest["schemaVersion"]!.GetValue<int>());
        Assert.Equal("Text", manifest["pins"]![1]!["kind"]!.GetValue<string>());
        Assert.DoesNotContain("isHidden", manifest.ToJsonString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("isClickThrough", manifest.ToJsonString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3, Directory.GetFiles(workspace.Paths.PinSessionRoot, "*.png").Length);
        Assert.All(Directory.GetFiles(workspace.Paths.PinSessionRoot, "*.png"),
            path => Assert.True(Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out _)));
    }

    [Fact]
    public void SaveThenLoad_RoundTripsProvenanceSeparatelyFromTheLegacyExportUrl()
    {
        using var workspace = new TempWorkspace();
        PinSessionSnapshot snapshot = Snapshot();
        snapshot = snapshot with
        {
            Pin = snapshot.Pin with
            {
                SourcePageUrl = string.Empty,
                SourceUrl = "https://example.test/spec?token=private#section",
                SourceDocumentPath = @"C:\specs\사양서.xlsx",
                SourceAppName = "Excel",
                SourceAppExecutablePath = @"C:\Office\EXCEL.EXE",
                SourceWindowTitle = "사양서 - Excel",
            }
        };
        Assert.True(CreateStore(workspace).Save([snapshot]));
        Assert.Equal(snapshot.Pin, Assert.Single(CreateStore(workspace).Load()).Pin);
        JsonNode manifest = JsonNode.Parse(ReadManifestText(workspace))!;
        Assert.Equal(1, manifest["schemaVersion"]!.GetValue<int>());
        Assert.Equal(string.Empty, manifest["pins"]![0]!["sourcePageUrl"]!.GetValue<string>());
        Assert.Equal(snapshot.Pin.SourceDocumentPath, manifest["pins"]![0]!["sourceDocumentPath"]!.GetValue<string>());
    }

    [Fact]
    public void Load_LegacySessionWithoutProvenanceFieldsKeepsTheSourcePageUrl()
    {
        using var workspace = new TempWorkspace();
        PinSessionSnapshot snapshot = Snapshot();
        Assert.True(CreateStore(workspace).Save([snapshot]));
        JsonNode manifest = JsonNode.Parse(ReadManifestText(workspace))!;
        JsonObject record = manifest["pins"]![0]!.AsObject();
        foreach (string key in new[] { "sourceUrl", "sourceDocumentPath", "sourceAppName", "sourceAppExecutablePath", "sourceWindowTitle" })
            record.Remove(key);
        File.WriteAllText(ManifestPath(workspace), manifest.ToJsonString());
        PinSessionPin restored = Assert.Single(CreateStore(workspace).Load()).Pin;
        Assert.Equal(snapshot.Pin, restored);
        Assert.Equal(snapshot.Pin.SourcePageUrl, restored.SourcePageUrl);
        Assert.Null(restored.SourceUrl);
        Assert.Null(restored.SourceDocumentPath);
        Assert.Null(restored.SourceAppName);
        Assert.Null(restored.SourceAppExecutablePath);
        Assert.Null(restored.SourceWindowTitle);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsRenderingMetadataAndDecodedPayloadRemainsReadableByLegacySchema()
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
            ReadManifestText(workspace), legacyOptions)!;
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
        JsonNode manifest = JsonNode.Parse(ReadManifestText(workspace))!;
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
        JsonNode manifest = JsonNode.Parse(ReadManifestText(workspace))!;
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
            .Sum(path => ReadImageBytes(path).LongLength));
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
        JsonNode manifest = JsonNode.Parse(ReadManifestText(workspace))!;
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
        Assert.Equal(Png, ReadImageBytes(imagePath));
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
            Assert.Equal(3, JsonNode.Parse(ReadManifestText(workspace))!["pins"]!.AsArray().Count);
        }

        Assert.True(store.Save(pins.Select(pin => pin with { PngBytes = null }).ToList()));
        Assert.Equal(pins.Select(pin => pin.Pin), store.Load().Select(pin => pin.Pin));
        Assert.Equal(Png, ReadImageBytes(lockedPath));
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
            Assert.Single(JsonNode.Parse(ReadManifestText(workspace))!["pins"]!.AsArray());
        }

        Assert.Equal(pin.Pin, Assert.Single(store.Load()).Pin);
        Assert.Equal(Png, ReadImageBytes(path));
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
        // These synchronous workers must contend on the store gate, not wait for a pool slot.
        Task<bool> save = Task.Factory.StartNew(() => store.Save([Snapshot()], canCommit: () =>
        {
            saveInsideGate.Set();
            return releaseSave.Wait(TimeSpan.FromSeconds(5));
        }), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Task<bool>? clear = null;
        try
        {
            Assert.True(saveInsideGate.Wait(TimeSpan.FromSeconds(5)));
            clear = Task.Factory.StartNew(() =>
            {
                clearStarted.Set();
                return store.Clear();
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Assert.True(clearStarted.Wait(TimeSpan.FromSeconds(5)));
            Assert.False(clear.IsCompleted);
        }
        finally
        {
            releaseSave.Set();
            if (clear is null) await save;
            else await Task.WhenAll(save, clear);
        }

        Assert.True(await save);
        Assert.True(await clear!);
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
        JsonNode manifest = JsonNode.Parse(ReadManifestText(workspace))!;
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

    [Fact]
    public void SaveDetailed_CompleteSaveReportsEveryRecordAndReadsBack()
    {
        using var workspace = new TempWorkspace();
        PinSessionStore store = CreateStore(workspace);
        PinSessionSnapshot first = Snapshot();
        PinSessionSnapshot sameContent = first with { Pin = first.Pin with { Zoom = 2.25 } };
        PinSessionSnapshot code = CodeSnapshot();

        PinSessionSaveResult result = store.SaveDetailed([first, sameContent, code]);

        Assert.Equal(PinSessionSaveOutcome.Committed, result.Outcome);
        Assert.True(result.IsComplete);
        Assert.Equal(3, result.RequestedCount);
        Assert.Equal(new[] { first.Pin.Id, first.Pin.Id, code.Pin.Id }, result.SavedPinIds);
        Assert.Empty(result.MissingPins);
        Assert.True(result.Generation > 0);
        Assert.Equal(result.Generation, store.Generation);
        Assert.True(store.VerifyReadBack(result));
        Assert.Equal(result.SavedPinIds, CreateStore(workspace).Load().Select(pin => pin.Pin.Id));
    }

    [Theory]
    [InlineData("write")]
    [InlineData("locked")]
    public void SaveDetailed_OneImageWriteFailureCommitsTheOtherPinsAsAPartialSave(string failure)
    {
        using var workspace = new TempWorkspace();
        PinSessionStore store = CreateStore(workspace);
        PinSessionSnapshot kept = WithOpeningOrder(1);
        PinSessionSnapshot failed = WithOpeningOrder(2);
        string failedPath = ImagePath(workspace, failed.Pin.Id);
        FileStream? held = null;
        if (failure == "write")
        {
            store.BeforeImageWriteForTest = id =>
            {
                if (id == failed.Pin.Id) throw new IOException("fixture write failure");
            };
        }
        else
        {
            // Another process holds a stale file where this pin's image belongs.
            Directory.CreateDirectory(workspace.Paths.PinSessionRoot);
            File.WriteAllBytes(failedPath, Png);
            held = new FileStream(failedPath, FileMode.Open, FileAccess.Read, FileShare.None);
        }
        PinSessionSaveResult partial;
        using (held) partial = store.SaveDetailed([kept, failed]);

        Assert.Equal(PinSessionSaveOutcome.Committed, partial.Outcome);
        Assert.False(partial.IsComplete);
        Assert.Equal(2, partial.RequestedCount);
        Assert.Equal(new[] { kept.Pin.Id }, partial.SavedPinIds);
        Assert.Equal(new PinSessionMissingPin(failed.Pin.Id, PinSessionMissingReason.ImageWriteFailed),
            Assert.Single(partial.MissingPins));
        Assert.False(store.VerifyReadBack(partial));
        Assert.Equal(kept.Pin, Assert.Single(CreateStore(workspace).Load()).Pin);
        Assert.Empty(Directory.GetFiles(workspace.Paths.PinSessionRoot, "*.tmp"));

        store.BeforeImageWriteForTest = null;
        PinSessionSaveResult retried = store.SaveDetailed([kept with { PngBytes = null }, failed]);
        Assert.True(retried.IsComplete);
        Assert.True(store.VerifyReadBack(retried));
        Assert.Equal(new[] { kept.Pin, failed.Pin }, CreateStore(workspace).Load().Select(pin => pin.Pin));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SaveDetailed_ReportsRecordsOverTheCountOrByteLimit(bool limitBytes)
    {
        using var workspace = new TempWorkspace();
        PinSessionSnapshot[] pins = [WithOpeningOrder(3), WithOpeningOrder(1), WithOpeningOrder(2)];
        var store = new PinSessionStore(workspace.Paths.PinSessionRoot, NullLogger.Instance,
            maxPins: limitBytes ? 50 : 2,
            maxPngBytes: limitBytes ? 2L * Png.Length : PinSessionStore.DefaultMaxPngBytes);

        PinSessionSaveResult result = store.SaveDetailed(pins);

        Assert.Equal(PinSessionSaveOutcome.Committed, result.Outcome);
        Assert.False(result.IsComplete);
        Assert.Equal(3, result.RequestedCount);
        Assert.Equal(new[] { pins[0].Pin.Id, pins[2].Pin.Id }, result.SavedPinIds);
        Assert.Equal(new PinSessionMissingPin(pins[1].Pin.Id, PinSessionMissingReason.OverLimit),
            Assert.Single(result.MissingPins));
        Assert.False(store.VerifyReadBack(result));
        Assert.Equal(result.SavedPinIds, store.Load().Select(pin => pin.Pin.Id));
    }

    [Fact]
    public void SaveDetailed_WindowsSharingAFailedImageReportTheFailureNotAMissingPng()
    {
        using var workspace = new TempWorkspace();
        PinSessionStore store = CreateStore(workspace);
        PinSessionSnapshot newest = WithOpeningOrder(2);
        // The app sends the PNG only with the newest window that shows this content.
        PinSessionSnapshot olderWindow = newest with { Pin = newest.Pin with { OpenedOrder = 1, Zoom = 2 }, PngBytes = null };
        store.BeforeImageWriteForTest = _ => throw new IOException("fixture write failure");

        PinSessionSaveResult result = store.SaveDetailed([olderWindow, newest]);

        Assert.Equal(PinSessionSaveOutcome.Committed, result.Outcome);
        Assert.Empty(result.SavedPinIds);
        Assert.Equal(new[]
        {
            new PinSessionMissingPin(newest.Pin.Id, PinSessionMissingReason.ImageWriteFailed),
            new PinSessionMissingPin(newest.Pin.Id, PinSessionMissingReason.ImageWriteFailed),
        }, result.MissingPins);
    }

    [Fact]
    public void SaveDetailed_ProtectionFailureLeavesOnlyThatPinOut()
    {
        using var workspace = new TempWorkspace();
        PinSessionStore store = CreateStore(workspace);
        PinSessionSnapshot kept = WithOpeningOrder(1);
        PinSessionSnapshot unprotected = WithOpeningOrder(2);
        store.BeforeImageProtectForTest = id =>
        {
            if (id == unprotected.Pin.Id) throw new CryptographicException("fixture protection failure");
        };

        PinSessionSaveResult result = store.SaveDetailed([kept, unprotected]);

        Assert.Equal(PinSessionSaveOutcome.Committed, result.Outcome);
        Assert.False(result.IsComplete);
        Assert.Equal(new[] { kept.Pin.Id }, result.SavedPinIds);
        Assert.Equal(new PinSessionMissingPin(unprotected.Pin.Id, PinSessionMissingReason.ProtectionFailed),
            Assert.Single(result.MissingPins));
        Assert.False(File.Exists(ImagePath(workspace, unprotected.Pin.Id)));
        Assert.False(store.VerifyReadBack(result));
        Assert.Equal(kept.Pin, Assert.Single(CreateStore(workspace).Load()).Pin);
    }

    [Fact]
    public void SaveDetailed_ReportsInvalidAndImagelessRecordsInRequestOrder()
    {
        using var workspace = new TempWorkspace();
        PinSessionStore store = CreateStore(workspace);
        PinSessionSnapshot invalid = Snapshot();
        invalid = invalid with { Pin = invalid.Pin with { Zoom = double.NaN } };
        PinSessionSnapshot good = Snapshot();
        PinSessionSnapshot imageless = Snapshot() with { PngBytes = null };

        PinSessionSaveResult result = store.SaveDetailed([invalid, good, imageless]);

        Assert.Equal(PinSessionSaveOutcome.Committed, result.Outcome);
        Assert.Equal(new[] { good.Pin.Id }, result.SavedPinIds);
        Assert.Equal(new[]
        {
            new PinSessionMissingPin(invalid.Pin.Id, PinSessionMissingReason.Invalid),
            new PinSessionMissingPin(imageless.Pin.Id, PinSessionMissingReason.ImageUnavailable),
        }, result.MissingPins);
        Assert.Equal(result.RequestedCount, result.SavedPinIds.Count + result.MissingPins.Count);
        Assert.False(result.IsComplete);
    }

    [Theory]
    [InlineData(PinSessionSaveOutcome.Canceled)]
    [InlineData(PinSessionSaveOutcome.ManifestUnreadable)]
    [InlineData(PinSessionSaveOutcome.Failed)]
    public void SaveDetailed_UncommittedSaveReportsEveryRecordMissingAndKeepsTheSession(PinSessionSaveOutcome outcome)
    {
        using var workspace = new TempWorkspace();
        PinSessionSnapshot original = Snapshot();
        PinSessionStore seed = CreateStore(workspace);
        PinSessionSaveResult committed = seed.SaveDetailed([original]);
        byte[] manifest = File.ReadAllBytes(ManifestPath(workspace));
        PinSessionStore store = outcome == PinSessionSaveOutcome.ManifestUnreadable ? CreateStore(workspace) : seed;
        long generation = store.Generation;
        PinSessionSnapshot[] requested = [original with { PngBytes = null }, Snapshot()];

        PinSessionSaveResult result;
        if (outcome == PinSessionSaveOutcome.Canceled) result = store.SaveDetailed(requested, canCommit: () => false);
        else
        {
            // An exclusive handle makes the manifest unreadable; a shared one only blocks its replacement.
            FileShare share = outcome == PinSessionSaveOutcome.ManifestUnreadable ? FileShare.None : FileShare.Read;
            using var held = new FileStream(ManifestPath(workspace), FileMode.Open, FileAccess.Read, share);
            result = store.SaveDetailed(requested);
        }

        Assert.Equal(outcome, result.Outcome);
        Assert.False(result.ManifestCommitted);
        Assert.False(result.IsComplete);
        Assert.Equal(2, result.RequestedCount);
        Assert.Empty(result.SavedPinIds);
        Assert.Equal(requested.Select(pin => new PinSessionMissingPin(pin.Pin.Id, PinSessionMissingReason.NotCommitted)),
            result.MissingPins);
        Assert.Equal(generation, result.Generation);
        Assert.Equal(generation, store.Generation);
        Assert.False(store.VerifyReadBack(result));
        Assert.Equal(manifest, File.ReadAllBytes(ManifestPath(workspace)));
        Assert.Equal(ImagePath(workspace, original.Pin.Id),
            Assert.Single(Directory.GetFiles(workspace.Paths.PinSessionRoot, "*.png")));
        Assert.True(seed.VerifyReadBack(committed));
    }

    [Fact]
    public void VerifyReadBack_RequiresTheLatestCommitOfThisStore()
    {
        using var workspace = new TempWorkspace();
        PinSessionStore store = CreateStore(workspace);
        PinSessionSnapshot pin = Snapshot();
        PinSessionSaveResult first = store.SaveDetailed([pin]);
        PinSessionSaveResult second = store.SaveDetailed([pin with { PngBytes = null, Pin = pin.Pin with { Zoom = 3 } }]);

        Assert.True(first.IsComplete);
        Assert.True(second.Generation > first.Generation);
        // The same pin is on disk, but with the later presentation, not what the first save wrote.
        Assert.False(store.VerifyReadBack(first));
        Assert.False(CreateStore(workspace).VerifyReadBack(second));
        // Another store holding the same content, such as another pin board, has its own commits.
        using var otherWorkspace = new TempWorkspace();
        PinSessionStore other = CreateStore(otherWorkspace);
        Assert.True(other.SaveDetailed([pin]).IsComplete);
        Assert.False(other.VerifyReadBack(second));
        Assert.False(store.VerifyReadBack(other.SaveDetailed([pin with { PngBytes = null }])));
        Assert.True(store.VerifyReadBack(second));

        Assert.True(store.Clear());

        Assert.True(store.Generation > second.Generation);
        Assert.False(store.VerifyReadBack(second));
    }

    [Fact]
    public void VerifyReadBack_RequiresEveryImageToReadBackWithoutBlockingLaterSaves()
    {
        using var workspace = new TempWorkspace();
        PinSessionStore store = CreateStore(workspace);
        PinSessionSnapshot pin = Snapshot();
        Assert.True(store.SaveDetailed([pin]).IsComplete);
        string path = ImagePath(workspace, pin.Pin.Id);
        PinSessionSaveResult carried;
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            // The record keeps its committed image, which cannot be read back yet.
            carried = store.SaveDetailed([pin with { PngBytes = null }]);
            Assert.True(carried.IsComplete);
            Assert.False(store.VerifyReadBack(carried));
        }
        Assert.True(store.VerifyReadBack(carried));

        byte[] saved = File.ReadAllBytes(path);
        File.WriteAllBytes(path, CorruptEnvelope(saved, "blob", PinSessionFilePurpose.Image));
        Assert.False(store.VerifyReadBack(carried));
        File.WriteAllBytes(path, saved);
        using (new FileStream(ManifestPath(workspace), FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.False(store.VerifyReadBack(carried));

        PinSessionSaveResult later = store.SaveDetailed([pin with { PngBytes = null }]);
        Assert.True(later.IsComplete);
        Assert.True(store.VerifyReadBack(later));
    }

    private static PinSessionStore CreateStore(TempWorkspace workspace) =>
        new(workspace.Paths.PinSessionRoot, NullLogger.Instance);

    private static string ReadManifestText(TempWorkspace workspace) =>
        Encoding.UTF8.GetString(ReadProtectedFile(ManifestPath(workspace), purpose: 1));

    private static byte[] ReadImageBytes(string path) => ReadProtectedFile(path, purpose: 2);

    // Decode independently of the production helper to assert the exact on-disk contract.
    private static byte[] ReadProtectedFile(string path, byte purpose)
    {
        byte[] bytes = File.ReadAllBytes(path);
        byte[] header = [77, 67, 80, 73, 78, 0, 1, purpose];
        Assert.True(bytes.Length > header.Length);
        Assert.Equal(header, bytes[..header.Length]);
        return ProtectedData.Unprotect(bytes[header.Length..], header, DataProtectionScope.CurrentUser);
    }

    // PinCodeDocument.Runs is a copied list, so record equality compares it by reference;
    // compare the code document structurally and every other field by record equality.
    private static void AssertSamePin(PinSessionPin expected, PinSessionPin actual)
    {
        Assert.Equal(expected with { CodeDocument = null }, actual with { CodeDocument = null });
        if (expected.CodeDocument is null) { Assert.Null(actual.CodeDocument); return; }
        Assert.NotNull(actual.CodeDocument);
        Assert.Equal(expected.CodeDocument.DisplayText, actual.CodeDocument.DisplayText);
        Assert.Equal(expected.CodeDocument.Background, actual.CodeDocument.Background);
        Assert.Equal(expected.CodeDocument.Foreground, actual.CodeDocument.Foreground);
        Assert.Equal(expected.CodeDocument.Runs, actual.CodeDocument.Runs);
    }

    private static void WriteLegacySession(TempWorkspace workspace, IReadOnlyList<PinSessionSnapshot> pins)
    {
        Directory.CreateDirectory(workspace.Paths.PinSessionRoot);
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            IgnoreReadOnlyProperties = true,
            Converters = { new JsonStringEnumConverter<PinSessionContentKind>(), new JsonStringEnumConverter<PinTextStyle>() }
        };
        // A record preserves schemaVersion with IgnoreReadOnlyProperties, matching the 4.5.0 writer.
        File.WriteAllText(ManifestPath(workspace), JsonSerializer.Serialize(
            new SessionManifest(1, pins.Select(pin => pin.Pin).ToList()), options));
        foreach (PinSessionSnapshot pin in pins) File.WriteAllBytes(ImagePath(workspace, pin.Pin.Id), pin.PngBytes!);
    }

    private static byte[] CorruptEnvelope(byte[] bytes, string corruption, PinSessionFilePurpose purpose)
    {
        byte[] damaged = bytes.ToArray();
        switch (corruption)
        {
            case "blob": damaged[^1] ^= 1; break;
            case "magic": damaged[0] ^= 1; break;
            case "version": damaged[6] = 99; break;
            case "purpose": damaged[7] = purpose == PinSessionFilePurpose.Manifest ? (byte)2 : (byte)1; break;
            case "truncated": return damaged[..PinSessionProtection.HeaderLength];
            case "foreign-entropy":
                byte[] wrongKey = ProtectedData.Protect([1, 2, 3], [77, 88, 99], DataProtectionScope.CurrentUser);
                return [.. damaged[..PinSessionProtection.HeaderLength], .. wrongKey];
            case "invalid-json": return PinSessionProtection.Protect("{invalid"u8.ToArray(), purpose);
            case "invalid-png": return PinSessionProtection.Protect("invalid png"u8.ToArray(), purpose);
            case "crc":
                byte[] png = Png.ToArray();
                png[45] ^= 1;
                return PinSessionProtection.Protect(png, purpose);
        }
        return damaged;
    }

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

    private sealed record SessionManifest(int SchemaVersion, List<PinSessionPin> Pins);
    private sealed record LegacySessionManifest(int SchemaVersion, List<LegacySessionPin> Pins);
    private sealed record LegacySessionPin(Guid Id, PinSessionContentKind Kind, string? OriginalText,
        string SourcePageUrl, bool UsesDeviceIndependentPixels, double ImageWidthDip, double ImageHeightDip,
        double Zoom, double Opacity, PinClosedPlacement? Placement, long OpenedOrder = 0);

    private sealed class RecordingLogger : ILogger
    {
        public List<string> Messages { get; } = [];
        public List<Exception?> Exceptions { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
            Exceptions.Add(exception);
        }
    }
}
