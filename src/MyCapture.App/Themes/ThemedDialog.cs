using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using MyCapture.App.Diagnostics;

namespace MyCapture.App.Themes;

/// <summary>
/// A follow-up command beside a dialog's standard buttons. <see cref="Run"/> returns a short
/// message to show in the dialog when the command could not finish, or <see langword="null"/>.
/// A helper that keeps the dialog open (open the save location, report a problem) leaves the
/// message and its details in view.
/// </summary>
internal sealed record DialogCommand(string Label, Func<string?> Run, bool Closes = false);

internal sealed class ThemedDialog : Window
{
    private readonly TextBlock _commandStatus;

    internal MessageBoxResult Result { get; private set; } = MessageBoxResult.Cancel;

    /// <param name="destructive">
    /// The OK action deletes or discards something: it takes <c>Button.Danger</c> and, as for
    /// any destructive confirmation, the caller makes Cancel the default.
    /// </param>
    /// <param name="commands">Follow-up commands shown after the recovery action.</param>
    internal ThemedDialog(string text, string title, MessageBoxButton buttons, MessageBoxResult defaultResult,
        string? details = null, Action? recovery = null, string? recoveryLabel = null, string? okLabel = null,
        bool destructive = false, IReadOnlyList<DialogCommand>? commands = null)
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
        panel.Children.Add(KoreanWordWrap.Show(new TextBlock { FontSize = 20, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap }, heading));
        panel.Children.Add(KoreanWordWrap.Show(new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 20) }, text));
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
        // A helper command that could not finish says so here, above the buttons.
        _commandStatus = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12), Visibility = Visibility.Collapsed };
        _commandStatus.SetResourceReference(TextBlock.ForegroundProperty, "State.Danger");
        AutomationProperties.SetLiveSetting(_commandStatus, AutomationLiveSetting.Polite);
        panel.Children.Add(_commandStatus);
        var actions = CreateActionRow();
        if (recovery is not null)
        {
            var resolve = MakeButton(recoveryLabel ?? UiText.Get("Dialog.Return"), MessageBoxResult.OK, true, false);
            resolve.Click += (_, _) => recovery();
            actions.Children.Add(resolve);
        }
        foreach (DialogCommand command in commands ?? [])
            actions.Children.Add(MakeCommandButton(command));
        (string Key, MessageBoxResult Result)[] choices = buttons switch
        {
            MessageBoxButton.OKCancel => [("Dialog.OK", MessageBoxResult.OK), ("Dialog.Cancel", MessageBoxResult.Cancel)],
            MessageBoxButton.YesNo => [("Dialog.Yes", MessageBoxResult.Yes), ("Dialog.No", MessageBoxResult.No)],
            MessageBoxButton.YesNoCancel => [("Dialog.Yes", MessageBoxResult.Yes), ("Dialog.No", MessageBoxResult.No), ("Dialog.Cancel", MessageBoxResult.Cancel)],
            _ => [("Dialog.Close", MessageBoxResult.OK)],
        };
        if (defaultResult == MessageBoxResult.None) defaultResult = choices[0].Result;
        foreach (var choice in choices)
        {
            bool isDefault = recovery is null && choice.Result == defaultResult;
            Button button = MakeButton(choice.Key == "Dialog.OK" && okLabel is not null ? okLabel : UiText.Get(choice.Key), choice.Result,
                isDefault, choice.Result is MessageBoxResult.Cancel || choices.Length == 1);
            if (destructive && choice.Result == MessageBoxResult.OK && !isDefault) button.SetResourceReference(StyleProperty, "Button.Danger");
            actions.Children.Add(button);
        }
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

    private Button MakeCommandButton(DialogCommand command)
    {
        Button button = CreateActionButton(command.Label, primary: false);
        button.Click += (_, _) =>
        {
            string? failure = command.Run();
            if (command.Closes && failure is null)
            {
                Result = MessageBoxResult.OK;
                Close();
                return;
            }

            KoreanWordWrap.Show(_commandStatus, failure);
            _commandStatus.Visibility = failure is null ? Visibility.Collapsed : Visibility.Visible;
        };
        return button;
    }

    /// <summary>Gap between the buttons of a dialog's action row.</summary>
    internal const double ActionSpacing = 8;

    /// <summary>Smallest width of a dialog action, so short captions keep a steady target.</summary>
    internal const double ActionMinWidth = 88;

    /// <summary>Height of a dialog action: the shared standard command tier.</summary>
    internal const double ActionHeight = 44;

    /// <summary>
    /// The action row every ordinary dialog ends with: right-aligned, the confirming action
    /// first and the dismissal last, as Windows dialogs and this app's settings and editor order
    /// them. Long captions wrap to another row instead of leaving the dialog.
    /// </summary>
    /// <remarks>
    /// The caller keeps <see cref="Button.IsDefault"/> on the confirming action and
    /// <see cref="Button.IsCancel"/> on the dismissal. A destructive confirmation makes the
    /// dismissal the default instead, so Enter never deletes, and gives the destructive action
    /// <see cref="CreateDangerButton"/>.
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
            Content = label,
        };
        CommandButtonPresentation.Apply(button);
        button.MinWidth = ActionMinWidth;
        // The leading gap of the first button falls outside the right-aligned row.
        button.Margin = new Thickness(ActionSpacing, 4, 0, 0);
        button.SetResourceReference(StyleProperty, primary ? "Button.Primary" : "Button.Secondary");
        return button;
    }

    /// <summary>A destructive dialog action: the bordered tier in the shared <c>Button.Danger</c> style.</summary>
    internal static Button CreateDangerButton(string label)
    {
        Button button = CreateActionButton(label, primary: false);
        button.SetResourceReference(StyleProperty, "Button.Danger");
        return button;
    }

    internal static bool IsSuitableOwner(Window? window) => window?.IsVisible == true
        && window is not (Capture.QuickAccessWindow or Capture.CaptureOverlayWindow or Capture.CountdownWindow or Pinning.PinWindow or Recording.RecordingControlWindow);

    /// <summary>
    /// What to tell the person about <paramref name="error"/>: an application message as
    /// written, otherwise the storage case it falls into, otherwise a generic line by type.
    /// </summary>
    /// <param name="location">The file or folder the operation used, when the caller knows it.
    /// A full disk names its drive from it, or from a path in the exception.</param>
    internal static string RecoveryMessage(Exception error, string? location = null) => UserFacingError.GetMessage(error)
        ?? (error is Recording.VideoLayerLimitException ? error.Message : null)
        ?? StorageMessage(error, location)
        ?? UiText.Get(error switch
    {
        UnauthorizedAccessException => "Dialog.Access",
        IOException => "Dialog.Storage",
        NotSupportedException => "Dialog.Unsupported",
        _ => "Dialog.Unexpected",
    });

    /// <summary>
    /// A failed save: the storage case when one applies (full disk, file in use, no access),
    /// otherwise the generic "could not read or save" line, whatever the exception type.
    /// </summary>
    internal static string SaveFailureMessage(Exception? error, string? location = null) =>
        (error is null ? null : UserFacingError.GetMessage(error) ?? StorageMessage(error, location)) ?? UiText.Get("Dialog.Storage");

    private static string? StorageMessage(Exception error, string? location) => UserFacingError.ClassifyStorage(error) switch
    {
        StorageFailure.DiskFull => DriveName(location ?? UserFacingError.FindPath(error)) is { } drive
            ? UiText.Format("Dialog.DiskFull", drive)
            : UiText.Get("Dialog.DiskFullUnknownDrive"),
        StorageFailure.InUse => UiText.Get("Dialog.InUse"),
        StorageFailure.AccessDenied => UiText.Get("Dialog.Access"),
        _ => null,
    };

    /// <summary>"C:" for a local path, the share root for a network path.</summary>
    internal static string? DriveName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            string? root = Path.GetPathRoot(path);
            return string.IsNullOrEmpty(root) ? null : root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// The folder a failed file operation used, for "Open save location": the caller's
    /// location, otherwise the path the exception names. Only a storage failure whose folder
    /// still exists offers it.
    /// </summary>
    internal static string? SaveLocationFor(Exception error, string? location = null)
    {
        if (error is not (IOException or UnauthorizedAccessException) && UserFacingError.ClassifyStorage(error) == StorageFailure.None) return null;
        string? path = location ?? UserFacingError.FindPath(error);
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            if (Directory.Exists(path)) return Path.GetFullPath(path);
            string? folder = Path.GetDirectoryName(path);
            return folder is not null && Directory.Exists(folder) ? folder : null;
        }
        catch (Exception problem) when (problem is ArgumentException or IOException or NotSupportedException or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>Opens a folder in File Explorer. Tests replace it so nothing is launched.</summary>
    internal static Action<string> OpenFolder { get; set; } = MyCapture.App.Settings.ExplorerLauncher.OpenFolder;

    /// <summary>[Open save location]: shows the folder and leaves the dialog open.</summary>
    internal static DialogCommand OpenSaveLocationCommand(string folder) => new(UiText.Get("Dialog.OpenSaveLocation"), () =>
    {
        try
        {
            OpenFolder(folder);
            return null;
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or IOException or ArgumentException)
        {
            return UiText.Get("Dialog.OpenSaveLocationFailed");
        }
    });

    /// <summary>[Report a problem]: the Settings support command, from an unexpected-error dialog.</summary>
    internal static DialogCommand ReportProblemCommand() => new(UiText.Get("Dialog.ReportProblem"),
        () => SupportDiagnostics.TryOpenIssuePage() ? null : UiText.Get("Support_OpenFailed"));

    /// <summary>
    /// An unexpected failure: what went wrong, then what to do next if it happens again (restart
    /// MyCapture, then copy the details into a problem report). The technical text stays under "Details".
    /// </summary>
    internal static string UnhandledMessage(Exception error) =>
        RecoveryMessage(error) + "\n\n" + UiText.Get("Onboard_ErrorNextStep");

    /// <summary>MyCapture could not start: the next steps replace "return to your work", which does not exist yet.</summary>
    internal static void StartupFailure(Exception error) => Present(null, CreateStartupFailure(error), MessageBoxImage.Error);

    /// <summary>The startup failure dialog, with [Report a problem] beside Close.</summary>
    internal static ThemedDialog CreateStartupFailure(Exception error) =>
        new(UiText.Get("Onboard_StartFailureBody"), UiText.Get("Onboard_StartFailureTitle"), MessageBoxButton.OK,
            MessageBoxResult.OK, error.ToString(), commands: [ReportProblemCommand()]);

    /// <summary>The unexpected-error dialog, with [Report a problem] beside Close.</summary>
    internal static ThemedDialog CreateUnhandled(Exception error) =>
        new(UnhandledMessage(error), UiText.Get("Dialog.AppFailure"), MessageBoxButton.OK, MessageBoxResult.OK,
            error.ToString(), commands: [ReportProblemCommand()]);

    internal static void ShowUnhandled(Exception error, Action<Exception>? showThemed = null,
        Action<string, string>? showFallback = null)
    {
        try
        {
            if (showThemed is not null) showThemed(error);
            else Present(null, CreateUnhandled(error), MessageBoxImage.Error);
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

    /// <summary>
    /// A failed operation. A storage failure whose folder is known also offers [Open save
    /// location] so the person can free space or close the program that holds the file.
    /// </summary>
    /// <param name="location">The file or folder the operation wrote, when the caller knows it.</param>
    internal static MessageBoxResult Error(Window? owner, string title, Exception error,
        Action? recovery = null, string? recoveryLabel = null, string? location = null) =>
        Present(owner, CreateError(title, error, recovery, recoveryLabel, location), MessageBoxImage.Error);

    /// <summary>The dialog <see cref="Error"/> shows, before it is placed or shown.</summary>
    internal static ThemedDialog CreateError(string title, Exception error,
        Action? recovery = null, string? recoveryLabel = null, string? location = null)
    {
        string? folder = SaveLocationFor(error, location);
        return new ThemedDialog(RecoveryMessage(error, location), title, MessageBoxButton.OK, MessageBoxResult.OK,
            error.ToString(), recovery, recoveryLabel, commands: folder is null ? null : [OpenSaveLocationCommand(folder)]);
    }

    internal static MessageBoxResult Show(string text, string title, MessageBoxButton buttons = MessageBoxButton.OK,
        MessageBoxImage icon = MessageBoxImage.Information, MessageBoxResult defaultResult = MessageBoxResult.None,
        string? details = null) => Show(null, text, title, buttons, icon, defaultResult, details);

    internal static MessageBoxResult Show(Window? owner, string text, string title, MessageBoxButton buttons = MessageBoxButton.OK,
        MessageBoxImage icon = MessageBoxImage.Information, MessageBoxResult defaultResult = MessageBoxResult.None,
        string? details = null, Action? recovery = null, string? recoveryLabel = null, string? okLabel = null,
        bool destructive = false, IReadOnlyList<DialogCommand>? commands = null) =>
        Present(owner, new ThemedDialog(text, title, buttons, defaultResult, details, recovery, recoveryLabel, okLabel, destructive, commands), icon);

    /// <summary>Shows a dialog built by one of the Create* factories modally over its owner.</summary>
    internal static MessageBoxResult Present(Window? owner, ThemedDialog dialog, MessageBoxImage icon)
    {
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
