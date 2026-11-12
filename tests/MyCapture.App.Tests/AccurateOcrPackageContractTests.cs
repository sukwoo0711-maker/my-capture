using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Core.Storage;
using MyCapture.Ocr;
using RapidOcrNet;
using Xunit;
using Xunit.Abstractions;

namespace MyCapture.App.Tests;

public sealed class AccurateOcrPackageContractTests(ITestOutputHelper output)
{
    [Fact]
    public void ReleasePublish_OmitsOnlyTheUnusedLatinPairFromThePackageCopy()
    {
        XDocument project = XDocument.Load(Path.Combine(RepositoryRoot(), "src", "MyCapture.App", "MyCapture.App.csproj"));
        XElement target = Assert.Single(project.Descendants("Target"),
            element => (string?)element.Attribute("Name") == "SelectMyCapturePublishedOcrModels");
        Assert.Equal("CopyModelsPublish", (string?)target.Attribute("BeforeTargets"));
        Assert.Equal("'$(Configuration)' == 'Release' and '$(SelfContained)' == 'true'", (string?)target.Attribute("Condition"));
        XElement removal = Assert.Single(target.Descendants("Models"));
        Assert.Equal("@(Models)", (string?)removal.Attribute("Remove"));
        Assert.Equal("'%(Models.Filename)%(Models.Extension)' == 'latin_PP-OCRv5_rec_mobile_infer.onnx' or '%(Models.Filename)%(Models.Extension)' == 'ppocrv5_latin_dict.txt'",
            (string?)removal.Attribute("Condition"));
        Assert.Empty(target.Descendants("Delete"));
        Assert.Empty(target.Descendants("RemoveDir"));
    }

    [Fact]
    public void AccurateOcrOfflineContract_AllProductInitializersPassExplicitShippedOrCatalogModels()
    {
        string engine = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "MyCapture.Ocr", "NeuralOcrEngine.cs"));
        string[] bundled = Regex.Matches(engine, @"Bundled\((RapidOcr\.[A-Za-z]+)\)")
            .Select(match => match.Groups[1].Value).OrderBy(value => value, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "RapidOcr.DefaultClsModelPath", "RapidOcr.DefaultDetModelPath" }, bundled);
        Assert.Contains("string rec = _store.PathFor(OcrModelCatalog.Recognition);", engine, StringComparison.Ordinal);
        Assert.Contains("string keys = _store.PathFor(OcrModelCatalog.Dictionary);", engine, StringComparison.Ordinal);
        Assert.Contains("engine.InitModels(det, cls, rec, keys, options);", engine, StringComparison.Ordinal);
        Assert.Contains("reader.InitModel(rec, _store.PathFor(OcrModelCatalog.Dictionary), options);", engine, StringComparison.Ordinal);

        // A new initializer, preset/default overload or another product source path needs an
        // explicit packaging decision. Do not let a dependency's Latin defaults return silently.
        string[] initializers = Directory.EnumerateFiles(Path.Combine(RepositoryRoot(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Split(Path.DirectorySeparatorChar).Any(part => part is "obj" or "bin"))
            .SelectMany(path => Regex.Matches(File.ReadAllText(path), @"\b[A-Za-z_][A-Za-z0-9_]*\.InitModels?\([^;]+;")
                .Select(match => match.Value))
            .OrderBy(value => value, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[]
        {
            "engine.InitModels(det, cls, rec, keys, options);",
            "reader.InitModel(rec, _store.PathFor(OcrModelCatalog.Dictionary), options);",
        }, initializers);
        Assert.Equal("korean_PP-OCRv5_rec_mobile.onnx", OcrModelCatalog.Recognition.FileName);
        Assert.Equal("ppocrv5_korean_dict.txt", OcrModelCatalog.Dictionary.FileName);
    }

    [NeuralOcrLiveFact]
    public void AccurateOcrOfflineContract_RealInferenceRequestsOnlyShippedOrVerifiedDownloadedFiles()
    {
        string? models = Environment.GetEnvironmentVariable(NeuralOcrLiveFactAttribute.ModelsVariable);
        Assert.False(string.IsNullOrWhiteSpace(models), "The required live test needs a local, verified Korean model cache.");
        string root = OwnedTestDirectory.Create("accurate-package-contract-");
        try
        {
            AppPaths paths = NeuralOcrEngineLiveTests.CopyModelsToIsolatedProfile(Path.GetFullPath(models!), root);
            using var http = new HttpClient(new RejectingHandler());
            var store = new OcrModelStore(paths, http, NullLogger.Instance);
            Assert.True(store.HasRequiredModels);
            int initialized = 0;
            using var engine = new NeuralOcrEngine(store, NullLogger<NeuralOcrEngine>.Instance,
                wholeSessionInitializing: (_, det, cls, rec, keys) =>
                {
                    Assert.Contains(Path.GetFileName(det), new[] { RapidOcr.DefaultDetModelPath, OcrModelCatalog.Detection.FileName });
                    Assert.Equal(RapidOcr.DefaultClsModelPath, Path.GetFileName(cls));
                    Assert.Equal(store.PathFor(OcrModelCatalog.Recognition), rec);
                    Assert.Equal(store.PathFor(OcrModelCatalog.Dictionary), keys);
                    foreach (string path in new[] { det, cls, rec, keys })
                    {
                        Assert.True(File.Exists(path), "The requested model must be shipped or a verified catalog download: " + path);
                        output.WriteLine("Accurate OCR opened: " + path);
                    }
                    initialized++;
                });
            engine.ModelVerificationTask.GetAwaiter().GetResult();
            StaTestHost.Run(() =>
            {
                BitmapSource image = Receipt();
                OcrRequest request = OcrRequest.FromBitmap(image, 1.0, ["ko-KR"], false, true, true);
                MyCapture.Ocr.OcrResult whole = engine.RecognizeAsync(request, CancellationToken.None).GetAwaiter().GetResult();
                Assert.Equal(OcrStatus.Success, whole.Status);
                Assert.Contains("12345", whole.Text, StringComparison.Ordinal);
                MyCapture.Ocr.OcrResult rows = engine.ReadRowsAsync(request, whole.Lines, CancellationToken.None).GetAwaiter().GetResult();
                Assert.Equal(OcrStatus.Success, rows.Status);
                Assert.Contains("12345", rows.Text, StringComparison.Ordinal);
                output.WriteLine("Whole image: " + whole.Text);
                output.WriteLine("Rows: " + rows.Text);
            });
            Assert.Equal(1, initialized);
        }
        finally
        {
            OwnedTestDirectory.Delete(root);
        }
    }

    private static BitmapSource Receipt()
    {
        var visual = new DrawingVisual();
        using (DrawingContext drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, 640, 360));
            drawing.DrawText(new FormattedText("RECEIPT\nTOTAL 12345", System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, new Typeface("Arial"), 32, Brushes.Black, 1), new Point(40, 48));
        }
        var bitmap = new RenderTargetBitmap(640, 360, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static string RepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")) && Directory.Exists(Path.Combine(directory.FullName, "src")))
                return directory.FullName;
        throw new DirectoryNotFoundException("The source repository was not found above the test output.");
    }

    private sealed class RejectingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Accurate OCR package tests must not download models.");
    }
}
