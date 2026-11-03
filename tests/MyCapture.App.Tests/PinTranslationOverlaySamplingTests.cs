using System.Windows.Media;
using MyCapture.App.Pinning;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class PinTranslationOverlaySamplingTests
{
    [Fact]
    public void PlateThresholdPreservesRoundTwoFloatingPointAccumulationOrder()
    {
        const int width = 6, height = 2;
        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                Color color = x >= 3 ? Color.FromRgb(128, 128, 128)
                    : y == 0 ? Color.FromRgb(128, 127, 70) : Color.FromRgb(129, 137, 149);
                int offset = (y * width + x) * 4;
                pixels[offset] = color.B;
                pixels[offset + 1] = color.G;
                pixels[offset + 2] = color.R;
                pixels[offset + 3] = 255;
            }
        // The outer ring is empty, so the old sampler visits the top/bottom edge
        // alternately. Expanding the squared terms into histogram sums rounds down
        // to 324 and changes the strict variance > 18*18 plate decision.
        PinOverlayBackground background = PinTranslationOverlay.EstimateBackground(pixels, width, height, width * 4, new(0, 0, width, height));
        Assert.Equal(Color.FromRgb(128, 128, 128), background.Color);
        Assert.Equal(324.00000000000006, background.Variance);
        Assert.True(background.NeedsPlate);
    }
}
