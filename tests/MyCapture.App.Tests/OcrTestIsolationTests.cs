using System.IO;
using System.Text.RegularExpressions;
using MyCapture.Ocr;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class OcrTestIsolationTests
{
    private static readonly Regex DefaultPaths = new(@"AppPaths\s*\.\s*Create" + @"Default\s*\(", RegexOptions.CultureInvariant);

    [Fact]
    public void TestSources_DoNotConstructDefaultProfilePaths()
    {
        string root = RepositoryRoot();
        var violations = new List<string>();
        int readOnlyPathContracts = 0;
        foreach (string file in Directory.EnumerateFiles(Path.Combine(root, "tests"), "*.cs", SearchOption.AllDirectories)
            .Where(file => !Path.GetRelativePath(root, file).Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj")))
        {
            string source = File.ReadAllText(file);
            int calls = DefaultPaths.Matches(source).Count;
            // These two Core contracts only compare DataRoot strings. They neither create
            // directories nor pass default paths to a model store or neural engine.
            if (Path.GetRelativePath(root, file).Replace('\\', '/') == "tests/MyCapture.Core.Tests/StoreEditionTests.cs")
            {
                readOnlyPathContracts = calls;
                continue;
            }
            if (calls > 0) violations.Add(Path.GetRelativePath(root, file));
        }
        Assert.Equal(2, readOnlyPathContracts);
        Assert.Empty(violations);
    }

    [Theory]
    [InlineData("AppPaths . Create", "Default ( )")]
    [InlineData("AppPaths\n.\nCreate", "Default\n(")]
    public void DefaultProfileSourceGuard_DetectsWhitespaceVariants(string prefix, string suffix) =>
        Assert.Matches(DefaultPaths, prefix + suffix);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LiveModels_AreCopiedIntoIsolatedProfileWithoutChangingSource(bool withDetector)
    {
        string source = OwnedTestDirectory.Create("ocr-model-source-");
        string root = OwnedTestDirectory.Create("ocr-model-copy-");
        try
        {
            foreach (OcrModelFile file in OcrModelCatalog.Required)
                File.WriteAllBytes(Path.Combine(source, file.FileName), [1, 2, 3]);
            if (withDetector)
                File.WriteAllBytes(Path.Combine(source, OcrModelCatalog.Detection.FileName), [4, 5]);
            string retired = Path.Combine(source, OcrModelCatalog.LegacySuperResolutionFiles[0]);
            File.WriteAllText(retired, "untouched");
            var paths = NeuralOcrEngineLiveTests.CopyModelsToIsolatedProfile(source, root);
            Assert.StartsWith(root + Path.DirectorySeparatorChar, paths.OcrModelsRoot, StringComparison.OrdinalIgnoreCase);
            foreach (OcrModelFile file in OcrModelCatalog.Required)
            {
                Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(paths.OcrModelsRoot, file.FileName)));
                Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(source, file.FileName)));
            }
            string detector = Path.Combine(paths.OcrModelsRoot, OcrModelCatalog.Detection.FileName);
            Assert.Equal(withDetector, File.Exists(detector));
            if (withDetector) Assert.Equal(new byte[] { 4, 5 }, File.ReadAllBytes(detector));
            Assert.Equal("untouched", File.ReadAllText(retired));
            Assert.False(File.Exists(Path.Combine(paths.OcrModelsRoot, Path.GetFileName(retired))));
        }
        finally
        {
            OwnedTestDirectory.Delete(root);
            OwnedTestDirectory.Delete(source);
        }
    }

    [Theory]
    [InlineData("ocr-live-models")]
    [InlineData(@"..\..\ocr-live-models")]
    [InlineData(@"C:\Users\someone\AppData\Local\MyCapture\ocr-live-models")]
    public void LiveModelSource_UsesOnlyTheFolderNameUnderTemp(string setting) =>
        Assert.Equal(Path.Combine(Path.GetTempPath(), "ocr-live-models"), NeuralOcrEngineLiveTests.LiveModelSource(setting));

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData(@"models\")]
    public void LiveModelSource_RejectsMissingOrTraversalFolderName(string setting) =>
        Assert.Throws<ArgumentException>(() => NeuralOcrEngineLiveTests.LiveModelSource(setting));

    private static string RepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "MyCapture.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException("The source checkout is required for the OCR isolation guard.");
    }
}
