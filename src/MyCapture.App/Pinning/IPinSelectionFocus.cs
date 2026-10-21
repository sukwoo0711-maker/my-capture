namespace MyCapture.App.Pinning;

/// <summary>Focus operations for selection mode; tests can observe them without activating a window.</summary>
internal interface IPinSelectionFocus
{
    bool IsActive(PinWindow owner);
    void Enter(PinWindow owner, PinTextSelectionView selection);
    void Restore(PinWindow owner);
}

internal sealed class NativePinSelectionFocus : IPinSelectionFocus
{
    internal static NativePinSelectionFocus Instance { get; } = new();

    public bool IsActive(PinWindow owner) => owner.IsActive;

    public void Enter(PinWindow owner, PinTextSelectionView selection)
    {
        owner.Activate();
        selection.FocusSelection();
    }

    public void Restore(PinWindow owner) => owner.Focus();
}
