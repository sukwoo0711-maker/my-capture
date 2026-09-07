using System.IO;

namespace MyCapture.App.Diagnostics;

/// <summary>Allocates a fresh diagnostic directory without accepting a path from callers.</summary>
internal static class UxReviewOutputDirectory
{
    internal static DirectoryInfo Create() => Directory.CreateTempSubdirectory("MyCapture-ux-review-");
}
