using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media.Imaging;
using MyCapture.App;
using MyCapture.App.Pinning;

namespace MyCapture.Tests;

/// <summary>
/// Process-wide clipboard isolation for tests and tutorial rendering. Keep only the latest
/// write so image-heavy suites do not retain every copied bitmap. Inspect this snapshot
/// instead of reading the user's clipboard.
/// </summary>
internal static class RecordingClipboard
{
    private static readonly object Gate = new();
    private static ClipboardWrite? _lastWrite;
    private static long _writeCount;

    internal static ClipboardWrite? LastWrite { get { lock (Gate) return _lastWrite; } }
    internal static long WriteCount { get { lock (Gate) return _writeCount; } }

    [ModuleInitializer]
    internal static void Initialize()
    {
        ClipboardWriter.InstallTestOverride(RecordDataObject, RecordText, GetVersion, TryRecordTextAtVersion);
        ClipboardAvailability.InstallTestOverride(HasPinnableContent);
    }

    private static bool HasPinnableContent()
    {
        lock (Gate)
            return _lastWrite is { } write && write.Formats.Keys.Any(format =>
                format is "PNG" || format == DataFormats.Bitmap || format == DataFormats.Dib
                || format == DataFormats.UnicodeText || format == DataFormats.Text);
    }

    private static void RecordDataObject(IDataObject data, bool copy)
    {
        var formats = new Dictionary<string, object?>(StringComparer.Ordinal);
        // Materialize on the calling STA before ClipboardImageService disposes its PNG stream.
        foreach (string format in data.GetFormats(autoConvert: false))
        {
            object? value = data.GetData(format, autoConvert: false);
            formats.Add(format, Snapshot(value));
        }
        Record(new ClipboardWrite(new ReadOnlyDictionary<string, object?>(formats), copy));
    }

    private static object? Snapshot(object? value)
    {
        if (value is MemoryStream memory) return memory.ToArray();
        if (value is Stream stream)
        {
            long? position = stream.CanSeek ? stream.Position : null;
            using var snapshot = new MemoryStream();
            try
            {
                if (position is not null) stream.Position = 0;
                stream.CopyTo(snapshot);
                return snapshot.ToArray();
            }
            finally
            {
                if (position is not null) stream.Position = position.Value;
            }
        }
        if (value is BitmapSource bitmap && !bitmap.IsFrozen)
        {
            BitmapSource frozen = bitmap.Clone();
            frozen.Freeze();
            return frozen;
        }
        return value;
    }

    private static void RecordText(string text) => Record(TextWrite(text));

    private static ClipboardWrite TextWrite(string text) => new(
        new ReadOnlyDictionary<string, object?>(new Dictionary<string, object?>
        {
            [DataFormats.UnicodeText] = text,
        }), Copy: null);

    private static uint GetVersion() { lock (Gate) return unchecked((uint)_writeCount); }

    private static bool TryRecordTextAtVersion(string text, uint expectedVersion)
    {
        lock (Gate)
        {
            if (unchecked((uint)_writeCount) != expectedVersion) return false;
            Record(TextWrite(text));
            return true;
        }
    }

    private static void Record(ClipboardWrite write)
    {
        lock (Gate)
        {
            _lastWrite = write;
            _writeCount++;
        }
    }

    internal sealed record ClipboardWrite(IReadOnlyDictionary<string, object?> Formats, bool? Copy);
}
