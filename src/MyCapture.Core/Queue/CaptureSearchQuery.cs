using System.Globalization;
using System.Text;
using MyCapture.Core.Capture;

namespace MyCapture.Core.Queue;

public enum CaptureSearchField
{
    Any, App, Title, Tag, Text, Url, Folder, Type, Pinned, Date, Host,
}

public sealed record CaptureSearchToken(CaptureSearchField Field, string Value, bool IsNegated,
    int Start, int Length);

/// <summary>A prepared query whose source spans also support removing individual conditions.</summary>
public sealed class CaptureSearchQuery
{
    private readonly CaptureSearchToken[] _tokens;
    // Normalized once per query: a host condition compares against every record.
    private readonly string?[] _hosts;

    private CaptureSearchQuery(string text, CaptureSearchToken[] tokens)
    {
        Text = text;
        _tokens = tokens;
        _hosts = Array.ConvertAll(tokens, static token =>
            token.Field == CaptureSearchField.Host ? NormalizeRequestedHost(token.Value) : null);
        Tokens = Array.AsReadOnly(tokens);
    }

    public string Text { get; }
    public IReadOnlyList<CaptureSearchToken> Tokens { get; }

    public static CaptureSearchQuery Parse(string? query)
    {
        string text = query ?? string.Empty;
        var tokens = new List<CaptureSearchToken>();
        for (int i = 0; i < text.Length;)
        {
            if (char.IsWhiteSpace(text[i])) { i++; continue; }
            int start = i;
            bool negated = text[i] == '-' && i + 1 < text.Length && !char.IsWhiteSpace(text[i + 1]);
            if (negated) i++;
            var value = new StringBuilder();
            bool quoted = false;
            int colon = -1;
            while (i < text.Length && (quoted || !char.IsWhiteSpace(text[i])))
            {
                char character = text[i++];
                if (character == '"') { quoted = !quoted; continue; }
                if (character == ':' && !quoted && colon < 0) colon = value.Length;
                value.Append(character);
            }
            string term = value.ToString();
            CaptureSearchField field = colon > 0 ? ParseField(term[..colon]) : CaptureSearchField.Any;
            tokens.Add(new CaptureSearchToken(field,
                field == CaptureSearchField.Any ? term : term[(colon + 1)..], negated, start, i - start));
        }
        return new CaptureSearchQuery(text, tokens.ToArray());
    }

    public string RemoveToken(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _tokens.Length);
        CaptureSearchToken token = _tokens[index];
        return Text.Remove(token.Start, token.Length).Trim();
    }

    public bool IsMatch(CaptureRecord record, string? folderPath = null, string? exportFileName = null) =>
        Match(record, folderPath, exportFileName, collectFields: false, out _);

    public bool TryMatch(CaptureRecord record, out CaptureMatchField fields, string? folderPath = null,
        string? exportFileName = null) =>
        Match(record, folderPath, exportFileName, collectFields: true, out fields);

    private bool Match(CaptureRecord record, string? folderPath, string? exportFileName, bool collectFields,
        out CaptureMatchField fields)
    {
        ArgumentNullException.ThrowIfNull(record);
        fields = CaptureMatchField.None;
        for (int index = 0; index < _tokens.Length; index++)
        {
            CaptureSearchToken token = _tokens[index];
            CaptureMatchField matched = token.Field == CaptureSearchField.Host
                ? MatchesHost(record.SourcePageUrl, _hosts[index]) ? CaptureMatchField.SourcePageUrl : CaptureMatchField.None
                : MatchToken(record, token, folderPath, exportFileName, collectFields && !token.IsNegated);
            if (token.IsNegated ? matched != CaptureMatchField.None : matched == CaptureMatchField.None)
            {
                fields = CaptureMatchField.None;
                return false;
            }
            if (!token.IsNegated) fields |= matched;
        }
        return true;
    }

    private static CaptureMatchField MatchToken(CaptureRecord record, CaptureSearchToken token,
        string? folderPath, string? exportFileName, bool collectFields)
    {
        string value = token.Value;
        if (value.Length == 0) return CaptureMatchField.None;
        if (token.Field == CaptureSearchField.Type)
        {
            bool match = value.Equals("image", StringComparison.OrdinalIgnoreCase) || value == "이미지"
                ? record.IsImage
                : (value.Equals("video", StringComparison.OrdinalIgnoreCase) || value == "동영상") && record.IsVideo;
            return match ? CaptureMatchField.MediaType : CaptureMatchField.None;
        }
        if (token.Field == CaptureSearchField.Pinned)
        {
            bool? pinned = value.Equals("yes", StringComparison.OrdinalIgnoreCase) || value == "예" ? true
                : value.Equals("no", StringComparison.OrdinalIgnoreCase) || value == "아니오" ? false : null;
            return pinned == record.IsManuallyPinned ? CaptureMatchField.Pinned : CaptureMatchField.None;
        }
        if (token.Field == CaptureSearchField.Date)
        {
            string format = value.Length == 7 ? "yyyy-MM" : "yyyy-MM-dd";
            bool valid = DateOnly.TryParseExact(value, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
            return valid && record.CreatedAt.ToString(format, CultureInfo.InvariantCulture) == value
                ? CaptureMatchField.Date : CaptureMatchField.None;
        }

        CaptureMatchField fields = CaptureMatchField.None;
        bool any = token.Field == CaptureSearchField.Any;
        if ((any || token.Field == CaptureSearchField.Title) && Contains(record.Title, value))
            fields |= CaptureMatchField.Title;
        if (!collectFields && fields != CaptureMatchField.None) return fields;
        if ((any || token.Field == CaptureSearchField.Title) && Contains(record.SourceWindowTitle, value))
            fields |= CaptureMatchField.WindowTitle;
        if (!collectFields && fields != CaptureMatchField.None) return fields;
        if (any && Contains(record.IsVideo ? "동영상 비디오 video recording mp4" : "이미지 사진 스크린샷 image screenshot png", value))
            fields |= CaptureMatchField.MediaType;
        if ((any || token.Field == CaptureSearchField.Tag) && Contains(record.Tags, value))
            fields |= CaptureMatchField.Tags;
        if (!collectFields && fields != CaptureMatchField.None) return fields;
        if ((any || token.Field == CaptureSearchField.Url) && Contains(record.SourcePageUrl, value))
            fields |= CaptureMatchField.SourcePageUrl;
        if ((any || token.Field == CaptureSearchField.Folder) && Contains(record.SourceDocumentPath, value))
            fields |= CaptureMatchField.SourceDocumentPath;
        if (token.Field == CaptureSearchField.Folder && Contains(folderPath, value))
            fields |= CaptureMatchField.Folder;
        if (!collectFields && fields != CaptureMatchField.None) return fields;
        if ((any || token.Field == CaptureSearchField.App) && Contains(CaptureTextSearch.SourceAppText(record), value))
            fields |= CaptureMatchField.SourceApp;
        if (!collectFields && fields != CaptureMatchField.None) return fields;
        if ((any || token.Field == CaptureSearchField.Text) && Contains(record.OcrText, value))
            fields |= CaptureMatchField.OcrText;
        if (any && Contains(exportFileName, value))
            fields |= CaptureMatchField.ExportFileName;
        return fields;
    }

    private static bool Contains(string? text, string value) =>
        !string.IsNullOrEmpty(text) && text.Contains(value, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The site condition generated by "show captures from this source": the URL's host must
    /// equal the requested host after IDN normalization, case folding and removing one leading
    /// "www.". Subdomains, ports, paths and query text never widen the match.
    /// </summary>
    private static bool MatchesHost(string? url, string? requestedHost) =>
        requestedHost is not null
        && Uri.TryCreate(url, UriKind.Absolute, out Uri? source)
        && (source.Scheme == Uri.UriSchemeHttp || source.Scheme == Uri.UriSchemeHttps)
        && CaptureProvenance.NormalizeHost(source).Equals(requestedHost, StringComparison.Ordinal);

    private static string? NormalizeRequestedHost(string value)
    {
        UriHostNameType hostType = Uri.CheckHostName(value);
        if (hostType == UriHostNameType.Unknown) return null;
        string host = hostType == UriHostNameType.IPv6 && !value.StartsWith('[') ? "[" + value + "]" : value;
        return Uri.TryCreate("https://" + host, UriKind.Absolute, out Uri? requested)
            ? CaptureProvenance.NormalizeHost(requested) : null;
    }

    private static CaptureSearchField ParseField(string key) => key.ToLowerInvariant() switch
    {
        "app" or "앱" => CaptureSearchField.App,
        "title" or "제목" => CaptureSearchField.Title,
        "tag" or "태그" => CaptureSearchField.Tag,
        "text" or "글자" => CaptureSearchField.Text,
        "url" or "주소" => CaptureSearchField.Url,
        // The condition chip reads "사이트"/"Site"; "host" is the generated, exact form.
        "host" or "site" or "사이트" or "호스트" => CaptureSearchField.Host,
        "folder" or "폴더" => CaptureSearchField.Folder,
        "type" or "종류" => CaptureSearchField.Type,
        "pinned" or "보관" => CaptureSearchField.Pinned,
        "date" or "날짜" => CaptureSearchField.Date,
        _ => CaptureSearchField.Any,
    };
}
