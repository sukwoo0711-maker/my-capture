namespace MyCapture.Core.Recording;

/// <summary>Operations allowed by the probed capture APIs, before any WinRT calls.</summary>
public readonly record struct WgcBorderPlan(
    bool RequestAccess, bool SetProperty, bool BorderRequired, bool ReadProperty, string Diagnostic);

public static class WgcBorderPolicy
{
    public static WgcBorderPlan Resolve(bool propertyPresent, bool accessPresent, bool requestBorderless)
    {
        if (!propertyPresent)
        {
            return new(false, false, true, false, "unavailable (pre-Windows 11)");
        }

        if (!requestBorderless)
        {
            return new(false, true, true, true, "not requested (border forced on)");
        }

        return accessPresent
            ? new(true, true, false, true, "pending")
            : new(false, false, true, true, "unavailable (borderless access API missing)");
    }
}
