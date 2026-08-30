using MyCapture.Core.Pin;
using Xunit;

namespace MyCapture.Core.Tests;

/// <summary>
/// The per-pin presentation state machine — zoom, opacity, click-through — with no WPF.
/// </summary>
public sealed class PinViewStateTests
{
    private static PinViewState NewState(double zoom = 1.0, double opacity = 1.0, double step = 0.1) =>
        new(imageWidthDip: 400, imageHeightDip: 300, initialZoom: zoom, initialOpacity: opacity, zoomStep: step);

    [Fact]
    public void Construction_ClampsInitialZoomAndOpacity()
    {
        var state = NewState(zoom: 50, opacity: -1);
        Assert.Equal(PinGeometry.MaxZoom, state.Zoom, 6);
        Assert.Equal(PinGeometry.MinOpacity, state.Opacity, 6);
    }

    [Fact]
    public void Dimensions_TrackZoom()
    {
        var state = NewState(zoom: 2.0);
        Assert.Equal(800, state.WidthDip, 3);
        Assert.Equal(600, state.HeightDip, 3);
    }

    [Fact]
    public void ApplyZoomStep_CompoundsAndClamps()
    {
        var state = NewState();
        Assert.Equal(1.1, state.ApplyZoomStep(1), 6);
        Assert.Equal(1.1 * 1.1, state.ApplyZoomStep(1), 6);
    }

    [Fact]
    public void ResetZoom_ReturnsToOne()
    {
        var state = NewState(zoom: 3.0);
        Assert.Equal(1.0, state.ResetZoom(), 6);
        Assert.Equal(400, state.WidthDip, 3);
    }

    [Fact]
    public void AdjustOpacity_ClampsWithinBounds()
    {
        var state = NewState(opacity: 1.0);
        Assert.Equal(0.9, state.AdjustOpacity(-0.1), 6);

        // Drive it below the floor: it stops at MinOpacity.
        for (int i = 0; i < 20; i++)
        {
            state.AdjustOpacity(-0.1);
        }

        Assert.Equal(PinGeometry.MinOpacity, state.Opacity, 6);

        // And back up to the ceiling.
        for (int i = 0; i < 20; i++)
        {
            state.AdjustOpacity(0.1);
        }

        Assert.Equal(PinGeometry.MaxOpacity, state.Opacity, 6);
    }

    [Fact]
    public void ToggleClickThrough_Flips()
    {
        var state = NewState();
        Assert.False(state.IsClickThrough);
        Assert.True(state.ToggleClickThrough());
        Assert.False(state.ToggleClickThrough());
    }

    [Fact]
    public void SetClickThrough_SetsExplicitValue()
    {
        var state = NewState();
        state.SetClickThrough(true);
        Assert.True(state.IsClickThrough);
        state.SetClickThrough(false);
        Assert.False(state.IsClickThrough);
    }

    [Fact]
    public void ZeroZoomStep_DefaultsToTenPercent()
    {
        var state = NewState(step: 0);
        Assert.Equal(1.1, state.ApplyZoomStep(1), 6);
    }
}

/// <summary>
/// The clipboard-read decision surface, independent of any real clipboard.
/// </summary>
public sealed class ClipboardImageOutcomeTests
{
    [Fact]
    public void Success_HasImageWhenPositiveDimensions()
    {
        ClipboardImageOutcome outcome = ClipboardImageOutcome.Success(320, 240);
        Assert.Equal(ClipboardImageStatus.Success, outcome.Status);
        Assert.True(outcome.HasImage);
        Assert.Equal(320, outcome.PixelWidth);
        Assert.Equal(240, outcome.PixelHeight);
    }

    [Fact]
    public void Success_ZeroDimensions_HasNoImage()
    {
        ClipboardImageOutcome outcome = ClipboardImageOutcome.Success(0, 0);
        Assert.False(outcome.HasImage);
    }

    [Fact]
    public void NoImage_IsNotSuccess()
    {
        ClipboardImageOutcome outcome = ClipboardImageOutcome.NoImage();
        Assert.Equal(ClipboardImageStatus.NoImage, outcome.Status);
        Assert.False(outcome.HasImage);
    }

    [Fact]
    public void Busy_IsNotSuccess()
    {
        ClipboardImageOutcome outcome = ClipboardImageOutcome.Busy();
        Assert.Equal(ClipboardImageStatus.Busy, outcome.Status);
        Assert.False(outcome.HasImage);
    }
}
