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
        Title = MyCapture.Core.Platform.AppIdentity.WindowLabel;
        Width = 500;
        SizeToContent = SizeToContent.Height;
        MaxHeight = SystemParameters.WorkArea.Height - 40;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        StandardWindowTheme.Apply(this);
        SetResourceReference(BackgroundProperty, "Surface.Raised");
        SetResourceReference(ForegroundProperty, "Text.Primary");
        var panel = new StackPanel { Margin = new Thickness(24) };
        const string productPrefix = "MyCapture — ";
        string heading = title.StartsWith(productPrefix, StringComparison.Ordinal) ? title[productPrefix.Length..] : title;
        panel.Children.Add(new TextBlock { Text = heading, FontSize = 20, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
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
        var actions = CreateActionRow();
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
        Button button = CreateActionButton(label, primary: isDefault);
        button.IsDefault = isDefault;
        button.IsCancel = isCancel;
        button.Click += (_, _) => { Result = result; Close(); };
        return button;
    }

    /// <summary>Gap between the buttons of a dialog's action row.</summary>
    internal const double ActionSpacing = 8;

    /// <summary>Smallest width of a dialog action, so short captions keep a steady target.</summary>
    internal const double ActionMinWidth = 88;

    /// <summary>Height of a dialog action: the regular 36-DIP hit target.</summary>
    internal const double ActionHeight = 36;

    /// <summary>
    /// The action row every ordinary dialog ends with: right-aligned, the confirming action
    /// first and the dismissal last, as Windows dialogs and this app's settings and editor order
    /// them. Long captions wrap to another row instead of leaving the dialog.
    /// </summary>
    /// <remarks>
    /// The caller keeps <see cref="Button.IsDefault"/> on the confirming action and
    /// <see cref="Button.IsCancel"/> on the dismissal. A destructive confirmation makes the
    /// dismissal the default instead, so Enter never deletes.
    /// </remarks>
    internal static WrapPanel CreateActionRow(params Button[] actions)
    {
        var row = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        foreach (Button action in actions) row.Children.Add(action);
        return row;
    }

    /// <summary>One dialog action in the filled (primary) or bordered (secondary) tier.</summary>
    internal static Button CreateActionButton(string label, bool primary)
    {
        var button = new Button
        {
            Content = label, MinWidth = ActionMinWidth, MinHeight = ActionHeight,
            // The leading gap of the first button falls outside the right-aligned row.
            Margin = new Thickness(ActionSpacing, 4, 0, 0),
        };
        button.SetResourceReference(StyleProperty, primary ? "Button.Primary" : "Button.Secondary");
        return button;
    }

    internal static bool IsSuitableOwner(Window? window) => window?.IsVisible == true
        && window is not (Capture.QuickAccessWindow or Capture.CaptureOverlayWindow or Capture.CountdownWindow or Pinning.PinWindow or Recording.RecordingControlWindow);

    internal static string RecoveryMessage(Exception error) => UserFacingError.GetMessage(error)
        ?? (error is Recording.VideoLayerLimitException ? error.Message : null)
        ?? UiText.Get(error switch
    {
        UnauthorizedAccessException => "Dialog.Access",
        IOException => "Dialog.Storage",
        NotSupportedException => "Dialog.Unsupported",
        _ => "Dialog.Unexpected",
    });

    /// <summary>
    /// An unexpected failure: what went wrong, then what to do next if it happens again (restart
    /// MyCapture, then copy the details). The technical text stays under "Details".
    /// </summary>
    internal static string UnhandledMessage(Exception error) =>
        RecoveryMessage(error) + "\n\n" + UiText.Get("Onboard_ErrorNextStep");

    /// <summary>MyCapture could not start: the next steps replace "return to your work", which does not exist yet.</summary>
    internal static void StartupFailure(Exception error) =>
        Show(null, UiText.Get("Onboard_StartFailureBody"), UiText.Get("Onboard_StartFailureTitle"),
            MessageBoxButton.OK, MessageBoxImage.Error, MessageBoxResult.OK, error.ToString());

    internal static void ShowUnhandled(Exception error, Action<Exception>? showThemed = null,
        Action<string, string>? showFallback = null)
    {
        try
        {
            if (showThemed is not null) showThemed(error);
            else Show(null, UnhandledMessage(error), UiText.Get("Dialog.AppFailure"), MessageBoxButton.OK,
                MessageBoxImage.Error, MessageBoxResult.OK, error.ToString());
        }
        catch (Exception)
        {
            string title = "MyCapture";
            string message;
            try { message = RecoveryMessage(error); }
            catch (Exception) { message = "An error occurred. Close this message and try again."; }
            if (showFallback is not null) showFallback(message, title);
            else MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

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
        owner = owner?.IsVisible == true ? owner : Application.Current?.Windows.OfType<Window>()
            .FirstOrDefault(window => window.IsActive && IsSuitableOwner(window));
        if (owner is not null) { dialog.Owner = owner; dialog.ShowInTaskbar = false; }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            // Ownerless errors must remain above visible topmost capture/pin/recording UI.
            bool keepTopmost = Application.Current?.Windows.OfType<Window>()
                .Any(window => window.IsVisible && window.Topmost) == true;
            dialog.Topmost = keepTopmost || icon == MessageBoxImage.Error;
            dialog.ContentRendered += (_, _) => { dialog.Activate(); dialog.Topmost = keepTopmost; };
        }
        dialog.ShowDialog();
        return dialog.Result;
    }
}
