namespace MyCapture.Core.Platform;

/// <summary>Identity from the shipped assembly, shared by window captions and the native shell.</summary>
public static class AppIdentity
{
    public static string Version { get; } = GetVersion(typeof(AppIdentity).Assembly.GetName().Version);
    public static string Label => "MyCapture-" + Version;

    public static string GetVersion(Version? version) => version is null
        ? "0.0.0"
        : $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";

    public static string FormatWindowTitle(string? context)
    {
        if (string.IsNullOrWhiteSpace(context) || context == "MyCapture") return Label;
        if (context == Label || context.StartsWith(Label + " — ", StringComparison.Ordinal)) return context;
        const string legacyPrefix = "MyCapture — ";
        if (context.StartsWith(legacyPrefix, StringComparison.Ordinal)) context = context[legacyPrefix.Length..];
        return Label + " — " + context;
    }
}
