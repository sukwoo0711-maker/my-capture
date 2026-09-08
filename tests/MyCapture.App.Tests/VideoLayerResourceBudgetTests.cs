using System.Buffers.Binary;
using MyCapture.App.Recording;
using MyCapture.Core.Recording;
using MyCapture.Platform.Recording;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class VideoLayerResourceBudgetTests
{
    [Fact]
    public void AggregateDecodedBudget_RejectsBeforeAnyBitmapDecodeEvenWhenAllLayersAreInactive()
    {
        FrameEditLayer[] layers = Enumerable.Range(0, 5).Select(_ => HeaderLayer()).ToArray();
        VideoLayerResourceBudget.Validate(layers[..4]); // Four 64 MiB assets exactly fit.
        Assert.Throws<VideoLayerLimitException>(() => VideoLayerResourceBudget.Validate(layers));
        Assert.Throws<VideoLayerLimitException>(() => FrameEditLayerRenderer.Decode(layers));
        StaTestHost.Run(() =>
        {
            var preview = new TimedTextPreviewView();
            Assert.Throws<VideoLayerLimitException>(() => preview.SetFrameLayers(layers));
            Assert.Equal(0, preview.DecodeAttempts);
        });
    }

    [Fact]
    public void AggregateEncodedBudget_CountsRepeatedPayloadReferencesAndRejectsExtraLayers()
    {
        string payload = new('A', (int)(VideoLayerResourceBudget.MaximumEncodedCharacters / 4));
        FrameEditLayer[] layers = Enumerable.Range(0, 5).Select(_ => new FrameEditLayer { OverlayPngBase64 = payload }).ToArray();
        VideoLayerResourceBudget.Validate(layers[..4]);
        Assert.Throws<VideoLayerLimitException>(() => VideoLayerResourceBudget.Validate(layers));
        Assert.Throws<VideoLayerLimitException>(() => VideoLayerResourceBudget.Validate(Enumerable.Range(0, 101).Select(_ => new FrameEditLayer()).ToArray()));
        Assert.Throws<VideoLayerLimitException>(() => VideoLayerResourceBudget.Validate([new FrameEditLayer { OverlayPngBase64 = new string(' ', 4097) }]));
    }

    [Fact]
    public void BothExportEntrypoints_RejectOversizedDocumentBeforeSourceOrOutputAccess()
    {
        VideoEditDocument document = VideoEditDocument.CreateFor(320, 240, 1000);
        document.FrameEditLayers = Enumerable.Range(0, 5).Select(_ => HeaderLayer()).ToList();
        var recording = new RecordingResult("does-not-exist.mp4", 1000, 10, 10, 320, 240);
        Assert.Throws<VideoLayerLimitException>(() => AnimatedGifExporter.Export(recording, document, "never-written.gif"));
        Assert.Throws<VideoLayerLimitException>(() => TrimReencoder.Reencode(recording.OutputPath, "never-written.mp4", 0, 1000,
            recording, _ => throw new InvalidOperationException("Encoder must not be allocated"), NullLogger.Instance,
            document.TextOverlays, document.FrameEditLayers));
    }

    internal static FrameEditLayer HeaderLayer()
    {
        byte[] header = new byte[33];
        new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(header, 0);
        "IHDR"u8.CopyTo(header.AsSpan(12, 4));
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16, 4), 4096);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(20, 4), 4096);
        header[24] = 8;
        header[25] = 6;
        return new FrameEditLayer { OverlayPngBase64 = Convert.ToBase64String(header), StartMs = 100, EndMs = 800 };
    }
}
