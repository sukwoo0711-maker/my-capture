namespace MyCapture.App.Themes;

/// <summary>The shared UI type ramp and the independent sizes used to draw document content.</summary>
public static class TypeScale
{
    /// <summary>Latin key caps, numeric badges and file-type chips; never Korean text.</summary>
    public const double Micro = 11;

    /// <summary>Secondary text, status, metadata and compact editor controls.</summary>
    public const double Caption = 12;

    /// <summary>Primary reading text, standard controls and section labels.</summary>
    public const double Body = 14;

    /// <summary>Compatibility alias for standard controls.</summary>
    public const double Control = Body;

    /// <summary>Emphasized values, panel headings and message titles.</summary>
    public const double BodyLarge = 16;

    /// <summary>Dialog titles, secondary-window titles and illustrative key caps.</summary>
    public const double Subtitle = 20;

    /// <summary>Page titles, hero titles and large illustration glyphs.</summary>
    public const double Title = 28;

    /// <summary>Compatibility alias for display titles.</summary>
    public const double Display = Title;

    /// <summary>Segoe Fluent Icons glyphs.</summary>
    public const double IconGlyph = 16;

    /// <summary>Document and illustration sizes whose existing geometry is independent of UI chrome.</summary>
    public static class Content
    {
        /// <summary>Default Markdown pin body.</summary>
        public const double MarkdownBody = 15;
        /// <summary>Markdown fenced code blocks.</summary>
        public const double MarkdownCode = 14;
        /// <summary>Markdown fallback and continuation notes.</summary>
        public const double MarkdownFallback = 13;
        /// <summary>Invisible Markdown horizontal-rule paragraph spacer.</summary>
        public const double MarkdownRuleSpacer = 1;
        /// <summary>First-level Markdown document heading.</summary>
        public const double MarkdownHeading1 = 28;
        /// <summary>Second-level Markdown document heading.</summary>
        public const double MarkdownHeading2 = 23;
        /// <summary>Third-level Markdown document heading.</summary>
        public const double MarkdownHeading3 = 19;
        /// <summary>Remaining Markdown document headings.</summary>
        public const double MarkdownHeadingOther = 16;
        /// <summary>Plain-text pin body.</summary>
        public const double PlainText = 16;
        /// <summary>Table pin cells.</summary>
        public const double TableText = 14;
        /// <summary>Syntax-highlighted code pin body.</summary>
        public const double CodeText = 15;
        /// <summary>Smallest user-selected recording wall-clock text.</summary>
        public const double WallClockMinimum = 10;
        /// <summary>Largest user-selected recording wall-clock text.</summary>
        public const double WallClockMaximum = 28;
        /// <summary>Initial recording wall-clock text.</summary>
        public const double WallClockDefault = 13;
        /// <summary>One wheel notch of recording wall-clock text adjustment.</summary>
        public const double WallClockStep = 1;
        /// <summary>Letter geometry in the text-style tool glyph.</summary>
        public const double TextStyleGlyph = 18;
        /// <summary>Captions printed into an exported contact sheet.</summary>
        public const double ContactSheetCaption = 9;
        /// <summary>Normalized em size for exported watermark geometry.</summary>
        public const double WatermarkEmSize = 96;
        /// <summary>Video text-overlay editing sample.</summary>
        public const double TimedOverlayEditor = 18;
        /// <summary>Maximum source-window title printed into a captured image.</summary>
        public const double SourceWindowTitle = 13;
        /// <summary>Countdown numeral drawn inside its fixed animation ring.</summary>
        public const double CountdownDigit = 54;
        /// <summary>Single digit drawn inside the step tool cursor badge.</summary>
        public const double CursorSingleDigit = 11.5;
        /// <summary>Two digits drawn inside the step tool cursor badge.</summary>
        public const double CursorTwoDigits = 10;
        /// <summary>Three digits drawn inside the step tool cursor badge.</summary>
        public const double CursorManyDigits = 7.5;
        /// <summary>Three-digit annotation preview inside its fixed badge.</summary>
        public const double StepPreviewDense = 15;
        /// <summary>One- or two-digit annotation preview inside its fixed badge.</summary>
        public const double StepPreviewNormal = 20;
    }
}
