using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using MyCapture.App.Themes;

namespace MyCapture.App.Editing;

/// <summary>What the user chose when closing an editor that still has unsaved edits.</summary>
internal enum EditorCloseChoice
{
    /// <summary>Keep editing; nothing is saved or closed.</summary>
    Cancel,

    /// <summary>Finish the edit through the normal Done commit, then close.</summary>
    SaveAndClose,

    /// <summary>Close and drop the edits made in this window.</summary>
    DontSave,
}

/// <summary>
/// The three-way close confirmation for the image editor:
/// [Save and close] [Don't save] [Cancel].
/// </summary>
/// <remarks>
/// <para>
/// The shared <see cref="ThemedDialog"/> only offers Yes/No/Cancel captions, and a choice that
/// discards work must say what it does. Shape, order and keys match the settings window's
/// close confirmation (<c>Settings.UnappliedChangesDialog</c>): a heading, one sentence, then
/// the action first, the alternative next and the dismissal last, as the WinUI dialog guidance
/// orders them. Enter picks the filled first button; Esc is Cancel, so a reflexive Esc never
/// discards anything.
/// </para>
/// <para>
/// The constructor takes only the heading, the sentence and the three captions, so the two
/// confirmations can later share one implementation.
/// </para>
/// </remarks>
internal sealed class EditorCloseDialog : Window
{
    internal EditorCloseDialog()
        : this(
            UiText.Get("EditorFlow_CloseTitle"),
            UiText.Get("EditorFlow_CloseMessage"),
            UiText.Get("EditorFlow_SaveAndClose"),
            UiText.Get("EditorFlow_DontSave"),
            UiText.Get("Dialog.Cancel"))
    {
    }

    internal EditorCloseDialog(string title, string message, string saveLabel, string dontSaveLabel, string cancelLabel)
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
        AutomationProperties.SetName(this, title);

        var panel = new StackPanel { Margin = new Thickness(24) };
        Heading = new TextBlock
        {
            Text = title,
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };
        panel.Children.Add(Heading);
        panel.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 12, 0, 20),
        });

        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        SaveButton = AddChoice(actions, saveLabel, EditorCloseChoice.SaveAndClose, "Button.Primary");
        SaveButton.IsDefault = true;
        DontSaveButton = AddChoice(actions, dontSaveLabel, EditorCloseChoice.DontSave, "Button.Secondary");
        CancelButton = AddChoice(actions, cancelLabel, EditorCloseChoice.Cancel, "Button.Secondary");
        CancelButton.IsCancel = true;
        panel.Children.Add(actions);
        Content = panel;

        Loaded += (_, _) => SaveButton.Focus();
    }

    /// <summary>The choice made, <see cref="EditorCloseChoice.Cancel"/> when the dialog was dismissed.</summary>
    internal EditorCloseChoice Choice { get; private set; } = EditorCloseChoice.Cancel;

    internal TextBlock Heading { get; }

    internal Button SaveButton { get; }

    internal Button DontSaveButton { get; }

    internal Button CancelButton { get; }

    /// <summary>Asks over <paramref name="owner"/> and returns the choice.</summary>
    internal static EditorCloseChoice Ask(Window owner)
    {
        var dialog = new EditorCloseDialog();
        if (owner.IsVisible) dialog.Owner = owner;
        else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        _ = dialog.ShowDialog();
        return dialog.Choice;
    }

    private Button AddChoice(Panel actions, string label, EditorCloseChoice choice, string style)
    {
        var button = new Button
        {
            Content = label,
            MinWidth = 88,
            MinHeight = 36,
            Margin = new Thickness(8, 4, 0, 0),
        };
        button.SetResourceReference(StyleProperty, style);
        AutomationProperties.SetName(button, label);
        button.Click += (_, _) =>
        {
            Choice = choice;
            Close();
        };
        actions.Children.Add(button);
        return button;
    }
}
