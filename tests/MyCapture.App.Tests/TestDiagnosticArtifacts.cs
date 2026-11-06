using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Media.Imaging;
using MyCapture.App.Diagnostics;
using MyCapture.Core.Serialization;
using Xunit;

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

    /// <summary>Resolves an explicitly supplied file or directory through the confined diagnostic path check.</summary>
    internal static string Confined(string path)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string parent = Path.GetDirectoryName(full) ?? throw new IOException("Diagnostic paths require a parent directory.");
        return DiagnosticOutputPaths.Child(parent, Path.GetFileName(full));
    }

    /// <summary>An explicitly supplied input file that must already exist; nothing is created or opened.</summary>
    internal static string ExistingFile(string path)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string parent = Path.GetDirectoryName(full) ?? throw new IOException("Diagnostic paths require a parent directory.");
        return DiagnosticOutputPaths.ExistingFile(parent, Path.GetFileName(full));
    }

    /// <summary>
    /// A new output directory. An existing file or directory is preserved and rejected; with
    /// <paramref name="requiredAncestor"/> the directory must also lie strictly below that ancestor.
    /// </summary>
    internal static string NewDirectory(string path, string? requiredAncestor = null)
    {
        string directory = Confined(path);
        if (requiredAncestor is not null)
        {
            string prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(requiredAncestor)) + Path.DirectorySeparatorChar;
            if (!directory.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Diagnostic output must be a new directory below its required root.");
        }
        if (Directory.Exists(directory) || File.Exists(directory))
            throw new IOException("Diagnostic output already exists; it is preserved. Choose a new path.");
        return DiagnosticOutputPaths.Create(directory);
    }

    /// <summary>
    /// Reads a pinned input once. Callers parse these exact bytes, so the verified hash and the parsed
    /// content cannot come from two different opens; a mismatch fails before anything is parsed.
    /// </summary>
    internal static byte[] ReadVerified(string path, string expectedSha256)
    {
        byte[] bytes = File.ReadAllBytes(path);
        Assert.Equal(expectedSha256, Convert.ToHexString(SHA256.HashData(bytes)));
        return bytes;
    }

    /// <summary>
    /// Writes verified bytes as a new evidence file for media APIs that reopen their input by path,
    /// then verifies the retained copy itself before the caller hands it to such an API.
    /// </summary>
    internal static string Retain(string directory, string name, byte[] verified, string expectedSha256)
    {
        Assert.Equal(expectedSha256, Convert.ToHexString(SHA256.HashData(verified)));
        string path = DiagnosticOutputPaths.Child(directory, name);
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            stream.Write(verified);
        Assert.Equal(verified, ReadVerified(path, expectedSha256));
        return path;
    }

    internal static string HashFile(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
