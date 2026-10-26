using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using MyCapture.Core.Pin;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class PinMarkdownSessionMetadataTests
{
    [Fact]
    public void MarkdownMetadata_RoundTripsWithoutLosingExactSource()
    {
        var pin = new PinSessionPin(Guid.NewGuid(), PinSessionContentKind.Text,
            "# Release\r\n\r\n**Ready**\r\n", "", true, 640, 220, 1.25, 1, null,
            TextStyle: PinTextStyle.Markdown);
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter<PinTextStyle>(), new JsonStringEnumConverter<PinSessionContentKind>() },
        };
        string json = JsonSerializer.Serialize(pin, options);
        PinSessionPin restored = Assert.IsType<PinSessionPin>(JsonSerializer.Deserialize<PinSessionPin>(json, options));
        Assert.Equal(pin, restored);
        Assert.Contains("\"Markdown\"", json);
        Assert.True(HasValidRenderingMetadata(restored));
        Assert.False(HasValidRenderingMetadata(restored with { Kind = PinSessionContentKind.Table }));
        Assert.False(HasValidRenderingMetadata(restored with { OriginalText = null }));
        Assert.False(HasValidRenderingMetadata(restored with { UsesDeviceIndependentPixels = false }));
        Assert.False(HasValidRenderingMetadata(restored with { CodeDocument = new PinCodeDocument("Ready", [], "#ffffff", "#000000") }));
    }

    // Exercise the store's exact validation gate without creating a workspace whose cleanup
    // would permanently remove files. Persistence itself is covered by the existing store tests.
    private static bool HasValidRenderingMetadata(PinSessionPin pin) => (bool)typeof(PinSessionStore)
        .GetMethod("HasValidRenderingMetadata", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [pin])!;
}
