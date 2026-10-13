using System.Windows;
using MyCapture.App.Themes;

namespace MyCapture.App.Gallery;

/// <summary>
/// A user deletion that Windows cannot move to the Recycle Bin (bin turned off, removable or
/// network drive, item larger than the bin) is never deleted silently and never triggers a Shell
/// prompt. The library says why and deletes permanently only after an explicit confirmation,
/// whose default is Cancel so Enter never deletes.
/// </summary>
internal static class LibraryPermanentDeletion
{
    internal static bool Confirm(Window owner, int count) =>
        ThemedDialog.Show(owner, UiText.Format("LibrarySelection_RecycleUnavailableMessage", count),
            UiText.Get("LibrarySelection_RecycleUnavailableTitle"), MessageBoxButton.OKCancel, MessageBoxImage.Warning,
            MessageBoxResult.Cancel, okLabel: UiText.Get("LibrarySelection_PermanentDelete"), destructive: true) == MessageBoxResult.OK;

    /// <summary>
    /// The library's delete confirmation: the action says where the items go
    /// ([Move to Recycle Bin]) in the shared danger style, and Enter keeps Cancel.
    /// </summary>
    /// <param name="entireLibrary">Empty the whole library rather than delete a selection.</param>
    internal static bool ConfirmRecycle(Window owner, int count, bool entireLibrary) =>
        ThemedDialog.Show(owner, RecyclePrompt(count, entireLibrary), UiText.Get("LibrarySelection_Delete"),
            MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel,
            okLabel: UiText.Get("LibrarySelection_MoveToRecycleBin"), destructive: true) == MessageBoxResult.OK;

    internal static string RecyclePrompt(int count, bool entireLibrary) =>
        UiText.Format(entireLibrary ? "LibrarySelection_ClearConfirm" : "LibrarySelection_DeleteConfirm", count);

    /// <returns>The permanent pass, or <see langword="null"/> when nothing was refused or the
    /// user kept the items.</returns>
    internal static async Task<T?> ResolveAsync<T>(IReadOnlyList<Guid> recycleUnavailable, Func<int, bool> confirm,
        Func<IReadOnlyList<Guid>, Task<T>> deletePermanently) where T : class
    {
        if (recycleUnavailable.Count == 0 || !confirm(recycleUnavailable.Count)) return null;
        return await deletePermanently(recycleUnavailable);
    }

    internal static string KeptStatus(int count) => UiText.Format("LibrarySelection_RecycleUnavailableKept", count);
}
