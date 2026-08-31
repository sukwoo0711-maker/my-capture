using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MyCapture.Core.Primitives;

/// <summary>
/// A straight-alpha 8-bit-per-channel colour.
/// </summary>
/// <remarks>
/// Serialises as <c>"#AARRGGBB"</c> via <see cref="ColorRgbaJsonConverter"/> rather
/// than as an object graph. Annotation files are inspected by hand during support
/// and diffed in version control during development, and four numeric properties
/// per colour makes both unreadable.
/// </remarks>
[JsonConverter(typeof(ColorRgbaJsonConverter))]
public readonly record struct ColorRgba(byte A, byte R, byte G, byte B)
{
    public static ColorRgba Transparent => new(0, 0, 0, 0);
    public static ColorRgba Black => new(255, 0, 0, 0);
    public static ColorRgba White => new(255, 255, 255, 255);

    public static ColorRgba FromRgb(byte r, byte g, byte b) => new(255, r, g, b);

    public ColorRgba WithAlpha(byte alpha) => this with { A = alpha };

    /// <summary>
    /// Multiplies the existing alpha by <paramref name="factor"/> (0..1).
    /// </summary>
    public ColorRgba Fade(double factor) =>
        this with { A = (byte)Math.Clamp(Math.Round(A * factor), 0, 255) };

    public string ToHex() =>
        string.Create(CultureInfo.InvariantCulture, $"#{A:X2}{R:X2}{G:X2}{B:X2}");

    /// <summary>
    /// Parses <c>#AARRGGBB</c>, <c>#RRGGBB</c>, <c>#ARGB</c> or <c>#RGB</c>.
    /// </summary>
    /// <remarks>
    /// The short forms exist because settings files are hand-edited; a user typing
    /// <c>#f00</c> should not silently fall back to black.
    /// </remarks>
    public static bool TryParse(string? text, out ColorRgba color)
    {
        color = default;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        ReadOnlySpan<char> s = text.AsSpan().Trim();
        if (s.Length > 0 && s[0] == '#')
        {
            s = s[1..];
        }

        static bool Hex(ReadOnlySpan<char> span, out byte value)
        {
            value = 0;
            return byte.TryParse(span, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        }

        switch (s.Length)
        {
            case 3:
                {
                    // #RGB -> each nibble duplicated, matching CSS semantics.
                    if (!Hex(s[..1], out byte r3) || !Hex(s[1..2], out byte g3) || !Hex(s[2..3], out byte b3))
                    {
                        return false;
                    }

                    color = new ColorRgba(255, (byte)(r3 * 17), (byte)(g3 * 17), (byte)(b3 * 17));
                    return true;
                }

            case 4:
                {
                    if (!Hex(s[..1], out byte a4) || !Hex(s[1..2], out byte r4) ||
                        !Hex(s[2..3], out byte g4) || !Hex(s[3..4], out byte b4))
                    {
                        return false;
                    }

                    color = new ColorRgba((byte)(a4 * 17), (byte)(r4 * 17), (byte)(g4 * 17), (byte)(b4 * 17));
                    return true;
                }

            case 6:
                {
                    if (!Hex(s[..2], out byte r6) || !Hex(s[2..4], out byte g6) || !Hex(s[4..6], out byte b6))
                    {
                        return false;
                    }

                    color = new ColorRgba(255, r6, g6, b6);
                    return true;
                }

            case 8:
                {
                    if (!Hex(s[..2], out byte a8) || !Hex(s[2..4], out byte r8) ||
                        !Hex(s[4..6], out byte g8) || !Hex(s[6..8], out byte b8))
                    {
                        return false;
                    }

                    color = new ColorRgba(a8, r8, g8, b8);
                    return true;
                }

            default:
                return false;
        }
    }

    public static ColorRgba Parse(string text) =>
        TryParse(text, out ColorRgba c)
            ? c
            : throw new FormatException($"'{text}' is not a recognised colour.");

    public override string ToString() => ToHex();
}

/// <summary>
/// Reads and writes <see cref="ColorRgba"/> as a hex string.
/// </summary>
public sealed class ColorRgbaJsonConverter : JsonConverter<ColorRgba>
{
    public override ColorRgba Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException($"Expected a colour string but found {reader.TokenType}.");
        }

        string? text = reader.GetString();
        if (!ColorRgba.TryParse(text, out ColorRgba color))
        {
            throw new JsonException($"'{text}' is not a recognised colour.");
        }

        return color;
    }

    public override void Write(Utf8JsonWriter writer, ColorRgba value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.ToHex());
    }
}
