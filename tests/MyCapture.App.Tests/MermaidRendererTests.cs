using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.App.Pinning;
using Xunit;

namespace MyCapture.App.Tests;

// Pure policy, pixel and construction tests. No browser, runtime lookup, user profile,
// clipboard, registry, network or filesystem creation is performed by these tests.
public sealed class MermaidRendererTests
{
    [Theory]
    [InlineData("https://mycapture-mermaid.invalid/index.html", true)]
    [InlineData("https://mycapture-mermaid.invalid/mermaid.min.js", true)]
    [InlineData("https://mycapture-mermaid.invalid/renderer.js", true)]
    [InlineData("https://mycapture-mermaid.invalid/renderer.css", true)]
    [InlineData("https://example.com/mermaid.min.js", false)]
    [InlineData("https://mycapture-mermaid.invalid.example.com/index.html", false)]
    [InlineData("https://mycapture-mermaid.invalid/secret.txt", false)]
    [InlineData("https://mycapture-mermaid.invalid/index.html?content=secret", false)]
    [InlineData("https://mycapture-mermaid.invalid/index.html#secret", false)]
    [InlineData("https://user:password@mycapture-mermaid.invalid/index.html", false)]
    [InlineData("https://mycapture-mermaid.invalid:9443/index.html", false)]
    [InlineData("http://mycapture-mermaid.invalid/index.html", false)]
    [InlineData("file:///C:/SyntheticProfile/settings.json", false)]
    [InlineData("data:text/html,<script>alert(1)</script>", false)]
    [InlineData("javascript:alert(1)", false)]
    public void OnlyFourBundledHttpsResourcesAreAllowed(string url, bool expected) =>
        Assert.Equal(expected, MermaidRenderer.IsAllowedResource(url));

    [Fact]
    public void NavigationAllowsOnlyTheFixedEntryDocument()
    {
        Assert.True(MermaidRenderer.IsAllowedNavigation(MermaidRenderer.DocumentUrl));
        Assert.False(MermaidRenderer.IsAllowedNavigation(MermaidRenderer.DocumentUrl + "#fragment"));
        Assert.False(MermaidRenderer.IsAllowedNavigation("https://example.com/"));
        Assert.False(MermaidRenderer.IsAllowedNavigation("https://mycapture-mermaid.invalid/renderer.js"));
        Assert.False(MermaidRenderer.IsAllowedNavigation("about:blank"));
    }

    [Fact]
    public void DiagramSourceIsAJsonArgumentAndNeverExecutableScript()
    {
        const string source = "flowchart LR\r\n A[\"한글 '</script>\"] --> B\n`); fetch('https://example.com'); //";
        string script = MermaidRenderer.BuildRenderScript(source, "request-123");
        const string prefix = "window.renderDiagram(";
        Assert.StartsWith(prefix, script);
        Assert.EndsWith(");", script);
        using JsonDocument args = JsonDocument.Parse("[" + script[prefix.Length..^2] + "]");
        Assert.Equal(source, args.RootElement[0].GetString());
        Assert.Equal("request-123", args.RootElement[1].GetString());
        Assert.DoesNotContain("</script>", script);
    }

    [Fact]
    public void ConstructionAndDisposalWithoutADiagramNeverCreateABrowser() => StaTestHost.Run(() =>
    {
        using var renderer = new MermaidRenderer(@"C:\SyntheticMermaidTest\unused-profile", Dispatcher.CurrentDispatcher);
        Assert.False(renderer.IsBrowserCreated);
        Assert.Equal(0u, renderer.BrowserProcessId);
        Assert.Equal(TimeSpan.FromSeconds(60), MermaidRenderer.IdleTimeout);
    });

    [Fact]
    public void EmptyAndOversizedSourceReturnFallbackWithoutCreatingABrowser() => StaTestHost.Run(() =>
    {
        using var renderer = new MermaidRenderer(@"C:\SyntheticMermaidTest\unused-profile", Dispatcher.CurrentDispatcher);
        foreach (string source in new[] { string.Empty, new string('a', 50001) })
        {
            MermaidRenderResult result = renderer.RenderAsync(source, 1, CancellationToken.None).GetAwaiter().GetResult();
            Assert.Null(result.Image);
            Assert.NotEmpty(result.Error!);
            Assert.False(renderer.IsBrowserCreated);
        }
    });

    [Theory]
    [InlineData(1, 96, 80)]
    [InlineData(1.5, 144, 53.3333333333)]
    [InlineData(2, 192, 40)]
    public void DecodedPixelsAreFrozenAndCarryTheRequestedDpi(double scale, double dpi, double width) => StaTestHost.Run(() =>
    {
        var source = BitmapSource.Create(80, 40, 96, 96, PixelFormats.Bgra32, null, new byte[80 * 40 * 4], 80 * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        BitmapSource image = MermaidRenderer.DecodeBitmap(stream.ToArray(), scale);
        Assert.True(image.IsFrozen);
        Assert.Equal(80, image.PixelWidth);
        Assert.Equal(40, image.PixelHeight);
        Assert.Equal(dpi, image.DpiX, 3);
        // WPF reports natural width through a native single-precision value.
        Assert.InRange(Math.Abs(width - image.Width), 0, 0.00001);
    });

    [Theory]
    [InlineData(double.NaN, 1)]
    [InlineData(double.PositiveInfinity, 1)]
    [InlineData(-1, 1)]
    [InlineData(0, 1)]
    [InlineData(0.25, 0.5)]
    [InlineData(1.5, 1.5)]
    [InlineData(20, 4)]
    public void RasterScaleIsFiniteAndBounded(double input, double expected) =>
        Assert.Equal(expected, MermaidRenderer.NormalizeScale(input));
}
