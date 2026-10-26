namespace MyCapture.App.Pinning;

internal enum PinTextLanguage { None, English, Korean, Mixed, Japanese, Chinese, Other }

internal enum PinTranslationAvailability { Translatable, AlreadyTargetLanguage, UnsupportedLanguage, NothingToTranslate }

/// <summary>Decides whether a pinned text can be translated for the application display language.</summary>
internal static class PinTextLanguageDecision
{
    private const double ScriptShare = 0.15;
    private const double Majority = 0.5;

    /// <summary>
    /// The dominant script of the human text. URLs, e-mails, paths, numbers, placeholders, inline code and
    /// identifiers are removed first. Japanese: kana present and kana plus Han at least 15% of the letters.
    /// Chinese: Han is a majority, no kana, little Hangul. Mixed: Latin and Hangul are each at least 15%.
    /// Korean or English: that script is the majority. Anything else with letters is Other.
    /// </summary>
    internal static PinTextLanguage Detect(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return PinTextLanguage.None;
        ScriptCounts counts = PinScript.Count(PinTranslationPlanner.MaskProtected(text));
        int letters = counts.Letters;
        if (letters == 0) return PinTextLanguage.None;

        double latin = (double)counts.Latin / letters;
        double hangul = (double)counts.Hangul / letters;
        double han = (double)counts.Han / letters;
        double kana = (double)counts.Kana / letters;
        if (counts.Kana > 0 && kana + han >= ScriptShare) return PinTextLanguage.Japanese;
        if (han > Majority && counts.Kana == 0 && hangul < ScriptShare) return PinTextLanguage.Chinese;
        if (latin >= ScriptShare && hangul >= ScriptShare) return PinTextLanguage.Mixed;
        if (hangul >= Majority) return PinTextLanguage.Korean;
        if (latin >= Majority) return PinTextLanguage.English;
        return PinTextLanguage.Other;
    }

    /// <summary>
    /// <paramref name="targetIsKorean"/> is true when the application displays Korean, so English is translated.
    /// The text is classified first; for JSON, code and tables only the human text (string values, comments, cells)
    /// counts. When the plan has translatable pieces the result is Translatable. Otherwise the language of the
    /// human text tells why: already the target language (Mixed text without a translatable run counts as
    /// that), an unsupported script, or nothing translatable (no letters, only code, identifiers or numbers).
    /// </summary>
    internal static PinTranslationAvailability Decide(string text, bool targetIsKorean, out PinTranslationDirection direction)
    {
        direction = targetIsKorean ? PinTranslationDirection.EnglishToKorean : PinTranslationDirection.KoreanToEnglish;
        if (string.IsNullOrWhiteSpace(text)) return PinTranslationAvailability.NothingToTranslate;

        PinTextFormat format = PinTranslationPlanner.Classify(text, false, false);
        if (PinTranslationPlanner.Create(text, format, direction).HasTranslatable) return PinTranslationAvailability.Translatable;

        PinTextLanguage language = Detect(PinTranslationPlanner.ExtractHumanText(text, format));
        return language switch
        {
            PinTextLanguage.None => PinTranslationAvailability.NothingToTranslate,
            PinTextLanguage.Japanese or PinTextLanguage.Chinese or PinTextLanguage.Other => PinTranslationAvailability.UnsupportedLanguage,
            PinTextLanguage.Mixed => PinTranslationAvailability.AlreadyTargetLanguage,
            PinTextLanguage.Korean => targetIsKorean ? PinTranslationAvailability.AlreadyTargetLanguage : PinTranslationAvailability.NothingToTranslate,
            _ => targetIsKorean ? PinTranslationAvailability.NothingToTranslate : PinTranslationAvailability.AlreadyTargetLanguage,
        };
    }
}
