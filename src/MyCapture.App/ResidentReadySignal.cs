namespace MyCapture.App;

/// <summary>Manual-reset readiness belongs only to the resident that acquired the ownership mutex.</summary>
internal sealed class ResidentReadySignal : IDisposable
{
    internal const string Name = @"Local\MyCapture.ResidentReady.{6F2A1C34-9B7E-4D51-8A0C-3E5D7B912F48}";
    private readonly EventWaitHandle _ready;

    internal ResidentReadySignal(string name = Name)
    {
        _ready = new EventWaitHandle(false, EventResetMode.ManualReset, name);
        // A secondary may still hold an old handle after the previous resident exits.
        _ready.Reset();
    }

    internal void MarkReady() => _ready.Set();

    internal static bool IsReady(string name = Name)
    {
        if (!EventWaitHandle.TryOpenExisting(name, out EventWaitHandle? ready)) return false;
        using (ready) return ready.WaitOne(0);
    }

    internal static bool NeedsSecondaryNotice(bool quiet, bool ready) => !quiet && !ready;

    public void Dispose()
    {
        _ready.Reset();
        _ready.Dispose();
    }
}
