using MyCapture.Core.Undo;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class UndoSavePointTests
{
    [Fact]
    public void UndoingRetainedHistoryAfterOverflowDoesNotReachTheInitialState()
    {
        var value = new Value();
        var history = new UndoStack();
        for (int i = 1; i <= 510; i++) history.Execute(new SetValue(value, i));

        Assert.Equal(500, history.UndoCount);
        history.UndoAll();

        Assert.Equal(10, value.Number);
        Assert.Equal(0, history.UndoCount);
        Assert.Equal(500, history.RedoCount);
        Assert.NotEqual(0, history.CurrentStateId);
        Assert.True(history.HasUnsavedChanges);
    }

    [Fact]
    public void UndoAndRedoRestoreTheExactSavedIdentity()
    {
        var value = new Value();
        var history = new UndoStack();
        Assert.False(history.HasUnsavedChanges);
        history.Execute(new SetValue(value, 1));
        history.MarkSaved();
        long saved = history.CurrentStateId;
        history.Execute(new SetValue(value, 2));
        Assert.True(history.HasUnsavedChanges);

        Assert.True(history.Undo());
        Assert.Equal(saved, history.CurrentStateId);
        Assert.False(history.HasUnsavedChanges);
        Assert.True(history.Undo());
        Assert.True(history.HasUnsavedChanges);
        Assert.True(history.Redo());
        Assert.Equal(saved, history.CurrentStateId);
        Assert.False(history.HasUnsavedChanges);
    }

    [Fact]
    public void NewBranchAtTheSameDepthCannotReuseTheSavedIdentity()
    {
        var value = new Value();
        var history = new UndoStack();
        history.Execute(new SetValue(value, 1));
        history.Execute(new SetValue(value, 2));
        history.MarkSaved();
        long saved = history.CurrentStateId;

        Assert.True(history.Undo());
        history.Execute(new SetValue(value, 3));

        Assert.Equal(2, history.UndoCount);
        Assert.Equal(0, history.RedoCount);
        Assert.NotEqual(saved, history.CurrentStateId);
        Assert.True(history.HasUnsavedChanges);
    }

    [Fact]
    public void MergedCommandsAdvanceIdentityAndUndoToTheirOriginalState()
    {
        var value = new Value();
        var history = new UndoStack(mergeWindow: TimeSpan.FromMinutes(1));
        history.Execute(new SetValue(value, 1, merge: true));
        long first = history.CurrentStateId;
        history.Execute(new SetValue(value, 2, merge: true));
        long merged = history.CurrentStateId;

        Assert.Equal(1, history.UndoCount);
        Assert.Equal("Set value", history.NextUndoDescription);
        Assert.NotEqual(first, merged);
        Assert.True(history.Undo());
        Assert.Equal(0, value.Number);
        Assert.Equal(0, history.CurrentStateId);
        Assert.False(history.HasUnsavedChanges);
        Assert.True(history.Redo());
        Assert.Equal(2, value.Number);
        Assert.Equal(merged, history.CurrentStateId);
        Assert.True(history.HasUnsavedChanges);
    }

    [Fact]
    public void SavingPreventsTheNextCompatibleEditFromMergingAcrossTheCheckpoint()
    {
        var value = new Value();
        var history = new UndoStack(mergeWindow: TimeSpan.FromMinutes(1));
        history.Execute(new SetValue(value, 1, merge: true));
        history.MarkSaved();
        long saved = history.CurrentStateId;
        history.Execute(new SetValue(value, 2, merge: true));

        Assert.Equal(2, history.UndoCount);
        Assert.True(history.Undo());
        Assert.Equal(1, value.Number);
        Assert.Equal(saved, history.CurrentStateId);
        Assert.False(history.HasUnsavedChanges);
    }

    [Fact]
    public void ClearingHistoryPreservesTheDocumentStateAndSavePoint()
    {
        var value = new Value();
        var history = new UndoStack();
        history.Execute(new SetValue(value, 1));
        long edited = history.CurrentStateId;
        history.Clear();

        Assert.Equal(edited, history.CurrentStateId);
        Assert.True(history.HasUnsavedChanges);
        Assert.Equal(0, history.UndoCount);
        history.MarkSaved();
        history.Execute(new SetValue(value, 2));
        Assert.True(history.Undo());
        Assert.Equal(edited, history.CurrentStateId);
        Assert.False(history.HasUnsavedChanges);
        history.Clear();
        Assert.False(history.HasUnsavedChanges);
    }

    [Fact]
    public void NestedBatchHasOneStateTransitionAndCannotBeSavedBeforeItFinishes()
    {
        var value = new Value();
        var history = new UndoStack();
        using (history.BeginBatch("outer"))
        {
            history.Execute(new SetValue(value, 1));
            using (history.BeginBatch("inner")) history.Execute(new SetValue(value, 2));
            Assert.True(history.HasUnsavedChanges);
            Assert.Throws<InvalidOperationException>(history.MarkSaved);
        }

        Assert.Equal(1, history.UndoCount);
        Assert.True(history.Undo());
        Assert.Equal(0, value.Number);
        Assert.False(history.HasUnsavedChanges);
        Assert.True(history.Redo());
        Assert.Equal(2, value.Number);
        Assert.True(history.HasUnsavedChanges);
    }

    [Fact]
    public void EmptyBatchLeavesTheSavePointAndRedoHistoryAlone()
    {
        var value = new Value();
        var history = new UndoStack();
        history.Execute(new SetValue(value, 1));
        Assert.True(history.Undo());
        using (history.BeginBatch("empty")) { }

        Assert.False(history.HasUnsavedChanges);
        Assert.Equal(0, history.CurrentStateId);
        Assert.Equal(1, history.RedoCount);
    }

    [Fact]
    public void AlreadyAppliedCommandGetsTheSameSavePointSemantics()
    {
        var value = new Value();
        var history = new UndoStack();
        var command = new SetValue(value, 7);
        command.Execute();
        history.Push(command);
        long edited = history.CurrentStateId;

        Assert.True(history.HasUnsavedChanges);
        Assert.True(history.Undo());
        Assert.False(history.HasUnsavedChanges);
        Assert.True(history.Redo());
        Assert.Equal(edited, history.CurrentStateId);
        Assert.Equal(7, value.Number);
    }

    private sealed class Value
    {
        public int Number { get; set; }
    }

    private sealed class SetValue(Value value, int next, bool merge = false) : IUndoableCommand
    {
        private readonly int _before = value.Number;
        private int _after = next;
        public string Description => "Set value";
        public void Execute() => value.Number = _after;
        public void Undo() => value.Number = _before;
        public bool TryMergeWith(IUndoableCommand other)
        {
            if (!merge || other is not SetValue command) return false;
            _after = command._after;
            return true;
        }
    }
}
