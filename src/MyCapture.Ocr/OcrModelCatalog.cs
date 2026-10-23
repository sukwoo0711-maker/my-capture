using MyCapture.Core.Settings;

namespace MyCapture.Ocr;

/// <summary>
/// Pinned PP-OCR model files and their first-run download sizes.
/// </summary>
internal static class OcrModelCatalog
{
    internal const string CatalogVersion = "v3.9.2";

    internal static readonly OcrModelFile Detection = new(
        "ch_PP-OCRv5_det_server.onnx",
        "https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.9.2/onnx/PP-OCRv5/det/ch_PP-OCRv5_det_server.onnx",
        "0F8846B1D4BBA223A2A2F9D9B44022FBC22CC019051A602B41A7FDA9667E4CAD",
        ExpectedBytes: 88_118_768);

    internal static readonly OcrModelFile Recognition = new(
        "korean_PP-OCRv5_rec_mobile.onnx",
        "https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.9.2/onnx/PP-OCRv5/rec/korean_PP-OCRv5_rec_mobile.onnx",
        "CD6E2EA50F6943CA7271EB8C56A877A5A90720B7047FE9C41A2E541A25773C9B",
        ExpectedBytes: 13_488_748);

    internal static readonly OcrModelFile Dictionary = new(
        "ppocrv5_korean_dict.txt",
        "https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.9.2/paddle/PP-OCRv5/rec/korean_PP-OCRv5_rec_mobile/ppocrv5_korean_dict.txt",
        "A88071C68C01707489BAA79EBE0405B7BEB5CCA229F4FC94CC3EF992328802D7",
        2048,
        ExpectedBytes: 47_451);

    internal static IReadOnlyList<OcrModelFile> Required { get; } = [Recognition, Dictionary];

    internal static IReadOnlyList<OcrModelFile> Optional { get; } = [Detection];

    // Recognition only: retained for the lead's separate one-time cleanup of old installs.
    // These files are never downloaded, verified or loaded by the OCR runtime.
    internal static IReadOnlyList<string> LegacySuperResolutionFiles => LegacyOcrModelFiles.SuperResolution;

    internal static long DownloadBytesFor(OcrQuality quality) => quality switch
    {
        OcrQuality.Accurate or OcrQuality.Enhanced => Required.Concat(Optional).Sum(file => file.ExpectedBytes!.Value),
        _ => 0,
    };

    internal static int DownloadMegabytesFor(OcrQuality quality) =>
        (int)Math.Ceiling(DownloadBytesFor(quality) / 1_000_000d);


}

internal sealed record OcrModelFile(
    string FileName,
    string Uri,
    string? ExpectedSha256,
    int MinimumBytes = 1024,
    long? ExpectedBytes = null,
    long MaxBytes = 256L * 1024 * 1024)
{
    internal long ByteLimit => Math.Min(ExpectedBytes ?? MaxBytes, MaxBytes);
}
