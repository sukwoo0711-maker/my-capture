using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Core.Capture;
using MyCapture.Core.Serialization;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class ColorPickFormatterTests
{
    [Theory]
    [InlineData(0x1A, 0x2B, 0x3C, ColorFormat.Hex, "#1A2B3C")]
    [InlineData(0x00, 0x00, 0x00, ColorFormat.Hex, "#000000")]
    [InlineData(0xFF, 0xFF, 0xFF, ColorFormat.Hex, "#FFFFFF")]
    [InlineData(26, 43, 60, ColorFormat.Rgb, "rgb(26, 43, 60)")]
    [InlineData(0, 0, 0, ColorFormat.Rgb, "rgb(0, 0, 0)")]
    [InlineData(26, 43, 60, ColorFormat.Hsl, "hsl(210, 40%, 17%)")]
    [InlineData(255, 0, 0, ColorFormat.Hsl, "hsl(0, 100%, 50%)")]
    [InlineData(0, 128, 0, ColorFormat.Hsl, "hsl(120, 100%, 25%)")]
    [InlineData(0, 0, 255, ColorFormat.Hsl, "hsl(240, 100%, 50%)")]
    [InlineData(255, 0, 255, ColorFormat.Hsl, "hsl(300, 100%, 50%)")]
    [InlineData(255, 255, 0, ColorFormat.Hsl, "hsl(60, 100%, 50%)")]
    [InlineData(0, 0, 0, ColorFormat.Hsl, "hsl(0, 0%, 0%)")]
    [InlineData(255, 255, 255, ColorFormat.Hsl, "hsl(0, 0%, 100%)")]
    [InlineData(128, 128, 128, ColorFormat.Hsl, "hsl(0, 0%, 50%)")]
    public void FormatsEveryConfiguredNotation(byte red, byte green, byte blue, ColorFormat format, string expected) =>
        Assert.Equal(expected, ColorSampleFormatter.Format(red, green, blue, format));

    [Fact]
    public void UndefinedFormatFallsBackToHexAndOutputIgnoresCulture()
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            Assert.Equal("#1A2B3C", ColorSampleFormatter.Format(0x1A, 0x2B, 0x3C, (ColorFormat)42));
            Assert.Equal("rgb(26, 43, 60)", ColorSampleFormatter.Format(26, 43, 60, ColorFormat.Rgb));
            Assert.Equal("hsl(210, 40%, 17%)", ColorSampleFormatter.Format(26, 43, 60, ColorFormat.Hsl));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void HslStaysInRangeForEveryPrimaryAndSecondaryHue()
    {
        for (int red = 0; red <= 255; red += 51)
        for (int green = 0; green <= 255; green += 51)
        for (int blue = 0; blue <= 255; blue += 51)
        {
            (int hue, int saturation, int lightness) = ColorSampleFormatter.ToHsl((byte)red, (byte)green, (byte)blue);
            Assert.InRange(hue, 0, 359);
            Assert.InRange(saturation, 0, 100);
            Assert.InRange(lightness, 0, 100);
        }
    }
}

public sealed class TextCaptureSettingsTests
{
    private static readonly Hotkey TextChord = new(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x54); // Ctrl+Alt+T

    [Fact]
    public void TextCaptureHotkeyIsUnassignedByDefaultAndInOlderFiles()
    {
        Assert.Equal(Hotkey.None, new AppSettings().Hotkeys.CaptureText);
        AppSettings older = JsonSerializer.Deserialize<AppSettings>(
            """{"hotkeys":{"capture":"Ctrl+Shift+C","repeatLastRegion":"Ctrl+Alt+R"}}""", JsonDefaults.Readable)!;
        Assert.Equal(Hotkey.None, older.Hotkeys.CaptureText);
        Assert.Equal("Ctrl+Alt+R", older.Hotkeys.RepeatLastRegion.ToString());
        Assert.True(older.Capture.AutoDetectWindows);
        Assert.Equal(ColorFormat.Hex, older.Capture.ColorFormat);
        Assert.Empty(new SettingsDraft(older).CaptureTextHotkey);
    }

    [Fact]
    public void TextCaptureHotkeyRoundTripsThroughCloneDraftAndDisk()
    {
        using var workspace = new TempWorkspace();
        var source = new AppSettings();
        source.Hotkeys.CaptureText = TextChord;
        Assert.Equal(TextChord, source.DeepClone().Hotkeys.CaptureText);

        var draft = new SettingsDraft(source);
        Assert.Equal("Ctrl+Alt+T", draft.CaptureTextHotkey);
        Assert.Equal(TextChord, draft.ToHotkeySettings().CaptureText);
        AppSettings mapped = draft.ToAppSettings();
        Assert.Equal(TextChord, mapped.Hotkeys.CaptureText);

        var store = new SettingsStore(workspace.Paths, NullLogger<SettingsStore>.Instance);
        store.Save(mapped);
        Assert.Contains("\"captureText\":", File.ReadAllText(workspace.Paths.SettingsFile));
        AppSettings loaded = store.Load();
        Assert.Equal(TextChord, loaded.Hotkeys.CaptureText);
        Assert.False(store.LastLoadMigratedUnsafeHotkeys);

        var cleared = new SettingsDraft(loaded) { CaptureTextHotkey = "" };
        Assert.False(cleared.HasErrors);
        Assert.Equal(Hotkey.None, cleared.ToAppSettings().Hotkeys.CaptureText);
    }

    [Fact]
    public void TextCaptureHotkeyTakesPartInDuplicateAndSafetyValidation()
    {
        var draft = new SettingsDraft(new AppSettings()) { CaptureTextHotkey = "Ctrl+Shift+C" };
        Assert.NotEmpty(draft.GetErrors(nameof(draft.CaptureHotkey)).Cast<string>());
        Assert.NotEmpty(draft.GetErrors(nameof(draft.CaptureTextHotkey)).Cast<string>());
        draft.CaptureTextHotkey = "T";
        Assert.Empty(draft.GetErrors(nameof(draft.CaptureHotkey)).Cast<string>());
        Assert.NotEmpty(draft.GetErrors(nameof(draft.CaptureTextHotkey)).Cast<string>());
        draft.CaptureTextHotkey = "Ctrl+Alt+T";
        Assert.False(draft.HasErrors);
    }

    [Fact]
    public void UnsafeTextCaptureHotkeyIsClearedAndPersistedOnce()
    {
        using var workspace = new TempWorkspace();
        workspace.Paths.EnsureCreated();
        File.WriteAllText(workspace.Paths.SettingsFile,
            """{"schemaVersion":4,"general":{"launchAtLogin":false},"hotkeys":{"capture":"Ctrl+Shift+C","captureText":"T"}}""");
        var store = new SettingsStore(workspace.Paths, NullLogger<SettingsStore>.Instance);
        AppSettings repaired = store.Load();
        Assert.Equal(Hotkey.None, repaired.Hotkeys.CaptureText);
        Assert.True(store.LastLoadMigratedUnsafeHotkeys);
        var next = new SettingsStore(workspace.Paths, NullLogger<SettingsStore>.Instance);
        Assert.Equal(Hotkey.None, next.Load().Hotkeys.CaptureText);
        Assert.False(next.LastLoadMigratedUnsafeHotkeys);
    }

    [Theory]
    [InlineData(ColorFormat.Hex)]
    [InlineData(ColorFormat.Rgb)]
    [InlineData(ColorFormat.Hsl)]
    public void ColorFormatAndAutoDetectAreEditableAndRoundTrip(ColorFormat format)
    {
        using var workspace = new TempWorkspace();
        var draft = new SettingsDraft(new AppSettings()) { ColorFormat = format, AutoDetectWindows = false };
        AppSettings mapped = draft.ToAppSettings();
        Assert.Equal(format, mapped.Capture.ColorFormat);
        Assert.False(mapped.Capture.AutoDetectWindows);
        var store = new SettingsStore(workspace.Paths, NullLogger<SettingsStore>.Instance);
        store.Save(mapped);
        AppSettings loaded = store.Load();
        Assert.Equal(format, loaded.Capture.ColorFormat);
        Assert.False(loaded.Capture.AutoDetectWindows);
        Assert.Equal(format, new SettingsDraft(loaded).ColorFormat);
    }

    [Fact]
    public void UndefinedColorFormatFallsBackToHexOnLoadAndInTheDraft()
    {
        using var workspace = new TempWorkspace();
        workspace.Paths.EnsureCreated();
        File.WriteAllText(workspace.Paths.SettingsFile, """{"schemaVersion":4,"capture":{"colorFormat":42}}""");
        AppSettings loaded = new SettingsStore(workspace.Paths, NullLogger<SettingsStore>.Instance).Load();
        Assert.Equal(ColorFormat.Hex, loaded.Capture.ColorFormat);
        var draft = new SettingsDraft(loaded) { ColorFormat = (ColorFormat)7 };
        Assert.Equal(ColorFormat.Hex, draft.ColorFormat);
    }
}
