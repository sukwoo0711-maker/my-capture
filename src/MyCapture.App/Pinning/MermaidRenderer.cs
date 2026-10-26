using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using MyCapture.App.Diagnostics;
using MyCapture.Platform.Display;

namespace MyCapture.App.Pinning;

/// <summary>A lazily created, network-isolated browser that returns only frozen diagram pixels.</summary>
internal sealed class MermaidRenderer : IMermaidRenderer, IDisposable
{
    internal const string HostName = "mycapture-mermaid.invalid";
    internal const string DocumentUrl = "https://" + HostName + "/index.html";
    internal static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RenderTimeout = TimeSpan.FromSeconds(15);
    private readonly string _userDataFolder;
    private readonly Dispatcher _dispatcher;
    private readonly ILogger? _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly DispatcherTimer _idle;
    private WebView2? _view;
    private Window? _host;
    private TaskCompletionSource<DiagramSize?>? _pending;
    private string? _pendingId;
    private bool _disposed;

    internal MermaidRenderer(string userDataFolder, Dispatcher dispatcher, ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userDataFolder);
        if (!Path.IsPathFullyQualified(userDataFolder)) throw new ArgumentException("An isolated absolute user-data folder is required.", nameof(userDataFolder));
        _userDataFolder = Path.GetFullPath(userDataFolder);
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _logger = logger;
        _idle = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = IdleTimeout };
        _idle.Tick += (_, _) => ReleaseBrowser();
    }

    // Probe hooks do not create a controller or read a user profile.
    internal bool IsBrowserCreated => _view?.CoreWebView2 is not null;
    internal uint BrowserProcessId => _view?.CoreWebView2?.BrowserProcessId ?? 0;

    public Task<MermaidRenderResult> RenderAsync(string source, double scale, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Length is 0 or > 50000)
            return Task.FromResult(new MermaidRenderResult(null, "Mermaid 문법과 길이를 확인하십시오."));
        return _dispatcher.CheckAccess() ? RenderOnDispatcherAsync(source, scale, cancellationToken)
            : _dispatcher.InvokeAsync(() => RenderOnDispatcherAsync(source, scale, cancellationToken)).Task.Unwrap();
    }

    private async Task<MermaidRenderResult> RenderOnDispatcherAsync(string source, double scale, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        _idle.Stop();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RenderTimeout);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await EnsureBrowserAsync(timeout.Token);
            CoreWebView2 core = _view!.CoreWebView2;
            string id = Guid.NewGuid().ToString("N");
            _pendingId = id;
            _pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            await core.ExecuteScriptAsync(BuildRenderScript(source, id)).WaitAsync(timeout.Token);
            DiagramSize? size = await _pending.Task.WaitAsync(timeout.Token);
            if (size is null) return new(null, "Mermaid 문법을 확인하십시오.");
            scale = NormalizeScale(scale);
            if (size.Width * size.Height * scale * scale > 16_000_000)
                return new(null, "Mermaid 그림이 너무 큽니다.");
            string parameters = JsonSerializer.Serialize(new
            {
                format = "png", captureBeyondViewport = true, fromSurface = true,
                clip = new { x = 0, y = 0, width = size.Width, height = size.Height, scale },
            });
            string screenshot = await core.CallDevToolsProtocolMethodAsync("Page.captureScreenshot", parameters).WaitAsync(timeout.Token);
            using JsonDocument response = JsonDocument.Parse(screenshot);
            byte[] png = Convert.FromBase64String(response.RootElement.GetProperty("data").GetString()!);
            cancellationToken.ThrowIfCancellationRequested();
            return new(DecodeBitmap(png, scale), null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ReleaseBrowser();
            throw;
        }
        catch (Exception error) when (error is WebView2RuntimeNotFoundException or DllNotFoundException or BadImageFormatException)
        {
            ReleaseBrowser();
            return new(null, "WebView2 런타임이 없어 그림을 표시할 수 없습니다.");
        }
        catch (Exception error) when (error is COMException or InvalidOperationException or IOException
            or UnauthorizedAccessException or OperationCanceledException or JsonException or FormatException or NotSupportedException)
        {
            // Never put a diagram, page URL, or JavaScript error into the log.
            _logger?.LogWarning("Mermaid rendering failed ({ErrorType})", error.GetType().Name);
            ReleaseBrowser();
            return new(null, "Mermaid 그림을 표시할 수 없습니다.");
        }
        finally
        {
            _pending = null;
            _pendingId = null;
            if (!_disposed && _view is not null) _idle.Start();
            _gate.Release();
        }
    }

    private async Task EnsureBrowserAsync(CancellationToken token)
    {
        if (_view?.CoreWebView2 is not null) return;
        string assets = Path.Combine(AppContext.BaseDirectory, "Assets", "Mermaid");
        if (!File.Exists(Path.Combine(assets, "mermaid.min.js"))) throw new FileNotFoundException("Bundled Mermaid is unavailable.");
        DiagnosticOutputPaths.Create(_userDataFolder);
        var options = new CoreWebView2EnvironmentOptions
        {
            ExclusiveUserDataFolderAccess = true,
            AdditionalBrowserArguments = "--disable-background-networking --disable-component-update --disable-sync --no-first-run --no-default-browser-check --disable-background-timer-throttling --disable-backgrounding-occluded-windows --disable-renderer-backgrounding --host-resolver-rules=\"MAP * ~NOTFOUND\"",
        };
        CoreWebView2Environment environment = await CoreWebView2Environment.CreateAsync(null, _userDataFolder, options).WaitAsync(token);
        token.ThrowIfCancellationRequested();
        if (!string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(environment.UserDataFolder)),
            Path.TrimEndingDirectorySeparator(_userDataFolder), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("WebView2 must use its isolated data folder.");
        if (_disposed) throw new ObjectDisposedException(nameof(MermaidRenderer));
        var view = new WebView2();
        _view = view;
        var host = new Window
        {
            Title = "MyCapture Mermaid renderer", Content = view, Width = 1024, Height = 1024,
            Left = -32000, Top = -32000, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false, ShowActivated = false, Focusable = false, IsHitTestVisible = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        _host = host;
        host.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(host).Handle;
            WindowStyleFacade.SetNoActivate(handle, true);
            WindowStyleFacade.ExcludeFromAltTab(handle);
        };
        host.Show();
        await view.EnsureCoreWebView2Async(environment).WaitAsync(token);
        token.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        CoreWebView2 core = view.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.Settings.AreDefaultScriptDialogsEnabled = false;
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.SetVirtualHostNameToFolderMapping(HostName, assets, CoreWebView2HostResourceAccessKind.DenyCors);
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
        core.WebResourceRequested += (_, args) =>
        {
            if (!IsAllowedResource(args.Request.Uri))
                args.Response = environment.CreateWebResourceResponse(null, 403, "Blocked", "Content-Length: 0\r\n");
        };
        core.NavigationStarting += (_, args) => args.Cancel = !IsAllowedNavigation(args.Uri);
        core.FrameNavigationStarting += (_, args) => args.Cancel = true;
        core.NewWindowRequested += (_, args) => args.Handled = true;
        core.DownloadStarting += (_, args) => args.Cancel = true;
        core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
        core.ServerCertificateErrorDetected += (_, args) => args.Action = CoreWebView2ServerCertificateErrorAction.Cancel;
        core.WebMessageReceived += OnWebMessage;
        core.ProcessFailed += (_, _) => _pending?.TrySetException(new InvalidOperationException("Diagram process ended."));
        var loaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Navigated(object? sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            if (args.IsSuccess) loaded.TrySetResult(true);
            else loaded.TrySetException(new InvalidOperationException("Diagram document failed to load."));
        }
        core.NavigationCompleted += Navigated;
        try
        {
            core.Navigate(DocumentUrl);
            await loaded.Task.WaitAsync(token);
        }
        finally { core.NavigationCompleted -= Navigated; }
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (!IsAllowedNavigation(args.Source) || _pending is null) return;
        try
        {
            using JsonDocument message = JsonDocument.Parse(args.WebMessageAsJson);
            JsonElement value = message.RootElement;
            if (value.GetProperty("id").GetString() != _pendingId) return;
            _pending.TrySetResult(value.GetProperty("ok").GetBoolean()
                ? ReadSize(value.GetProperty("width").GetDouble(), value.GetProperty("height").GetDouble()) : null);
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException)
        { _pending.TrySetResult(null); }
    }

    internal static bool IsAllowedNavigation(string uri) => string.Equals(uri, DocumentUrl, StringComparison.Ordinal);
    internal static bool IsAllowedResource(string address) => Uri.TryCreate(address, UriKind.Absolute, out Uri? uri)
        && uri.Scheme == Uri.UriSchemeHttps && uri.Host == HostName && uri.IsDefaultPort && uri.UserInfo.Length == 0
        && uri.Query.Length == 0 && uri.Fragment.Length == 0
        && uri.AbsolutePath is "/index.html" or "/renderer.css" or "/renderer.js" or "/mermaid.min.js";
    internal static string BuildRenderScript(string source, string id) =>
        $"window.renderDiagram({JsonSerializer.Serialize(source)},{JsonSerializer.Serialize(id)});";
    internal static double NormalizeScale(double scale) => double.IsFinite(scale) && scale > 0 ? Math.Clamp(scale, 0.5, 4) : 1;
    private static DiagramSize? ReadSize(double width, double height) =>
        double.IsFinite(width) && double.IsFinite(height) && width is > 0 and <= 4024 && height is > 0 and <= 4024
            ? new(width, height) : null;
    private sealed record DiagramSize(double Width, double Height);

    internal static BitmapSource DecodeBitmap(byte[] png, double scale)
    {
        using var stream = new MemoryStream(png, writable: false);
        var frame = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        if ((long)frame.PixelWidth * frame.PixelHeight > 16_000_000) throw new InvalidOperationException("Diagram image bounds.");
        var formatted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        int stride = checked(formatted.PixelWidth * 4);
        byte[] pixels = new byte[checked(stride * formatted.PixelHeight)];
        formatted.CopyPixels(pixels, stride, 0);
        var bitmap = BitmapSource.Create(formatted.PixelWidth, formatted.PixelHeight, 96 * NormalizeScale(scale),
            96 * NormalizeScale(scale), PixelFormats.Bgra32, null, pixels, stride);
        bitmap.Freeze();
        return bitmap;
    }

    private void ReleaseBrowser()
    {
        _idle.Stop();
        _pending?.TrySetCanceled();
        WebView2? view = _view;
        Window? host = _host;
        _view = null;
        _host = null;
        if (host is not null) host.Content = null;
        view?.Dispose();
        host?.Close();
        // The environment has exclusive ownership of its profile. Releasing its sole
        // controller lets WebView2 shut down the browser and all renderer/utility children.
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_dispatcher.CheckAccess()) ReleaseBrowser();
        else if (!_dispatcher.HasShutdownStarted) _dispatcher.Invoke(ReleaseBrowser);
    }
}
