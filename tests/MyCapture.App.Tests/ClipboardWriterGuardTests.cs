using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Editing;
using MyCapture.App.GitHub;
using MyCapture.App.Pinning;
using MyCapture.App.Threading;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class ClipboardWriterGuardTests
{
    private readonly bool _installedBeforeTestConstruction = ClipboardWriter.IsTestOverrideInstalled;
    private readonly bool _availabilityInstalledBeforeTestConstruction = ClipboardAvailability.IsTestOverrideInstalled;

    // Include method groups as well as invocations. Aliasing/static imports would hide the
    // Clipboard qualifier, so require those writes to use the gateway too.
    private static readonly Regex DirectWrite = new(
        @"\bClipboard\s*\.\s*(?:Set\w*|Clear)\b|\b(?:EmptyClipboard|SetClipboardData|OleSetClipboard)\b|\busing\s+(?:static\s+|@?\w+\s*=\s*)(?:global::)?System\s*\.\s*Windows\s*\.\s*(?:Forms\s*\.\s*)?Clipboard\s*;",
        RegexOptions.CultureInvariant);

    private static readonly Regex DirectAvailabilityQuery = new(
        @"\b(?:IsClipboardFormatAvailable|CountClipboardFormats|EnumClipboardFormats|GetUpdatedClipboardFormats|GetPriorityClipboardFormat)\b|\bClipboard\s*\.\s*Contains\w*\b|\busing\s+(?:static\s+|@?\w+\s*=\s*)(?:global::)?System\s*\.\s*Windows\s*\.\s*(?:Forms\s*\.\s*)?Clipboard\s*;",
        RegexOptions.CultureInvariant);

    [Fact]
    public void ModuleInitializer_InstallsOverrideBeforeTestsAreConstructed()
    {
        Assert.True(_installedBeforeTestConstruction);
        Assert.True(ClipboardWriter.IsTestOverrideInstalled);
        Assert.True(_availabilityInstalledBeforeTestConstruction);
        Assert.True(ClipboardAvailability.IsTestOverrideInstalled);
    }

    [Fact]
    public void InstalledOverride_CannotBeReplacedOrReset()
    {
        Assert.True(_installedBeforeTestConstruction);
        Assert.Throws<InvalidOperationException>(() => ClipboardWriter.InstallTestOverride(
            (_, _) => throw new InvalidOperationException("Replacement must not run."),
            _ => throw new InvalidOperationException("Replacement must not run."),
            () => throw new InvalidOperationException("Replacement must not run."),
            (_, _) => throw new InvalidOperationException("Replacement must not run.")));

        long before = RecordingClipboard.WriteCount;
        const string text = "Recorded only: 한글\r\nclipboard guard";
        ClipboardWriter.SetText(text);
        Assert.Equal(before + 1, RecordingClipboard.WriteCount);
        RecordingClipboard.ClipboardWrite write = Assert.IsType<RecordingClipboard.ClipboardWrite>(RecordingClipboard.LastWrite);
        Assert.Null(write.Copy);
        Assert.Equal(text, write.Formats[DataFormats.UnicodeText]);
    }

    [Fact]
    public void InstalledAvailabilityOverride_CannotBeReplacedOrReset()
    {
        Assert.True(_availabilityInstalledBeforeTestConstruction);
        Assert.Throws<InvalidOperationException>(() => ClipboardAvailability.InstallTestOverride(
            () => throw new InvalidOperationException("Replacement must not run.")));
        Assert.Throws<ArgumentNullException>(() => ClipboardAvailability.InstallTestOverride(null!));
        Assert.True(ClipboardAvailability.IsTestOverrideInstalled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AvailabilityQuery_UsesRecordedFormats(bool hasText)
    {
        Assert.True(_availabilityInstalledBeforeTestConstruction);
        await StaThreadTask.RunAsync(() =>
        {
            var data = new DataObject();
            data.SetData("MyCapture.Test.Unpinnable", "fixture");
            if (hasText) data.SetText("recorded text", TextDataFormat.UnicodeText);
            ClipboardWriter.SetDataObject(data, copy: true);
            Assert.Equal(hasText, ClipboardAvailability.HasPinnableContent());
            return true;
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DataObjectWrite_RecordsCopyFlagAndMaterializesDisposedStream(bool copy)
    {
        Assert.True(_installedBeforeTestConstruction);
        byte[] expected = [1, 2, 3, 4];
        await StaThreadTask.RunAsync(() =>
        {
            using var stream = new MemoryStream(expected);
            var data = new DataObject();
            data.SetData("PNG", stream);
            data.SetText("retained text", TextDataFormat.UnicodeText);
            ClipboardWriter.SetDataObject(data, copy);
            return true;
        });

        RecordingClipboard.ClipboardWrite write = Assert.IsType<RecordingClipboard.ClipboardWrite>(RecordingClipboard.LastWrite);
        Assert.Equal(copy, write.Copy);
        Assert.Equal(expected, Assert.IsType<byte[]>(write.Formats["PNG"]));
        Assert.Equal("retained text", write.Formats[DataFormats.UnicodeText]);
    }

    [Fact]
    public async Task ImageAndTextServices_DefaultCopyPathsReachRecordingFake()
    {
        Assert.True(_installedBeforeTestConstruction);
        var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 4, 8, 12, 255 }, 4);
        bitmap.Freeze();

        Assert.True(await ClipboardImageService.CopyImageWithTextAsync(bitmap, sourceUrl: null, "source text"));
        RecordingClipboard.ClipboardWrite image = Assert.IsType<RecordingClipboard.ClipboardWrite>(RecordingClipboard.LastWrite);
        Assert.True(image.Copy);
        Assert.Equal("source text", image.Formats[DataFormats.UnicodeText]);
        byte[] png = Assert.IsType<byte[]>(image.Formats["PNG"]);
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, png.Take(8).ToArray());
        Assert.True(Assert.IsAssignableFrom<BitmapSource>(image.Formats[DataFormats.Bitmap]).IsFrozen);

        Assert.True(await ClipboardImageService.CopyTextAsync("exact\ttext\r\n한글"));
        RecordingClipboard.ClipboardWrite text = Assert.IsType<RecordingClipboard.ClipboardWrite>(RecordingClipboard.LastWrite);
        Assert.True(text.Copy);
        Assert.Equal("exact\ttext\r\n한글", text.Formats[DataFormats.UnicodeText]);
    }

    [Fact]
    public async Task ConditionalUrlWrite_UsesFakeVersionAndRejectsStaleCopy()
    {
        Assert.True(_installedBeforeTestConstruction);
        uint version = GitHubClipboardCommit.GetVersion();
        Assert.True(await GitHubClipboardCommit.CopyUrlAsync("https://example.test/attachment.png", version));
        RecordingClipboard.ClipboardWrite write = Assert.IsType<RecordingClipboard.ClipboardWrite>(RecordingClipboard.LastWrite);
        Assert.Equal("https://example.test/attachment.png", write.Formats[DataFormats.UnicodeText]);
        Assert.NotEqual(version, GitHubClipboardCommit.GetVersion());

        Assert.False(await GitHubClipboardCommit.CopyUrlAsync("stale", version));
        Assert.Same(write, RecordingClipboard.LastWrite);
    }

    [Fact]
    public void ProductSource_OnlyGatewayMayWriteToSystemClipboard()
    {
        string root = RepositoryRoot();
        string gateway = Path.Combine(root, "src", "MyCapture.App", "ClipboardWriter.cs");
        Assert.True(File.Exists(gateway));
        AssertNoMatches(Path.Combine(root, "src"), DirectWrite, "Route clipboard writes through ClipboardWriter:", gateway);
    }

    [Fact]
    public void TestAndTutorialSources_DoNotBypassGateway()
    {
        string root = RepositoryRoot();
        // This file contains intentional scanner fixtures, never executable clipboard calls.
        AssertNoMatches(Path.Combine(root, "tests"), DirectWrite, "Route clipboard writes through ClipboardWriter:",
            Path.Combine(root, "tests", "MyCapture.App.Tests", "ClipboardWriterGuardTests.cs"));
        AssertNoMatches(Path.Combine(root, "build", "tools", "TutorialVideoGenerator"), DirectWrite, "Route clipboard writes through ClipboardWriter:");
    }

    [Fact]
    public void TestAndTutorialSources_DoNotQueryNativeClipboardAvailability()
    {
        string root = RepositoryRoot();
        const string guidance = "Inject clipboard availability or use the module-isolated ClipboardAvailability gateway:";
        AssertNoMatches(Path.Combine(root, "tests"), DirectAvailabilityQuery, guidance,
            Path.Combine(root, "tests", "MyCapture.App.Tests", "ClipboardWriterGuardTests.cs"));
        AssertNoMatches(Path.Combine(root, "build", "tools", "TutorialVideoGenerator"), DirectAvailabilityQuery, guidance);
    }

    [Theory]
    [InlineData("Clipboard.SetDataObject(data, true);")]
    [InlineData("System.Windows.Clipboard.SetText(text);")]
    [InlineData("global::System.Windows.Clipboard.SetImage(image);")]
    [InlineData("Clipboard \n . Clear();")]
    [InlineData("Action<string> copy = Clipboard.SetText;")]
    [InlineData("using Clip = System.Windows.Clipboard;")]
    [InlineData("using static global::System.Windows.Clipboard;")]
    [InlineData("SetClipboardData(13, memory);")]
    [InlineData("EmptyClipboard();")]
    [InlineData("[DllImport(\"ole32.dll\", EntryPoint = \"OleSetClipboard\")]")]
    public void SourceGuard_DetectsDirectWritesAndImports(string source) => Assert.Matches(DirectWrite, source);

    [Theory]
    [InlineData("ClipboardWriter.SetText(text);")]
    [InlineData("data.SetText(text);")]
    [InlineData("Clipboard.GetText();")]
    public void SourceGuard_AllowsGatewayDataObjectsAndReads(string source) => Assert.DoesNotMatch(DirectWrite, source);

    [Theory]
    [InlineData("IsClipboardFormatAvailable(13);")]
    [InlineData("Func<uint, bool> query = IsClipboardFormatAvailable;")]
    [InlineData("[DllImport(\"user32.dll\", EntryPoint = \"IsClipboardFormatAvailable\")]")]
    [InlineData("[LibraryImport(\"user32.dll\", EntryPoint = \"IsClipboardFormatAvailable\")]")]
    [InlineData("CountClipboardFormats();")]
    [InlineData("EnumClipboardFormats(0);")]
    [InlineData("GetUpdatedClipboardFormats(formats, count, out written);")]
    [InlineData("GetPriorityClipboardFormat(formats, count);")]
    [InlineData("System.Windows.Clipboard.ContainsImage();")]
    [InlineData("Clipboard \n . ContainsText();")]
    [InlineData("using Clip = System.Windows.Clipboard;")]
    [InlineData("using static global::System.Windows.Forms.Clipboard;")]
    public void SourceGuard_DetectsDirectAvailabilityQueriesAndImports(string source) =>
        Assert.Matches(DirectAvailabilityQuery, source);

    [Theory]
    [InlineData("ClipboardAvailability.HasPinnableContent();")]
    [InlineData("ClipboardAvailability.HasPinnableContent(_ => false, () => 0);")]
    [InlineData("data.GetDataPresent(DataFormats.UnicodeText);")]
    public void SourceGuard_AllowsIsolatedAvailabilityGatewayAndDataObjects(string source) =>
        Assert.DoesNotMatch(DirectAvailabilityQuery, source);

    private static void AssertNoMatches(string directory, Regex forbidden, string guidance, string? excludedFile = null)
    {
        string[] files = Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(file => !Path.GetRelativePath(directory, file).Split(Path.DirectorySeparatorChar)
                .Any(part => part is "bin" or "obj"))
            .Where(file => !string.Equals(file, excludedFile, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.NotEmpty(files);
        var violations = new List<string>();
        foreach (string file in files)
        {
            string source = File.ReadAllText(file);
            foreach (Match match in forbidden.Matches(source))
            {
                int line = source.AsSpan(0, match.Index).Count('\n') + 1;
                violations.Add($"{Path.GetRelativePath(directory, file)}:{line}: {match.Value}");
            }
        }
        Assert.True(violations.Count == 0, guidance + "\n" + string.Join("\n", violations));
    }

    private static string RepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "MyCapture.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException("The source checkout is required for the clipboard write guard.");
    }
}
