using System.Text.Json;
using System.Windows;
using Microsoft.Extensions.Logging;
using MyCapture.App.Editing;
using MyCapture.Core.Serialization;
using MyCapture.Core.Storage;

namespace MyCapture.App.Recording;

internal sealed partial class VideoEditorWindow
{
    private EditDraftSession? _draftSession;
    private bool _recoveredDraft;
    private bool _discardedDraft;
    private bool _closedForRecovery;
    private string? _draftVideoSourceHash;
    private void InitializeDraftRecovery()
    {
        if (Application.Current is not App { DraftStore: { } store }) return;
        _draftSession = MakeDraftSession(store);
    }
    private EditDraftSession MakeDraftSession(EditDraftStore store, Guid? id = null, long revision = 0) =>
        new(store, Dispatcher, async () =>
        {
            if (!_mediaReady || _committed) return null;
            string json = JsonSerializer.Serialize(BuildCurrentDocument(), JsonDefaults.Compact);
            var recording = _recording;
            string source = await Task.Run(() => store.EnsureVideoSourceAsync(_draftSession!.Id, recording.OutputPath));
            return new DraftSnapshot("video", json, new Dictionary<string, string>
            {
                ["recording"] = JsonSerializer.Serialize(recording, JsonDefaults.Compact),
                ["source"] = source,
                ["sourceHash"] = _draftVideoSourceHash ??= await Task.Run(() =>
                {
                    using var stream = System.IO.File.OpenRead(source);
                    return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
                }),
            }, new Dictionary<string, byte[]>());
        }, error =>
        {
            _log.LogError(error, "Could not preserve video editing draft");
            _statusLabel.Text = UiText.Get("EditRecovery_SaveFailed");
        }, id, revision);
    internal void AdoptRecoveredDraft(EditDraftStore store, Guid id, long revision)
    {
        _recoveredDraft = true;
        _draftSession = MakeDraftSession(store, id, revision);
    }
    internal Task FlushDraftAsync() => _draftSession?.FlushAsync() ?? Task.CompletedTask;
    private async Task<bool> RetireDraftBeforeCloseAsync()
    {
        bool running = _operationRunning;
        SetOperationRunning(true);
        try { return _draftSession is null || await _draftSession.RetireAsync(); }
        finally { SetOperationRunning(running); }
    }
    private Task _draftFinalization = Task.CompletedTask;
    private void FinishDraftRecovery()
    {
        _closedForRecovery = true;
        if (_draftSession is null) return;
        _draftFinalization = _committed || _discardedDraft || !HasUnsavedEdits()
            ? _draftSession.RetireAsync() : _draftSession.FlushAsync();
        EditDraftSession.TrackFinalization(_draftFinalization);
    }
    internal async Task<bool> PrepareForAppExitAsync()
    {
        bool enabled = IsEnabled; IsEnabled = false;
        try { return await PrepareProtectedExitAsync(); }
        finally { IsEnabled = enabled; }
    }
    private async Task<bool> PrepareProtectedExitAsync()
    {
        if (_closedForRecovery) { await _draftFinalization; return true; }
        await FlushDraftAsync();
        DateTime deadline = DateTime.UtcNow.AddSeconds(60);
        if (_exportDialog is { IsVisible: true } export)
        {
            export.Close();
            while (export.IsVisible && DateTime.UtcNow < deadline) await Task.Delay(50);
            if (export.IsVisible) return false;
        }
        // Save/finalize operations finish naturally; cancellation cannot silently discard edits.
        while (_operationRunning && DateTime.UtcNow < deadline) await Task.Delay(50);
        if (_operationRunning) return false;
        if (_closedForRecovery) { await _draftFinalization; return true; }
        if (HasUnsavedEdits())
        {
            EditorCloseChoice choice = AskUnsavedEdits();
            if (choice == EditorCloseChoice.Cancel) return false;
            if (choice == EditorCloseChoice.SaveAndClose)
            {
                CommitTrim();
                while (_operationRunning && DateTime.UtcNow < deadline) await Task.Delay(50);
                await _draftFinalization;
                return _closedForRecovery && _committed;
            }
            if (!await RetireDraftBeforeCloseAsync()) return false;
            _discardedDraft = true;
        }
        Close(); await _draftFinalization; return _closedForRecovery;
    }
}
