using System.IO;
using MyCapture.Core.Recording;

namespace MyCapture.Platform.Recording;

/// <summary>
/// File boundary for the shared Core MP4 track parser. Non-MP4 sources retain Media Foundation's
/// compressed-video fallback; MP4 headers use the same bounded parser as gallery import.
/// </summary>
internal static class MediaFoundationVideoTrackDuration
{
    internal static bool TryRead(string path, out double milliseconds)
    {
        // The source is the same caller-selected local file opened by the MF video reader.
        // codeql[cs/path-injection]
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        milliseconds = 0;
        Span<byte> signature = stackalloc byte[8];
        if (file.Length < signature.Length) return false;
        file.ReadExactly(signature);
        if (!signature[4..].SequenceEqual("ftyp"u8)) return false;
        double? duration = Mp4VideoTrackDuration.ReadMilliseconds(file);
        if (duration is not > 0) return false;
        milliseconds = duration.Value;
        return true;
    }
}
