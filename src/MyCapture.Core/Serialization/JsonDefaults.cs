using System.Text.Json;
using System.Text.Json.Serialization;

namespace MyCapture.Core.Serialization;

/// <summary>
/// Shared <see cref="JsonSerializerOptions"/> for every file this app writes.
/// </summary>
/// <remarks>
/// <para>
/// Centralised because the settings file, the capture index and annotation layer
/// files are all read back by later versions of the app and by humans during
/// support. Letting each store configure its own options is how one of them ends up
/// writing a subtly different shape that the next version cannot read.
/// </para>
/// <para>
/// camelCase is not cosmetic here. The settings file is documented as user-editable,
/// and camelCase is what anyone editing a JSON config expects to type. Case
/// insensitivity is enabled on top so a file written by hand in PascalCase still
/// loads rather than silently reverting every value to its default — a failure mode
/// that looks exactly like the app ignoring the user's settings.
/// </para>
/// </remarks>
public static class JsonDefaults
{
    /// <summary>
    /// Compact output. Used for machine-managed files.
    /// </summary>
    public static JsonSerializerOptions Compact { get; } = Create(indented: false);

    /// <summary>
    /// Indented output. Used for files a human is expected to open.
    /// </summary>
    public static JsonSerializerOptions Readable { get; } = Create(indented: true);

    private static JsonSerializerOptions Create(bool indented)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = indented,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,

            // Both are tolerated because these files are hand-edited. Rejecting a
            // trailing comma would turn a harmless typo into "the app lost my
            // settings".
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,

            // Enums are written as names. A numeric enum in a config file is
            // unreadable, and renumbering an enum would silently change behaviour.
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        };

        // populateMissingResolver: true installs the reflection-based resolver.
        // Freezing without it throws on .NET 8+, and freezing at all is worthwhile
        // because these instances are shared static state: a caller mutating them
        // later would silently change the on-disk format for every store.
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
