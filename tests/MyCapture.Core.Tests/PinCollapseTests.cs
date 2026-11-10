using System.Text.Json;
using System.Text.Json.Nodes;
using MyCapture.Core.Pin;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class PinCollapseTests
{
    [Theory]
    [InlineData(800, 600, 96, 72)]
    [InlineData(600, 800, 72, 96)]
    [InlineData(400, 100, 128, 32)]
    [InlineData(100, 1000, 32, 160)]
    [InlineData(1000, 100, 160, 32)]
    [InlineData(1, 1, 96, 96)]
    public void CollapseKeepsZoomAndRestoresOpacityAndSize(double width, double height, double thumbWidth, double thumbHeight)
    {
        var state = new PinViewState(width, height, 1.75, .7, .1);
        state.SetCollapsed(true);
        Assert.True(state.IsCollapsed);
        Assert.Equal(thumbWidth, state.WidthDip);
        Assert.Equal(thumbHeight, state.HeightDip);
        Assert.Equal(1.75, state.ApplyZoomStep(3));
        Assert.Equal(1.75, state.SetZoom(3));
        Assert.Equal(1.75, state.ResetZoom());
        state.SetOpacity(.3);
        state.SetCollapsed(true); // Idempotent: does not overwrite the saved opacity.
        state.IsHidden = true;
        state.IsHidden = false;
        Assert.True(state.IsCollapsed);
        Assert.Equal(.7, state.ExpandedOpacity);
        state.SetCollapsed(false);
        Assert.Equal(width * 1.75, state.WidthDip);
        Assert.Equal(height * 1.75, state.HeightDip);
        Assert.Equal(.7, state.Opacity);
    }

    [Fact]
    public void OptionalSessionFieldsAreIgnoredByLegacyShapeAndAbsentFieldsExpand()
    {
        var pin = new PinSessionPin(Guid.NewGuid(), PinSessionContentKind.Image, null, "", false,
            800, 600, 1.75, .7, null, IsCollapsed: true, CollapsedOpacity: .3);
        string json = JsonSerializer.Serialize(pin);
        var legacy = JsonSerializer.Deserialize<LegacyPin>(json)!;
        Assert.Equal(pin.Id, legacy.Id);
        Assert.Equal(1.75, legacy.Zoom);
        Assert.Equal(.7, legacy.Opacity);
        Assert.Equal(800, legacy.ImageWidthDip);
        Assert.Equal(pin, JsonSerializer.Deserialize<PinSessionPin>(json));
        var oldJson = JsonNode.Parse(json)!.AsObject();
        oldJson.Remove(nameof(PinSessionPin.IsCollapsed));
        oldJson.Remove(nameof(PinSessionPin.CollapsedOpacity));
        var fromOld = JsonSerializer.Deserialize<PinSessionPin>(oldJson.ToJsonString())!;
        Assert.False(fromOld.IsCollapsed);
        Assert.Null(fromOld.CollapsedOpacity);
        Assert.Equal(pin.Zoom, fromOld.Zoom);
    }

    private sealed record LegacyPin(Guid Id, double ImageWidthDip, double ImageHeightDip, double Zoom, double Opacity);
}
