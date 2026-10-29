using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Diagnostics;
using MyCapture.App.Pinning;
using MyCapture.Core.Localization;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Ocr;
using MyCapture.Ocr.Translation;
using Xunit;
using Xunit.Abstractions;

namespace MyCapture.App.Tests;

/// <summary>
/// Explicit evidence that a real, visible pin window goes original → overlay → document → overlay →
/// original with real Windows OCR and the English-to-Korean model, keeping its frame throughout.
/// </summary>
public sealed class PinTranslationOverlayWindowEvidenceTests(ITestOutputHelper output)
{
    [PinOverlayEvidenceFact]
    public Task VisiblePinWindowSwitchesOverlayDocumentAndOriginalInPlace() => RunAsync(async () =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        string directory = DiagnosticOutputPaths.Create(
            Environment.GetEnvironmentVariable(PinTranslationOverlayEvidenceTests.OutputVariable)!);
        string? models = Environment.GetEnvironmentVariable(PinTranslationOverlayEvidenceTests.ModelsVariable);
        Assert.False(string.IsNullOrWhiteSpace(models), "Set the model folder variable.");
        TranslationModelSpec model = TranslationModelCatalog.EnglishToKorean;
        string modelDirectory = DiagnosticOutputPaths.Child(models!, model.FolderName);
        using var http = new HttpClient(new RejectingHandler());
        var store = new TranslationModelStore(AppPaths.CreateForRoot(directory), http, NullLogger.Instance,
            files: model.Files, folderName: model.FolderName);
        using var service = new TextTranslationService(store,
            () => MarianTranslator.Load(modelDirectory, model.ModelFiles, MarianTranslator.DefaultThreads),
            NullLogger.Instance, TimeSpan.FromMinutes(10));
        var translator = new InstalledTranslator(service);
        var ocr = new WindowsOcrService(NullLogger<WindowsOcrService>.Instance);
        var manager = new PinManager(() => new PinSettings(), NullLogger.Instance) { ActivatePinForTest = static _ => { } };
        manager.ConfigureTranslation(direction => direction == PinTranslationDirection.EnglishToKorean ? translator : null);
        manager.ConfigureImageTranslation((image, token) =>
            PinTranslationEngines.RecognizeResultAsync(ocr, image, new OcrSettings(), token));
        try
        {
            PinWindow pin = manager.PinImage(PinTranslationOverlayEvidenceTests.Draw("light-ui", korean: false));
            await Settle();
            Rect frame = Frame(pin);
            Capture(directory, "window-0-original.png", pin);

            pin.InvokeTranslationAction();
            await pin.TranslationWork;
            await Settle();
            Assert.Equal(PinTranslationState.Translated, pin.TranslationState);
            Assert.Equal(frame, Frame(pin));
            Capture(directory, "window-1-overlay.png", pin);
            string overlayMenu = Menu(pin);
            output.WriteLine("overlay menu: " + overlayMenu);
            Assert.Contains(UiText.Get("Pin_TranslationDocumentView"), overlayMenu);

            pin.SetTranslationDocumentDisplay(true);
            await Settle();
            Capture(directory, "window-2-document.png", pin);
            string documentMenu = Menu(pin);
            output.WriteLine("document menu: " + documentMenu);
            Assert.Contains(UiText.Get("Pin_TranslationPictureView"), documentMenu);

            pin.SetTranslationDocumentDisplay(false);
            await Settle();
            Assert.Equal(frame, Frame(pin));
            pin.InvokeTranslationAction();
            await Settle();
            Assert.Equal(frame, Frame(pin));
            Capture(directory, "window-3-original-again.png", pin);
            output.WriteLine($"frame {frame}; menu after returning: {Menu(pin)}");
        }
        finally { manager.CloseAll(); }
    });

    private static Rect Frame(Window window) => new(window.Left, window.Top, window.ActualWidth, window.ActualHeight);

    private static async Task Settle()
    {
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        await Task.Delay(400);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    private static string Menu(Window window) => window.ContextMenu is not { } menu ? string.Empty
        : string.Join(" | ", menu.Items.OfType<MenuItem>().Select(item => Header(item.Header)));

    private static string Header(object? header) => header switch
    {
        string text => text,
        TextBlock block => block.Text,
        DependencyObject element => string.Join(" ", LogicalTreeHelper.GetChildren(element).Cast<object>().Select(Header)),
        _ => header?.ToString() ?? string.Empty,
    };

    private static void Capture(string directory, string name, Window window)
    {
        DpiScale dpi = VisualTreeHelper.GetDpi(window);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX),
            (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY), 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(DiagnosticOutputPaths.Child(directory, name));
        encoder.Save(stream);
    }

    private static Task RunAsync(Func<Task> action)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread thread = WpfTestThread.Create(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await action(); completion.TrySetResult(true); }
                catch (Exception ex) { completion.TrySetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); }
            }));
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    /// <summary>The model files are read in place, so the installation check is answered here.</summary>
    private sealed class InstalledTranslator(ITextTranslator inner) : ITextTranslator
    {
        public long ModelDownloadBytes => inner.ModelDownloadBytes;
        public Task<bool> IsModelInstalledAsync(CancellationToken cancellationToken) => Task.FromResult(true);
        public Task DownloadModelAsync(IProgress<TranslationDownloadProgress>? progress, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The evidence run never downloads.");
        public Task<IReadOnlyList<TranslatedText>> TranslateAsync(IReadOnlyList<string> sources,
            IProgress<TranslationProgress>? progress, CancellationToken cancellationToken) =>
            inner.TranslateAsync(sources, progress, cancellationToken);
    }

    private sealed class RejectingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
    }
}
