namespace MyCapture.App.Diagnostics;

/// <summary>
/// Named moments on the way from a confirmed still capture to its quick access card (the lift, the
/// save and copy resuming on the UI thread, the card being scheduled, built and shown, the flight),
/// for the opt-in app probe (<c>AppResponsivenessProbeTests</c>, receipt sections).
/// </summary>
/// <remarks>
/// Off in the product: nothing sets <see cref="Mark"/>, so each moment costs one null check and
/// changes nothing. The probe sets it in its own test process only.
/// </remarks>
internal static class ReceiptTimeline
{
    internal static Action<string>? Mark { get; set; }
}
