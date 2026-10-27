using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using MyCapture.Core.Platform;

namespace MyCapture.App.Diagnostics;

/// <summary>
/// The only shape a file log line may take: time, level, numbers and identifiers from code.
/// Never the formatted message, argument values, exception text, stack, user input or paths.
/// </summary>
/// <remarks>
/// Every word after the level is a fixed key or a value that passes <see cref="Token"/>: the logger
/// category (a type name), the exception type and the method that threw. Nothing in the codebase
/// sets an event ID, so the message template, a constant in source, is kept as an eight-digit
/// SHA-256 prefix (msg=) that matches a line to its call site without writing the template's words.
/// </remarks>
internal static partial class SafeLogLine
{
    /// <summary>Stands in for a missing value or one that is not a plain identifier.</summary>
    internal const string Unknown = "-";

    private const string TokenCharacters = @"[A-Za-z0-9_.+`<>-]";

    [GeneratedRegex(@"\A" + TokenCharacters + @"{1,200}\z", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();

    // The bundle re-checks every line against this grammar, so a line from anywhere else is dropped.
    [GeneratedRegex(@"\A\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z (?:TRC|DBG|INF|WRN|ERR|CRT)(?: [a-z-]{1,20}=" +
        TokenCharacters + "{1,200}| " + TokenCharacters + @"{1,200}){1,16}\z", RegexOptions.CultureInvariant)]
    private static partial Regex LinePattern();

    internal static string Token(string? value) =>
        value is not null && TokenPattern().IsMatch(value) ? value : Unknown;

    internal static bool IsWellFormed(string line) => LinePattern().IsMatch(line);

    internal static string Format(DateTimeOffset utc, LogLevel level, string category, int eventId,
        string? template, Exception? exception)
    {
        var line = new StringBuilder(160);
        Begin(line, utc, level).Append(' ').Append(Token(category))
            .Append(" event=").Append(eventId.ToString(CultureInfo.InvariantCulture))
            .Append(" msg=").Append(template is null ? Unknown : TemplateId(template));
        if (exception is not null)
        {
            AppendException(line, "ex", "hresult", exception);
            line.Append(" site=").Append(Token(Site(exception)));
            Exception root = exception.GetBaseException();
            if (!ReferenceEquals(root, exception)) AppendException(line, "base", "base-hresult", root);
        }
        return line.ToString();
    }

    /// <summary>The first line of each process: which build (with its commit) on which Windows.</summary>
    internal static string Session(DateTimeOffset utc)
    {
        Assembly app = typeof(SafeLogLine).Assembly;
        return Session(utc, app.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? app.GetName().Version?.ToString() ?? string.Empty, Environment.OSVersion.Version.ToString(), AppPackage.IsPackaged);
    }

    internal static string Session(DateTimeOffset utc, string version, string windowsVersion, bool packaged) =>
        Begin(new StringBuilder(120), utc, LogLevel.Information)
            .Append(" session version=").Append(Token(version))
            .Append(" windows=").Append(Token(windowsVersion))
            .Append(" packaged=").Append(packaged ? "true" : "false").ToString();

    /// <summary>Lines lost to a full queue or an unwritable folder, reported once writing resumes.</summary>
    internal static string Dropped(DateTimeOffset utc, long count) =>
        Begin(new StringBuilder(64), utc, LogLevel.Warning)
            .Append(" log dropped=").Append(count.ToString(CultureInfo.InvariantCulture)).ToString();

    internal static string LaunchActivation(DateTimeOffset utc, LaunchActivationKind kind) =>
        Begin(new StringBuilder(96), utc, LogLevel.Information).Append(" launch activation=")
            .Append(kind switch
            {
                LaunchActivationKind.Regular => "regular",
                LaunchActivationKind.Background => "background",
                LaunchActivationKind.StartupTask => "startup-task",
                LaunchActivationKind.Secondary => "secondary",
                _ => Unknown,
            }).ToString();

    internal static string LaunchFirstVisible(DateTimeOffset utc, LaunchVisibleKind kind, long milliseconds) =>
        Begin(new StringBuilder(96), utc, LogLevel.Information).Append(" launch first-visible=")
            .Append(kind switch
            {
                LaunchVisibleKind.Starting => "starting",
                LaunchVisibleKind.SecondaryNotice => "secondary-notice",
                LaunchVisibleKind.Presentation => "presentation",
                _ => Unknown,
            }).Append(" elapsed-ms=").Append(Math.Max(0, milliseconds).ToString(CultureInfo.InvariantCulture)).ToString();

    /// <summary>Eight lowercase hex digits of the template's SHA-256; stable across builds and machines.</summary>
    internal static string TemplateId(string template) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(template)).AsSpan(0, 4));

    /// <summary>
    /// The message template of a structured log state, read without running the formatter.
    /// Argument values in the same state are never touched.
    /// </summary>
    internal static string? TemplateOf<TState>(TState state)
    {
        try
        {
            if (state is IReadOnlyList<KeyValuePair<string, object?>> { Count: > 0 } values
                && values[^1] is { Key: "{OriginalFormat}", Value: string template })
                return template;
        }
        catch (Exception)
        {
            // Logging never throws: a custom state that cannot report its template is logged without one.
        }
        return null;
    }

    private static StringBuilder Begin(StringBuilder line, DateTimeOffset utc, LogLevel level) =>
        line.Append(utc.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture))
            .Append(' ').Append(Level(level));

    private static void AppendException(StringBuilder line, string typeKey, string hresultKey, Exception exception) =>
        line.Append(' ').Append(typeKey).Append('=').Append(Token(exception.GetType().FullName))
            .Append(' ').Append(hresultKey).Append("=0x")
            .Append(exception.HResult.ToString("X8", CultureInfo.InvariantCulture));

    private static string? Site(Exception exception)
    {
        try
        {
            MethodBase? method = exception.TargetSite;
            return method?.DeclaringType?.FullName is { } type ? type + "." + method.Name : method?.Name;
        }
        catch (Exception)
        {
            // Missing reflection metadata leaves the site unknown rather than losing the line.
            return null;
        }
    }

    private static string Level(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        _ => "CRT",
    };
}
