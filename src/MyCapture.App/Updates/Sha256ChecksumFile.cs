using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text.RegularExpressions;

namespace MyCapture.App.Updates;

/// <summary>
/// Parses and queries standard SHA256SUMS.txt files produced by the build and release pipeline.
/// </summary>
public sealed class Sha256ChecksumFile
{
    private static readonly Regex ChecksumLineRegex = new(
        @"^(?<hash>[0-9a-fA-F]{64})\s+[*§]?(?<filename>.+)$",
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
                continue;
            }

            string hash = match.Groups["hash"].Value.ToLowerInvariant();
            string filename = match.Groups["filename"].Value.Trim();

            // Strip relative directory qualifiers like ./ or .\ if present
            if (filename.StartsWith("./", StringComparison.Ordinal) ||
                filename.StartsWith(".\\", StringComparison.Ordinal))
            {
                filename = filename.Substring(2);
            }

            // Normalize to leaf filename for lookup resilience
            string leafName = Path.GetFileName(filename);
            if (!string.IsNullOrWhiteSpace(leafName))
            {
                checksums[leafName] = hash;
            }
        }

        return new Sha256ChecksumFile(checksums);
    }

    public bool TryGetChecksum(string filename, [NotNullWhen(true)] out string? sha256)
    {
        sha256 = null;
        if (string.IsNullOrWhiteSpace(filename))
        {
            return false;
        }

        string leafName = Path.GetFileName(filename.Trim());
        return _checksums.TryGetValue(leafName, out sha256);
    }
}
