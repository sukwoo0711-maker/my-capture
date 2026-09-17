using Microsoft.ML.OnnxRuntime;
using System.IO;

namespace MyCapture.Ocr;

/// <summary>
/// Real-ESRGAN x4 (RRDBNet, BSD-3, xinntao/Real-ESRGAN) ONNX weights exported by
/// Qualcomm AI Hub for Real-ESRGAN-x4plus. The model is fixed 128×128 → 512×512 and
/// stores its weights as external ONNX data beside the model file. Hashes pin the
/// exact downloaded bytes; a truncated or altered file is never used.
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
        "A88071C68C01707489BAA79EBE0405B7BEB5CCA229F4FC94CC3EF992328802D7",
        2048);

    internal static IReadOnlyList<OcrModelFile> Required { get; } = [Recognition, Dictionary];

    internal static IReadOnlyList<OcrModelFile> Optional { get; } = [Detection];

    internal static readonly OcrModelFile SuperResolution = new(
        "realesrgan_x4.onnx",
        SuperResolutionUri,
        "29FFD5BC0277B19536CD39B737627FB2D79DF9999B8329741B558498DD5E31F7");

    internal static readonly OcrModelFile SuperResolutionData = new(
        "realesrgan_x4.onnx.data",
        SuperResolutionUri,
        "28FADA125730D3C87D504D48DD8332837F65811EC431A570EA4919BA3EEE3287");

    internal static string ZipMemberNameFor(string fileName) => fileName switch
    {
        "realesrgan_x4.onnx" => "real_esrgan_x4plus.onnx",
        "realesrgan_x4.onnx.data" => "real_esrgan_x4plus.data",
        _ => throw new ArgumentException($"No zip member mapping for {fileName}", nameof(fileName)),
    };

    private const string SuperResolutionUri =
        "https://qaihub-public-assets.s3.us-west-2.amazonaws.com/qai-hub-models/models/real_esrgan_x4plus/releases/v0.62.2/real_esrgan_x4plus-onnx-float.zip";
}

internal sealed record OcrModelFile(
    string FileName,
    string Uri,
    string? ExpectedSha256,
    int MinimumBytes = 1024);
