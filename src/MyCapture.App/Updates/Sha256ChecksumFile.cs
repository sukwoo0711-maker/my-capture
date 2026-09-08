using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text.RegularExpressions;

namespace MyCapture.App.Updates;

/// <summary>
/// Parses and queries standard SHA256SUMS.txt files produced by the build and release pipeline.
/// Rejects duplicate or conflicting entries and path-qualified filenames.
/// </summary>
public sealed class Sha256ChecksumFile
{
    private static readonly Regex ChecksumLineRegex = new(
        @"^(?<hash>[0-9a-fA-F]{64})\s+[* ]?(?<filename>[^\r\n]+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly Dictionary<string, string> _checksums;

    private Sha256ChecksumFile(Dictionary<string, string> checksums)
    {
        _checksums = checksums;
    }

    public static Sha256ChecksumFile Parse(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var checksums = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var reader = new StringReader(content);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            line = line.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            Match match = ChecksumLineRegex.Match(line);
            if (!match.Success)
            {
                throw new UpdateException(
                    UpdateErrorKind.ChecksumParseFailed,
                    $"Malformed checksum line: '{line}'.");
            }

            string hash = match.Groups["hash"].Value.ToLowerInvariant();
            string rawFilename = match.Groups["filename"].Value.Trim();

            // Reject path-qualified filenames: no directory separators, relative segments, or invalid characters
            if (rawFilename.Contains('/') ||
                rawFilename.Contains('\\') ||
                rawFilename.StartsWith('.') ||
                rawFilename.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new UpdateException(
                    UpdateErrorKind.ChecksumParseFailed,
                    $"Path-qualified or invalid filename '{rawFilename}' is not allowed in checksum file.");
            }

            // Reject duplicate or conflicting checksum entries
            if (!checksums.TryAdd(rawFilename, hash))
            {
                throw new UpdateException(
                    UpdateErrorKind.ChecksumParseFailed,
                    $"Duplicate or conflicting checksum entry for '{rawFilename}'.");
            }
        }

        if (checksums.Count == 0)
        {
            throw new UpdateException(
                UpdateErrorKind.ChecksumParseFailed,
                "Checksum file contains no valid entries.");
        }

        return new Sha256ChecksumFile(checksums);
    }

    public bool TryGetChecksum(string filename, [NotNullWhen(true)] out string? sha256)
    {
        sha256 = null;
        if (string.IsNullOrWhiteSpace(filename) ||
            filename.Contains('/') ||
            filename.Contains('\\') ||
            filename.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return false;
        }

        // Exact match lookup only; no basename acceptance
        return _checksums.TryGetValue(filename.Trim(), out sha256);
    }
}
