using System.IO;
using System.Windows.Media.Imaging;
using MyCapture.App.Editing;
using MyCapture.App.Pinning;
using MyCapture.Core.Capture;
using MyCapture.Core.Settings;
using MyCapture.Platform.Shell;

namespace MyCapture.App.Preview;

/// <summary>Explicit clipboard ports keep human preview I/O real and automated tests isolated.</summary>
internal sealed record PreviewClipboardAccess(
    Func<Task<ClipboardImageReader.PinReadAttempt>> Read,
    Func<BitmapSource, string?, string?, CaptureProvenance?, ClipboardImageService.PngCache?, Task<bool>> CopyImage,
    Func<string, Task<bool>> CopyText)
{
    internal static PreviewClipboardAccess SystemClipboard { get; } = new(
        ClipboardImageReader.ReadPinAsync,
        static (image, url, text, provenance, cache) => ClipboardImageService.CopyImageWithTextAsync(
            image, url, text, writeData: null, provenance, pngCache: cache),
        ClipboardImageService.CopyTextAsync);

    internal PinManager CreateManager(Func<PinSettings> settings, PinImageSaveService? saveService) =>
        new(settings, saveService, CopyImage, CopyText, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

    internal async Task<ClipboardImageReader.PinReadAttempt> ReadWhileActiveAsync(Func<bool> active)
    {
        if (!active()) return ClipboardImageReader.PinReadAttempt.NoContent();
        ClipboardImageReader.PinReadAttempt read = await Read();
        return active() ? read : ClipboardImageReader.PinReadAttempt.NoContent();
    }
}

/// <summary>One preview shortcut lease and its user-visible state; no native calls without a registrar.</summary>
internal sealed class PreviewPinInput : IDisposable
{
    internal const int HotkeyId = 0x76A1;
    internal const uint WmHotkey = 0x0312;
    internal static readonly Hotkey Primary = new(HotkeyModifiers.None, 0x72);
    internal static readonly Hotkey Fallback = new(HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x72);
    internal const string F3UnavailableMessage = "Store판 MyCapture를 종료하면 F3을 쓸 수 있습니다";
    private readonly IHotkeyRegistrar _registrar;
    private readonly Func<Task<PasteResult>> _paste;
    private bool _registered;
    private bool _disposed;

    internal PreviewPinInput(IHotkeyRegistrar registrar, Func<Task<PasteResult>> paste)
    {
        _registrar = registrar ?? throw new ArgumentNullException(nameof(registrar));
        _paste = paste ?? throw new ArgumentNullException(nameof(paste));
    }

    internal event Action? Changed;
    internal string ShortcutText { get; private set; } = "없음";
    internal string ShortcutNotice { get; private set; } = string.Empty;
    internal string StatusText { get; private set; } = "대기";
    internal bool IsBusy { get; private set; }
    internal Task LastPinTask { get; private set; } = Task.CompletedTask;
    internal Exception? LastError { get; private set; }
    internal int PrimaryRegistrationError { get; private set; }
    internal int FallbackRegistrationError { get; private set; }

    internal void RegisterShortcut()
    {
        if (_disposed) return;
        ReleaseShortcut();
        PrimaryRegistrationError = 0;
        FallbackRegistrationError = 0;
        if (_registrar.TryRegister(HotkeyId, Primary, out int primaryError, out _))
        {
            _registered = true;
            ShortcutText = "F3";
            ShortcutNotice = string.Empty;
        }
        else
        {
            PrimaryRegistrationError = primaryError;
            ShortcutNotice = F3UnavailableMessage;
            if (_registrar.TryRegister(HotkeyId, Fallback, out int fallbackError, out _))
            {
                _registered = true;
                ShortcutText = "Ctrl+Shift+F3";
            }
            else
            {
                FallbackRegistrationError = fallbackError;
                ShortcutText = "없음";
                ShortcutNotice += "\nCtrl+Shift+F3도 사용할 수 없습니다. 클립보드 핀 버튼을 사용하십시오.";
            }
        }
        Changed?.Invoke();
    }

    internal bool HandleMessage(uint message, nint wParam)
    {
        if (_disposed || !_registered || message != WmHotkey || wParam != HotkeyId) return false;
        if (!IsBusy) LastPinTask = PinClipboardAsync();
        return true;
    }

    internal async Task PinClipboardAsync()
    {
        if (_disposed || IsBusy) return;
        IsBusy = true;
        LastError = null;
        StatusText = "클립보드 읽는 중";
        Changed?.Invoke();
        try
        {
            PasteResult result = await _paste();
            if (_disposed) return;
            StatusText = result switch
            {
                PasteResult.Pinned => "핀 열림",
                PasteResult.ClipboardBusy => "클립보드를 읽을 수 없습니다. 다시 시도하십시오.",
                _ => "텍스트나 이미지를 복사한 후 다시 시도하십시오.",
            };
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException
            or UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException)
        {
            if (_disposed) return;
            LastError = error;
            StatusText = "핀을 열 수 없습니다. 다시 시도하십시오.";
        }
        finally
        {
            IsBusy = false;
            if (!_disposed) Changed?.Invoke();
        }
    }

    private void ReleaseShortcut()
    {
        if (_registered) _registrar.Unregister(HotkeyId);
        _registered = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ReleaseShortcut();
    }
}
