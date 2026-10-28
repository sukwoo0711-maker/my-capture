using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using MyCapture.App.Media;
using MyCapture.Core.Capture;

namespace MyCapture.App.Editing;

internal sealed partial class AnnotationEditorControl
{
    private string? _sourceFilePath;
    private long? _sourceFileSize;
    private CancellationTokenSource? _sourceFileSizeCts;
    private Window? _sourceFileSizeOwner;

    internal string? SourceFilePath
    {
        get => _sourceFilePath;
        set
        {
            VerifyAccess();
            if (string.Equals(_sourceFilePath, value, StringComparison.Ordinal)) return;
            CancelSourceFileSizeRead();
            _sourceFilePath = value;
            _sourceFileSize = null;
            RefreshDimensionsText();
            if (IsLoaded) StartSourceFileSizeRead();
        }
    }

    internal Task SourceFileSizeReadTask { get; private set; } = Task.CompletedTask;
    internal Func<string?, CancellationToken, Task<long?>> SourceFileSizeReader { get; set; } = MediaFileSizeReader.ReadAsync;

    private void StartSourceFileSizeRead()
    {
        CancelSourceFileSizeRead();
        Window? owner = Window.GetWindow(this);
        if (!ReferenceEquals(_sourceFileSizeOwner, owner))
        {
            if (_sourceFileSizeOwner is not null) _sourceFileSizeOwner.Closed -= OnSourceFileSizeOwnerClosed;
            _sourceFileSizeOwner = owner;
            if (owner is not null) owner.Closed += OnSourceFileSizeOwnerClosed;
        }
        var request = _sourceFileSizeCts = new CancellationTokenSource();
        string? path = _sourceFilePath;
        SourceFileSizeReadTask = ReadSourceFileSizeAsync(path, request.Token);
    }

    private async Task ReadSourceFileSizeAsync(string? path, CancellationToken token)
    {
        try
        {
            long? size = await SourceFileSizeReader(path, token).ConfigureAwait(false);
            if (token.IsCancellationRequested || Dispatcher.HasShutdownStarted) return;
            await Dispatcher.InvokeAsync(() =>
            {
                if (token.IsCancellationRequested || !IsLoaded || !string.Equals(path, _sourceFilePath, StringComparison.Ordinal)) return;
                _sourceFileSize = size;
                RefreshDimensionsText();
            });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private void CancelSourceFileSizeRead()
    {
        _sourceFileSizeCts?.Cancel();
        _sourceFileSizeCts?.Dispose();
        _sourceFileSizeCts = null;
    }

    private void OnSourceFileSizeOwnerClosed(object? sender, EventArgs e) => UnloadSourceFileSize();

    private void UnloadSourceFileSize()
    {
        CancelSourceFileSizeRead();
        if (_sourceFileSizeOwner is not null) _sourceFileSizeOwner.Closed -= OnSourceFileSizeOwnerClosed;
        _sourceFileSizeOwner = null;
    }

    private void RefreshDimensionsText()
    {
        if (_dimensionsText is null) return;
        string dimensions = UiText.Format("Text_009548FC2109", _canvasWidth, _canvasHeight);
        _dimensionsText.Text = _sourceFileSize is { } bytes ? dimensions + " · " + MediaDetailsFormatter.FileSize(bytes) : dimensions;
    }

    internal MediaDetailsSource ReadMediaDetailsSource() => new(SourceFilePath,
        _selectedBitmap.PixelWidth, _selectedBitmap.PixelHeight,
        CanvasWidth: _controller.Document.CanvasWidth, CanvasHeight: _controller.Document.CanvasHeight);

    private Button BuildMediaInfoButton(bool compact = false)
    {
        var button = new Button { Name = "MediaInfo", Content = UiText.Get("MediaInfo.Title"), ToolTip = UiText.Get("MediaInfo.Title") };
        button.SetResourceReference(StyleProperty, "Button.Secondary");
        AutomationProperties.SetName(button, UiText.Get("MediaInfo.Title"));
        CommandButtonPresentation.Apply(button, compact);
        button.Click += async (_, _) =>
        {
            if (Window.GetWindow(this) is { } owner)
                await MediaDetailsWindow.ShowAsync(owner, [ReadMediaDetailsSource()]);
        };
        return button;
    }
}
