using System.Windows;
using MyCapture.App.Themes;

namespace MyCapture.App.Gallery;

/// <summary>Library deletion prompts and the preservation policy for unavailable recycling.</summary>
internal static class LibraryPermanentDeletion
{
    /// <summary>
    /// The library's delete confirmation: the action says where the items go
    /// ([Move to Recycle Bin]) in the shared danger style, and Enter keeps Cancel.
    /// </summary>
    /// <param name="entireLibrary">Empty the whole library rather than delete a selection.</param>
    internal static bool ConfirmRecycle(Window owner, int count, bool entireLibrary) =>
        ThemedDialog.Present(owner, CreateRecycleConfirmation(count, entireLibrary), MessageBoxImage.Warning) == MessageBoxResult.OK;

    /// <summary>
    /// The confirmation <see cref="ConfirmRecycle"/> shows, before it is placed or shown. The
    /// tutorial clip generator renders it offscreen.
    /// </summary>
    internal static ThemedDialog CreateRecycleConfirmation(int count, bool entireLibrary) =>
        new(RecyclePrompt(count, entireLibrary), UiText.Get("LibrarySelection_Delete"), MessageBoxButton.OKCancel,
            MessageBoxResult.Cancel, okLabel: UiText.Get("LibrarySelection_MoveToRecycleBin"), destructive: true);

    internal static string RecyclePrompt(int count, bool entireLibrary) =>
        UiText.Format(entireLibrary ? "LibrarySelection_ClearConfirm" : "LibrarySelection_DeleteConfirm", count);

    // Kept for the existing gallery call boundary. Refused recycling never prompts for or
    // invokes permanent deletion, including when an older caller supplies those delegates.
    internal static Task<T?> ResolveAsync<T>(IReadOnlyList<Guid> recycleUnavailable, Func<int, bool> confirm,
        Func<IReadOnlyList<Guid>, Task<T>> deletePermanently) where T : class => Task.FromResult<T?>(null);

    internal static bool Confirm(Window owner, int count) => false;

    internal static string KeptStatus(int count) => UiText.Format("LibrarySelection_RecycleUnavailableKept", count);
}
