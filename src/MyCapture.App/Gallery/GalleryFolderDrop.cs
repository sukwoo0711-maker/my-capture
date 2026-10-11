using MyCapture.Core.Queue;

namespace MyCapture.App.Gallery;

/// <summary>What releasing a library drag over a target would do.</summary>
internal enum GalleryDropAction
{
    None,
    /// <summary>Captures move into the target folder.</summary>
    MoveCaptures,
    /// <summary>Captures leave their folder and return to the date library.</summary>
    RemoveFromFolder,
    /// <summary>A folder dragged from the navigation tree moves under the target.</summary>
    MoveFolder,
}

/// <param name="Action">The operation a drop performs, or <see cref="GalleryDropAction.None"/>.</param>
/// <param name="Target">The scope the payload moves into.</param>
/// <param name="Count">Captures that would actually change folder (1 for a folder move).</param>
/// <param name="Hint">The short label shown beside the pointer, for example "제품리뷰 폴더로 이동 · 2개".</param>
internal readonly record struct GalleryDropPlan(GalleryDropAction Action, CaptureFolderScope Target, int Count, string Hint)
{
    internal bool CanDrop => Action != GalleryDropAction.None;
}

/// <summary>
/// Pure drop rules for the library, shared by folder cards, the folder tree, dates and the library root ("라이브러리").
/// </summary>
/// <remarks>
/// <para>
/// Only this window's own payload is accepted: a drag that brings files from File Explorer, or
/// captures from another MyCapture window, carries no matching owner and is refused, so the
/// library never imports or moves anything it did not start. The OLE data object of a capture
/// drag still carries the staged PNG/MP4 files, which is what an external drop (desktop, File
/// Explorer) receives; that export path does not pass through these rules.
/// </para>
/// <para>
/// A drop that would change nothing (every capture already in the target, or a folder dropped on
/// its own parent) is refused so the pointer and hint never promise a move. Folder cards accept
/// captures only; folders are reorganised in the navigation tree as before.
/// </para>
/// </remarks>
internal static class GalleryDropPlanner
{
    internal static GalleryDropPlan Plan(CaptureQueue queue, Guid owner, GalleryFolderDragPayload? payload,
        CaptureFolderScope? target, bool folderCard)
    {
        ArgumentNullException.ThrowIfNull(queue);
        if (payload is null || payload.Owner != owner || target is not { } scope
            || !GalleryWindow.CanDropFolderPayload(queue, payload, scope)) return default;

        if (payload.FolderId is { } folderId)
        {
            if (folderCard || queue.FindFolder(folderId)?.ParentId == scope.FolderId) return default;
            string folderHint = scope.FolderId is { } parent && queue.FindFolder(parent) is { } parentFolder
                ? UiText.Format("LibraryUx_DropFolderInto", parentFolder.Name)
                : UiText.Get("LibraryUx_DropFolderTop");
            return new GalleryDropPlan(GalleryDropAction.MoveFolder, scope, 1, folderHint);
        }

        int moving = payload.Captures.Distinct()
            .Count(id => queue.Find(id) is { } record && record.FolderId != scope.FolderId);
        if (moving == 0) return default;
        if (scope.FolderId is { } destination && queue.FindFolder(destination) is { } folder)
            return new GalleryDropPlan(GalleryDropAction.MoveCaptures, scope, moving,
                UiText.Format("LibraryUx_DropMoveTo", folder.Name, moving));
        return new GalleryDropPlan(GalleryDropAction.RemoveFromFolder, scope, moving,
            UiText.Format("LibraryUx_DropRemoveFromFolder", moving));
    }
}
