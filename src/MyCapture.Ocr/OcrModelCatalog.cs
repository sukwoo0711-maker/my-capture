namespace MyCapture.Ocr;

/// <summary>
/// PP-OCRv5 files fetched from RapidAI's published ModelScope catalog. Hashes match
/// <c>default_models.yaml</c> v3.9.2 so a truncated download cannot be used for recognition.
/// </summary>
internal static class OcrModelCatalog
{
    internal const string CatalogVersion = "v3.9.2";

    internal static readonly OcrModelFile Detection = new(
        "ch_PP-OCRv5_det_server.onnx",
        "https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.9.2/onnx/PP-OCRv5/det/ch_PP-OCRv5_det_server.onnx",
        "0F8846B1D4BBA223A2A2F9D9B44022FBC22CC019051A602B41A7FDA9667E4CAD");

    internal static readonly OcrModelFile Recognition = new(
        "korean_PP-OCRv5_rec_mobile.onnx",
        "https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.9.2/onnx/PP-OCRv5/rec/korean_PP-OCRv5_rec_mobile.onnx",
        "CD6E2EA50F6943CA7271EB8C56A877A5A90720B7047FE9C41A2E541A25773C9B");

    internal static readonly OcrModelFile Dictionary = new(
        "ppocrv5_korean_dict.txt",
        "https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.9.2/paddle/PP-OCRv5/rec/korean_PP-OCRv5_rec_mobile/ppocrv5_korean_dict.txt",
        null,
        2048);

    internal static IReadOnlyList<OcrModelFile> Required { get; } = [Recognition, Dictionary];

    internal static IReadOnlyList<OcrModelFile> Optional { get; } = [Detection];
}

internal sealed record OcrModelFile(
    string FileName,
    string Uri,
    string? ExpectedSha256,
    int MinimumBytes = 1024);
