using System.Globalization;
using System.IO;

namespace MyCapture.App.Diagnostics;

/// <summary>Pure parsing for the explicit recording-audio diagnostic; Seconds excludes pauses.</summary>
internal sealed record RecordingAudioSelfTestOptions(
    string OutputDirectory,
    double Seconds,
    bool SystemAudio,
    bool Microphone,
    double? PauseAtSeconds,
    double PauseSeconds,
    bool Tone)
{
    internal const string CommandLineSwitch = "--selftest-recording-audio";

    internal static RecordingAudioSelfTestOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Any(value => string.Equals(value, "--selftest-resident", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Recording-audio and resident diagnostics cannot be combined.", nameof(args));
        if (args.Length < 2 || !string.Equals(args[0], CommandLineSwitch, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(args[1]) || args[1].StartsWith('-'))
            throw new ArgumentException($"Expected {CommandLineSwitch} <output-directory> followed by options.", nameof(args));

        string output = NormalizeRoot(args[1]);
        double seconds = 10, pauseFor = 0;
        double? pauseAt = null;
        bool noAudio = false, microphone = false, tone = false;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int index = 2; index < args.Length; index++)
        {
            string option = args[index];
            if (string.IsNullOrWhiteSpace(option) || !seen.Add(option))
                throw new ArgumentException("Diagnostic options must be nonempty and appear only once.", nameof(args));
            switch (option.ToLowerInvariant())
            {
                case "--seconds": seconds = ReadNumber(args, ref index); break;
                case "--pause-at": pauseAt = ReadNumber(args, ref index); break;
                case "--pause-for": pauseFor = ReadNumber(args, ref index); break;
                case "--no-audio": noAudio = true; break;
                case "--microphone": microphone = true; break;
                case "--tone": tone = true; break;
                default: throw new ArgumentException($"Unknown recording-audio option: {option}", nameof(args));
            }
        }

        if (seconds is < 1 or > 86_400)
            throw new ArgumentException("Active recording seconds must be between 1 and 86400.", nameof(args));
        if (seen.Contains("--pause-at") != seen.Contains("--pause-for"))
            throw new ArgumentException("--pause-at and --pause-for must be specified together.", nameof(args));
        if (pauseAt is { } at && (at <= 0 || at >= seconds || pauseFor <= 0 || pauseFor > 3_600))
            throw new ArgumentException("Pause must start within active recording time and last more than 0 and at most 3600 seconds.", nameof(args));
        if (noAudio && (microphone || tone))
            throw new ArgumentException("--no-audio cannot be combined with --microphone or --tone.", nameof(args));

        return new(output, seconds, !noAudio, microphone, pauseAt, pauseFor, tone);
    }

    /// <summary>
    /// Rejects lexical overlap without reading or creating either directory. The caller must
    /// still use DiagnosticOutputPaths.Create for its existing local-drive and no-link checks.
    /// </summary>
    internal static string ValidateIsolatedRoot(string output, string realProfile)
    {
        string destination = NormalizeRoot(output);
        string profile = NormalizeRoot(realProfile);
        if (IsWithin(destination, profile) || IsWithin(profile, destination))
            throw new ArgumentException("Diagnostic output must be separate from the real profile and its ancestors.", nameof(output));
        return destination;
    }

    private static bool IsWithin(string path, string parent) =>
        string.Equals(path, parent, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    private static string NormalizeRoot(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!string.Equals(path, path.Trim(), StringComparison.Ordinal))
            throw new ArgumentException("Diagnostic paths cannot have leading or trailing whitespace.", nameof(path));
        string normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (normalized.StartsWith(@"\\", StringComparison.Ordinal))
            throw new ArgumentException("Diagnostic paths must use a local drive path without a device prefix.", nameof(path));
        string tail = normalized[Path.GetPathRoot(normalized)!.Length..];
        foreach (string component in tail.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (component.EndsWith(' ') || component.EndsWith('.')
                || component.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new ArgumentException("Diagnostic paths must contain unambiguous directory names.", nameof(path));
        }
        return normalized;
    }

    private static double ReadNumber(string[] args, ref int index)
    {
        string option = args[index];
        if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index])
            || !string.Equals(args[index], args[index].Trim(), StringComparison.Ordinal)
            || !double.TryParse(args[index], NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent,
                CultureInfo.InvariantCulture, out double number) || !double.IsFinite(number))
            throw new ArgumentException($"{option} requires one finite number using a decimal point.", nameof(args));
        return number;
    }
}
