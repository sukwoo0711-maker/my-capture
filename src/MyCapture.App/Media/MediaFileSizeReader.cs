using System.IO;
using System.Security;

namespace MyCapture.App.Media;

/// <summary>Optional file facts must never hold up an editor's first frame.</summary>
internal static class MediaFileSizeReader
{
    internal static Task<long?> ReadAsync(string? path, CancellationToken cancellationToken) =>
        ReadAsync(path, cancellationToken, static file => new FileInfo(file).Length);

    internal static Task<long?> ReadAsync(string? path, CancellationToken cancellationToken, Func<string, long> readLength)
    {
        if (string.IsNullOrWhiteSpace(path)) return Task.FromResult<long?>(null);
        return Task.Run<long?>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                long bytes = readLength(path);
                cancellationToken.ThrowIfCancellationRequested();
                return bytes >= 0 ? bytes : null;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException
                or ArgumentException or NotSupportedException or SecurityException)
            {
                return null;
            }
        }, cancellationToken);
    }
}
