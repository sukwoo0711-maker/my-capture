using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MyCapture.App.Diagnostics;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class HotfixDialogSelectionTests
{
    [Theory]
    [InlineData("ko-KR", false)]
    [InlineData("en-US", false)]
    [InlineData("ko-KR", true)]
    [InlineData("en-US", true)]
    public void UnhandledDialogSelectsAndClicksOnlyStandardClose(string language, bool reverseOrder) =>
        WithUnhandledDialog(language, (dialog, row, buttons) =>
        {
            Assert.Equal([UiText.Get("Dialog.ReportProblem"), UiText.Get("Dialog.Close")],
                buttons.Select(button => (string)button.Content));
            Button close = buttons[1];
            if (reverseOrder)
            {
                row.Children.Remove(close);
                row.Children.Insert(0, close);
            }

            Button selected = HotfixUxSelfTest.SelectStandardCloseAction(dialog);
            Assert.Same(close, selected);
            Assert.True(selected.IsCancel && selected.IsDefault && selected.IsVisible && selected.IsEnabled);
            Assert.Equal(UiText.Get("Dialog.Close"), selected.Content);
            Assert.True(dialog.IsVisible);
            selected.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(dialog.IsVisible);
            Assert.Equal(MessageBoxResult.OK, dialog.Result);
        });

    [Theory]
    [InlineData("missing cancel")]
    [InlineData("duplicate cancel")]
    [InlineData("not default")]
    [InlineData("hidden")]
    [InlineData("collapsed")]
    [InlineData("disabled")]
    [InlineData("wrong label")]
    public void InvalidCloseActionFailsSelection(string defect) =>
        WithUnhandledDialog("en-US", (dialog, _, buttons) =>
        {
            Button close = buttons[1];
            switch (defect)
            {
                case "missing cancel": close.IsCancel = false; break;
                case "duplicate cancel": buttons[0].IsCancel = true; break;
                case "not default": close.IsDefault = false; break;
                case "hidden": close.Visibility = Visibility.Hidden; break;
                case "collapsed": close.Visibility = Visibility.Collapsed; break;
                case "disabled": close.IsEnabled = false; break;
                case "wrong label": close.Content = UiText.Get("Dialog.ReportProblem"); break;
                default: throw new ArgumentOutOfRangeException(nameof(defect));
            }

            Assert.Throws<InvalidOperationException>(() => HotfixUxSelfTest.SelectStandardCloseAction(dialog));
            Assert.True(dialog.IsVisible);
        });

    private static void WithUnhandledDialog(string language, Action<ThemedDialog, WrapPanel, Button[]> verify) => StaTestHost.Run(() =>
    {
        using var scope = UiText.UseLanguage(language);
        Action<string> previous = SupportDiagnostics.OpenUri;
        var opened = new List<string>();
        ThemedDialog dialog = ThemedDialog.CreateUnhandled(new InvalidOperationException("selector fixture"));
        try
        {
            SupportDiagnostics.OpenUri = opened.Add;
            Ocr410Fixture.LoadTheme(dialog);
            FluidMotion.SetWindowEntrance(dialog, false);
            dialog.WindowStartupLocation = WindowStartupLocation.Manual;
            dialog.Left = -10000;
            dialog.Top = -10000;
            dialog.ShowActivated = false;
            dialog.ShowInTaskbar = false;
            dialog.Show();
            dialog.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(dialog.UpdateLayout));
            var panel = (StackPanel)((ScrollViewer)dialog.Content).Content;
            WrapPanel row = panel.Children.OfType<WrapPanel>().Single();
            verify(dialog, row, row.Children.OfType<Button>().ToArray());
            Assert.Empty(opened);
        }
        finally
        {
            SupportDiagnostics.OpenUri = previous;
            dialog.Close();
        }
    });
}
