using System.Text.Json;
using MyCapture.Core.Queue;
using MyCapture.Core.Recording;
using MyCapture.Core.Serialization;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class VideoEditDocumentTests
{
    [Fact]
    public void LegacyCaptureRecordJson_DefaultsMediaKindToImage()
    {
        const string legacyJson = """
            {
              "id": "68ec57ca-c857-49f0-8fd7-5714a489340a",
              "width": 1280,
              "height": 720,
              "relativeDirectory": "2026-09/legacy"
            }
            """;

        CaptureRecord? record = JsonSerializer.Deserialize<CaptureRecord>(
            legacyJson,
            JsonDefaults.Compact);

        Assert.NotNull(record);
        Assert.Equal(CaptureMediaKind.Image, record.MediaKind);
        Assert.True(record.IsImage);
        Assert.False(record.IsVideo);
    }

    [Fact]
    public void NormalizeFor_ClampsTrimAndOverlaysAndDropsInvalidEntries()
    {
        Guid retainedId = Guid.NewGuid();
        var document = new VideoEditDocument
        {
            CanvasWidth = 1,
            CanvasHeight = 1,
            SourceDurationMs = 12,
            TrimInMs = -50,
            TrimOutMs = 2_500,
            TextOverlays =
            [
                new TimedTextOverlay
                {
                    Id = retainedId,
                    StartMs = -20,
                    EndMs = 2_500,
                    Text = "  keep me  ",
                    Placement = (VideoTextPlacement)999,
                },
                new TimedTextOverlay { StartMs = 100, EndMs = 100, Text = "empty interval" },
                new TimedTextOverlay { StartMs = double.NaN, EndMs = 200, Text = "bad time" },
                new TimedTextOverlay { StartMs = 10, EndMs = 20, Text = "   " },
                null!,
            ],
        };

        VideoEditDocument normalized = document.NormalizeFor(1920, 1080, 1_000);

        Assert.Equal(1920, normalized.CanvasWidth);
        Assert.Equal(1080, normalized.CanvasHeight);
        Assert.Equal(1_000, normalized.SourceDurationMs);
        Assert.Equal(0, normalized.TrimInMs);
        Assert.Equal(1_000, normalized.TrimOutMs);

        TimedTextOverlay overlay = Assert.Single(normalized.TextOverlays);
        Assert.Equal(retainedId, overlay.Id);
        Assert.Equal(0, overlay.StartMs);
        Assert.Equal(1_000, overlay.EndMs);
        Assert.Equal("keep me", overlay.Text);
        Assert.Equal(VideoTextPlacement.Bottom, overlay.Placement);
    }

    [Fact]
    public void NormalizeFor_InvalidTrimIntervalFallsBackToFullDuration()
    {
        var document = new VideoEditDocument
        {
            TrimInMs = 900,
            TrimOutMs = 100,
        };

        VideoEditDocument normalized = document.NormalizeFor(640, 360, 1_000);

        Assert.Equal(0, normalized.TrimInMs);
        Assert.Equal(1_000, normalized.TrimOutMs);
    }

    [Fact]
    public void TimedTextOverlay_UsesHalfOpenStartInclusiveEndExclusiveInterval()
    {
        var overlay = new TimedTextOverlay { StartMs = 100, EndMs = 200, Text = "note" };

        Assert.False(overlay.IsActiveAt(99.999));
        Assert.True(overlay.IsActiveAt(100));
        Assert.True(overlay.IsActiveAt(199.999));
        Assert.False(overlay.IsActiveAt(200));
        Assert.False(overlay.IsActiveAt(double.NaN));
        Assert.False(overlay.IsActiveAt(double.PositiveInfinity));
    }

    [Fact]
    public void Clone_ReturnsADeepDetachedCopy()
    {
        Guid overlayId = Guid.NewGuid();
        var original = new VideoEditDocument
        {
            CanvasWidth = 800,
            CanvasHeight = 450,
            SourceDurationMs = 4_000,
            TrimInMs = 250,
            TrimOutMs = 3_500,
            TextOverlays =
            [
                new TimedTextOverlay
                {
                    Id = overlayId,
                    StartMs = 500,
                    EndMs = 1_500,
                    Text = "original",
                    Placement = VideoTextPlacement.Top,
                },
            ],
        };

        VideoEditDocument clone = original.Clone();

        Assert.NotSame(original, clone);
        Assert.NotSame(original.TextOverlays, clone.TextOverlays);
        TimedTextOverlay clonedOverlay = Assert.Single(clone.TextOverlays);
        Assert.NotSame(original.TextOverlays[0], clonedOverlay);
        Assert.Equal(overlayId, clonedOverlay.Id);
        Assert.Equal("original", clonedOverlay.Text);

        clonedOverlay.Text = "changed";
        clone.TextOverlays.Add(new TimedTextOverlay());
        Assert.Equal("original", original.TextOverlays[0].Text);
        Assert.Single(original.TextOverlays);
    }

    [Fact]
    public void NormalizeFor_RejectsUnknownSchemaVersion()
    {
        var document = new VideoEditDocument
        {
            SchemaVersion = VideoEditDocument.CurrentSchemaVersion + 1,
        };

        NotSupportedException error = Assert.Throws<NotSupportedException>(
            () => document.NormalizeFor(1920, 1080, 1_000));

        Assert.Contains("schema", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NormalizeFor_EnforcesTextAndOverlayCapsAndMakesIdsUnique()
    {
        Guid duplicateId = Guid.NewGuid();
        var overlays = Enumerable.Range(0, VideoEditDocument.MaximumOverlayCount + 5)
            .Select(index => new TimedTextOverlay
            {
                Id = duplicateId,
                StartMs = index,
                EndMs = index + 1,
                Text = new string('x', VideoEditDocument.MaximumTextLength + 25),
            })
            .ToList();
        var document = new VideoEditDocument { TextOverlays = overlays };

        VideoEditDocument normalized = document.NormalizeFor(1920, 1080, 10_000);

        Assert.Equal(VideoEditDocument.MaximumOverlayCount, normalized.TextOverlays.Count);
        Assert.All(
            normalized.TextOverlays,
            overlay => Assert.Equal(VideoEditDocument.MaximumTextLength, overlay.Text.Length));
        Assert.Equal(
            normalized.TextOverlays.Count,
            normalized.TextOverlays.Select(overlay => overlay.Id).Distinct().Count());
        Assert.All(normalized.TextOverlays, overlay => Assert.NotEqual(Guid.Empty, overlay.Id));
    }
}
