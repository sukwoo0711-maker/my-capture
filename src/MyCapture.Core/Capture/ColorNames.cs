namespace MyCapture.Core.Capture;

/// <summary>One CSS named colour: its CSS name, its Korean transliteration and its sRGB value.</summary>
public sealed record NamedColor(string Name, string KoreanName, byte Red, byte Green, byte Blue)
{
    /// <summary>The CIE L*a*b* value (D65) the nearest-name search compares.</summary>
    internal ColorNames.Lab Lab { get; } = ColorNames.ToLab(Red, Green, Blue);
}

/// <summary>The named colour closest to a sample, and whether the sample is exactly that colour.</summary>
public readonly record struct ColorNameMatch(NamedColor Color, bool Exact, double Distance);

/// <summary>
/// The named colours of CSS Color Module Level 4 (W3C) and the nearest of them to a sampled pixel.
/// </summary>
/// <remarks>
/// The specification lists 148 keywords. Nine pairs share one value (aqua and cyan, fuchsia and
/// magenta, and seven grey/gray spellings); each pair is offered once, under Cyan, Magenta and the
/// Gray spelling, so 139 colours remain. The nearest name is chosen by the CIEDE2000 colour
/// difference, which follows perceived difference better than distance in RGB. A sample that equals
/// a named value is an exact match; anything else is shown as an approximation.
/// </remarks>
public static class ColorNames
{
    /// <summary>Every CSS keyword in the specification's order, with the name it is shown under.</summary>
    public static IReadOnlyList<(string Keyword, string Name)> Keywords { get; }

    /// <summary>The 139 distinct named colours, in CSS name order.</summary>
    public static IReadOnlyList<NamedColor> All { get; }

    private static readonly Dictionary<int, NamedColor> ByValue;

    static ColorNames()
    {
        // CSS keyword, value, Korean transliteration (Korean loanword orthography), and for an alias the name it shares.
        (string Keyword, int Value, string Korean, string? AliasOf)[] table =
        [
            ("aliceblue", 0xF0F8FF, "앨리스 블루", null),
            ("antiquewhite", 0xFAEBD7, "앤티크 화이트", null),
            ("aqua", 0x00FFFF, "시안", "Cyan"),
            ("aquamarine", 0x7FFFD4, "아쿠아마린", null),
            ("azure", 0xF0FFFF, "애저", null),
            ("beige", 0xF5F5DC, "베이지", null),
            ("bisque", 0xFFE4C4, "비스크", null),
            ("black", 0x000000, "블랙", null),
            ("blanchedalmond", 0xFFEBCD, "블랜치드 아몬드", null),
            ("blue", 0x0000FF, "블루", null),
            ("blueviolet", 0x8A2BE2, "블루 바이올렛", null),
            ("brown", 0xA52A2A, "브라운", null),
            ("burlywood", 0xDEB887, "벌리우드", null),
            ("cadetblue", 0x5F9EA0, "커뎃 블루", null),
            ("chartreuse", 0x7FFF00, "샤르트뢰즈", null),
            ("chocolate", 0xD2691E, "초콜릿", null),
            ("coral", 0xFF7F50, "코럴", null),
            ("cornflowerblue", 0x6495ED, "콘플라워 블루", null),
            ("cornsilk", 0xFFF8DC, "콘실크", null),
            ("crimson", 0xDC143C, "크림슨", null),
            ("cyan", 0x00FFFF, "시안", null),
            ("darkblue", 0x00008B, "다크 블루", null),
            ("darkcyan", 0x008B8B, "다크 시안", null),
            ("darkgoldenrod", 0xB8860B, "다크 골든로드", null),
            ("darkgray", 0xA9A9A9, "다크 그레이", null),
            ("darkgreen", 0x006400, "다크 그린", null),
            ("darkgrey", 0xA9A9A9, "다크 그레이", "DarkGray"),
            ("darkkhaki", 0xBDB76B, "다크 카키", null),
            ("darkmagenta", 0x8B008B, "다크 마젠타", null),
            ("darkolivegreen", 0x556B2F, "다크 올리브 그린", null),
            ("darkorange", 0xFF8C00, "다크 오렌지", null),
            ("darkorchid", 0x9932CC, "다크 오키드", null),
            ("darkred", 0x8B0000, "다크 레드", null),
            ("darksalmon", 0xE9967A, "다크 새먼", null),
            ("darkseagreen", 0x8FBC8F, "다크 시 그린", null),
            ("darkslateblue", 0x483D8B, "다크 슬레이트 블루", null),
            ("darkslategray", 0x2F4F4F, "다크 슬레이트 그레이", null),
            ("darkslategrey", 0x2F4F4F, "다크 슬레이트 그레이", "DarkSlateGray"),
            ("darkturquoise", 0x00CED1, "다크 터쿼이즈", null),
            ("darkviolet", 0x9400D3, "다크 바이올렛", null),
            ("deeppink", 0xFF1493, "딥 핑크", null),
            ("deepskyblue", 0x00BFFF, "딥 스카이 블루", null),
            ("dimgray", 0x696969, "딤 그레이", null),
            ("dimgrey", 0x696969, "딤 그레이", "DimGray"),
            ("dodgerblue", 0x1E90FF, "다저 블루", null),
            ("firebrick", 0xB22222, "파이어브릭", null),
            ("floralwhite", 0xFFFAF0, "플로럴 화이트", null),
            ("forestgreen", 0x228B22, "포리스트 그린", null),
            ("fuchsia", 0xFF00FF, "마젠타", "Magenta"),
            ("gainsboro", 0xDCDCDC, "게인즈버러", null),
            ("ghostwhite", 0xF8F8FF, "고스트 화이트", null),
            ("gold", 0xFFD700, "골드", null),
            ("goldenrod", 0xDAA520, "골든로드", null),
            ("gray", 0x808080, "그레이", null),
            ("green", 0x008000, "그린", null),
            ("greenyellow", 0xADFF2F, "그린 옐로", null),
            ("grey", 0x808080, "그레이", "Gray"),
            ("honeydew", 0xF0FFF0, "허니듀", null),
            ("hotpink", 0xFF69B4, "핫 핑크", null),
            ("indianred", 0xCD5C5C, "인디언 레드", null),
            ("indigo", 0x4B0082, "인디고", null),
            ("ivory", 0xFFFFF0, "아이보리", null),
            ("khaki", 0xF0E68C, "카키", null),
            ("lavender", 0xE6E6FA, "라벤더", null),
            ("lavenderblush", 0xFFF0F5, "라벤더 블러시", null),
            ("lawngreen", 0x7CFC00, "론 그린", null),
            ("lemonchiffon", 0xFFFACD, "레몬 시폰", null),
            ("lightblue", 0xADD8E6, "라이트 블루", null),
            ("lightcoral", 0xF08080, "라이트 코럴", null),
            ("lightcyan", 0xE0FFFF, "라이트 시안", null),
            ("lightgoldenrodyellow", 0xFAFAD2, "라이트 골든로드 옐로", null),
            ("lightgray", 0xD3D3D3, "라이트 그레이", null),
            ("lightgreen", 0x90EE90, "라이트 그린", null),
            ("lightgrey", 0xD3D3D3, "라이트 그레이", "LightGray"),
            ("lightpink", 0xFFB6C1, "라이트 핑크", null),
            ("lightsalmon", 0xFFA07A, "라이트 새먼", null),
            ("lightseagreen", 0x20B2AA, "라이트 시 그린", null),
            ("lightskyblue", 0x87CEFA, "라이트 스카이 블루", null),
            ("lightslategray", 0x778899, "라이트 슬레이트 그레이", null),
            ("lightslategrey", 0x778899, "라이트 슬레이트 그레이", "LightSlateGray"),
            ("lightsteelblue", 0xB0C4DE, "라이트 스틸 블루", null),
            ("lightyellow", 0xFFFFE0, "라이트 옐로", null),
            ("lime", 0x00FF00, "라임", null),
            ("limegreen", 0x32CD32, "라임 그린", null),
            ("linen", 0xFAF0E6, "리넨", null),
            ("magenta", 0xFF00FF, "마젠타", null),
            ("maroon", 0x800000, "머룬", null),
            ("mediumaquamarine", 0x66CDAA, "미디엄 아쿠아마린", null),
            ("mediumblue", 0x0000CD, "미디엄 블루", null),
            ("mediumorchid", 0xBA55D3, "미디엄 오키드", null),
            ("mediumpurple", 0x9370DB, "미디엄 퍼플", null),
            ("mediumseagreen", 0x3CB371, "미디엄 시 그린", null),
            ("mediumslateblue", 0x7B68EE, "미디엄 슬레이트 블루", null),
            ("mediumspringgreen", 0x00FA9A, "미디엄 스프링 그린", null),
            ("mediumturquoise", 0x48D1CC, "미디엄 터쿼이즈", null),
            ("mediumvioletred", 0xC71585, "미디엄 바이올렛 레드", null),
            ("midnightblue", 0x191970, "미드나이트 블루", null),
            ("mintcream", 0xF5FFFA, "민트 크림", null),
            ("mistyrose", 0xFFE4E1, "미스티 로즈", null),
            ("moccasin", 0xFFE4B5, "모카신", null),
            ("navajowhite", 0xFFDEAD, "나바호 화이트", null),
            ("navy", 0x000080, "네이비", null),
            ("oldlace", 0xFDF5E6, "올드 레이스", null),
            ("olive", 0x808000, "올리브", null),
            ("olivedrab", 0x6B8E23, "올리브 드래브", null),
            ("orange", 0xFFA500, "오렌지", null),
            ("orangered", 0xFF4500, "오렌지 레드", null),
            ("orchid", 0xDA70D6, "오키드", null),
            ("palegoldenrod", 0xEEE8AA, "페일 골든로드", null),
            ("palegreen", 0x98FB98, "페일 그린", null),
            ("paleturquoise", 0xAFEEEE, "페일 터쿼이즈", null),
            ("palevioletred", 0xDB7093, "페일 바이올렛 레드", null),
            ("papayawhip", 0xFFEFD5, "파파야 휩", null),
            ("peachpuff", 0xFFDAB9, "피치 퍼프", null),
            ("peru", 0xCD853F, "페루", null),
            ("pink", 0xFFC0CB, "핑크", null),
            ("plum", 0xDDA0DD, "플럼", null),
            ("powderblue", 0xB0E0E6, "파우더 블루", null),
            ("purple", 0x800080, "퍼플", null),
            ("rebeccapurple", 0x663399, "레베카 퍼플", null),
            ("red", 0xFF0000, "레드", null),
            ("rosybrown", 0xBC8F8F, "로지 브라운", null),
            ("royalblue", 0x4169E1, "로열 블루", null),
            ("saddlebrown", 0x8B4513, "새들 브라운", null),
            ("salmon", 0xFA8072, "새먼", null),
            ("sandybrown", 0xF4A460, "샌디 브라운", null),
            ("seagreen", 0x2E8B57, "시 그린", null),
            ("seashell", 0xFFF5EE, "시셸", null),
            ("sienna", 0xA0522D, "시에나", null),
            ("silver", 0xC0C0C0, "실버", null),
            ("skyblue", 0x87CEEB, "스카이 블루", null),
            ("slateblue", 0x6A5ACD, "슬레이트 블루", null),
            ("slategray", 0x708090, "슬레이트 그레이", null),
            ("slategrey", 0x708090, "슬레이트 그레이", "SlateGray"),
            ("snow", 0xFFFAFA, "스노", null),
            ("springgreen", 0x00FF7F, "스프링 그린", null),
            ("steelblue", 0x4682B4, "스틸 블루", null),
            ("tan", 0xD2B48C, "탠", null),
            ("teal", 0x008080, "틸", null),
            ("thistle", 0xD8BFD8, "시슬", null),
            ("tomato", 0xFF6347, "토마토", null),
            ("turquoise", 0x40E0D0, "터쿼이즈", null),
            ("violet", 0xEE82EE, "바이올렛", null),
            ("wheat", 0xF5DEB3, "휘트", null),
            ("white", 0xFFFFFF, "화이트", null),
            ("whitesmoke", 0xF5F5F5, "화이트 스모크", null),
            ("yellow", 0xFFFF00, "옐로", null),
            ("yellowgreen", 0x9ACD32, "옐로 그린", null),
        ];

        var keywords = new List<(string, string)>(table.Length);
        var colors = new List<NamedColor>(table.Length);
        ByValue = new Dictionary<int, NamedColor>(table.Length);
        foreach ((string keyword, int value, string korean, string? aliasOf) in table)
        {
            string name = aliasOf ?? PascalName(keyword);
            keywords.Add((keyword, name));
            if (aliasOf is not null) continue;
            var color = new NamedColor(name, korean, (byte)(value >> 16), (byte)(value >> 8), (byte)value);
            colors.Add(color);
            ByValue.Add(value, color);
        }
        Keywords = keywords;
        All = colors;
    }

    /// <summary>The named colour nearest to an sRGB sample by CIEDE2000; exact when the value is named.</summary>
    public static ColorNameMatch Nearest(byte red, byte green, byte blue)
    {
        if (ByValue.TryGetValue((red << 16) | (green << 8) | blue, out NamedColor? exact))
            return new ColorNameMatch(exact, true, 0);
        Lab sample = ToLab(red, green, blue);
        NamedColor best = All[0];
        double bestDistance = double.PositiveInfinity;
        for (int index = 0; index < All.Count; index++)
        {
            double distance = DeltaE2000(sample, All[index].Lab);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = All[index];
            }
        }
        return new ColorNameMatch(best, false, bestDistance);
    }

    /// <summary>
    /// The name as shown: the CSS name in English, the Korean transliteration with the CSS name in
    /// Korean (<c>마젠타 (Magenta)</c>), and a leading <c>≈</c> when the sample only approximates it.
    /// </summary>
    public static string Display(ColorNameMatch match, bool korean)
    {
        string name = korean ? $"{match.Color.KoreanName} ({match.Color.Name})" : match.Color.Name;
        return match.Exact ? name : "≈ " + name;
    }

    /// <summary>The first line of a two-line display: the transliteration in Korean, the CSS name otherwise.</summary>
    public static string PrimaryName(ColorNameMatch match, bool korean)
    {
        string name = korean ? match.Color.KoreanName : match.Color.Name;
        return match.Exact ? name : "≈ " + name;
    }

    private static string PascalName(string keyword)
    {
        // The CSS keywords are lower case run-ons. The longest known word at each position gives the
        // conventional capitalised spelling (darkgoldenrod is DarkGoldenrod, not DarkGoldEnrod).
        var result = new System.Text.StringBuilder(keyword.Length);
        int position = 0;
        while (position < keyword.Length)
        {
            string? word = null;
            foreach (string candidate in Words)
                if (string.CompareOrdinal(keyword, position, candidate, 0, candidate.Length) == 0
                    && (word is null || candidate.Length > word.Length)) word = candidate;
            if (word is null) throw new InvalidOperationException("Unknown colour word in " + keyword);
            result.Append(char.ToUpperInvariant(word[0])).Append(word, 1, word.Length - 1);
            position += word.Length;
        }
        return result.ToString();
    }

    // Each run-on keyword splits into these words; a single-word name is its own entry.
    private static readonly string[] Words =
    [
        "alice", "almond", "antique", "aqua", "aquamarine", "azure", "beige", "bisque", "black", "blanched", "blue",
        "blush", "brick", "brown", "burly", "cadet", "chartreuse", "chiffon", "chocolate", "coral", "cornflower",
        "cornsilk", "cream", "crimson", "cyan", "dark", "deep", "dim", "dodger", "drab", "fire", "floral", "forest",
        "gainsboro", "ghost", "gold", "goldenrod", "gray", "green", "honeydew", "hot", "indian", "indigo", "ivory",
        "khaki", "lace", "lavender", "lawn", "lemon", "light", "lime", "linen", "magenta", "maroon", "medium",
        "midnight", "mint", "misty", "moccasin", "navajo", "navy", "old", "olive", "orange", "orchid", "pale",
        "papaya", "peach", "peru", "pink", "plum", "powder", "puff", "purple", "rebecca", "red", "rose", "rosy",
        "royal", "saddle", "salmon", "sandy", "sea", "seashell", "sienna", "silver", "sky", "slate", "smoke", "snow",
        "spring", "steel", "tan", "teal", "thistle", "tomato", "turquoise", "violet", "wheat", "whip", "white",
        "wood", "yellow",
    ];

    /// <summary>A CIE L*a*b* colour (D65 white point).</summary>
    public readonly record struct Lab(double L, double A, double B);

    /// <summary>sRGB (IEC 61966-2-1) to CIE L*a*b* under D65.</summary>
    public static Lab ToLab(byte red, byte green, byte blue)
    {
        double r = Linear(red), g = Linear(green), b = Linear(blue);
        double x = (0.4124564 * r + 0.3575761 * g + 0.1804375 * b) / 0.95047;
        double y = 0.2126729 * r + 0.7151522 * g + 0.0721750 * b;
        double z = (0.0193339 * r + 0.1191920 * g + 0.9503041 * b) / 1.08883;
        double fx = F(x), fy = F(y), fz = F(z);
        return new Lab(116 * fy - 16, 500 * (fx - fy), 200 * (fy - fz));

        static double Linear(byte channel)
        {
            double value = channel / 255.0;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        static double F(double t) => t > 216.0 / 24389.0 ? Math.Cbrt(t) : (24389.0 / 27.0 * t + 16) / 116;
    }

    /// <summary>The CIEDE2000 colour difference (CIE 142-2001) with unit weighting factors.</summary>
    public static double DeltaE2000(Lab first, Lab second)
    {
        double c1 = Math.Sqrt(first.A * first.A + first.B * first.B);
        double c2 = Math.Sqrt(second.A * second.A + second.B * second.B);
        double meanC = (c1 + c2) / 2;
        double meanC7 = Math.Pow(meanC, 7);
        double g = 0.5 * (1 - Math.Sqrt(meanC7 / (meanC7 + Math.Pow(25, 7))));
        double a1 = (1 + g) * first.A, a2 = (1 + g) * second.A;
        double c1p = Math.Sqrt(a1 * a1 + first.B * first.B), c2p = Math.Sqrt(a2 * a2 + second.B * second.B);
        double h1p = Hue(first.B, a1), h2p = Hue(second.B, a2);

        double deltaL = second.L - first.L;
        double deltaC = c2p - c1p;
        double deltaH = 0;
        if (c1p * c2p != 0)
        {
            deltaH = h2p - h1p;
            if (deltaH > 180) deltaH -= 360;
            else if (deltaH < -180) deltaH += 360;
        }
        double deltaHTerm = 2 * Math.Sqrt(c1p * c2p) * Math.Sin(Radians(deltaH / 2));

        double meanL = (first.L + second.L) / 2;
        double meanCp = (c1p + c2p) / 2;
        double meanH = h1p + h2p;
        if (c1p * c2p != 0)
        {
            if (Math.Abs(h1p - h2p) <= 180) meanH /= 2;
            else meanH = meanH < 360 ? (meanH + 360) / 2 : (meanH - 360) / 2;
        }

        double t = 1 - 0.17 * Math.Cos(Radians(meanH - 30)) + 0.24 * Math.Cos(Radians(2 * meanH))
            + 0.32 * Math.Cos(Radians(3 * meanH + 6)) - 0.20 * Math.Cos(Radians(4 * meanH - 63));
        double deltaTheta = 30 * Math.Exp(-Math.Pow((meanH - 275) / 25, 2));
        double meanCp7 = Math.Pow(meanCp, 7);
        double rc = 2 * Math.Sqrt(meanCp7 / (meanCp7 + Math.Pow(25, 7)));
        double meanL50 = (meanL - 50) * (meanL - 50);
        double sl = 1 + 0.015 * meanL50 / Math.Sqrt(20 + meanL50);
        double sc = 1 + 0.045 * meanCp;
        double sh = 1 + 0.015 * meanCp * t;
        double rt = -Math.Sin(Radians(2 * deltaTheta)) * rc;

        double l = deltaL / sl, c = deltaC / sc, h = deltaHTerm / sh;
        return Math.Sqrt(l * l + c * c + h * h + rt * c * h);

        static double Hue(double b, double a)
        {
            if (b == 0 && a == 0) return 0;
            double degrees = Math.Atan2(b, a) * 180 / Math.PI;
            return degrees < 0 ? degrees + 360 : degrees;
        }

        static double Radians(double degrees) => degrees * Math.PI / 180;
    }
}
