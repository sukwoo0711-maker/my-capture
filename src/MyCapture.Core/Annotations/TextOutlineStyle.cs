namespace MyCapture.Core.Annotations;

/// <summary>The glyph outline recorded in an editable text layer.</summary>
public enum TextOutlineStyle
{
    /// <summary>Preserves the contrast and weight of layers saved before the text inspector.</summary>
    Legacy = 0,

    /// <summary>A thin dark outline, switching to light only for very dark text.</summary>
    Consistent = 1,
}
