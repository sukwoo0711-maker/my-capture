using System.Globalization;
using System.Resources;

namespace MyCapture.Core.Localization;

/// <summary>Explicit UI resources; persisted values and user content never pass through this provider.</summary>
public static class UiText
{
    private static readonly ResourceManager Resources = new("MyCapture.Core.Localization.Strings", typeof(UiText).Assembly);
    private static readonly AsyncLocal<CultureInfo?> ScopedCulture = new();
    private static readonly CultureInfo SystemCulture = CultureInfo.CurrentUICulture;
    private static CultureInfo _applicationCulture = ResolveCulture(null, SystemCulture);
    public static CultureInfo Culture => ScopedCulture.Value ?? _applicationCulture;

    public static CultureInfo ResolveCulture(string? preference, CultureInfo systemCulture)
    {
        string language = string.IsNullOrWhiteSpace(preference) ? systemCulture.TwoLetterISOLanguageName : preference.Trim().Split('-')[0];
        // Korean remains the neutral resource language. Unsupported OS/preferences use English.
        return CultureInfo.GetCultureInfo(language.Equals("ko", StringComparison.OrdinalIgnoreCase) ? "ko-KR" : "en");
    }

    public static void Configure(string? preference, CultureInfo? systemCulture = null)
    {
        _applicationCulture = ResolveCulture(preference, systemCulture ?? SystemCulture);
        CultureInfo.CurrentUICulture = _applicationCulture;
        CultureInfo.DefaultThreadCurrentUICulture = _applicationCulture;
    }

    public static string Get(string key) => Resources.GetString(key, Culture) ?? throw new MissingManifestResourceException($"Missing UI resource: {key}");

    /// <summary>
    /// Formats a translated composite string. A count may choose its noun with
    /// <c>{0:plural:item|items}</c>: the first form when the argument is exactly 1, the second
    /// otherwise. Korean has no plural, so only English resources use it.
    /// </summary>
    public static string Format(string key, params object?[] arguments)
    {
        CultureInfo culture = Culture;
        string format = Get(key);
        return format.Contains(PluralFormatter.Marker, StringComparison.Ordinal)
            ? string.Format(new PluralFormatter(culture), format, arguments)
            : string.Format(culture, format, arguments);
    }

    /// <summary>Formats every item as the culture would, except the <c>plural:</c> selectors.</summary>
    private sealed class PluralFormatter(CultureInfo culture) : IFormatProvider, ICustomFormatter
    {
        internal const string Marker = ":plural:";
        private const string Prefix = "plural:";

        public object? GetFormat(Type? formatType) => formatType == typeof(ICustomFormatter) ? this : culture.GetFormat(formatType);

        public string Format(string? format, object? arg, IFormatProvider? formatProvider)
        {
            if (format is not null && format.StartsWith(Prefix, StringComparison.Ordinal))
            {
                string[] forms = format[Prefix.Length..].Split('|');
                return forms.Length >= 2 && !IsOne(arg) ? forms[1] : forms[0];
            }

            return arg switch
            {
                null => string.Empty,
                IFormattable formattable => formattable.ToString(format, culture),
                _ => arg.ToString() ?? string.Empty,
            };
        }

        private static bool IsOne(object? value) => value switch
        {
            double number => number == 1,
            float number => number == 1,
            decimal number => number == 1,
            sbyte or byte or short or ushort or int or uint or long or ulong =>
                Convert.ToDecimal(value, CultureInfo.InvariantCulture) == 1m,
            _ => false,
        };
    }

    /// <summary>Context-local override for previews/tests; does not mutate another thread's language.</summary>
    public static IDisposable UseLanguage(string language)
    {
        CultureInfo? previous = ScopedCulture.Value;
        ScopedCulture.Value = ResolveCulture(language, CultureInfo.GetCultureInfo("ko-KR"));
        return new Scope(() => ScopedCulture.Value = previous);
    }
    private sealed class Scope(Action restore) : IDisposable
    {
        private Action? _restore = restore;
        public void Dispose() => Interlocked.Exchange(ref _restore, null)?.Invoke();
    }
}
