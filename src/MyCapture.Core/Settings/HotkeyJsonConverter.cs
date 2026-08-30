using System.Text.Json;
using System.Text.Json.Serialization;

namespace MyCapture.Core.Settings;

/// <summary>
/// Reads and writes <see cref="Hotkey"/> as <c>"Ctrl+Shift+C"</c>.
/// </summary>
/// <remarks>
/// An unparseable value falls back to <see cref="Hotkey.None"/> instead of throwing.
/// A typo in a hand-edited settings file should cost the user one hotkey, not the
/// entire settings file — and the settings store reports the problem so the UI can
/// surface it.
/// </remarks>
public sealed class HotkeyJsonConverter : JsonConverter<Hotkey>
{
    public override Hotkey Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return Hotkey.None;
        }

        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException($"Expected a hotkey string but found {reader.TokenType}.");
        }

        string? text = reader.GetString();
        return Hotkey.TryParse(text, out Hotkey hotkey) ? hotkey : Hotkey.None;
    }

    public override void Write(Utf8JsonWriter writer, Hotkey value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);
        writer.WriteStringValue(value.ToString());
    }
}
