using System.Diagnostics;
using System.IO;
using System.Text.Json;
using MyCapture.Ocr.Translation;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// The OPUS-MT en-ko source tokenizer must reproduce the ids Microsoft.ML.Tokenizers 2.0.1 gave the
/// translation probe (SentencePieceTokenizer.EncodeToTokens without BOS/EOS). The expected ids in the
/// case fixture were generated with that library.
/// </summary>
public sealed class TranslationSentencePieceTests
{
    private static readonly Lazy<byte[]> ModelBytes = new(() => File.ReadAllBytes(FixturePath("opus-mt-en-ko-source.spm")));
    private static readonly Lazy<SentencePieceModel> Model = new(() => SentencePieceModel.Load(FixturePath("opus-mt-en-ko-source.spm")));
    private static readonly Lazy<IReadOnlyList<(string Text, int[] Ids)>> Cases = new(LoadCases);

    [Fact]
    public void LoadsTheOpusSourceVocabulary()
    {
        SentencePieceModel model = Model.Value;
        Assert.Equal(32000, model.PieceCount);
        Assert.Equal(0, model.UnknownId);
        Assert.Equal(1, model.BeginOfSentenceId);
        Assert.Equal(2, model.EndOfSentenceId);
        Assert.Equal("<unk>", model.PieceAt(0));
        Assert.Equal("</s>", model.PieceAt(2));
        Assert.Equal("\u2581the", model.PieceAt(5));
        Assert.Throws<ArgumentOutOfRangeException>(() => model.PieceAt(32000));
        Assert.Throws<ArgumentOutOfRangeException>(() => model.PieceAt(-1));
    }

    [Fact]
    public void EveryReferenceCaseEncodesToTheMicrosoftMlTokenizersIds()
    {
        SentencePieceModel model = Model.Value;
        var failures = new List<string>();
        foreach ((string text, int[] expected) in Cases.Value)
        {
            int[] actual = model.EncodeToIds(text);
            if (!actual.SequenceEqual(expected))
            {
                failures.Add($"{JsonSerializer.Serialize(text)}: expected [{string.Join(", ", expected)}], got [{string.Join(", ", actual)}]");
            }
        }

        Assert.True(Cases.Value.Count >= 170, $"Only {Cases.Value.Count} reference cases were loaded.");
        Assert.Empty(failures);
    }

    [Fact]
    public void UnpairedSurrogatesEncodeAsReplacementCharacters()
    {
        // System.Text.Json cannot carry these strings, so their reference ids live here.
        (string Text, int[] Ids)[] cases =
        [
            ("a\uD800b", [10, 1489]),
            ("a\uDC00b", [10, 1489]),
            ("x\uDC00\uD800y", [921, 4231]),
            ("end\uD83D", [350]),
            ("\uDE00start", [451]),
            ("\uD800", []),
        ];

        SentencePieceModel model = Model.Value;
        foreach ((string text, int[] ids) in cases)
        {
            Assert.Equal(ids, model.EncodeToIds(text));
            string replaced = text.Replace('\uD800', '\uFFFD').Replace('\uDC00', '\uFFFD')
                .Replace('\uD83D', '\uFFFD').Replace('\uDE00', '\uFFFD');
            Assert.Equal(model.EncodeToIds(replaced), model.EncodeToIds(text));
        }
    }

    [Theory]
    [InlineData("Hello     world  again", "\u2581Hello\u2581world\u2581again")]
    [InlineData("\uFF21\uFF22\uFF23\uFF11\uFF12\uFF13\uFF01", "\u2581ABC123!")]
    [InlineData("cafe\u0301", "\u2581caf\u00E9")]
    [InlineData("2.1 \u00B5A", "\u25812.1\u2581\u03BCA")]
    [InlineData("\uFEFFStart\u200Bnow", "\u2581Start\u2581now")]
    [InlineData("\u2581hello \u2581\u2581world", "\u2581hello\u2581world")]
    [InlineData(" \t\u00A0\u3000 ", "")]
    [InlineData("", "")]
    // nmt_nfkc deletes U+007F; Microsoft.ML.Tokenizers keeps it, so it becomes an unknown piece.
    [InlineData("del\u007Fete", "\u2581del\u007Fete")]
    public void NormalizesLikeMicrosoftMlTokenizers(string text, string expected)
    {
        Assert.Equal(expected, Model.Value.Normalize(text));
    }

    [Fact]
    public void ConsecutiveUnknownCharactersStaySeparate()
    {
        // C++ SentencePiece would merge the six unknown characters into one id.
        int[] expected = [15, 0, 0, 0, 0, 0, 0];
        Assert.Equal(expected, Model.Value.EncodeToIds("\u6F22\u5B57\u6F22\u5B57\u6F22\u5B57"));
    }

    [Fact]
    public void TruncatedModelsAreRejected()
    {
        byte[] model = ModelBytes.Value;
        // Field boundaries before the trainer and normalizer specs, positions inside them, and a spread of others.
        int[] lengths = [0, 1, 2, 3, 100, 1000, 551_989, 551_990, 552_283, 552_284, 552_300, model.Length - 1];
        foreach (int length in lengths.Concat(Enumerable.Range(1, 23).Select(i => i * 34_337)))
        {
            Assert.Throws<InvalidDataException>(() => SentencePieceModel.Parse(model.AsSpan(0, length)));
        }
    }

    [Fact]
    public void MalformedProtobufIsRejected()
    {
        byte[][] malformed =
        [
            [0x00],                                   // field number 0
            [0x0A, 0xFF],                             // truncated length varint
            [0x0A, 0x05, 0x0A],                       // length beyond the data
            [0x0E, 0x00],                             // wire type 6
            [0x0F, 0x00],                             // wire type 7
            [0x0C],                                   // group end without a start
            [0x0B, 0x08, 0x01],                       // unterminated group
            [0x0B, 0x14],                             // group closed by another field
            [0x08, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x01], // eleven-byte varint
            [0x0D, 0x00, 0x00],                       // truncated fixed32
            [.. Enumerable.Repeat((byte)0x0B, 100)],  // groups nested too deeply
        ];

        foreach (byte[] data in malformed)
        {
            Assert.Throws<InvalidDataException>(() => SentencePieceModel.Parse(data));
        }
    }

    [Fact]
    public void UnsupportedOrInconsistentModelsAreRejected()
    {
        byte[] model = ModelBytes.Value;
        byte[][] suffixes =
        [
            [0x12, 0x02, 0x18, 0x02],                         // trainer_spec.model_type = BPE
            [0x12, 0x03, 0x98, 0x02, 0x01],                   // trainer_spec.byte_fallback = true
            [0x12, 0x03, 0xC0, 0x01, 0x01],                   // trainer_spec.treat_whitespace_as_suffix = true
            [0x12, 0x03, 0xC0, 0x02, 0x05],                   // trainer_spec.unk_id names a normal piece
            [0x12, 0x05, 0xC8, 0x02, 0xC0, 0xB8, 0x02],       // trainer_spec.bos_id = 40000
            [0x0A, 0x08, 0x0A, 0x04, 0x3C, 0x6D, 0x63, 0x3E, 0x18, 0x04], // user-defined piece "<mc>"
            [0x0A, 0x08, 0x0A, 0x04, 0x3C, 0x6D, 0x63, 0x3E, 0x18, 0x07], // piece "<mc>" of type 7
            [0x0A, 0x08, 0x0A, 0x06, 0xE2, 0x96, 0x81, 0x74, 0x68, 0x65], // "\u2581the" defined twice
            [0x0A, 0x00],                                     // empty piece
            [0x0A, 0x03, 0x0A, 0x01, 0xFF],                   // piece that is not UTF-8
            [0x1A, 0x07, 0x12, 0x05, 0xFF, 0xFF, 0x00, 0x00, 0x00], // charsmap trie larger than the blob
            [0x1A, 0x04, 0x12, 0x02, 0x01, 0x02],             // charsmap shorter than its header
        ];

        foreach (byte[] suffix in suffixes)
        {
            Assert.Throws<InvalidDataException>(() => SentencePieceModel.Parse([.. model, .. suffix]));
        }

        // The same "<mc>" as a normal piece is accepted, so the rejections above come from the piece type.
        Assert.Equal(32001, SentencePieceModel.Parse([.. model, 0x0A, 0x06, 0x0A, 0x04, 0x3C, 0x6D, 0x63, 0x3E]).PieceCount);
        Assert.Throws<InvalidDataException>(() => SentencePieceModel.Load(FixturePath("opus-mt-en-ko-source-spm-cases.json")));
    }

    [Fact]
    public void UnknownFieldsOfEveryWireTypeAreSkipped()
    {
        byte[] unknownFields =
        [
            0x78, 0x96, 0x01,                                     // field 15, varint
            0x81, 0x01, 1, 2, 3, 4, 5, 6, 7, 8,                   // field 16, fixed64
            0x8A, 0x01, 0x03, (byte)'a', (byte)'b', (byte)'c',    // field 17, length-delimited
            0x93, 0x01, 0x08, 0x01, 0x13, 0x08, 0x02, 0x14, 0x94, 0x01, // field 18, nested groups
            0x9D, 0x01, 1, 2, 3, 4,                               // field 19, fixed32
        ];
        // A later trainer_spec with bos_id = -1, sign-extended to a ten-byte varint, disables <s>.
        byte[] negativeBos = [0x12, 0x0C, 0xC8, 0x02, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01];

        SentencePieceModel model = SentencePieceModel.Parse([.. unknownFields, .. ModelBytes.Value, .. negativeBos]);

        Assert.Equal(32000, model.PieceCount);
        Assert.Equal(-1, model.BeginOfSentenceId);
        Assert.Equal(2, model.EndOfSentenceId);
        foreach ((string text, int[] ids) in Cases.Value.Take(40))
        {
            Assert.Equal(ids, model.EncodeToIds(text));
        }
    }

    [Theory]
    // A second normalizer_spec merges into the first one, as protobuf requires; ids from Microsoft.ML.Tokenizers.
    [InlineData(new byte[] { 0x1A, 0x02, 0x18, 0x00 }, "Hello world", "Hello\u2581world", new[] { 20301, 124 })]
    [InlineData(new byte[] { 0x1A, 0x02, 0x18, 0x00 }, "\uFF21\uFF22\uFF23 x", "ABC\u2581x", new[] { 220, 6188, 921 })]
    [InlineData(new byte[] { 0x1A, 0x02, 0x20, 0x00 }, "  Hello   world  ", "\u2581\u2581\u2581Hello\u2581\u2581\u2581world\u2581\u2581", new[] { 15, 15, 5286, 15, 15, 124, 15, 15 })]
    [InlineData(new byte[] { 0x1A, 0x02, 0x28, 0x00 }, "Hello world", " Hello world", new[] { 0, 20301, 0, 7389 })]
    public void RepeatedNormalizerSpecsMerge(byte[] suffix, string text, string normalized, int[] ids)
    {
        SentencePieceModel model = SentencePieceModel.Parse([.. ModelBytes.Value, .. suffix]);
        Assert.Equal(normalized, model.Normalize(text));
        Assert.Equal(ids, model.EncodeToIds(text));
    }

    [Fact]
    public void LongParagraphEncodesQuickly()
    {
        SentencePieceModel model = Model.Value;
        string paragraph = Cases.Value.Select(c => c.Text).First(t => t.Length >= 2000);
        for (int i = 0; i < 20; i++)
        {
            model.EncodeToIds(paragraph);
        }

        double fastest = double.MaxValue;
        for (int i = 0; i < 10; i++)
        {
            long started = Stopwatch.GetTimestamp();
            model.EncodeToIds(paragraph);
            fastest = Math.Min(fastest, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }

        Assert.True(fastest < 50, $"Encoding {paragraph.Length} characters took {fastest:F2} ms.");
    }

    [Fact]
    public void ParallelEncodingMatchesSequentialEncoding()
    {
        SentencePieceModel model = Model.Value;
        string[] texts = [.. Cases.Value.Select(c => c.Text)];
        int[][] sequential = [.. texts.Select(model.EncodeToIds)];
        var parallel = new int[texts.Length * 8][];

        Parallel.For(0, parallel.Length, new ParallelOptions { MaxDegreeOfParallelism = 8 },
            i => parallel[i] = model.EncodeToIds(texts[i % texts.Length]));

        for (int i = 0; i < parallel.Length; i++)
        {
            Assert.Equal(sequential[i % texts.Length], parallel[i]);
        }
    }

    private static List<(string Text, int[] Ids)> LoadCases()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(FixturePath("opus-mt-en-ko-source-spm-cases.json")));
        var cases = new List<(string Text, int[] Ids)>();
        foreach (JsonElement item in document.RootElement.EnumerateArray())
        {
            string text = item.GetProperty("text").GetString() ?? throw new InvalidDataException("A case has no text.");
            int[] ids = [.. item.GetProperty("ids").EnumerateArray().Select(id => id.GetInt32())];
            cases.Add((text, ids));
        }

        return cases;
    }

    private static string FixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
}
