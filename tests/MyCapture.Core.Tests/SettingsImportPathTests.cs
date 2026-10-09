using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class SettingsImportPathTests
{
    private const string ImportedJson = "{\"schemaVersion\":4,\"general\":{\"isFirstRun\":false},\"capture\":{\"delaySeconds\":17}}";

    [Theory]
    [InlineData(".txt")]
    [InlineData(".png")]
    [InlineData(".zip")]
    public void ImportRejectsExistingNonJsonFileAndPreservesBothSentinels(string extension)
    {
        string root = ValidatedFilePaths.CreateTemporaryDirectory();
        var paths = AppPaths.CreateForRoot(root);
        string live = ValidatedFilePaths.FileWithinRoot(root, paths.SettingsFile, ".json");
        File.WriteAllText(live, "{}");
        string source = ValidatedFilePaths.FileWithinRoot(root, "foreign" + extension, extension);
        File.WriteAllText(source, ImportedJson);
        var store = new SettingsStore(paths, NullLogger<SettingsStore>.Instance);

        Assert.Throws<IOException>(() => store.ImportFrom(source));

        Assert.Equal(ImportedJson, File.ReadAllText(source));
        Assert.Equal("{}", File.ReadAllText(live));
    }

    [Fact]
    public void ImportRejectsRelativePathEvenWhenItResolvesToValidJson()
    {
        // A temp directory may be on another drive on Windows CI, in which case
        // GetRelativePath returns an absolute path and never exercises rejection.
        string root = ValidatedFilePaths.CreateDirectory(Path.Combine(
            Environment.CurrentDirectory, "settings-import-" + Guid.NewGuid().ToString("N")));
        string source = ValidatedFilePaths.FileWithinRoot(root, "import.json", ".json");
        File.WriteAllText(source, ImportedJson);
        string relative = Path.GetRelativePath(Environment.CurrentDirectory, source);
        Assert.False(Path.IsPathFullyQualified(relative));
        var store = new SettingsStore(AppPaths.CreateForRoot(root), NullLogger<SettingsStore>.Instance);

        Assert.Throws<IOException>(() => store.ImportFrom(relative));

        Assert.Equal(ImportedJson, File.ReadAllText(source));
    }

    [Fact]
    public void ImportAcceptsExplicitlySelectedJsonOutsideLiveRootAndPreservesBothFiles()
    {
        string owner = ValidatedFilePaths.CreateTemporaryDirectory();
        string liveRoot = ValidatedFilePaths.CreateDirectory(Path.Combine(owner, "settings"));
        string sibling = ValidatedFilePaths.CreateDirectory(Path.Combine(owner, "settings-backup"));
        string live = ValidatedFilePaths.FileWithinRoot(liveRoot, "settings.json", ".json");
        string source = ValidatedFilePaths.FileWithinRoot(sibling, "portable.JSON", ".json");
        File.WriteAllText(live, "{}");
        File.WriteAllText(source, ImportedJson);
        var store = new SettingsStore(AppPaths.CreateForRoot(liveRoot), NullLogger<SettingsStore>.Instance);

        Assert.Equal(17, store.ImportFrom(source).Capture.DelaySeconds);

        Assert.Equal(ImportedJson, File.ReadAllText(source));
        Assert.Equal("{}", File.ReadAllText(live));
        Assert.Empty(store.LastLoadWarnings);
    }
}
