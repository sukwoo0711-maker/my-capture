using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MyCapture.Core.Export;
using MyCapture.Core.Localization;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class ContactSheetTests
{
    [Theory]
    [InlineData(ContactSheetGrid.OneByOne, 1, 1)]
    [InlineData(ContactSheetGrid.OneByTwo, 1, 2)]
    [InlineData(ContactSheetGrid.TwoByTwo, 2, 2)]
    [InlineData(ContactSheetGrid.TwoByThree, 2, 3)]
    [InlineData(ContactSheetGrid.ThreeByFour, 3, 4)]
    public void CellsFillEveryPageWithoutOverlap(ContactSheetGrid grid, int columns, int rows)
    {
        foreach (ContactSheetPageSize size in Enum.GetValues<ContactSheetPageSize>())
        {
            var layout = new ContactSheetLayout(new(size, grid));
            Assert.Equal(columns * rows, layout.CellsPerPage);
            Assert.Equal(2, layout.PageCount(layout.CellsPerPage + 1));
            for (int index = 0; index < layout.CellsPerPage; index++)
            {
                ContactSheetRect cell = layout.Cell(index);
                Assert.True(cell.Width > 0 && cell.Height > 0);
                Assert.InRange(cell.X + cell.Width, 0, layout.PageWidth - ContactSheetLayout.Margin + .001);
                Assert.InRange(cell.Y + cell.Height, 0, layout.PageHeight - ContactSheetLayout.Margin + .001);
                if (index % columns > 0) Assert.Equal(layout.Cell(index - 1).X + cell.Width + ContactSheetLayout.Gap, cell.X, 6);
                if (index >= columns) Assert.Equal(layout.Cell(index - columns).Y + cell.Height + ContactSheetLayout.Gap, cell.Y, 6);
            }
        }
    }

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(60, 20000)]
    [InlineData(20000, 60)]
    [InlineData(100, 100)]
    public void FitPreservesAspectAndCenters(double width, double height)
    {
        var bounds = new ContactSheetRect(10, 20, 300, 200);
        ContactSheetRect fit = ContactSheetLayout.Fit(width, height, bounds);
        Assert.Equal(width / height, fit.Width / fit.Height, 8);
        Assert.Equal(160, fit.X + fit.Width / 2, 8);
        Assert.Equal(120, fit.Y + fit.Height / 2, 8);
        Assert.True(fit.Width <= bounds.Width + .001 && fit.Height <= bounds.Height + .001);
    }

    [Fact]
    public void LimitsAreEnforcedBeforeRenderingOrWriting()
    {
        var layout = new ContactSheetLayout(new(Grid: ContactSheetGrid.OneByOne));
        Assert.Equal(100, layout.PageCount(100));
        Assert.Throws<ArgumentOutOfRangeException>(() => layout.PageCount(101));
        var dense = new ContactSheetLayout(new(Grid: ContactSheetGrid.ThreeByFour));
        Assert.Equal(42, dense.PageCount(500));
        Assert.Throws<ArgumentOutOfRangeException>(() => dense.PageCount(501));
        Assert.Throws<ArgumentOutOfRangeException>(() => dense.PageCount(0));
        using var output = new MemoryStream();
        Assert.Throws<ArgumentOutOfRangeException>(() => ContactSheetPdfWriter.Write(output, new(), 501,
            _ => throw new Xunit.Sdk.XunitException("Renderer must not run")));
        Assert.Equal(0, output.Length);
        Assert.Throws<ArgumentOutOfRangeException>(() => ContactSheetLayout.Fit(0, 1, new(0, 0, 1, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => ContactSheetLayout.Fit(double.NaN, 1, new(0, 0, 1, 1)));
    }

    [Theory]
    [InlineData(ContactSheetCaption.Title, 1)]
    [InlineData(ContactSheetCaption.CaptureTime, 1)]
    [InlineData(ContactSheetCaption.TitleAndCaptureTime, 2)]
    [InlineData(ContactSheetCaption.None, 0)]
    public void CaptionsContainOnlyRequestedMetadata(ContactSheetCaption mode, int count)
    {
        var time = new DateTimeOffset(2026, 10, 14, 9, 30, 0, TimeSpan.FromHours(9));
        var culture = CultureInfo.GetCultureInfo("ko-KR");
        var options = new ContactSheetOptions(Caption: mode);
        var lines = ContactSheetCaptions.Lines(options, "한글 제목", time, "비공개 앱", "비공개 창", culture);
        Assert.Equal(count, lines.Count);
        if (mode is ContactSheetCaption.Title or ContactSheetCaption.TitleAndCaptureTime) Assert.Contains("한글 제목", lines);
        if (mode is ContactSheetCaption.CaptureTime or ContactSheetCaption.TitleAndCaptureTime)
            Assert.Contains(time.ToLocalTime().ToString("g", culture), lines);
        Assert.DoesNotContain("비공개 앱", lines);
        Assert.DoesNotContain("비공개 창", lines);
        var withSource = ContactSheetCaptions.Lines(options with { ShowSource = true }, "한글 제목", time, "앱", "창\r\n제목", culture);
        Assert.Equal(count + 2, withSource.Count);
        Assert.Equal("앱", withSource[^2]);
        Assert.Equal("창  제목", withSource[^1]);
        Assert.Empty(ContactSheetCaptions.Lines(new(Caption: ContactSheetCaption.Title), "", time, "앱", "창", culture));
    }

    [Theory]
    [InlineData(1, ContactSheetGrid.OneByOne, 1)]
    [InlineData(13, ContactSheetGrid.TwoByThree, 3)]
    [InlineData(500, ContactSheetGrid.ThreeByFour, 42)]
    [InlineData(100, ContactSheetGrid.OneByOne, 100)]
    public void PdfObjectsStreamsCrossReferencesAndTrailerAreConsistent(int count, ContactSheetGrid grid, int pages)
    {
        using var output = new MemoryStream();
        int rendered = 0;
        // Structural fixture only; the App tests supply a real WPF-encoded JPEG.
        byte[] jpeg = [0xff, 0xd8, 0x80, 0x00, 0xff, 0xd9];
        ContactSheetPdfWriter.Write(output, new(Grid: grid), count, index =>
        {
            Assert.Equal(rendered++, index);
            Assert.True(output.Length > 0);
            return new(jpeg, 1, 1);
        });
        byte[] bytes = output.ToArray();
        string pdf = Encoding.Latin1.GetString(bytes);
        Assert.StartsWith("%PDF-1.4\n", pdf);
        Assert.EndsWith("%%EOF\n", pdf);
        Assert.Equal(pages, Regex.Matches(pdf, @"/Type /Page\b").Count);
        Assert.Contains($"/Count {pages} /Kids [", pdf);
        Assert.Equal(count, Regex.Matches(pdf, "/Filter /DCTDecode").Count);
        int xref = int.Parse(Regex.Match(pdf, @"startxref\n(\d+)\n").Groups[1].Value, CultureInfo.InvariantCulture);
        Assert.Equal("xref", pdf.Substring(xref, 4));
        string[] entries = pdf[xref..].Split('\n');
        int objects = 3 + 2 * pages + count;
        Assert.Equal($"0 {objects}", entries[1]);
        Assert.Equal("0000000000 65535 f ", entries[2]);
        Assert.Contains($"/Size {objects} /Root 1 0 R", pdf);
        for (int id = 1; id < objects; id++)
        {
            int offset = int.Parse(entries[id + 2][..10], CultureInfo.InvariantCulture);
            Assert.StartsWith($"{id} 0 obj\n", pdf[offset..]);
        }
        foreach (Match stream in Regex.Matches(pdf, @"/Length (\d+) >>\nstream\n"))
        {
            int length = int.Parse(stream.Groups[1].Value, CultureInfo.InvariantCulture);
            int end = stream.Index + stream.Length + length;
            Assert.StartsWith("endstream", pdf[end..].TrimStart('\n'));
        }
        Assert.Equal(count, rendered);
    }

    [Fact]
    public void CancellationStopsBeforeNextCellAndDoesNotFinalize()
    {
        using var cancellation = new CancellationTokenSource();
        using var output = new MemoryStream();
        int rendered = 0;
        var progress = new InlineProgress(done => { Assert.Equal(1, done); cancellation.Cancel(); });
        Assert.Throws<OperationCanceledException>(() => ContactSheetPdfWriter.Write(output, new(), 10, _ =>
        {
            rendered++;
            return new([0xff, 0xd8, 0xff, 0xd9], 1, 1);
        }, progress, cancellation.Token));
        Assert.Equal(1, rendered);
        Assert.DoesNotContain("%%EOF", Encoding.ASCII.GetString(output.ToArray()));
    }

    [Fact]
    public void OversizedCellAndNonEmptyOutputAreRejected()
    {
        using var output = new MemoryStream();
        Assert.Throws<ArgumentException>(() => ContactSheetPdfWriter.Write(output, new(), 1,
            _ => new([0xff, 0xd8, 0xff, 0xd9], 10000, 1)));
        Assert.Throws<ArgumentException>(() => ContactSheetPdfWriter.Write(output, new(), 1,
            _ => throw new Xunit.Sdk.XunitException("Must reject non-empty output")));
    }

    [Theory]
    [InlineData("ko")]
    [InlineData("en")]
    public void DialogResourcesExistInBothLanguages(string language)
    {
        using var scope = UiText.UseLanguage(language);
        Assert.NotEmpty(UiText.Get("ContactSheet.Action"));
        Assert.DoesNotContain("plural:", UiText.Format("ContactSheet.PageCount", 2, 1));
        Assert.DoesNotContain("plural:", UiText.Format("ContactSheet.SkippedVideos", 1));
    }

    private sealed class InlineProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }

    [Fact]
    public void WriterSupportsNonSeekableStreamsAndLeavesThemOpen()
    {
        using var output = new NonSeekableStream();
        ContactSheetPdfWriter.Write(output, new(), 1, _ => new([0xff, 0xd8, 0xff, 0xd9], 1, 1));
        Assert.EndsWith("%%EOF\n", Encoding.ASCII.GetString(output.ToArray()));
        Assert.True(output.CanWrite);
    }

    private sealed class NonSeekableStream : MemoryStream
    {
        public override bool CanSeek => false;
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    }
}
