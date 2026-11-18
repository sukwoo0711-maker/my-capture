using System.Windows;
using System.Windows.Controls;
using MyCapture.App.Ocr;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class DialogControlLanguageTests
{
    [Theory]
    [InlineData("ko-KR")]
    [InlineData("en-US")]
    public void OcrReplacementDefaultsToNoAndItsCancelActionCannotConfirm(string language) => StaTestHost.Run(() =>
    {
        using var scope = UiText.UseLanguage(language);
        var owner = new OcrResultWindow();
        ThemedDialog dialog = owner.CreateReplacementConfirmation();
        try
        {
            var panel = Assert.IsType<StackPanel>(Assert.IsType<ScrollViewer>(dialog.Content).Content);
            var actions = Assert.Single(panel.Children.OfType<WrapPanel>());
            Button[] buttons = actions.Children.OfType<Button>().ToArray();
            Assert.Equal(2, buttons.Length);
            Assert.False(buttons[0].IsDefault);
            Assert.False(buttons[0].IsCancel);
            Assert.True(buttons[1].IsDefault);
            Assert.True(buttons[1].IsCancel);
            Assert.Equal(UiText.Get("Dialog.Yes"), SurfaceShortcutHintTests.PrimaryCaption(buttons[0]));
            Assert.Equal(UiText.Get("Dialog.No"), SurfaceShortcutHintTests.PrimaryCaption(buttons[1]));
            buttons[1].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(MessageBoxResult.No, dialog.Result);
        }
        finally { dialog.Close(); owner.CloseForExit(); }
    });
}
