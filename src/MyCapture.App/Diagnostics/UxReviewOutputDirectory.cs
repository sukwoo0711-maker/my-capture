using System.IO;

namespace MyCapture.App.Diagnostics;

/// <summary>Allocates a fresh diagnostic directory without accepting a path from callers.</summary>
internal static class UxReviewOutputDirectory
{
    internal static DirectoryInfo Create() => Directory.CreateTempSubdirectory("MyCapture-ux-review-");

    /// <summary>A process-specific receipt beside the generated directory, for WinExe callers.</summary>
    internal static string WriteReceipt(DirectoryInfo output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (!output.Exists || output.Parent is null || !output.Name.StartsWith("MyCapture-ux-review-", StringComparison.Ordinal))
            throw new IOException("A generated UX review directory is required.");
        string path = Path.Combine(output.Parent.FullName, $"MyCapture-ux-review-output-{Environment.ProcessId}.txt");
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(file, new System.Text.UTF8Encoding(false));
        writer.WriteLine(output.FullName);
        return path;
    }
}
