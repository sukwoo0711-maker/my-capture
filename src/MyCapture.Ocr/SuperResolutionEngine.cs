namespace MyCapture.Ocr;

/// <summary>
/// Legacy names retained for the lead's separate old-install cleanup. This source file no
/// longer contains a super-resolution engine, model metadata, download URI or loader.
/// </summary>
internal static class LegacyOcrModelFiles
{
    internal static IReadOnlyList<string> SuperResolution { get; } =
        ["realesrgan_x4.onnx", "realesrgan_x4.onnx.data", "real_esrgan_x4plus.data"];
}
