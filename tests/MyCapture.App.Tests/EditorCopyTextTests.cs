using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.App.Editing;
using MyCapture.App.Ocr;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// The editor recognises the text of the image as edited, from a command beside the redaction
/// command.
/// </summary>
public sealed class EditorCopyTextTests
{
    [Fact]
    public Task CopyTextSitsBesideRedactionAndRecognisesTheEditedImage() => RunStaAsync(async () =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        var shown = new List<(BitmapSource Image, string Context)>();
        try
        {
            // Without the app's recognition route the command is there but disabled.
            AnnotationEditorControl offline = Editor(out _);
            Button unavailable = Command(offline, "EditorFlowCopyText");
            Assert.False(unavailable.IsEnabled);
            Assert.Equal(UiText.Get("TextCapture_Unavailable"), AutomationProperties.GetHelpText(unavailable));

            TextRecognitionRoute.Show = (image, context) => shown.Add((image, context));
            TextRecognitionRoute.IsAvailable = () => true;
            AnnotationEditorControl editor = Editor(out BitmapSource bitmap);
            Button copyText = Command(editor, "EditorFlowCopyText");
            Assert.True(copyText.IsEnabled);
            Assert.Equal("텍스트 복사", AutomationProperties.GetName(copyText));
            Assert.Equal(UiText.Get("EditorFlow_CopyTextHint"), copyText.ToolTip);
            var row = (Panel)copyText.Parent;
            Assert.Equal(UiText.Get("Text_E395E2567985"), AutomationProperties.GetName(row.Children[row.Children.IndexOf(copyText) - 1]));

            copyText.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            // Disabled while the edited image renders, so one click is one recognition.
            Assert.False(copyText.IsEnabled);
            for (int attempt = 0; attempt < 200 && (shown.Count == 0 || !copyText.IsEnabled); attempt++)
                await Task.Delay(10);
            (BitmapSource image, string context) = Assert.Single(shown);
            Assert.Equal((bitmap.PixelWidth, bitmap.PixelHeight), (image.PixelWidth, image.PixelHeight));
            Assert.Equal("텍스트 복사", context);
            Assert.True(copyText.IsEnabled);

            // When the engine goes away, a click says so instead of opening an empty window.
            TextRecognitionRoute.IsAvailable = () => false;
            await editor.RecognizeCurrentTextAsync();
            Assert.Single(shown);
            Assert.Equal(UiText.Get("TextCapture_Unavailable"), editor.StatusText);
            Assert.False(copyText.IsEnabled);
        }
        finally
        {
            TextRecognitionRoute.Show = null;
            TextRecognitionRoute.IsAvailable = null;
        }
    });

    private static AnnotationEditorControl Editor(out BitmapSource bitmap)
    {
        byte[] pixels = new byte[160 * 90 * 4];
        Array.Fill(pixels, (byte)0xDD);
        bitmap = BitmapSource.Create(160, 90, 96, 96, PixelFormats.Bgra32, null, pixels, 160 * 4);
        bitmap.Freeze();
        var region = new RectD(0, 0, 160, 90);
        return new AnnotationEditorControl(new FrozenFrame(bitmap, region, null, 0), region, bitmap);
    }

    private static Button Command(AnnotationEditorControl editor, string automationId) =>
        Descendants(editor).OfType<Button>().Single(button => AutomationProperties.GetAutomationId(button) == automationId);

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        foreach (object child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is not DependencyObject node) continue;
            yield return node;
            foreach (DependencyObject descendant in Descendants(node)) yield return descendant;
        }
    }

    private static Task RunStaAsync(Func<Task> action)
    {
        var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = WpfTestThread.Create(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            try
            {
                Task work = action();
                var running = new DispatcherFrame();
                _ = work.ContinueWith(_ => dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() => running.Continue = false)),
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                Dispatcher.PushFrame(running);
                // The editor builds text boxes: shut down through the TSF cleanup, which a direct
                // shutdown would skip, and still before the result is reported.
                WpfTestThread.ShutdownDispatcher();
                work.GetAwaiter().GetResult();
                completion.TrySetResult(null);
            }
            catch (Exception ex)
            {
                completion.TrySetException(new Xunit.Sdk.XunitException($"Async STA body threw: {ex}"));
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
