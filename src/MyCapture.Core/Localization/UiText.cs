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
    public static string Format(string key, params object?[] arguments) => string.Format(Culture, Get(key), arguments);

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
