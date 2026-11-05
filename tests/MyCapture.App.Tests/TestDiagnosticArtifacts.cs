using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Media.Imaging;
using MyCapture.App.Diagnostics;
using MyCapture.Core.Serialization;

namespace MyCapture.App.Tests;

/// <summary>Opt-in synthetic evidence retained in a new directory under repository artifacts.</summary>
internal static class TestDiagnosticArtifacts
{
    internal static string? Create(string variable, string caseName)
    {
        string? requested = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(requested)) return null;
        DirectoryInfo? repository = new(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "src", "MyCapture.App", "MyCapture.App.csproj")))
            repository = repository.Parent;
        if (repository is null) throw new IOException("Could not locate the test repository.");
        string allowed = Path.GetFullPath(Path.Combine(repository.FullName, "artifacts")) + Path.DirectorySeparatorChar;
        string parent = Path.GetFullPath(requested, repository.FullName);
        if (!parent.StartsWith(allowed, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Test evidence must be a child of repository artifacts.");
        parent = DiagnosticOutputPaths.Create(parent);
        string directory = DiagnosticOutputPaths.Child(parent, $"{caseName}-{Guid.NewGuid():N}");
        if (Directory.Exists(directory) || File.Exists(directory)) throw new IOException("Evidence directory already exists.");
        return DiagnosticOutputPaths.Create(directory);
    }

    internal static void WriteJson<T>(string directory, string name, T value)
    {
        using var stream = new FileStream(DiagnosticOutputPaths.Child(directory, name), FileMode.CreateNew, FileAccess.Write);
        JsonSerializer.Serialize(stream, value, JsonDefaults.Readable);
    }

    internal static void SavePng(string directory, string name, BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new FileStream(DiagnosticOutputPaths.Child(directory, name), FileMode.CreateNew, FileAccess.Write);
        encoder.Save(stream);
    }

    internal static string HashFile(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
