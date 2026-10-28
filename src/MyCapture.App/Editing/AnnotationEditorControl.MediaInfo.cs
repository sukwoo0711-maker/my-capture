using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using MyCapture.App.Media;

namespace MyCapture.App.Editing;

internal sealed partial class AnnotationEditorControl
{
    internal string? SourceFilePath { get; set; }

    internal MediaDetailsSource ReadMediaDetailsSource() => new(SourceFilePath,
        _selectedBitmap.PixelWidth, _selectedBitmap.PixelHeight,
        CanvasWidth: _controller.Document.CanvasWidth, CanvasHeight: _controller.Document.CanvasHeight);

    private Button BuildMediaInfoButton(bool compact = false)
    {
        var button = new Button { Name = "MediaInfo", Content = UiText.Get("MediaInfo.Title"), ToolTip = UiText.Get("MediaInfo.Title") };
        button.SetResourceReference(StyleProperty, "Button.Ghost");
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
