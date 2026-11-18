using System.Globalization;
using System.Text.RegularExpressions;

namespace MyCapture.Ocr.Translation;

/// <summary>
/// The same value written another way on the other side of a translation: "nine" and "9시", "November" and
/// "11월", "2:30 PM" and "오후 2시 30분", "U.S." and "미국". Each pair is taken from what is actually present in
/// both texts, in either direction, and every mention is used once, so two "two"s need two renderings and a
/// rendering of one value cannot also stand for another. Anything not listed here (code, paths, versions,
/// numbers with units, identifiers, developer acronyms) is compared exactly by <see cref="ProtectedTokens"/>.
/// Developer notations that look like everyday values stay exact: a bare "15:10" in the source is a time only
/// after a word such as "at" or next to another time, "Dec" is a month only next to a day or a year, the listed
/// acronyms count only in a sentence, and "OK" counts as 확인 only on a button and never as a status.
/// </summary>
internal sealed partial class ValueEquivalence
{
    private const char MaskCharacter = '￼';

    private enum Form
    {
        Digit,
        Word,
        Korean,
    }

    private readonly record struct Mention(int Start, int Length, int Value, Form Form)
    {
        internal int End => Start + Length;
    }

    // Per character: 0, or the number of the pairing (from 1) whose mention covers it.
    private readonly int[] _source;
    private readonly int[] _output;
    private int _pairs;

    private ValueEquivalence(int sourceLength, int outputLength)
    {
        _source = new int[sourceLength];
        _output = new int[outputLength];
    }

    /// <summary>True when nothing on either side was paired.</summary>
    internal bool IsEmpty => _pairs == 0;

    /// <summary>
    /// Pairs equivalent mentions of <paramref name="source"/> and <paramref name="output"/>: clock times first,
    /// then months, then the listed acronyms, then counts. Spans already found by <see cref="ProtectedTokens.Find"/>
    /// can be passed in so the texts are not scanned twice.
    /// </summary>
    internal static ValueEquivalence Match(string source, string output,
        IReadOnlyList<ProtectedSpan>? sourceSpans = null, IReadOnlyList<ProtectedSpan>? outputSpans = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(output);
        var result = new ValueEquivalence(source.Length, output.Length);
        if (source.Length == 0 || output.Length == 0) return result;
        sourceSpans ??= ProtectedTokens.Find(source);
        outputSpans ??= ProtectedTokens.Find(output);
        result.Pair(Eligible(Times(source, isSource: true), sourceSpans), Eligible(Times(output, isSource: false), outputSpans),
            static (_, _) => true);
        result.Pair(Eligible(Months(source), sourceSpans), Eligible(Months(output), outputSpans),
            static (s, o) => (s == Form.Digit) != (o == Form.Digit));
        result.Pair(Eligible(Concepts(source), sourceSpans), Eligible(Concepts(output), outputSpans),
            static (s, o) => s != Form.Korean || o != Form.Korean);
        List<Mention> sourceNumbers = Eligible(Numbers(source, sourceSpans, isOutput: false), sourceSpans);
        result.Pair(sourceNumbers, WithoutExactDigits(sourceNumbers, Eligible(Numbers(output, outputSpans, isOutput: true), outputSpans)),
            static (s, o) => (s == Form.Digit) != (o == Form.Digit));
        return result;
    }

    /// <summary>
    /// An output digit that copies a source digit belongs to that digit, so "an hour" or "one" cannot take the
    /// "1" that the source's own "1" needs.
    /// </summary>
    private static List<Mention> WithoutExactDigits(List<Mention> source, List<Mention> output)
    {
        var copies = new Dictionary<int, int>();
        foreach (Mention mention in source)
            if (mention.Form == Form.Digit) copies[mention.Value] = copies.GetValueOrDefault(mention.Value) + 1;
        if (copies.Count == 0) return output;
        return output.FindAll(mention =>
        {
            if (mention.Form != Form.Digit || copies.GetValueOrDefault(mention.Value) == 0) return true;
            copies[mention.Value]--;
            return false;
        });
    }

    /// <summary>
    /// A mention inside a larger protected span (a URL, a path, an identifier) is part of that span, not a value
    /// of its own. <see cref="ProtectedTokens.Find"/> returns its spans in order and without overlaps.
    /// </summary>
    private static List<Mention> Eligible(List<Mention> mentions, IReadOnlyList<ProtectedSpan> spans)
    {
        if (mentions.Count == 0 || spans.Count == 0) return mentions;
        return mentions.FindAll(mention =>
        {
            // The first span that ends after the mention starts.
            int low = 0, high = spans.Count;
            while (low < high)
            {
                int middle = (low + high) / 2;
                if (spans[middle].Start + spans[middle].Length <= mention.Start) low = middle + 1;
                else high = middle;
            }
            for (int index = low; index < spans.Count && spans[index].Start < mention.End; index++)
                if (spans[index].Start < mention.Start || spans[index].Start + spans[index].Length > mention.End) return false;
            return true;
        });
    }

    /// <summary>A source span that a paired mention accounts for.</summary>
    internal bool CoversSource(ProtectedSpan span) => Covers(_source, span);

    /// <summary>An output span that a paired mention accounts for.</summary>
    internal bool CoversOutput(ProtectedSpan span) => Covers(_output, span);

    /// <summary>The source with every paired mention blanked out, keeping all positions.</summary>
    internal string MaskSource(string source) => _pairs == 0 ? source : Mask(source, _source);

    /// <summary>The output with every paired mention blanked out, keeping all positions.</summary>
    internal string MaskOutput(string output) => _pairs == 0 ? output : Mask(output, _output);

    /// <summary>
    /// Everyday English abbreviations that the dotted-identifier pattern catches but that are not identifiers:
    /// a translation may spell them out or leave them away. "a.m."/"p.m." and the country abbreviations are not
    /// here; they count only through a paired clock time or a listed counterpart.
    /// </summary>
    internal static bool IsPlainAbbreviation(ProtectedSpan span) =>
        span.Kind == "dotted" && PlainAbbreviations.Contains(span.Text);

    private static readonly HashSet<string> PlainAbbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        "e.g", "i.e", "Ph.D", "a.k.a",
    };

    private void Pair(List<Mention> source, List<Mention> output, Func<Form, Form, bool> allowed)
    {
        if (source.Count == 0 || output.Count == 0) return;
        // Output mentions not taken by an earlier kind, by value and form, in reading order. Mentions of one kind
        // may overlap when they carry different values, so a queue head taken through another value is skipped.
        var waiting = new Dictionary<(int Value, Form Form), Queue<Mention>>();
        foreach (Mention o in output)
        {
            if (Claimed(_output, o.Start, o.End)) continue;
            if (!waiting.TryGetValue((o.Value, o.Form), out Queue<Mention>? queue)) waiting[(o.Value, o.Form)] = queue = new();
            queue.Enqueue(o);
        }
        foreach (Mention s in source)
        {
            if (Claimed(_source, s.Start, s.End)) continue;
            Queue<Mention>? best = null;
            foreach (Form form in Forms)
            {
                if (!allowed(s.Form, form) || !waiting.TryGetValue((s.Value, form), out Queue<Mention>? queue)) continue;
                while (queue.Count > 0 && Claimed(_output, queue.Peek().Start, queue.Peek().End)) queue.Dequeue();
                if (queue.Count == 0) continue;
                if (best is null || queue.Peek().Start < best.Peek().Start) best = queue;
            }
            if (best is null) continue;
            Mention o = best.Dequeue();
            _pairs++;
            Array.Fill(_source, _pairs, s.Start, s.Length);
            Array.Fill(_output, _pairs, o.Start, o.Length);
        }
    }

    private static readonly Form[] Forms = [Form.Digit, Form.Word, Form.Korean];

    private static bool Claimed(int[] owners, int start, int end)
    {
        for (int index = start; index < end; index++)
            if (owners[index] != 0) return true;
        return false;
    }

    private static bool Covers(int[] owners, ProtectedSpan span)
    {
        if (span.Length == 0 || span.Start + span.Length > owners.Length) return false;
        int owner = owners[span.Start];
        if (owner == 0) return false;
        for (int index = span.Start + 1; index < span.Start + span.Length; index++)
            if (owners[index] != owner) return false;
        return true;
    }

    private static string Mask(string text, int[] owners)
    {
        if (owners.Length != text.Length) throw new ArgumentException("The text is not the one that was matched.", nameof(text));
        char[] characters = text.ToCharArray();
        for (int index = 0; index < characters.Length; index++)
            if (owners[index] != 0) characters[index] = MaskCharacter;
        return new string(characters);
    }

    /// <summary>Up to <paramref name="length"/> characters just before <paramref name="index"/>.</summary>
    private static string Before(string text, int index, int length) => text[Math.Max(0, index - length)..index];

    // ---------------------------------------------------------------- clock times

    // "2:30", "2:30 PM", "10 a.m.", "nine pm"; "오후 2시 30분", "3시 반", "오전 9:15". "2시간" is a duration, not a time.
    [GeneratedRegex(@"(?<![0-9A-Za-z_.:])(?:(?<kmer>오전|오후)\s*)?(?:(?<h>\d{1,2})\s*시(?!간)(?:\s*(?<m>\d{1,2})\s*분|\s*(?<half>반))?|(?<colon>(?<h>\d{1,2}):(?<m>[0-5]\d))(?![0-9:])(?:\s*(?<emer>[aApP]\.[mM]\.?|[aApP][mM])(?![A-Za-z0-9_]))?|(?<h>\d{1,2}|(?i:one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve))\s*(?<emer>[aApP]\.[mM]\.?|[aApP][mM])(?![A-Za-z0-9_]))",
        RegexOptions.CultureInvariant)]
    private static partial Regex TimePattern();

    // A word that puts a time after it: "at 14:30", "from 9:00", "until 18:00". "to" and "and" are not here:
    // they also stand between two bit ranges ("bits 23:16 and 15:10"), so they only join a time to another
    // that is one on its own (TimeConnector).
    [GeneratedRegex(@"(?<![A-Za-z])(?:at|by|until|till|before|after|from|around|about|since|between|past)\s+\z",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex TimeCueBefore();

    // What joins two times into one span of the day: "9:30 to 18:00", "9:00–17:30".
    [GeneratedRegex(@"\A\s*(?:(?:to|and|until|till|through)\s+|(?<dash>[-–~])\s*)\z",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex TimeConnector();

    private static List<Mention> Times(string text, bool isSource)
    {
        var found = new List<(Match Match, int Value, bool Bare)>();
        foreach (Match match in TimePattern().Matches(text))
        {
            if (match.Groups["kmer"].Success && match.Groups["emer"].Success) continue;
            string hourText = match.Groups["h"].Value;
            int hour = char.IsAsciiDigit(hourText[0]) ? int.Parse(hourText, CultureInfo.InvariantCulture) : EnglishNumber(hourText);
            int minute = match.Groups["half"].Success ? 30
                : match.Groups["m"].Success ? int.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture) : 0;
            string meridiem = match.Groups["kmer"].Success ? match.Groups["kmer"].Value : match.Groups["emer"].Value;
            bool? afternoon = meridiem.Length == 0 ? null : meridiem is "오후" || meridiem[0] is 'p' or 'P';
            if (TimeValue(hour, minute, afternoon) is int value)
                found.Add((match, value, match.Groups["colon"].Success && meridiem.Length == 0));
        }

        var mentions = new List<Mention>(found.Count);
        for (int index = 0; index < found.Count; index++)
        {
            (Match match, int value, bool bare) = found[index];
            // In the source a bare "15:10" may be a bit range, an aspect ratio or a verse. It is a clock time only
            // after a time word or when joined to a time that is one; the output may write any time this way.
            if (isSource && bare && !IsTimeAnchor(text, found[index])
                && !(index > 0 && JoinsTime(text, found[index - 1], found[index]))
                && !(index + 1 < found.Count && JoinsTime(text, found[index], found[index + 1])))
                continue;
            mentions.Add(new(match.Index, match.Length, value, Form.Digit));
        }
        return mentions;
    }

    private static bool IsTimeAnchor(string text, (Match Match, int Value, bool Bare) time) =>
        !time.Bare || TimeCueBefore().IsMatch(Before(text, time.Match.Index, 16));

    /// <summary>
    /// Two times joined by "to", "and", "until", "till", "through" or a dash, where one of them is a time on its
    /// own. Two bare colon times joined by a dash ("9:00–17:30") are hours of the day together.
    /// </summary>
    private static bool JoinsTime(string text, (Match Match, int Value, bool Bare) first, (Match Match, int Value, bool Bare) second)
    {
        int end = first.Match.Index + first.Match.Length;
        if (second.Match.Index - end > 12) return false;
        Match connector = TimeConnector().Match(text[end..second.Match.Index]);
        if (!connector.Success) return false;
        return IsTimeAnchor(text, first) || IsTimeAnchor(text, second)
            || (connector.Groups["dash"].Success && first.Match.Groups["colon"].Success && second.Match.Groups["colon"].Success);
    }

    /// <summary>
    /// A time with a meridiem or past 12 is one moment of the day; a bare "2:30" or "2시" is either of two. The
    /// two kinds never pair, so "2:30 PM" needs "오후" (or "14:30") and "14:30" never becomes a bare "2시 30분".
    /// </summary>
    private static int? TimeValue(int hour, int minute, bool? afternoon)
    {
        if (minute > 59 || hour > 24) return null;
        if (afternoon is bool pm)
        {
            if (hour is < 1 or > 12) return pm && hour is > 12 and < 24 ? 10_000 + hour * 60 + minute : null;
            int day = pm ? (hour == 12 ? 12 : hour + 12) : (hour == 12 ? 0 : hour);
            return 10_000 + day * 60 + minute;
        }
        return hour is 0 or > 12 ? 10_000 + hour % 24 * 60 + minute : 20_000 + hour * 60 + minute;
    }

    // ---------------------------------------------------------------- months

    [GeneratedRegex(@"(?<![A-Za-z])(?:(?:(?<name>January|February|March|April|June|July|August|September|October|November|December)|(?<abbr>Jan|Feb|Mar|Apr|Jun|Jul|Aug|Sept|Sep|Oct|Nov|Dec))\.?|(?<may>May)(?=\s+\d)|(?<=(?:\d|\b(?:[Ii]n|[Oo]f|[Ss]ince|[Uu]ntil|[Ff]rom|[Bb]y|[Tt]o|[Dd]uring|[Ee]arly|[Ll]ate|[Mm]id))[\s-]+)(?<may>May))(?![A-Za-z])|(?<![0-9])(?<number>1[0-2]|[1-9])\s?월",
        RegexOptions.CultureInvariant)]
    private static partial Regex MonthPattern();

    // A day or a year after an abbreviated month: "Dec 12", "Dec. 2025", "Dec, 2025".
    [GeneratedRegex(@"\G\.?,?\s+\d{1,4}(?![0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex DayAfterMonth();

    // A day or a year before an abbreviated month: "12 Dec".
    [GeneratedRegex(@"(?<![0-9])\d{1,4}\s+\z", RegexOptions.CultureInvariant)]
    private static partial Regex DayBeforeMonth();

    private static readonly string[] MonthNames =
        ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"];

    private static List<Mention> Months(string text)
    {
        var mentions = new List<Mention>();
        foreach (Match match in MonthPattern().Matches(text))
        {
            if (match.Groups["number"].Success)
            {
                mentions.Add(new(match.Index, match.Length, int.Parse(match.Groups["number"].Value, CultureInfo.InvariantCulture), Form.Digit));
                continue;
            }
            Group abbreviation = match.Groups["abbr"];
            // "Dec", "Oct" and "Sep" also head number-base and separator columns; they are months next to a date.
            if (abbreviation.Success && !DayAfterMonth().IsMatch(text, abbreviation.Index + abbreviation.Length)
                && !DayBeforeMonth().IsMatch(Before(text, match.Index, 8)))
                continue;
            string name = match.Groups["name"].Success ? match.Groups["name"].Value
                : abbreviation.Success ? abbreviation.Value : match.Groups["may"].Value;
            int month = Array.IndexOf(MonthNames, name[..3].ToLowerInvariant()) + 1;
            mentions.Add(new(match.Index, match.Length, month, Form.Word));
        }
        return mentions;
    }

    // ---------------------------------------------------------------- listed acronyms

    // The value of each entry is its index. "OK" is not in the list: it is read by OkValue.
    private static readonly (Regex? English, Regex Korean)[] ConceptPatterns =
    [
        (Concept(@"U\.S\.A\.?|U\.S\.?|USA|US"), new Regex("미국", RegexOptions.CultureInvariant)),
        (Concept(@"U\.K\.?|UK"), new Regex("영국", RegexOptions.CultureInvariant)),
        (Concept(@"E\.U\.?|EU"), new Regex(@"유럽\s?연합", RegexOptions.CultureInvariant)),
        (Concept(@"U\.N\.?|UN"), new Regex("유엔", RegexOptions.CultureInvariant)),
        (null, new Regex("확인", RegexOptions.CultureInvariant)),
        (Concept("TV"), new Regex("텔레비전|티비", RegexOptions.CultureInvariant)),
        (Concept("AI"), new Regex(@"인공\s?지능", RegexOptions.CultureInvariant)),
        (null, new Regex("좋|괜찮|오케이", RegexOptions.CultureInvariant)),
    ];

    private const int OkConfirm = 4;
    private const int OkFine = 7;

    // Never next to a hyphen: "en-US" and "US-ASCII" are names, not the country.
    private static Regex Concept(string forms) =>
        new(@"(?<![A-Za-z0-9_.\-])(?:" + forms + @")(?![A-Za-z0-9_\-])", RegexOptions.CultureInvariant);

    private static readonly Regex OkPattern = Concept("OK");

    // "click OK", "press the OK", "select on OK"; "OK button".
    [GeneratedRegex(@"(?<![A-Za-z])(?:click|press|select|choose|tap|hit|push)\s+(?:on\s+)?(?:the\s+)?\z",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex OkButtonBefore();

    [GeneratedRegex(@"\G\s+button(?![A-Za-z])", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex OkButtonAfter();

    // "returns OK", "HTTP 200 OK", "status: OK"; "OK(", "OK =", "OK:".
    [GeneratedRegex(@"(?:(?<![A-Za-z])(?:return|returns|returned|code|status|http|response|result|value)|(?<![0-9])\d{3})\s*[:=]?\s+\z",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex OkStatusBefore();

    [GeneratedRegex(@"\G\s*[(=:]", RegexOptions.CultureInvariant)]
    private static partial Regex OkStatusAfter();

    [GeneratedRegex(@"\S+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceWord();

    private static List<Mention> Concepts(string text)
    {
        var mentions = new List<Mention>();
        // "AI" is also an analog input and "US" a microsecond in a table cell; the country, the union and the
        // rest are meant only in a sentence of at least four words.
        bool sentence = WhitespaceWord().Count(text) >= 4;
        for (int concept = 0; concept < ConceptPatterns.Length; concept++)
        {
            if (sentence && ConceptPatterns[concept].English is Regex english)
                foreach (Match match in english.Matches(text))
                    mentions.Add(new(match.Index, match.Length, concept, Form.Word));
            foreach (Match match in ConceptPatterns[concept].Korean.Matches(text))
                mentions.Add(new(match.Index, match.Length, concept, Form.Korean));
        }
        foreach (Match match in OkPattern.Matches(text))
            if (OkValue(text, match) is int value) mentions.Add(new(match.Index, match.Length, value, Form.Word));
        mentions.Sort(static (left, right) => left.Start.CompareTo(right.Start));
        return mentions;
    }

    /// <summary>
    /// "OK" is 확인 only on a button ("Click OK", "the OK button") and 좋다/괜찮다 only when it is not a status or
    /// a return value. A text that is only "OK" stays exact.
    /// </summary>
    private static int? OkValue(string text, Match match)
    {
        if (text.AsSpan().Trim(" \t\r\n.,;:!?\"'()[]").SequenceEqual("OK")) return null;
        string before = Before(text, match.Index, 24);
        int end = match.Index + match.Length;
        if (OkButtonBefore().IsMatch(before) || OkButtonAfter().IsMatch(text, end)) return OkConfirm;
        if (OkStatusBefore().IsMatch(before) || OkStatusAfter().IsMatch(text, end)) return null;
        return OkFine;
    }

    // ---------------------------------------------------------------- counts

    private const string Tens = "twenty|thirty|forty|fifty|sixty|seventy|eighty|ninety";
    private const string Units = "one|two|three|four|five|six|seven|eight|nine";
    private const string UpTo99 = "(?:(?:" + Tens + ")(?:[- ](?:" + Units + ")(?![A-Za-z]))?|zero|" + Units
        + "|ten|eleven|twelve|thirteen|fourteen|fifteen|sixteen|seventeen|eighteen|nineteen)";

    // "three hundred", "one hundred and five", "nine thousand"; "a couple of", "a dozen", "an hour", "once",
    // "twice"; "twenty-one", "nine", "third". A plain "a" before any other noun is not a count.
    [GeneratedRegex(@"(?<![A-Za-z])(?:(?<hundreds>" + UpTo99 + @")[- ]+hundred(?:\s+(?:and\s+)?(?<rest>" + UpTo99 + @"))?|(?<thousands>" + Units + @")[- ]+thousand|(?<couple>a\s+couple\s+of)|(?<dozen>a\s+dozen)|an?\s+(?<unit>second|minute|hour|day|week|month|year)|(?<times>once|twice)|(?<word>" + UpTo99 + @"|hundred|thousand|first|second|third|fourth|fifth|sixth|seventh|eighth|ninth|tenth|eleventh|twelfth))(?![A-Za-z])",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex EnglishNumberPattern();

    // A unit or a currency next to a count makes it a measurement ("2 kg", "5 MB", "$3"), which stays exact.
    [GeneratedRegex(@"\G\s?(?:(?:kg|g|mg|µg|t|km|m|cm|mm|µm|nm|l|L|ml|mL|lb|lbs|oz|ft|in|mi|yd|h|hr|hrs|min|s|sec|ms|µs|us|ns|px|pt|dp|dpi|em|K|B|kB|KB|MB|GB|TB|bit|bits|Hz|kHz|MHz|GHz|V|mV|A|mA|W|kW|mAh|fps|x|X)(?![A-Za-z])|[%°µμ‰/×])",
        RegexOptions.CultureInvariant)]
    private static partial Regex UnitAfter();

    // The whole count at the start of a measurement span: "50" of "50%", "10" of "10km"; not "3" of "3.3 V".
    [GeneratedRegex(@"\A(?:0|[1-9][0-9]{0,3})(?![0-9]|[.,][0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex LeadingCount();

    private static bool HasUnit(string text, ProtectedSpan span)
    {
        int before = span.Start - 1;
        if (before >= 0 && text[before] == ' ') before--;
        return (before >= 0 && text[before] is '$' or '€' or '£' or '¥' or '₩' or '#')
            || UnitAfter().IsMatch(text, span.Start + span.Length);
    }

    // Native Korean numerals before a counter ("두 개", "열두 시"), 하나/둘/셋/넷 as words of their own, and the
    // day counts 하루/이틀/사흘/나흘 ("2 days" is 이틀). Sino-Korean numerals are left out: 이 is also "this" and a
    // particle, 일 also "work" and "day".
    [GeneratedRegex(@"(?<![가-힣])(?:(?<det>(?:열|스물)?(?:한|두|세|네|다섯|여섯|일곱|여덟|아홉)|열|스무|스물)(?=\s+(?<next>[가-힣]+))|(?<pron>하나|둘|셋|넷)(?=$|[^가-힣]|(?:이|가|을|를|은|는|의|도|만|씩|와|과|로|에)(?![가-힣]))|(?<days>하루|이틀|사흘|나흘))",
        RegexOptions.CultureInvariant)]
    private static partial Regex KoreanNumberPattern();

    // "9천", "3백", "5만": a digit written with a Korean place word right after it.
    [GeneratedRegex(@"\G(?:천|백|만)", RegexOptions.CultureInvariant)]
    private static partial Regex KoreanPlace();

    // After these, 한/세/네/열 are verb or noun forms ("말한 것", "열 수 있습니다"), not numbers.
    private static readonly string[] NotCounters = ["수", "때", "것", "거", "적", "뿐", "듯", "채", "대로", "만큼", "바", "데", "지"];

    private static readonly string[] Particles =
        ["", "이", "가", "을", "를", "은", "는", "에", "에는", "도", "만", "으로", "로", "이다", "입니다", "이며", "과", "와"];

    private static bool IsDependentNoun(string next) =>
        NotCounters.Any(noun => next.StartsWith(noun, StringComparison.Ordinal)
            && Particles.Contains(next[noun.Length..], StringComparer.Ordinal));

    private static readonly Dictionary<string, int> EnglishValues = new(StringComparer.OrdinalIgnoreCase)
    {
        ["zero"] = 0, ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5, ["six"] = 6, ["seven"] = 7,
        ["eight"] = 8, ["nine"] = 9, ["ten"] = 10, ["eleven"] = 11, ["twelve"] = 12, ["thirteen"] = 13, ["fourteen"] = 14,
        ["fifteen"] = 15, ["sixteen"] = 16, ["seventeen"] = 17, ["eighteen"] = 18, ["nineteen"] = 19, ["twenty"] = 20,
        ["thirty"] = 30, ["forty"] = 40, ["fifty"] = 50, ["sixty"] = 60, ["seventy"] = 70, ["eighty"] = 80, ["ninety"] = 90,
        ["hundred"] = 100, ["thousand"] = 1000, ["first"] = 1, ["second"] = 2, ["third"] = 3, ["fourth"] = 4, ["fifth"] = 5,
        ["sixth"] = 6, ["seventh"] = 7, ["eighth"] = 8, ["ninth"] = 9, ["tenth"] = 10, ["eleventh"] = 11, ["twelfth"] = 12,
        ["once"] = 1, ["twice"] = 2,
    };

    private static readonly Dictionary<string, int> KoreanValues = new(StringComparer.Ordinal)
    {
        ["한"] = 1, ["두"] = 2, ["세"] = 3, ["네"] = 4, ["다섯"] = 5, ["여섯"] = 6, ["일곱"] = 7, ["여덟"] = 8, ["아홉"] = 9,
        ["열"] = 10, ["스무"] = 20, ["스물"] = 20, ["하나"] = 1, ["둘"] = 2, ["셋"] = 3, ["넷"] = 4,
        ["하루"] = 1, ["이틀"] = 2, ["사흘"] = 3, ["나흘"] = 4,
    };

    private static int EnglishNumber(string word) => EnglishValues[word];

    /// <summary>"twenty-one", "twenty one", "nine": the sum of the words.</summary>
    private static int UpTo99Value(string words)
    {
        int value = 0;
        foreach (string word in words.Split(['-', ' '], StringSplitOptions.RemoveEmptyEntries)) value += EnglishValues[word];
        return value;
    }

    private static int EnglishCount(Match match)
    {
        if (match.Groups["hundreds"].Success)
            return UpTo99Value(match.Groups["hundreds"].Value) * 100
                + (match.Groups["rest"].Success ? UpTo99Value(match.Groups["rest"].Value) : 0);
        if (match.Groups["thousands"].Success) return EnglishValues[match.Groups["thousands"].Value] * 1000;
        if (match.Groups["couple"].Success) return 2;
        if (match.Groups["dozen"].Success) return 12;
        if (match.Groups["unit"].Success) return 1;
        if (match.Groups["times"].Success) return EnglishValues[match.Groups["times"].Value];
        return UpTo99Value(match.Groups["word"].Value);
    }

    private static List<Mention> Numbers(string text, IReadOnlyList<ProtectedSpan> spans, bool isOutput)
    {
        var mentions = new List<Mention>();
        foreach (ProtectedSpan span in spans)
        {
            if (span.Kind == "num" && span.Length <= 4 && span.Text.All(char.IsAsciiDigit) && (span.Length == 1 || span.Text[0] != '0')
                && (isOutput || !HasUnit(text, span)))
            {
                // Only a bare count in the source: a number with a unit, a decimal, a range or a code keeps its
                // exact digits. The output may carry the unit or the currency of the number word it renders.
                int value = int.Parse(span.Text, CultureInfo.InvariantCulture);
                Match place = KoreanPlace().Match(text, span.Start + span.Length);
                if (place.Success)
                    value *= place.Value switch { "천" => 1000, "백" => 100, _ => 10_000 };
                mentions.Add(new(span.Start, span.Length + place.Length, value, Form.Digit));
                continue;
            }
            if (!isOutput || span.Kind is not ("num" or "alnum")) continue;
            // "50%", "48 MHz", "10km": the count with its unit, as one value of the whole span.
            Match count = LeadingCount().Match(span.Text);
            if (!count.Success) continue;
            if (span.Kind == "alnum")
            {
                Match unit = UnitAfter().Match(span.Text, count.Length);
                if (!unit.Success || unit.Length != span.Length - count.Length) continue;
            }
            mentions.Add(new(span.Start, span.Length, int.Parse(count.Value, CultureInfo.InvariantCulture), Form.Digit));
        }
        foreach (Match match in EnglishNumberPattern().Matches(text))
            mentions.Add(new(match.Index, match.Length, EnglishCount(match), Form.Word));
        foreach (Match match in KoreanNumberPattern().Matches(text))
        {
            if (match.Groups["pron"].Success || match.Groups["days"].Success)
            {
                mentions.Add(new(match.Index, match.Length, KoreanValues[match.Value], Form.Word));
                continue;
            }
            string word = match.Groups["det"].Value;
            if (word is "한" or "세" or "네" or "열" && IsDependentNoun(match.Groups["next"].Value)) continue;
            int value = word.Length > 1 && word[0] is '열' or '스' && word is not "스무" and not "스물"
                ? (word[0] == '열' ? 10 : 20) + KoreanValues[word[(word[0] == '열' ? 1 : 2)..]]
                : KoreanValues[word];
            mentions.Add(new(match.Index, match.Length, value, Form.Word));
        }
        mentions.Sort(static (left, right) => left.Start.CompareTo(right.Start));
        return mentions;
    }
}
