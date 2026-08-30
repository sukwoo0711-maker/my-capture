namespace MyCapture.App.Editing;

/// <summary>
/// The active editing tool.
/// </summary>
/// <remarks>
/// The MVP set required by the product: a selection/move tool plus the five drawing
/// tools. Snipaste ships more (mosaic, numbered markers) but these six cover every
/// annotation the competitive matrix flags as table stakes, and each maps directly to
/// an existing <see cref="MyCapture.Core.Annotations.AnnotationItem"/> type so no new
/// domain shape is invented in the UI layer.
/// </remarks>
internal enum EditorTool
{
    /// <summary>Pick, move, resize and restyle existing annotations.</summary>
    Select,

    /// <summary>Drag out a rectangle outline.</summary>
    Rectangle,

    /// <summary>Drag out a single-headed arrow.</summary>
    Arrow,

    /// <summary>Freehand pen stroke.</summary>
    Pen,

    /// <summary>Click to place a text box and type into a real TextBox.</summary>
    Text,

    /// <summary>Pick an image from disk and drop it onto the capture.</summary>
    Image,
}
