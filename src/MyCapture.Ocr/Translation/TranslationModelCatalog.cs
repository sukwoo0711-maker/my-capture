namespace MyCapture.Ocr.Translation;

/// <summary>
/// One pinned file of the translation model. <see cref="FileName"/> is the release asset name
/// and the only name the store writes; nothing the server sends becomes a path.
/// </summary>
internal sealed record TranslationModelFile(string FileName, string Uri, string Sha256, long Bytes);

/// <summary>
/// Pinned files of the offline English-to-Korean model (OPUS-MT tc-big, int8). They are downloaded
/// only when the user turns translation on. The isolated preview can pre-seed model files. The three graphs
/// share their weights through the four weights files, written by
/// build/tools/translation-model/dedupe_opus_onnx.py; the tokenizer files are unchanged.
/// </summary>
internal static class TranslationModelCatalog
{
    internal const string FolderName = "opus-mt-en-ko-1";

    internal const string DownloadBase =
        "https://github.com/sukwoo0711-maker/my-capture/releases/download/translation-models-1/";

    private const string Encoder = "opus-mt-en-ko-encoder.onnx";
    private const string Decoder = "opus-mt-en-ko-decoder.onnx";
    private const string DecoderWithPast = "opus-mt-en-ko-decoder-with-past.onnx";
    private const string SourceVocabulary = "opus-mt-en-ko-source.spm";
    private const string TargetVocabulary = "opus-mt-en-ko-vocab.json";

    internal static IReadOnlyList<TranslationModelFile> Files { get; } =
    [
        Asset(Encoder, "68B8712EDF70DE3E02D693CD192D5162D1288A63AE3F5B853789FC3715CC81EE", 150_396),
        Asset(Decoder, "246718FA107A2CD96EC9D3DE4A736303E56D8E66E734180C2E0EF6F9735378E1", 366_443),
        Asset(DecoderWithPast, "2973DC754D0157C614D2F729F353C0B476C4F4B444A06CEB792E44F52DA93B82", 323_008),
        Asset("opus-mt-en-ko-weights-1.bin", "BF640EE406A2C7676DB2582696DA71951882DB357329B0FB631074241A32482D", 63_500_288),
        Asset("opus-mt-en-ko-weights-2.bin", "B64E4CE1120BCBB7B125BFA601569E12AA531F2ACAFB6A6CB82ACAC4F8352E09", 66_658_304),
        Asset("opus-mt-en-ko-weights-3.bin", "BC78727FC6D0671E94097919842220084A25A6EB64218B0AEF275A54D1E5817F", 67_108_864),
        Asset("opus-mt-en-ko-weights-4.bin", "EDADB3B70C7F35623C79B0A47CE4B9D935318F03A95B2DDFE71A83B4998716D8", 46_400_512),
        Asset(SourceVocabulary, "3D0591E65C49541D82F48DF33D7B322C3D4EE7AA0EE8747F9A7F9355DBF22C95", 789_870),
        Asset(TargetVocabulary, "9E03014979D240F6A6D1C5DC53CD14F4841351326D345D437D48D115EFF67B5F", 958_876),
    ];

    /// <summary>The files the translator opens; the weights files are found through the graphs.</summary>
    internal static MarianModelFiles ModelFiles { get; } =
        new(Encoder, Decoder, DecoderWithPast, SourceVocabulary, TargetVocabulary);

    internal static long TotalBytes { get; } = Files.Sum(file => file.Bytes);

    internal static int TotalMegabytes => (int)Math.Ceiling(TotalBytes / 1_000_000d);

    /// <summary>One model's isolated storage folder and the files its engine consumes.</summary>
    internal sealed record Definition(string FolderName, IReadOnlyList<TranslationModelFile> Files, MarianModelFiles ModelFiles)
    {
        internal long TotalBytes => Files.Sum(file => file.Bytes);
    }

    private const string KoreanToEnglishOnnxBase =
        "https://huggingface.co/Xenova/opus-mt-ko-en/resolve/d9fa1ac6008242100fecb4fff9b0a5917a1be81f/";
    private const string KoreanToEnglishTokenizerBase =
        "https://huggingface.co/Helsinki-NLP/opus-mt-ko-en/resolve/e42d1f41b66194e6d10512f8a27bebc1f4f5097e/";

    internal static Definition EnglishToKorean { get; } = new(FolderName, Files, ModelFiles);

    /// <summary>Unmodified upstream int8 graphs and tokenizer, pinned to immutable revisions.</summary>
    internal static Definition KoreanToEnglish { get; } = new("opus-mt-ko-en-1",
    [
        new("opus-mt-ko-en-encoder.onnx", KoreanToEnglishOnnxBase + "onnx/encoder_model_quantized.onnx",
            "210E1D2CB5B457CDEEFFE288099CC9D1DF9F13C35DC3D39FDE4BA252AF877380", 52_899_741),
        new("opus-mt-ko-en-decoder.onnx", KoreanToEnglishOnnxBase + "onnx/decoder_model_quantized.onnx",
            "0C961EA33A62EA84E73C9EC94C0737E65D06B5F73A7D0398B018FBBB7262D929", 59_842_101),
        new("opus-mt-ko-en-decoder-with-past.onnx", KoreanToEnglishOnnxBase + "onnx/decoder_with_past_model_quantized.onnx",
            "196BD1705AE0EAC256F1A9AB8C82C243DECDAD124C5ACE67FE073D9E0FC6956E", 56_570_637),
        new("opus-mt-ko-en-source.spm", KoreanToEnglishTokenizerBase + "source.spm",
            "C9496F7C2BE9AECB84C751AE9F35A875915DDE8E3892F652A5C76811AB2A0F49", 841_805),
        new("opus-mt-ko-en-vocab.json", KoreanToEnglishTokenizerBase + "vocab.json",
            "D42E3838E7ED0E43785FA013EF22AB4D382248CDF4E959C46A2B48DB5EB019E9", 1_719_866),
    ], new("opus-mt-ko-en-encoder.onnx", "opus-mt-ko-en-decoder.onnx", "opus-mt-ko-en-decoder-with-past.onnx",
        "opus-mt-ko-en-source.spm", "opus-mt-ko-en-vocab.json", SourceTokenVocabulary: "opus-mt-ko-en-vocab.json"));

    internal static Definition For(TranslationDirection direction) => direction switch
    {
        TranslationDirection.EnglishToKorean => EnglishToKorean,
        TranslationDirection.KoreanToEnglish => KoreanToEnglish,
        _ => throw new ArgumentOutOfRangeException(nameof(direction)),
    };
    private static TranslationModelFile Asset(string fileName, string sha256, long bytes) =>
        new(fileName, DownloadBase + fileName, sha256, bytes);
}
