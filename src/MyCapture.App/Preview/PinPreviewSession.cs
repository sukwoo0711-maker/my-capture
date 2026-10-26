using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Diagnostics;
using MyCapture.App.Pinning;
using MyCapture.App.Themes;
using MyCapture.Core.Platform;
using MyCapture.Core.Settings;
using MyCapture.Ocr.Translation;
using MyCapture.Platform.Shell;

namespace MyCapture.App.Preview;

/// <summary>A user-operated pin host with no resident shell, profile store or global shortcuts.</summary>
internal sealed class PinPreviewSession : IDisposable
{
    internal const string SampleSource = "# Release checklist\n\n- [x] Build verified\n- [ ] Payment flow\n\nReview before release.\n\n```ts\nconst timeoutMs = 5000;\n```";
    internal const string EnglishSampleSource = "The update takes about two minutes.\nIncrease the timeout from 5000 ms to 30000 ms.\nSave your work before closing the window.";
    private readonly PreviewLaunchOptions _options;
    private readonly Dispatcher _dispatcher;
    private readonly AppSettings _settings = PreviewLaunchOptions.CreateSettings();
    private readonly PreviewCopyBuffer _copies = new();
    private Mutex? _mutex;
    private EventWaitHandle? _activation;
    private RegisteredWaitHandle? _activationWait;
    private bool _ownsMutex;
    private volatile bool _disposed;
    private PinManager? _pins;
    private TextTranslationService? _translator;

    internal PinPreviewSession(PreviewLaunchOptions options, Dispatcher dispatcher)
    {
        _options = options;
        _dispatcher = dispatcher;
    }

    internal Window? Window { get; private set; }
    internal IStartupRegistration StartupRegistration { get; } = new PreviewStartupRegistration();

    internal bool Start()
    {
        // This checks junctions before creating the one explicitly supplied preview root.
        DiagnosticOutputPaths.Create(_options.ProfileRoot);
        _activation = new EventWaitHandle(false, EventResetMode.AutoReset, _options.ActivationName);
        _mutex = new Mutex(true, _options.MutexName, out _ownsMutex);
        if (!_ownsMutex)
        {
            NativeMessageWindow.AllowResidentForegroundActivation();
            _activation.Set();
            return false;
        }

        UiText.Configure(_settings.General.Language);
        ThemeService.ApplyFromSettings(_settings.General);
        _pins = new PinManager(() => _settings.Pin, saveService: null,
            (_, _, text) => _copies.CopyTextAsync(text ?? string.Empty),
            _copies.CopyTextAsync, NullLogger.Instance);
        _pins.BeforeShowForTest = pin => pin.OpenSource = static _ => Task.FromResult(string.Empty);
        if (AppArchitecture.SupportsNeuralOcr)
        {
            // Only this profile's verified model files are visible. No model is read until
            // the user invokes the normal translation action on a pin.
            string models = DiagnosticOutputPaths.Child(_options.ProfileRoot, "translation-models");
            _ = DiagnosticOutputPaths.Child(models, TranslationModelCatalog.FolderName);
            var store = new TranslationModelStore(_options.Paths, NullLogger<TranslationModelStore>.Instance);
            _translator = new TextTranslationService(store, NullLogger<TextTranslationService>.Instance);
            _pins.ConfigureTranslation(_translator);
        }
        Window = CreateWindow();
        WindowIdentity.Attach(Window);
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

    private Window CreateWindow()
    {
        var window = new Window
        {
            Title = "MyCapture 미리보기", Width = 660, Height = 650,
            MinWidth = 480, MinHeight = 480, WindowStartupLocation = WindowStartupLocation.CenterScreen,
        };
        var layout = new Grid { Margin = new Thickness(20) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var sourceLabel = new Label { Content = "원문", Padding = new Thickness(0, 0, 0, 8) };
        layout.Children.Add(sourceLabel);
        TextBox markdownSource = CreateSourceBox(SampleSource, "Consolas");
        TextBox englishSource = CreateSourceBox(EnglishSampleSource, "Segoe UI");
        PreviewCopyCommands.Attach(markdownSource, _copies);
        PreviewCopyCommands.Attach(englishSource, _copies);
        var sources = new TabControl();
        sources.Items.Add(new TabItem { Header = "Markdown", Content = markdownSource });
        sources.Items.Add(new TabItem { Header = "영어", Content = englishSource });
        sources.SelectedIndex = 0;
        sourceLabel.Target = markdownSource;
        sources.SelectionChanged += (_, _) => sourceLabel.Target = sources.SelectedIndex == 0 ? markdownSource : englishSource;
        Grid.SetRow(sources, 1);
        layout.Children.Add(sources);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 12) };
        AddOpenButton(actions, () => sources.SelectedIndex == 0
            ? (markdownSource.Text, true) : (englishSource.Text, false));
        Grid.SetRow(actions, 2);
        layout.Children.Add(actions);
        var copyLabel = new Label { Content = "복사 결과", Padding = new Thickness(0, 0, 0, 8) };
        Grid.SetRow(copyLabel, 3);
        layout.Children.Add(copyLabel);
        var result = new TextBox
        {
            IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontSize = 15, Padding = new Thickness(10),
        };
        PreviewCopyCommands.Attach(result, _copies);
        copyLabel.Target = result;
        _copies.Changed += text => result.Text = text;
        Grid.SetRow(result, 4);
        layout.Children.Add(result);
        window.Content = layout;
        return window;
    }

    private static TextBox CreateSourceBox(string source, string font) => new()
    {
        Text = source, AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new FontFamily(font),
        FontSize = 15, Padding = new Thickness(10),
    };

    private void AddOpenButton(Panel panel, Func<(string Text, bool Markdown)> readSource)
    {
        var button = new Button { Content = "핀 열기", Padding = new Thickness(14, 8, 14, 8) };
        button.SetResourceReference(FrameworkElement.StyleProperty, "Button.Primary");
        button.Click += async (_, _) =>
        {
            (string text, bool markdown) = readSource();
            if (_disposed || _pins is null || string.IsNullOrWhiteSpace(text)) return;
            button.IsEnabled = false;
            try
            {
                PinContent content = CreateContent(text, markdown);
                // The injected reader is mandatory: no Windows clipboard API is used.
                await _pins.PasteFromClipboardAsync(() => Task.FromResult(ClipboardImageReader.PinReadAttempt.Success(content)));
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException)
            {
                ThemedDialog.Show("핀을 열 수 없습니다.", "MyCapture 미리보기", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally { if (!_disposed) button.IsEnabled = true; }
        };
        panel.Children.Add(button);
    }

    internal static PinContent CreateContent(string source, bool markdown) => markdown
        ? PinContent.FromMarkdown(ClipboardMarkdownRenderer.Render(source), source)
        : PinContent.FromText(ClipboardTextRenderer.Render(source, false), source, false);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _activationWait?.Unregister(null);
        _pins?.CloseAllForExit();
        _translator?.Dispose();
        _activation?.Dispose();
        if (_ownsMutex) _mutex?.ReleaseMutex();
        _mutex?.Dispose();
    }
}
