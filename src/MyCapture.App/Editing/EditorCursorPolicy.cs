using MyCapture.Core.Annotations;

namespace MyCapture.App.Editing;

/// <summary>The pointer shapes the editor canvas shows.</summary>
internal enum EditorCursorKind
{
    /// <summary>System arrow: the selection tool over empty canvas.</summary>
    Arrow,

    /// <summary>System four-way arrow: a movable annotation or crop region under the pointer.</summary>
    Move,

    ResizeNorthwestSoutheast,
    ResizeNortheastSouthwest,
    ResizeNorthSouth,
    ResizeWestEast,

    /// <summary>System I-beam: the text tool places a caret where it clicks.</summary>
    TextBeam,

    /// <summary>System crosshair: rectangle, ellipse and arrow drag from the exact pixel.</summary>
    Cross,

    /// <summary>Central locator; the surface shows the actual circular pen footprint.</summary>
    Pen,

    /// <summary>Central locator; the surface shows the actual circular highlighter footprint.</summary>
    Highlighter,

    /// <summary>Crosshair with the number the next click adds.</summary>
    StepNumber,

    /// <summary>Crop corners; the hot spot is where the two corners cross.</summary>
    Crop,

    /// <summary>Crosshair with the mosaic tool icon.</summary>
    Mosaic,

    /// <summary>Crosshair with the blur tool icon.</summary>
    Blur,

    /// <summary>Crosshair with the image tool icon.</summary>
    Image,
}

/// <summary>What lies under the pointer, as far as the cursor choice cares.</summary>
/// <param name="Handle">The selection handle under the pointer, if any.</param>
/// <param name="OverAnnotation">True when an annotation the selection tool can move is under the pointer.</param>
/// <param name="CropGrip">The crop grip under the pointer while a crop is pending or an image edge is offered for cropping.</param>
internal readonly record struct EditorPointerTarget(
    ResizeHandle Handle = ResizeHandle.None,
    bool OverAnnotation = false,
    CropGrip? CropGrip = null);

/// <summary>Chooses the canvas cursor from the active tool and what is under the pointer.</summary>
/// <remarks>
/// Kept free of WPF so the mapping is testable on its own. Like Paint and Snipping Tool,
/// shape tools keep the system crosshair for pixel-exact starts and selection keeps the system
/// move and resize arrows; tools whose result is not a dragged outline show their own icon so
/// the active tool is visible where the user is looking.
/// </remarks>
internal static class EditorCursorPolicy
{
    internal static EditorCursorKind Resolve(EditorTool tool, EditorPointerTarget target)
    {
        if (target.CropGrip is CropGrip grip)
        {
            return grip switch
            {
                CropGrip.Move => EditorCursorKind.Move,
                CropGrip.TopLeft or CropGrip.BottomRight => EditorCursorKind.ResizeNorthwestSoutheast,
                CropGrip.TopRight or CropGrip.BottomLeft => EditorCursorKind.ResizeNortheastSouthwest,
                CropGrip.Top or CropGrip.Bottom => EditorCursorKind.ResizeNorthSouth,
                CropGrip.Left or CropGrip.Right => EditorCursorKind.ResizeWestEast,
                _ => EditorCursorKind.Crop,
            };
        }

        return tool switch
        {
            EditorTool.Select => target.Handle switch
            {
                ResizeHandle.TopLeft or ResizeHandle.BottomRight => EditorCursorKind.ResizeNorthwestSoutheast,
                ResizeHandle.TopRight or ResizeHandle.BottomLeft => EditorCursorKind.ResizeNortheastSouthwest,
                ResizeHandle.TopCenter or ResizeHandle.BottomCenter => EditorCursorKind.ResizeNorthSouth,
                ResizeHandle.MiddleLeft or ResizeHandle.MiddleRight => EditorCursorKind.ResizeWestEast,
                _ when target.OverAnnotation => EditorCursorKind.Move,
                _ => EditorCursorKind.Arrow,
            },
            EditorTool.Text => EditorCursorKind.TextBeam,
            EditorTool.Pen => EditorCursorKind.Pen,
            EditorTool.Highlighter => EditorCursorKind.Highlighter,
            EditorTool.StepNumber => EditorCursorKind.StepNumber,
            EditorTool.Crop => EditorCursorKind.Crop,
            EditorTool.Mosaic => EditorCursorKind.Mosaic,
            EditorTool.Blur => EditorCursorKind.Blur,
            EditorTool.Image => EditorCursorKind.Image,
            _ => EditorCursorKind.Cross,
        };
    }

    /// <summary>True for the kinds drawn by <see cref="EditorCursors"/> rather than taken from Windows.</summary>
    internal static bool IsCustom(EditorCursorKind kind) => kind >= EditorCursorKind.Pen;
}
