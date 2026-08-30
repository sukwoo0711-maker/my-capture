using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;

namespace MyCapture.Core.Settings;

/// <summary>
/// Modifier keys, matching the <c>MOD_*</c> values accepted by <c>RegisterHotKey</c>.
/// </summary>
[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Windows = 0x0008,
}

/// <summary>
/// A global hotkey, stored in settings and registered with the OS verbatim.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="VirtualKey"/> is a Win32 virtual-key code rather than a WPF
/// <c>Key</c>. <c>RegisterHotKey</c> speaks virtual-key codes, and round-tripping
/// through a WPF enum adds a mapping layer that silently loses keys such as the
/// Korean/English toggle or the numeric keypad duplicates.
/// </para>
/// <para>
/// Serialised as a human-editable string (<c>"Ctrl+Shift+C"</c>) so users can fix a
/// hotkey conflict by editing settings.json when the app is not running.
/// </para>
/// </remarks>
[JsonConverter(typeof(HotkeyJsonConverter))]
public sealed record Hotkey
{
    /// <summary>An unassigned hotkey. Registration is skipped.</summary>
    public static Hotkey None { get; } = new(HotkeyModifiers.None, 0);

    public Hotkey(HotkeyModifiers modifiers, uint virtualKey)
    {
        Modifiers = modifiers;
        VirtualKey = virtualKey;
    }

    public HotkeyModifiers Modifiers { get; init; }

    public uint VirtualKey { get; init; }

    [JsonIgnore]
    public bool IsAssigned => VirtualKey != 0;

    // Virtual-key codes used by the defaults and by the parser's friendly names.
    public const uint VkC = 0x43;
    public const uint VkX = 0x58;
    public const uint VkF1 = 0x70;
    public const uint VkF3 = 0x72;

    public override string ToString()
    {
        if (!IsAssigned)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();

        // Order is fixed rather than following the flag order so that a hotkey
        // always renders the way Windows documents it.
        if (Modifiers.HasFlag(HotkeyModifiers.Control)) sb.Append("Ctrl+");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) sb.Append("Alt+");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) sb.Append("Shift+");
        if (Modifiers.HasFlag(HotkeyModifiers.Windows)) sb.Append("Win+");

        sb.Append(KeyName(VirtualKey));
        return sb.ToString();
    }

    public static bool TryParse(string? text, out Hotkey hotkey)
    {
        hotkey = None;

        if (string.IsNullOrWhiteSpace(text))
        {
            return true; // Empty means "unassigned", which is valid.
        }

        HotkeyModifiers modifiers = HotkeyModifiers.None;
        uint key = 0;

        foreach (string rawPart in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (rawPart.ToUpperInvariant())
            {
                case "CTRL" or "CONTROL":
                    modifiers |= HotkeyModifiers.Control;
                    continue;
                case "ALT":
                    modifiers |= HotkeyModifiers.Alt;
                    continue;
                case "SHIFT":
                    modifiers |= HotkeyModifiers.Shift;
                    continue;
                case "WIN" or "WINDOWS":
                    modifiers |= HotkeyModifiers.Windows;
                    continue;
            }

            if (!TryParseKeyName(rawPart, out key))
            {
                return false;
            }
        }

        if (key == 0)
        {
            return false;
        }

        hotkey = new Hotkey(modifiers, key);
        return true;
    }

    private static bool TryParseKeyName(string name, out uint virtualKey)
    {
        virtualKey = 0;
        string upper = name.ToUpperInvariant();

        // Single letter or digit maps directly: VK_A..VK_Z and VK_0..VK_9 share
        // their ASCII values.
        if (upper.Length == 1 && ((upper[0] >= 'A' && upper[0] <= 'Z') || (upper[0] >= '0' && upper[0] <= '9')))
        {
            virtualKey = upper[0];
            return true;
        }

        // Function keys: VK_F1 is 0x70 and the rest are contiguous.
        if (upper.Length >= 2 && upper[0] == 'F' &&
            int.TryParse(upper.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out int fn) &&
            fn is >= 1 and <= 24)
        {
            virtualKey = (uint)(VkF1 + fn - 1);
            return true;
        }

        virtualKey = upper switch
        {
            "INSERT" or "INS" => 0x2D,
            "DELETE" or "DEL" => 0x2E,
            "HOME" => 0x24,
            "END" => 0x23,
            "PAGEUP" or "PGUP" => 0x21,
            "PAGEDOWN" or "PGDN" => 0x22,
            "PRINTSCREEN" or "PRTSC" => 0x2C,
            "SPACE" => 0x20,
            "ESC" or "ESCAPE" => 0x1B,
            "TAB" => 0x09,
            "ENTER" or "RETURN" => 0x0D,
            "BACKSPACE" => 0x08,
            "UP" => 0x26,
            "DOWN" => 0x28,
            "LEFT" => 0x25,
            "RIGHT" => 0x27,
            "OEMTILDE" or "`" => 0xC0,
            "OEMCOMMA" or "," => 0xBC,
            "OEMPERIOD" or "." => 0xBE,
            _ => 0,
        };

        return virtualKey != 0;
    }

    private static string KeyName(uint virtualKey)
    {
        if ((virtualKey >= 'A' && virtualKey <= 'Z') || (virtualKey >= '0' && virtualKey <= '9'))
        {
            return ((char)virtualKey).ToString();
        }

        if (virtualKey is >= VkF1 and <= VkF1 + 23)
        {
            return string.Create(CultureInfo.InvariantCulture, $"F{virtualKey - VkF1 + 1}");
        }

        return virtualKey switch
        {
            0x2D => "Insert",
            0x2E => "Delete",
            0x24 => "Home",
            0x23 => "End",
            0x21 => "PageUp",
            0x22 => "PageDown",
            0x2C => "PrintScreen",
            0x20 => "Space",
            0x1B => "Esc",
            0x09 => "Tab",
            0x0D => "Enter",
            0x08 => "Backspace",
            0x26 => "Up",
            0x28 => "Down",
            0x25 => "Left",
            0x27 => "Right",
            0xC0 => "`",
            0xBC => ",",
            0xBE => ".",
            _ => string.Create(CultureInfo.InvariantCulture, $"VK_0x{virtualKey:X2}"),
        };
    }
}
