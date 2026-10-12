using System.Windows;
using MyCapture.Core.Storage;

namespace MyCapture.App.Editing;

internal partial class AnnotationEditorWindow
{
    private EditDraftSession? _draftSession;
    private bool _recoveredDraft;
    private bool _unpersistedCapture;
    private bool _discardedDraft;
    private bool _closedForRecovery;
    private void InitializeDraftRecovery()
    {
        if (Application.Current is not App { DraftStore: { } store }) return;
        _draftSession = new EditDraftSession(store, Dispatcher, _editor.CaptureDraftAsync, _ => _editor.ReportDraftFailure());
        _editor.DraftChanged += OnDraftChanged;
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
        _draftSession = new EditDraftSession(store, Dispatcher, _editor.CaptureDraftAsync, _ => _editor.ReportDraftFailure(), id, revision);
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
        while ((_editor.IsEditingOperationInProgress || IsCloseInProgress) && DateTime.UtcNow < deadline) await Task.Delay(50);
        if (_editor.IsEditingOperationInProgress || IsCloseInProgress) return false;
        await FlushDraftAsync();
        if (_closedForRecovery) { await _draftFinalization; return true; }
        if (!_committed && NeedsUnsavedProtection)
        {
            switch (ConfirmClose())
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
