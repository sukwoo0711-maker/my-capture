using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.Ocr;

namespace MyCapture.App.Diagnostics;

/// <summary>Only the runner's copied, hash-pinned synthetic input; no user profile discovery.</summary>
internal sealed class ResidentLongRunInputs
{
    internal sealed record FilePin(string Name, long Bytes, string Sha256);
    internal sealed record Specification(string Schema, string Provenance, FilePin Image,
        JsonElement OcrGolden, string TranslationSource, string TranslationExpected,
        string[] PreferredLanguages, string ModelManifestSha256);

    private readonly string _root;
    private readonly string _specSha;
    internal Specification Spec { get; }
    internal BitmapSource Image { get; }
    internal string PixelSha256 { get; }

    internal ResidentLongRunInputs(string output)
    {
        _root = DiagnosticOutputPaths.Child(output, "inputs");
        string file = DiagnosticOutputPaths.Child(_root, "inputs.json");
        _specSha = HashFile(file);
        Spec = JsonSerializer.Deserialize<Specification>(File.ReadAllText(file))
            ?? throw new InvalidDataException("Missing long-run input specification.");
        if (Spec.Schema != "resident-soak-synthetic-input-v1" || Spec.Provenance != "SYNTHETIC_DEVELOPMENT_ONLY"
            || Spec.PreferredLanguages.Length == 0 || string.IsNullOrWhiteSpace(Spec.TranslationSource)
            || string.IsNullOrWhiteSpace(Spec.TranslationExpected) || Spec.ModelManifestSha256.Length != 64)
            throw new InvalidDataException("Long-run inputs are incomplete.");
        string image = DiagnosticOutputPaths.Child(_root, Spec.Image.Name);
        Verify(image, Spec.Image);
        Image = Load(image);
        PixelSha256 = Pixels(Image);
        // Golden is a pre-existing saved result from this exact input/engine contract, not truth
        // inferred from the candidate. Runtime identity is enforced by the outer manifest.
        foreach (string name in new[] { "text", "language", "lines", "textAngle" })
            if (!Spec.OcrGolden.TryGetProperty(name, out _)) throw new InvalidDataException("Incomplete OCR golden.");
    }

    internal void VerifyUnchanged()
    {
        if (HashFile(DiagnosticOutputPaths.Child(_root, "inputs.json")) != _specSha)
            throw new IOException("Long-run specification changed.");
        Verify(DiagnosticOutputPaths.Child(_root, Spec.Image.Name), Spec.Image);
        if (Pixels(Image) != PixelSha256) throw new IOException("Long-run pixels changed.");
    }

    internal static JsonElement Projection(OcrResult result) => JsonSerializer.SerializeToElement(new
    {
        text = result.Text, language = result.LanguageTag,
        lines = result.Lines.Select(line => new { text = line.Text, bounds = line.Bounds,
            words = line.Words.Select(word => new { text = word.Text, bounds = word.Bounds }).ToArray() }).ToArray(),
        textAngle = result.TextAngle,
    });

    internal void RequireGolden(OcrResult result)
    {
        if (result.Status != OcrStatus.Success || !result.HasText)
            throw new InvalidDataException("Real OCR did not complete successfully: " + result.Status);
        JsonElement actual = Projection(result);
        if (!JsonElement.DeepEquals(actual, Spec.OcrGolden))
            throw new InvalidDataException("Real OCR differs from the pinned synthetic development golden.");
        foreach (OcrLine line in result.Lines)
        {
            Check(line.Bounds);
            foreach (OcrWord word in line.Words) Check(word.Bounds);
        }
        void Check(OcrRect box)
        {
            if (!double.IsFinite(box.X) || !double.IsFinite(box.Y) || !double.IsFinite(box.Width)
                || !double.IsFinite(box.Height) || box.X < 0 || box.Y < 0 || box.Width < 0 || box.Height < 0
                || box.Right > Image.PixelWidth || box.Bottom > Image.PixelHeight)
                throw new InvalidDataException("OCR coordinates are outside the synthetic source.");
        }
    }

    internal static BitmapSource Load(string path)
    {
        using var input = File.OpenRead(path);
        BitmapSource result = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad).Frames[0];
        result.Freeze();
        return result;
    }

    internal static string Pixels(BitmapSource image)
    {
        BitmapSource pixels = image.Format == PixelFormats.Bgra32 ? image
            : new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        byte[] bytes = new byte[checked(pixels.PixelWidth * pixels.PixelHeight * 4)];
        pixels.CopyPixels(bytes, pixels.PixelWidth * 4, 0);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    internal static string HashFile(string path)
    {
        // Reuse the diagnostic path validator immediately before opening.
        path = DiagnosticOutputPaths.Child(Path.GetDirectoryName(path)!, Path.GetFileName(path));
        using var input = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(input));
    }

    private static void Verify(string path, FilePin pin)
    {
        if (new FileInfo(path).Length != pin.Bytes || !string.Equals(HashFile(path), pin.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Pinned synthetic input changed.");
    }
}
