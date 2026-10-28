using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Diagnostics;
using MyCapture.App.Pinning;
using MyCapture.App.Themes;
using MyCapture.Core.Platform;
using MyCapture.Core.Settings;
using MyCapture.Ocr;
using MyCapture.Ocr.Translation;
using MyCapture.Platform.Shell;

namespace MyCapture.App.Preview;

/// <summary>A separate-profile pin host using real clipboard I/O and its own global shortcut lease.</summary>
internal sealed class PinPreviewSession : IDisposable
{
    internal const string SampleSource = "# Release checklist\n\n- [x] Build verified\n- [ ] Payment flow\n\nReview before release.\n\n```ts\nconst timeoutMs = 5000;\n```";
    internal const string EnglishSampleSource = "The update takes about 2 minutes.\nIncrease the timeout from 5000 ms to 30000 ms.\nSave your work before closing the window.";
    private readonly PreviewLaunchOptions _options;
    private readonly Dispatcher _dispatcher;
    private readonly AppSettings _settings = PreviewLaunchOptions.CreateSettings();
    private readonly PreviewClipboardAccess _clipboard = PreviewClipboardAccess.SystemClipboard;
    private Mutex? _mutex;
    private EventWaitHandle? _activation;
    private RegisteredWaitHandle? _activationWait;
    private bool _ownsMutex;
    private volatile bool _disposed;
    private PinManager? _pins;
    private MermaidRenderer? _mermaidRenderer;
    private PinTranslationEngines? _translators;
    private MyCapture.Ocr.IOcrService? _ocr;
    private NativeMessageWindow? _messageWindow;
    private PreviewPinInput? _input;

    internal PinPreviewSession(PreviewLaunchOptions options, Dispatcher dispatcher)
    {
        _options = options;
        _dispatcher = dispatcher;
    }

    internal Window? Window { get; private set; }
    internal IStartupRegistration StartupRegistration { get; } = new PreviewStartupRegistration();

    internal bool Start()
    {
        // Check junctions before creating the explicitly supplied preview root.
        DiagnosticOutputPaths.Create(_options.ProfileRoot);
        _activation = new EventWaitHandle(false, EventResetMode.AutoReset, _options.ActivationName);
        _mutex = new Mutex(true, _options.MutexName, out _ownsMutex);
        if (!_ownsMutex)
        {
            _activation.Set();
            return false;
        }

        UiText.Configure(_settings.General.Language);
        _ = PinRenderWarmup.StartAsync();
        ThemeService.ApplyFromSettings(_settings.General);
        var saveService = new PinImageSaveService(() => _settings, () => _options.Paths,
            NullLogger<PinImageSaveService>.Instance);
        _pins = _clipboard.CreateManager(() => _settings.Pin, saveService);
        _mermaidRenderer = new MermaidRenderer(
            DiagnosticOutputPaths.Child(_options.ProfileRoot, "mermaid-browser"), _dispatcher);
        _pins.ConfigureMermaid(_mermaidRenderer);
        if (AppArchitecture.SupportsNeuralOcr)
        {
            // Only this profile's models are visible, and loading remains a manual pin action.
            string models = DiagnosticOutputPaths.Child(_options.ProfileRoot, "translation-models");
            _ = DiagnosticOutputPaths.Child(models, TranslationModelCatalog.FolderName);
            var store = new TranslationModelStore(_options.Paths, NullLogger<TranslationModelStore>.Instance);
            var englishToKorean = new TextTranslationService(store, NullLogger<TextTranslationService>.Instance);
            TextTranslationService? koreanToEnglish = null;
            if (TranslationModelCatalog.KoreanToEnglish.IsPublished)
            {
                var koreanStore = new TranslationModelStore(_options.Paths, NullLogger<TranslationModelStore>.Instance,
                    TranslationModelCatalog.KoreanToEnglish);
                koreanToEnglish = new TextTranslationService(koreanStore, NullLogger<TextTranslationService>.Instance,
                    TranslationModelCatalog.KoreanToEnglish.ModelFiles);
            }
            _translators = new PinTranslationEngines(englishToKorean, koreanToEnglish);
            _pins.ConfigureTranslation(_translators.For);
            // A picture pin is read with Windows OCR, on this PC, when the user asks to translate it.
            _ocr = new WindowsOcrService(NullLogger<WindowsOcrService>.Instance);
            _pins.ConfigureImageTranslation((image, token) => PinTranslationEngines.RecognizeAsync(
                _ocr, image, _settings.Ocr, token));
        }
        _messageWindow = new NativeMessageWindow();
        _input = new PreviewPinInput(new NativeHotkeyRegistrar(_messageWindow.Handle),
            () => _pins.PasteFromClipboardAsync(() => _clipboard.ReadWhileActiveAsync(() => !_disposed)));
        _messageWindow.MessageReceived += OnNativeMessage;
        Window = new PinPreviewWindow(_input);
        Window.Closed += (_, _) => Dispose();
        WindowIdentity.Attach(Window);
        _input.RegisterShortcut();
        _activationWait = ThreadPool.RegisterWaitForSingleObject(_activation, (_, _) =>
        {
            if (_disposed || _dispatcher.HasShutdownStarted) return;
            try
            {
                _ = _dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_disposed || Window is null) return;
                    if (Window.WindowState == WindowState.Minimized) Window.WindowState = WindowState.Normal;
                    Window.Show();
                    Window.Activate();
                }));
            }
            catch (InvalidOperationException) when (_dispatcher.HasShutdownStarted) { }
        }, null, Timeout.Infinite, executeOnlyOnce: false);
        Window.Show();
        return true;
    }

    private void OnNativeMessage(object? sender, NativeWindowMessageEventArgs args)
    {
        if (_input?.HandleMessage(args.Message, args.WParam) == true) args.Handled = true;
    }

    // Kept for deterministic renderer fixtures; the human host always reads the Windows clipboard.
    internal static PinContent CreateContent(string source, bool markdown) => markdown
        ? PinContent.FromMarkdown(ClipboardMarkdownRenderer.Render(source), source)
        : PinContent.FromText(ClipboardTextRenderer.Render(source, false), source, false);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _activationWait?.Unregister(null);
        _input?.Dispose();
        if (_messageWindow is not null) _messageWindow.MessageReceived -= OnNativeMessage;
        _messageWindow?.Dispose();
        _pins?.CloseAllForExit();
        _mermaidRenderer?.Dispose();
        _translators?.Dispose();
        _activation?.Dispose();
        if (_ownsMutex) _mutex?.ReleaseMutex();
        _mutex?.Dispose();
    }
}
