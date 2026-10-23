using System.Windows;
using MyCapture.Core.Storage;

namespace MyCapture.App.Editing;

internal partial class AnnotationEditorWindow
{
    internal const string DraftFrameStillImageKey = "frameStillImage";
    internal const string DraftRecordIdKey = "recordId";
    internal const string DraftRecordRevisionKey = "recordRevision";
    private EditDraftSession? _draftSession;
    private bool _recoveredDraft;
    private bool _unpersistedCapture;
    private bool _discardedDraft;
    private bool _closedForRecovery;

    /// <summary>
    /// The library capture (and the content revision it was opened at) that this editor saves
    /// to, when one exists. Its draft records both, so recovery commits back onto that capture
    /// instead of creating a new one. Evaluated on the dispatcher for every draft snapshot.
    /// </summary>
    internal Func<(Guid Id, long ContentRevision)?>? DraftRecordSource { get; set; }

    private void InitializeDraftRecovery()
    {
        if (Application.Current is not App { DraftStore: { } store }) return;
        _draftSession = new EditDraftSession(store, Dispatcher, CaptureDraftSnapshotAsync, _ => _editor.ReportDraftFailure());
        _editor.DraftChanged += OnDraftChanged;
    }

    internal async Task<DraftSnapshot?> CaptureDraftSnapshotAsync()
    {
        (Guid Id, long ContentRevision)? record = DraftRecordSource?.Invoke();
        DraftSnapshot? snapshot = await _editor.CaptureDraftAsync();
        if (snapshot is null) return null;
        if (FrameStillImage is not null)
            snapshot.Metadata[DraftFrameStillImageKey] = System.Text.Json.JsonSerializer.Serialize(FrameStillImage);
        if (record is not { } target) return snapshot;
        snapshot.Metadata[DraftRecordIdKey] = target.Id.ToString("N");
        snapshot.Metadata[DraftRecordRevisionKey] =
            target.ContentRevision.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return snapshot;
    }
    private void OnDraftChanged(object? sender, EventArgs e) => _draftSession?.Changed();
    internal void MarkUnpersistedCapture()
    {
        _unpersistedCapture = true;
        _draftSession?.Changed();
    }
    private bool NeedsUnsavedProtection => _editor.HasUnsavedChanges || _recoveredDraft || _unpersistedCapture;
    internal void AdoptRecoveredDraft(EditDraftStore store, Guid id, long revision)
    {
        _recoveredDraft = true;
        _draftSession = new EditDraftSession(store, Dispatcher, CaptureDraftSnapshotAsync, _ => _editor.ReportDraftFailure(), id, revision);
        _editor.DraftChanged -= OnDraftChanged;
        _editor.DraftChanged += OnDraftChanged;
    }
    internal Task FlushDraftAsync() => _draftSession?.FlushAsync() ?? Task.CompletedTask;
    private bool _retiringDraft;
    private Task<bool>? _discardCloseTask;
    internal bool IsCloseInProgress => _retiringDraft || _discardCloseTask is { IsCompleted: false };
    private async Task<bool> CompleteRequestedCloseAsync()
    {
        if (!_closedForRecovery) return false;
        await _draftFinalization;
        return true;
    }
    private async Task<bool> DiscardAndCloseAsync()
    {
        bool enabled = IsEnabled; IsEnabled = false;
        try
        {
            // The first Closing callback must unwind before WPF can accept another Close.
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Input);
            if (!await RetireDraftBeforeCloseAsync()) return false;
            _discardedDraft = true;
            Close();
            await _draftFinalization;
            return _closedForRecovery;
        }
        finally { IsEnabled = enabled; }
    }
    private Task<bool>? _saveCloseTask;
    /// <summary>True from the Save and close answer until its save has closed the window or failed.</summary>
    internal bool IsSavingToClose => _saveCloseTask is { IsCompleted: false };
    /// <summary>
    /// Saves through the Done commit, which closes the window once the edit is saved. Completes with
    /// whether the window closed; it stays open, with the failure in its status line, when saving fails.
    /// </summary>
    private async Task<bool> SaveAndCloseAsync()
    {
        // Started after the Closing callback that asked has unwound: a commit that finishes
        // synchronously must not re-enter Close while the window is still closing.
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Input);
        _editor.SaveAndClose();
        while (_editor.IsCommitInProgress && !_closedForRecovery) await Task.Delay(50);
        await _draftFinalization;
        return _closedForRecovery;
    }
    private async Task<bool> RetireDraftBeforeCloseAsync()
    {
        if (_retiringDraft) return false;
        _retiringDraft = true;
        bool enabled = IsEnabled; IsEnabled = false;
        try { return _draftSession is null || await _draftSession.RetireAsync(); }
        finally { IsEnabled = enabled; _retiringDraft = false; }
    }
    private Task _draftFinalization = Task.CompletedTask;
    private void FinishDraftRecovery()
    {
        _closedForRecovery = true;
        _editor.DraftChanged -= OnDraftChanged;
        if (_draftSession is null) return;
        _draftFinalization = _committed || _discardedDraft || !NeedsUnsavedProtection
            ? _draftSession.RetireAsync() : _draftSession.FlushAsync();
        EditDraftSession.TrackFinalization(_draftFinalization);
    }
    internal async Task<bool> PrepareForAppExitAsync()
    {
        // The discard operation owns its disabled state until it restores the window.
        if (_discardCloseTask is { IsCompleted: false } pendingDiscard)
            return await pendingDiscard;
        bool enabled = IsEnabled; IsEnabled = false;
        try { return await PrepareProtectedExitAsync(); }
        finally { IsEnabled = enabled; }
    }
    private async Task<bool> PrepareProtectedExitAsync()
    {
        if (_closedForRecovery) { await _draftFinalization; return true; }
        var deadline = DateTime.UtcNow.AddSeconds(60);
        // A question already on screen (a capture shortcut asked first) is answered before exit asks.
        // Ordinary close can discard pending mosaics, but protected exit must include a
        // completed gesture in its flush before asking whether to keep the editor open.
        while ((_editor.IsEditingOperationInProgress || _editor.HasUnsettledMosaicWork || IsCloseInProgress || IsAskingToClose || IsSavingToClose) && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        if (_editor.IsEditingOperationInProgress || _editor.HasUnsettledMosaicWork || IsCloseInProgress || IsAskingToClose || IsSavingToClose) return false;
        await FlushDraftAsync();
        if (_closedForRecovery) { await _draftFinalization; return true; }
        if (!_committed && NeedsUnsavedProtection)
        {
            switch (AskBeforeClosing())
            {
                case EditorCloseChoice.Cancel: return false;
                case EditorCloseChoice.SaveAndClose:
                    _editor.SaveAndClose();
                    while (_editor.IsCommitInProgress && DateTime.UtcNow < deadline) await Task.Delay(50);
                    await _draftFinalization;
                    return _closedForRecovery && _committed;
                case EditorCloseChoice.DontSave:
                    if (!await RetireDraftBeforeCloseAsync()) return false;
                    _discardedDraft = true; break;
            }
        }
        Close();
        await _draftFinalization;
        return _closedForRecovery;
    }
}
