using System.Windows.Media.Imaging;
using MyCapture.Core.Localization;
using MyCapture.App.Themes;
using MyCapture.Ocr;

namespace MyCapture.App.Pinning;

internal sealed partial class PinWindow
{
    private CancellationTokenSource? _selectionRecognition;
    internal Func<BitmapSource, CancellationToken, Task<OcrResult>>? RecognizeSelection { get; set; }

    internal Task SelectionWork { get; private set; } = Task.CompletedTask;
    internal bool IsRecognizingSelection => _selectionRecognition is not null;

    private async Task EnterImageSelectionAsync()
    {
        if (RecognizeSelection is not { } recognize || _isClosed || _state.IsHidden) return;
        var cancellation = new CancellationTokenSource();
        _selectionRecognition = cancellation;
        ShowFeedback(UiText.Get("Pin_SelectTextRecognizing"), FeedbackKind.Progress);
        try
        {
            BitmapSource image = await PinTranslationService.FreezeOverlaySourceAsync(DisplayContent.Image, cancellation.Token);
            // Frozen pixels can cross threads; synchronous OCR preparation cannot block
            // moving, closing, or the next F2/Esc while this mode is being prepared.
            OcrResult result = await Task.Run(() => recognize(image, cancellation.Token), cancellation.Token);
            if (!ReferenceEquals(_selectionRecognition, cancellation) || cancellation.IsCancellationRequested
                || _isClosed || _state.IsHidden) return;
            _selectionRecognition = null;
            if (!result.HasText)
            {
                ShowFeedback(UiText.Get("Pin_TranslationNoText"), FeedbackKind.Warning);
                return;
            }
            var selection = new PinTextSelectionView(result, image.PixelWidth, image.PixelHeight);
            if (selection.ImageSelection!.Selection.Words.Length == 0)
            {
                ShowFeedback(UiText.Get("Pin_TranslationNoText"), FeedbackKind.Warning);
                return;
            }
            EnterTextSelection(selection);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception)
        {
            if (ReferenceEquals(_selectionRecognition, cancellation) && !_isClosed)
                ShowFeedback(UiText.Get("Text_AA254F35F02E"), FeedbackKind.Warning);
        }
        finally
        {
            if (ReferenceEquals(_selectionRecognition, cancellation)) _selectionRecognition = null;
            cancellation.Dispose();
        }
    }

    private bool CancelSelectionRecognition()
    {
        if (_selectionRecognition is not { } cancellation) return false;
        _selectionRecognition = null;
        cancellation.Cancel();
        DismissFeedback();
        return true;
    }
}
