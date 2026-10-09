namespace MyCapture.Core.Platform;

/// <summary>Identity from the shipped assembly, shared by window captions and the native shell.</summary>
public static class AppIdentity
{
    public static string Version { get; } = GetVersion(typeof(AppIdentity).Assembly.GetName().Version);
    public static string Label => "MyCapture-" + Version;
    public const string WindowLabel = "MyCapture";

    public static string GetVersion(Version? version) => version is null
        ? "0.0.0"
        : $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";

    public static string FormatWindowTitle(string? context)
    {
        if (string.IsNullOrWhiteSpace(context) || context == WindowLabel || context == Label) return WindowLabel;
        const string prefix = WindowLabel + " — ";
        if (context.StartsWith(prefix, StringComparison.Ordinal)) return context;
        if (context.StartsWith(Label + " — ", StringComparison.Ordinal)) context = context[(Label.Length + 3)..];
        return prefix + context;
    }
}
