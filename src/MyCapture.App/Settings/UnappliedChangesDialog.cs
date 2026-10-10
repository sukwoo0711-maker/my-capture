using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using MyCapture.App.Themes;

namespace MyCapture.App.Settings;

/// <summary>What the user chose when closing the settings window with unapplied changes.</summary>
internal enum SettingsCloseChoice
{
    /// <summary>Keep the window open with the edits (and any theme preview) as they are.</summary>
    Cancel,

    /// <summary>Apply the draft, then close if it applied cleanly.</summary>
    Apply,

    /// <summary>Discard the draft, restore the saved theme and close.</summary>
    Discard,
}

/// <summary>
/// Asks whether to apply the pending settings before the window closes.
/// </summary>
/// <remarks>
/// Buttons follow the WinUI dialog order: the primary action first, the alternative next and
/// the dismissal last. Enter applies and closes; Esc keeps editing, so a reflexive Esc never
/// discards anything.
/// </remarks>
internal sealed class UnappliedChangesDialog : Window
{
    internal UnappliedChangesDialog()
    {
        Title = MyCapture.Core.Platform.AppIdentity.WindowLabel;
        Width = 460;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        StandardWindowTheme.Apply(this);
        SetResourceReference(BackgroundProperty, "Surface.Raised");
        SetResourceReference(ForegroundProperty, "Text.Primary");
        AutomationProperties.SetName(this, UiText.Get("SettingsUx_CloseTitle"));

        var panel = new StackPanel { Margin = new Thickness(24) };
        Heading = new TextBlock
        {
            Text = UiText.Get("SettingsUx_CloseTitle"), FontSize = 20, FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };
        panel.Children.Add(Heading);
        panel.Children.Add(new TextBlock
        {
            Text = UiText.Get("SettingsUx_CloseMessage"), TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 12, 0, 20),
        });

        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        ApplyButton = MakeButton("SettingsUx_CloseApply", "Button.Primary", SettingsCloseChoice.Apply);
        ApplyButton.IsDefault = true;
        DiscardButton = MakeButton("SettingsUx_CloseDiscard", "Button.Secondary", SettingsCloseChoice.Discard);
        CancelButton = MakeButton("Dialog.Cancel", "Button.Secondary", SettingsCloseChoice.Cancel);
        CancelButton.IsCancel = true;
        actions.Children.Add(ApplyButton);
        actions.Children.Add(DiscardButton);
        actions.Children.Add(CancelButton);
        panel.Children.Add(actions);
        Content = panel;
        Loaded += (_, _) => ApplyButton.Focus();
    }

    /// <summary>The choice; closing the dialog any other way keeps editing.</summary>
    internal SettingsCloseChoice Choice { get; private set; } = SettingsCloseChoice.Cancel;

    internal TextBlock Heading { get; }

    internal Button ApplyButton { get; }

    internal Button DiscardButton { get; }

    internal Button CancelButton { get; }

    /// <summary>Shows the dialog over <paramref name="owner"/> and returns the choice.</summary>
    internal static SettingsCloseChoice Ask(Window owner)
    {
        var dialog = new UnappliedChangesDialog();
        if (owner.IsVisible) dialog.Owner = owner;
        else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dialog.ShowDialog();
        return dialog.Choice;
    }

    private Button MakeButton(string key, string style, SettingsCloseChoice choice)
    {
        string label = UiText.Get(key);
        var button = new Button { Content = label, MinWidth = 88, MinHeight = 36, Margin = new Thickness(8, 4, 0, 0) };
        button.SetResourceReference(StyleProperty, style);
        AutomationProperties.SetName(button, label);
        button.Click += (_, _) =>
        {
            Choice = choice;
            Close();
        };
        return button;
    }
}
