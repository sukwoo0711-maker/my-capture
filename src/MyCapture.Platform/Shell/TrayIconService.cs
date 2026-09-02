using System.IO;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using MyCapture.Platform.Interop;

namespace MyCapture.Platform.Shell;

public enum TrayIconState
{
    Idle,
    Capturing,
    Busy,
    Error,
}

public enum TrayBalloonKind
{
    Information,
    Warning,
    Error,
}

/// <summary>Absolute paths to the four small, taskbar-safe state ICO assets.</summary>
public sealed record TrayIconAssets(string Idle, string Capturing, string Busy, string Error);

/// <summary>
/// Owns the notification-area icon through Shell_NotifyIcon, including icon state,
/// a native keyboard-accessible context menu, and recovery after Explorer restarts.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private const uint IconId = 1;
    private const uint MenuCapture = 100;
    private const uint MenuGallery = 101;
    private const uint MenuSettings = 102;
    private const uint MenuExit = 103;
    private const uint MenuCaptureWindow = 104;
    private const uint MenuCaptureFullScreen = 105;
    private const uint MenuRepeatLastRegion = 106;
    private const uint MenuDelayedCapture = 107;
    private const uint MenuScrollingCapture = 108;

    private readonly NativeMessageWindow _window;
    private readonly TrayIconAssets _assets;
    private readonly ILogger<TrayIconService> _log;
    private readonly Dictionary<TrayIconState, IntPtr> _icons = [];
    private TrayIconState _state;
    private int _captureCount;
    private bool _initialized;
    private bool _added;
    private bool _usesVersion4;
    private bool _scrollingCaptureActive;
    private bool _disposed;

    public TrayIconService(
        NativeMessageWindow window,
        TrayIconAssets assets,
        ILogger<TrayIconService> log)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _assets = assets ?? throw new ArgumentNullException(nameof(assets));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _window.MessageReceived += OnMessageReceived;
    }

    public event EventHandler? CaptureRequested;
    public event EventHandler? GalleryRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler? ExitRequested;
    public event EventHandler? CaptureWindowRequested;
    public event EventHandler? CaptureFullScreenRequested;
    public event EventHandler? RepeatLastRegionRequested;
    public event EventHandler? DelayedCaptureRequested;
    public event EventHandler? ScrollingCaptureRequested;

    public TrayIconState State => _state;

    public int CaptureCount => _captureCount;

    public void Initialize()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_initialized)
        {
            throw new InvalidOperationException("The tray icon has already been initialized.");
        }

        try
        {
            IntPtr idle = LoadIcon(_assets.Idle);
            _icons.Add(TrayIconState.Idle, idle);
            _icons.Add(TrayIconState.Capturing, LoadIcon(_assets.Capturing));
            _icons.Add(TrayIconState.Busy, LoadIcon(_assets.Busy));
            _icons.Add(TrayIconState.Error, LoadIcon(_assets.Error));
            AddIcon();
            _initialized = true;
        }
        catch
        {
            DestroyLoadedIcons();
            throw;
        }
    }

    public void SetState(TrayIconState state)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureInitialized();

        _state = state;
        ModifyIcon(NativeMethods.NIF_ICON | NativeMethods.NIF_TIP | NativeMethods.NIF_SHOWTIP);
    }

    /// <summary>Changes the scrolling menu label between start and cancel on its next open.</summary>
    public void SetScrollingCaptureActive(bool active)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _scrollingCaptureActive = active;
    }

    /// <summary>
    /// Removes the icon and posts the registered TaskbarCreated message so diagnostics
    /// can verify Explorer-restart recovery without actually restarting Explorer.
    /// </summary>
    public bool PostExplorerRestartDiagnostic()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureInitialized();

        if (_added)
        {
            NativeMethods.NOTIFYICONDATA data = CreateData();
            NotifyOrThrow(NativeMethods.NIM_DELETE, ref data, "remove the tray icon for diagnostics");
            _added = false;
        }

        return _window.Post(_window.TaskbarCreatedMessage, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>Whether this process currently believes its shell icon is installed.</summary>
    public bool IsAdded => _added;


    public void SetCaptureCount(int count)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureInitialized();

        _captureCount = Math.Max(0, count);
        ModifyIcon(NativeMethods.NIF_TIP | NativeMethods.NIF_SHOWTIP);
    }

    public void ShowBalloon(
        string title,
        string message,
        TrayBalloonKind kind = TrayBalloonKind.Information,
        bool playSound = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureInitialized();

        NativeMethods.NOTIFYICONDATA data = CreateData();
        data.uFlags = NativeMethods.NIF_INFO;
        data.szInfoTitle = Truncate(title, 63);
        data.szInfo = Truncate(message, 255);
        data.dwInfoFlags = kind switch
        {
            TrayBalloonKind.Warning => NativeMethods.NIIF_WARNING,
            TrayBalloonKind.Error => NativeMethods.NIIF_ERROR,
            _ => NativeMethods.NIIF_INFO,
        };

        if (!playSound)
        {
            data.dwInfoFlags |= NativeMethods.NIIF_NOSOUND;
        }

        NotifyOrThrow(NativeMethods.NIM_MODIFY, ref data, "show a tray notification");
    }

    private void AddIcon()
    {
        NativeMethods.NOTIFYICONDATA data = CreateData();
        data.uFlags = NativeMethods.NIF_MESSAGE |
                      NativeMethods.NIF_ICON |
                      NativeMethods.NIF_TIP |
                      NativeMethods.NIF_SHOWTIP;
        data.uCallbackMessage = NativeMethods.NOTIFYICON_CALLBACK_MESSAGE;
        data.hIcon = IconForState();
        data.szTip = BuildTooltip();

        NotifyOrThrow(NativeMethods.NIM_ADD, ref data, "add the tray icon");
        _added = true;

        data.uTimeoutOrVersion = NativeMethods.NOTIFYICON_VERSION_4;
        _usesVersion4 = NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_SETVERSION, ref data);
        if (!_usesVersion4)
        {
            _log.LogWarning(
                "Shell_NotifyIcon NIM_SETVERSION failed; using legacy callback semantics");
        }
    }

    private void ModifyIcon(uint flags)
    {
        NativeMethods.NOTIFYICONDATA data = CreateData();
        data.uFlags = flags;

        if ((flags & NativeMethods.NIF_ICON) != 0)
        {
            data.hIcon = IconForState();
        }

        if ((flags & NativeMethods.NIF_TIP) != 0)
        {
            data.szTip = BuildTooltip();
        }

        NotifyOrThrow(NativeMethods.NIM_MODIFY, ref data, "update the tray icon");
    }

    private void OnMessageReceived(object? sender, NativeWindowMessageEventArgs e)
    {
        if (e.Message == _window.TaskbarCreatedMessage)
        {
            if (_initialized)
            {
                _added = false;
                try
                {
                    AddIcon();
                    _log.LogInformation("Restored tray icon after Explorer restart");
                }
                catch (Win32Exception ex)
                {
                    _log.LogError(ex, "Could not restore tray icon after Explorer restart");
                }
            }

            return;
        }

        if (e.Message != NativeMethods.NOTIFYICON_CALLBACK_MESSAGE)
        {
            return;
        }

        int notification = unchecked((ushort)(e.LParam.ToInt64() & 0xFFFF));
        switch (notification)
        {
            case NativeMethods.NIN_SELECT:
            case NativeMethods.NIN_KEYSELECT:
            case NativeMethods.WM_LBUTTONUP:
                e.Handled = true;
                GalleryRequested?.Invoke(this, EventArgs.Empty);
                break;

            case NativeMethods.WM_CONTEXTMENU:
            case NativeMethods.WM_RBUTTONUP:
                e.Handled = true;
                ShowContextMenu(_usesVersion4 ? PointFromPackedValue(e.WParam) : null);
                break;
        }
    }

    private void ShowContextMenu(NativeMethods.POINT? shellAnchor)
    {
        IntPtr menu = NativeMethods.CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            throw NewWin32Exception("create the tray context menu");
        }

        try
        {
            AppendMenu(menu, NativeMethods.MF_STRING, MenuCapture, "영역 캡처(&R)\tCtrl+Shift+C");
            AppendMenu(menu, NativeMethods.MF_STRING, MenuCaptureWindow, "창 캡처(&W)");
            AppendMenu(menu, NativeMethods.MF_STRING, MenuCaptureFullScreen, "전체 화면 캡처(&F)");
            AppendMenu(
                menu,
                NativeMethods.MF_STRING,
                MenuScrollingCapture,
                _scrollingCaptureActive ? "스크롤 캡처 취소(&S)" : "스크롤 캡처(&S)");
            AppendMenu(menu, NativeMethods.MF_STRING, MenuRepeatLastRegion, "이전 영역 반복(&L)");
            AppendMenu(menu, NativeMethods.MF_STRING, MenuDelayedCapture, "지연 캡처(&D)");
            AppendMenu(menu, NativeMethods.MF_SEPARATOR, 0, null);
            AppendMenu(menu, NativeMethods.MF_STRING, MenuGallery, "라이브러리(&G)\tCtrl+Shift+Z");
            AppendMenu(menu, NativeMethods.MF_SEPARATOR, 0, null);
            AppendMenu(menu, NativeMethods.MF_STRING, MenuSettings, "설정(&O)");
            AppendMenu(menu, NativeMethods.MF_SEPARATOR, 0, null);
            AppendMenu(menu, NativeMethods.MF_STRING, MenuExit, "종료(&X)");
            _ = NativeMethods.SetMenuDefaultItem(menu, MenuCapture, 0);

            NativeMethods.POINT point;
            if (shellAnchor is NativeMethods.POINT providedAnchor)
            {
                point = providedAnchor;
            }
            else if (!NativeMethods.GetCursorPos(out point))
            {
                throw NewWin32Exception("locate the tray context menu");
            }

            _ = NativeMethods.SetForegroundWindow(_window.Handle);
            uint selected = NativeMethods.TrackPopupMenuEx(
                menu,
                NativeMethods.TPM_RIGHTBUTTON | NativeMethods.TPM_RETURNCMD,
                point.X,
                point.Y,
                _window.Handle,
                IntPtr.Zero);

            DispatchMenuCommand(selected);
            _ = NativeMethods.PostMessage(
                _window.Handle,
                NativeMethods.WM_NULL,
                IntPtr.Zero,
                IntPtr.Zero);
        }
        finally
        {
            _ = NativeMethods.DestroyMenu(menu);
        }
    }

    private void DispatchMenuCommand(uint selected)
    {
        switch (selected)
        {
            case MenuCapture:
                CaptureRequested?.Invoke(this, EventArgs.Empty);
                break;
            case MenuCaptureWindow:
                CaptureWindowRequested?.Invoke(this, EventArgs.Empty);
                break;
            case MenuCaptureFullScreen:
                CaptureFullScreenRequested?.Invoke(this, EventArgs.Empty);
                break;
            case MenuScrollingCapture:
                ScrollingCaptureRequested?.Invoke(this, EventArgs.Empty);
                break;
            case MenuRepeatLastRegion:
                RepeatLastRegionRequested?.Invoke(this, EventArgs.Empty);
                break;
            case MenuDelayedCapture:
                DelayedCaptureRequested?.Invoke(this, EventArgs.Empty);
                break;
            case MenuGallery:
                GalleryRequested?.Invoke(this, EventArgs.Empty);
                break;
            case MenuSettings:
                SettingsRequested?.Invoke(this, EventArgs.Empty);
                break;
            case MenuExit:
                ExitRequested?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    private static NativeMethods.POINT PointFromPackedValue(IntPtr packed)
    {
        long value = packed.ToInt64();
        return new NativeMethods.POINT
        {
            X = unchecked((short)(value & 0xFFFF)),
            Y = unchecked((short)((value >> 16) & 0xFFFF)),
        };
    }

    private void AppendMenu(IntPtr menu, uint flags, uint command, string? label)
    {
        if (!NativeMethods.AppendMenu(menu, flags, command, label))
        {
            throw NewWin32Exception("build the tray context menu");
        }
    }

    private NativeMethods.NOTIFYICONDATA CreateData() => new()
    {
        cbSize = (uint)Marshal.SizeOf<NativeMethods.NOTIFYICONDATA>(),
        hWnd = _window.Handle,
        uID = IconId,
        szTip = string.Empty,
        szInfo = string.Empty,
        szInfoTitle = string.Empty,
    };

    private IntPtr IconForState() => _icons.TryGetValue(_state, out IntPtr icon)
        ? icon
        : _icons[TrayIconState.Idle];

    private string BuildTooltip()
    {
        string status = _state switch
        {
            TrayIconState.Capturing => "영역 선택 중",
            TrayIconState.Busy => "처리 중",
            TrayIconState.Error => "확인 필요",
            _ => "준비됨",
        };

        return Truncate($"MyCapture — {status} · 캡처 {_captureCount:N0}개", 127);
    }

    private IntPtr LoadIcon(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Tray icon asset was not found.", path);
        }

        uint dpi = NativeMethods.GetDpiForWindow(_window.Handle);
        dpi = dpi == 0 ? 96u : dpi;
        int width = Math.Max(16, NativeMethods.GetSystemMetricsForDpi(NativeMethods.SM_CXSMICON, dpi));
        int height = Math.Max(16, NativeMethods.GetSystemMetricsForDpi(NativeMethods.SM_CYSMICON, dpi));
        IntPtr icon = NativeMethods.LoadImage(
            IntPtr.Zero,
            path,
            NativeMethods.IMAGE_ICON,
            width,
            height,
            NativeMethods.LR_LOADFROMFILE);

        return icon != IntPtr.Zero
            ? icon
            : throw NewWin32Exception($"load tray icon '{path}'");
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    private static void NotifyOrThrow(
        uint operation,
        ref NativeMethods.NOTIFYICONDATA data,
        string action)
    {
        if (!NativeMethods.Shell_NotifyIcon(operation, ref data))
        {
            throw NewWin32Exception(action);
        }
    }

    private static Win32Exception NewWin32Exception(string action)
    {
        int error = Marshal.GetLastWin32Error();
        return new Win32Exception(error, $"Could not {action}. Win32 error {error}.");
    }

    private void EnsureInitialized()
    {
        if (!_initialized)
        {
            throw new InvalidOperationException("The tray icon has not been initialized.");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _window.MessageReceived -= OnMessageReceived;

        if (_added)
        {
            NativeMethods.NOTIFYICONDATA data = CreateData();
            if (!NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_DELETE, ref data))
            {
                _log.LogDebug("Shell_NotifyIcon NIM_DELETE failed during shutdown");
            }

            _added = false;
        }

        DestroyLoadedIcons();
    }

    private void DestroyLoadedIcons()
    {
        foreach (IntPtr icon in _icons.Values.Distinct())
        {
            if (icon != IntPtr.Zero)
            {
                _ = NativeMethods.DestroyIcon(icon);
            }
        }

        _icons.Clear();
    }
}
