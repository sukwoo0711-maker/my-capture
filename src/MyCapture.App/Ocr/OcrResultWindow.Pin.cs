using System.Windows;
using System.Windows.Controls;
using MyCapture.Ocr;

namespace MyCapture.App.Ocr;

internal sealed class OcrPinRequest(string text, OcrExtractionMode kind) : EventArgs
{
    internal string Text { get; } = text;
    internal OcrExtractionMode Kind { get; } = kind;
    internal Task<bool>? Completion { get; set; }
}

internal sealed partial class OcrResultWindow
{
    private Button _pinButton = null!;
    private Button _tablePinButton = null!;
    private bool _pinning;
    internal event EventHandler<OcrPinRequest>? PinRequested;

    private Button CreatePinButton()
    {
        var button = MakeButton(UiText.Get("Ocr_Pin"), UiText.Get("Ocr_Pin"));
        button.SetResourceReference(StyleProperty, "Button.Secondary");
        button.Margin = new Thickness(0, 2, 8, 2);
        button.Click += async (_, _) => await RequestPinAsync();
        return button;
    }

    private void UpdatePinButtons()
    {
        bool table = ExtractionMode == OcrExtractionMode.Table;
        _pinButton.Visibility = table || ExtractionMode == OcrExtractionMode.Translation
            ? Visibility.Collapsed : Visibility.Visible;
        _pinButton.IsEnabled = _tablePinButton.IsEnabled = !_pinning && !_recognitionOperation
            && !_qualityOperation && !string.IsNullOrWhiteSpace(ActiveText);
    }

    internal async Task RequestPinAsync()
    {
        CommitTableEdit();
        string text = ExtractionMode == OcrExtractionMode.Table ? OcrTableExport.Tsv(CurrentTable()) : ActiveText;
        if (_pinning || _recognitionOperation || _qualityOperation
            || ExtractionMode == OcrExtractionMode.Translation || string.IsNullOrWhiteSpace(text)) return;
        var request = new OcrPinRequest(text, ExtractionMode);
        OcrResult? source = _result;
        _pinning = true;
        UpdatePinButtons();
        try
        {
            PinRequested?.Invoke(this, request);
            bool pinned = request.Completion is { } completion && await completion;
            if (ReferenceEquals(source, _result))
                _status.Text = UiText.Get(pinned ? "Ocr_Pinned" : "Onboard_PinFailed");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (ReferenceEquals(source, _result)) _status.Text = UiText.Get("Onboard_PinFailed");
        }
        finally { _pinning = false; UpdatePinButtons(); }
    }
}
