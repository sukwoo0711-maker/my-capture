using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace MyCapture.App.Themes;

internal sealed class ThemedDialog : Window
{
    internal MessageBoxResult Result { get; private set; } = MessageBoxResult.Cancel;
    internal ThemedDialog(string text, string title, MessageBoxButton buttons, MessageBoxResult defaultResult,
        string? details = null, Action? recovery = null, string? recoveryLabel = null)
    {
        Title = title;
        Width = 500;
        SizeToContent = SizeToContent.Height;
        MaxHeight = SystemParameters.WorkArea.Height - 40;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        StandardWindowTheme.Apply(this);
        SetResourceReference(BackgroundProperty, "Surface.Raised");
        SetResourceReference(ForegroundProperty, "Text.Primary");
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 20) });
        if (!string.IsNullOrWhiteSpace(details))
        {
            var content = new StackPanel();
            var technical = new TextBox { Text = details, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
                MaxHeight = 180, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            AutomationProperties.SetName(technical, UiText.Get("Dialog.Details"));
            content.Children.Add(technical);
            var copy = new Button { Content = UiText.Get("QuickAccess.Copy"), MinHeight = 36, HorizontalAlignment = HorizontalAlignment.Left };
            copy.Click += (_, _) => { try { Clipboard.SetText(details); } catch (System.Runtime.InteropServices.COMException) { technical.SelectAll(); technical.Focus(); } };
            content.Children.Add(copy);
            var detailsHeading = new TextBlock { Text = UiText.Get("Dialog.Details") };
            detailsHeading.SetResourceReference(TextBlock.ForegroundProperty, "Text.Primary");
            var expander = new Expander { Header = detailsHeading, IsExpanded = false, Content = content, Margin = new Thickness(0, 0, 0, 16) };
            AutomationProperties.SetName(expander, UiText.Get("Dialog.Details"));
            expander.SetResourceReference(ForegroundProperty, "Text.Primary");
            panel.Children.Add(expander);
        }
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        if (recovery is not null)
        {
            var resolve = MakeButton(recoveryLabel ?? UiText.Get("Dialog.Return"), MessageBoxResult.OK, true, false);
            resolve.Click += (_, _) => recovery();
            actions.Children.Add(resolve);
        }
        (string Key, MessageBoxResult Result)[] choices = buttons switch
        {
            MessageBoxButton.OKCancel => [("Dialog.OK", MessageBoxResult.OK), ("Dialog.Cancel", MessageBoxResult.Cancel)],
            MessageBoxButton.YesNo => [("Dialog.Yes", MessageBoxResult.Yes), ("Dialog.No", MessageBoxResult.No)],
            MessageBoxButton.YesNoCancel => [("Dialog.Yes", MessageBoxResult.Yes), ("Dialog.No", MessageBoxResult.No), ("Dialog.Cancel", MessageBoxResult.Cancel)],
            _ => [("Dialog.Close", MessageBoxResult.OK)],
        };
        if (defaultResult == MessageBoxResult.None) defaultResult = choices[0].Result;
        foreach (var choice in choices)
            actions.Children.Add(MakeButton(UiText.Get(choice.Key), choice.Result,
                recovery is null && choice.Result == defaultResult, choice.Result is MessageBoxResult.Cancel || choices.Length == 1));
        panel.Children.Add(actions);
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private Button MakeButton(string label, MessageBoxResult result, bool isDefault, bool isCancel)
    {
        var button = new Button { Content = label, MinWidth = 80, MinHeight = 36,
            Margin = new Thickness(8, 4, 0, 0), IsDefault = isDefault, IsCancel = isCancel };
        button.SetResourceReference(StyleProperty, isDefault ? "Button.Primary" : "Button.Secondary");
        button.Click += (_, _) => { Result = result; Close(); };
        return button;
    }

    internal static string RecoveryMessage(Exception error) => UiText.Get(error switch
    {
        UnauthorizedAccessException => "Dialog.Access",
        IOException => "Dialog.Storage",
        NotSupportedException => "Dialog.Unsupported",
        _ => "Dialog.Unexpected",
    });

    internal static MessageBoxResult Error(Window? owner, string title, Exception error,
        Action? recovery = null, string? recoveryLabel = null) => Show(owner, RecoveryMessage(error), title,
            MessageBoxButton.OK, MessageBoxImage.Error, MessageBoxResult.OK, error.ToString(), recovery, recoveryLabel);

    internal static MessageBoxResult Show(string text, string title, MessageBoxButton buttons = MessageBoxButton.OK,
        MessageBoxImage icon = MessageBoxImage.Information, MessageBoxResult defaultResult = MessageBoxResult.None,
        string? details = null) => Show(null, text, title, buttons, icon, defaultResult, details);

    internal static MessageBoxResult Show(Window? owner, string text, string title, MessageBoxButton buttons = MessageBoxButton.OK,
        MessageBoxImage icon = MessageBoxImage.Information, MessageBoxResult defaultResult = MessageBoxResult.None,
        string? details = null, Action? recovery = null, string? recoveryLabel = null)
    {
        var dialog = new ThemedDialog(text, title, buttons, defaultResult, details, recovery, recoveryLabel);
        if (owner?.IsVisible == true) dialog.Owner = owner;
        else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dialog.ShowDialog();
        return dialog.Result;
    }
}
