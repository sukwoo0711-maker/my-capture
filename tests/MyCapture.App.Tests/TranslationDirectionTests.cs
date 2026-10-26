using System.IO;
using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Core.Storage;
using MyCapture.Ocr.Translation;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class TranslationDirectionTests
{
    private static readonly Lazy<SentencePieceModel> Source = new(() => SentencePieceModel.Load(Fixture("opus-mt-en-ko-source.spm")));

    [Fact]
    public void DefaultCatalogAndStoreRemainEnglishToKorean()
    {
        using var reject = new RejectHttp();
        using var http = new HttpClient(reject);
        var paths = AppPaths.CreateForRoot(Path.Combine(Path.GetTempPath(), "MyCapture-direction-no-write-" + Guid.NewGuid().ToString("N")));
        var store = new TranslationModelStore(paths, http, NullLogger.Instance);
        Assert.Equal(TranslationDirection.EnglishToKorean, store.Direction);
        Assert.Equal(TranslationModelCatalog.FolderName, Path.GetFileName(store.ModelDirectory));
        Assert.Equal(TranslationModelCatalog.Files, store.Files);
        Assert.Same(TranslationModelCatalog.ModelFiles, store.ModelFiles);
        Assert.Equal(246_256_561L, store.TotalBytes);
        Assert.Null(store.ModelFiles.SourceTokenVocabulary);
        Assert.Equal(0, reject.Attempts);
    }

    [Fact]
    public void DirectionsHaveIsolatedFoldersFilesAndLazyEngines()
    {
        using var reject = new RejectHttp();
        using var http = new HttpClient(reject);
        var paths = AppPaths.CreateForRoot(Path.Combine(Path.GetTempPath(), "MyCapture-direction-no-write-" + Guid.NewGuid().ToString("N")));
        var enko = new TranslationModelStore(paths, http, NullLogger.Instance);
        var koen = new TranslationModelStore(paths, http, NullLogger.Instance, direction: TranslationDirection.KoreanToEnglish);
        using var englishTarget = new TextTranslationService(koen, NullLogger<TextTranslationService>.Instance);
        using var koreanTarget = new TextTranslationService(enko, NullLogger<TextTranslationService>.Instance);
        Assert.NotEqual(enko.ModelDirectory, koen.ModelDirectory);
        Assert.Equal("opus-mt-ko-en-1", Path.GetFileName(koen.ModelDirectory));
        Assert.Equal(171_874_150L, koen.TotalBytes);
        Assert.Equal(koen.TotalBytes, englishTarget.ModelDownloadBytes);
        Assert.Equal(koen.ModelFiles.TargetVocabulary, koen.ModelFiles.SourceTokenVocabulary);
        Assert.All(koen.Files, file => Assert.StartsWith("opus-mt-ko-en-", file.FileName));
        Assert.False(englishTarget.IsEngineLoaded);
        Assert.False(koreanTarget.IsEngineLoaded);
        Assert.False(Directory.Exists(paths.DataRoot));
        Assert.Equal(0, reject.Attempts);
    }

    [Fact]
    public void EveryKoreanToEnglishAssetIsPinnedAndAllEngineInputsAreVerified()
    {
        TranslationModelCatalog.Definition model = TranslationModelCatalog.KoreanToEnglish;
        string[] inputs = [model.ModelFiles.Encoder, model.ModelFiles.Decoder, model.ModelFiles.DecoderWithPast,
            model.ModelFiles.SourceVocabulary, model.ModelFiles.TargetVocabulary, model.ModelFiles.SourceTokenVocabulary!];
        Assert.All(inputs, name => Assert.Single(model.Files, file => file.FileName == name));
        Assert.All(model.Files, file =>
        {
            Assert.True(file.Bytes > 0);
            Assert.Matches("^[A-F0-9]{64}$", file.Sha256);
            var uri = new Uri(file.Uri);
            Assert.Equal("https", uri.Scheme);
            Assert.Equal("huggingface.co", uri.Host);
            Assert.DoesNotContain("/main/", uri.AbsolutePath);
            Assert.Matches("/resolve/[0-9a-f]{40}/", uri.AbsolutePath);
        });
        Assert.Throws<ArgumentOutOfRangeException>(() => TranslationModelCatalog.For((TranslationDirection)99));
    }

    [Fact]
    public void SeparateVocabularyKeepsEveryExistingReferenceTokenAndEosUnchanged()
    {
        var vocabulary = new MarianSourceVocabulary(Source.Value);
        using var json = JsonDocument.Parse(File.ReadAllText(Fixture("opus-mt-en-ko-source-spm-cases.json")));
        Assert.True(json.RootElement.GetArrayLength() >= 170);
        foreach (JsonElement item in json.RootElement.EnumerateArray())
        {
            int[] expected = [.. item.GetProperty("ids").EnumerateArray().Select(id => id.GetInt32()), Source.Value.EndOfSentenceId];
            Assert.Equal(expected, vocabulary.Encode(item.GetProperty("text").GetString()!, int.MaxValue));
        }
    }

    [Fact]
    public void SharedVocabularyMapsPieceTextAndEosInsteadOfUsingSentencePieceIds()
    {
        SentencePieceModel source = Source.Value;
        var map = Enumerable.Range(0, source.PieceCount).ToDictionary(source.PieceAt, id => source.PieceCount - id);
        var vocabulary = new MarianSourceVocabulary(source, map);
        const string sentence = "The device operates from 5000 ms to 30000 ms.";
        int[] expected = [.. source.EncodeToIds(sentence).Select(id => map[source.PieceAt(id)]), map["</s>"]];
        Assert.Equal(expected, vocabulary.Encode(sentence, int.MaxValue));
        Assert.NotEqual(source.EndOfSentenceId, expected[^1]);
        Assert.Equal([expected[0], expected[1], map["</s>"]], vocabulary.Encode(sentence, 3));
    }

    [Fact]
    public void SharedVocabularyUsesItsUnknownIdAndRejectsMissingSpecialOrDuplicateIds()
    {
        var map = new Dictionary<string, int> { ["<unk>"] = 91, ["</s>"] = 90 };
        var vocabulary = new MarianSourceVocabulary(Source.Value, map);
        int[] encoded = vocabulary.Encode("hello", int.MaxValue);
        Assert.Equal(90, encoded[^1]);
        Assert.All(encoded[..^1], id => Assert.Equal(91, id));
        Assert.Throws<InvalidDataException>(() => new MarianSourceVocabulary(Source.Value, new Dictionary<string, int> { ["</s>"] = 0 }));
        Assert.Throws<InvalidDataException>(() => new MarianSourceVocabulary(Source.Value, new Dictionary<string, int> { ["</s>"] = 0, ["<unk>"] = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => vocabulary.Encode("hello", 0));
    }

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private sealed class RejectHttp : HttpMessageHandler
    {
        internal int Attempts;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Attempts++; throw new InvalidOperationException("These tests never access HTTP."); }
    }
}
