using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;

namespace MyCapture.App.Pinning;

/// <summary>
/// Reads the language that Visual Studio Code, and editors built on it, attach to a copy.
/// </summary>
/// <remarks>
/// VS Code publishes its copied text as code HTML without any language marker, so the HTML alone
/// cannot tell a Markdown file from source code. Chromium stores non-standard DataTransfer types
/// in one pickled clipboard format; VS Code's <c>vscode-editor-data</c> entry there names the
/// language of the copied text in <c>mode</c>, for example <c>markdown</c> or <c>c</c>.
/// </remarks>
internal static class ClipboardEditorLanguage
{
    internal const string ChromiumCustomDataFormat = "Chromium Web Custom MIME Data Format";
    internal const string VsCodeEditorDataType = "vscode-editor-data";
    private const int MaxPickleBytes = 1 << 20;
    private const int MaxEntries = 256;
    private const int MaxEditorDataCharacters = 16_384;

    /// <summary>Returns the copied editor language, or null when the copy does not name one.</summary>
    internal static string? TryRead(IDataObject data)
    {
        ArgumentNullException.ThrowIfNull(data);
        try
        {
            if (!data.GetDataPresent(ChromiumCustomDataFormat, autoConvert: false)) return null;
            byte[]? pickle = data.GetData(ChromiumCustomDataFormat, autoConvert: false) switch
            {
                byte[] bytes => bytes,
                Stream stream => ReadBounded(stream),
                _ => null,
            };
            return pickle is null ? null : TryParseVsCodeMode(pickle);
        }
        catch (Exception ex) when (ex is ArgumentException
                                   or FormatException
                                   or InvalidOperationException
                                   or NotSupportedException
                                   or IOException
                                   or ExternalException)
        {
            // The language is an optional hint. A producer that fails to render it must not hide
            // the text, HTML or picture of the same copy.
            return null;
        }
    }

    /// <summary>Reads <c>mode</c> from the <c>vscode-editor-data</c> entry of a Chromium custom-data pickle.</summary>
    internal static string? TryParseVsCodeMode(ReadOnlySpan<byte> pickle)
    {
        string? editorData = TryFindEntry(pickle, VsCodeEditorDataType);
        if (editorData is null) return null;
        try
        {
            using JsonDocument json = JsonDocument.Parse(editorData, new JsonDocumentOptions { MaxDepth = 8 });
            return json.RootElement.ValueKind == JsonValueKind.Object
                   && json.RootElement.TryGetProperty("mode", out JsonElement mode)
                   && mode.ValueKind == JsonValueKind.String
                ? mode.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static byte[]? ReadBounded(Stream stream)
    {
        if (stream.CanSeek)
        {
            if (stream.Length > MaxPickleBytes) return null;
            stream.Position = 0;
        }
        using var detached = new MemoryStream();
        byte[] buffer = new byte[4096];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (detached.Length + read > MaxPickleBytes) return null;
            detached.Write(buffer, 0, read);
        }
        return detached.ToArray();
    }

    /// <summary>
    /// base::Pickle layout written by Chromium's custom-data writer: a uint32 payload size, then
    /// a uint32 entry count and, per entry, a type and a value as int32 UTF-16 length plus
    /// characters, each field padded to four bytes.
    /// </summary>
    private static string? TryFindEntry(ReadOnlySpan<byte> pickle, string type)
    {
        if (pickle.Length < 8 || pickle.Length > MaxPickleBytes) return null;
        uint payloadSize = BinaryPrimitives.ReadUInt32LittleEndian(pickle);
        if (payloadSize > (uint)(pickle.Length - 4)) return null;
        ReadOnlySpan<byte> payload = pickle.Slice(4, (int)payloadSize);
        int offset = 0;
        if (!TryReadUInt32(payload, ref offset, out uint count) || count > MaxEntries) return null;
        for (uint index = 0; index < count; index++)
        {
            if (!TryReadString16(payload, ref offset, out ReadOnlySpan<byte> name)
                || !TryReadString16(payload, ref offset, out ReadOnlySpan<byte> value))
            {
                return null;
            }
            if (name.Length == type.Length * 2 && Encoding.Unicode.GetString(name) == type)
            {
                return value.Length / 2 > MaxEditorDataCharacters ? null : Encoding.Unicode.GetString(value);
            }
        }
        return null;
    }

    private static bool TryReadUInt32(ReadOnlySpan<byte> payload, ref int offset, out uint value)
    {
        value = 0;
        if (payload.Length - offset < 4) return false;
        value = BinaryPrimitives.ReadUInt32LittleEndian(payload[offset..]);
        offset += 4;
        return true;
    }

    private static bool TryReadString16(ReadOnlySpan<byte> payload, ref int offset, out ReadOnlySpan<byte> value)
    {
        value = default;
        if (!TryReadUInt32(payload, ref offset, out uint characters)) return false;
        long bytes = characters * 2L;
        if (bytes > payload.Length - offset) return false;
        value = payload.Slice(offset, (int)bytes);
        offset = (int)Math.Min(payload.Length, offset + ((bytes + 3) & ~3L));
        return true;
    }
}
