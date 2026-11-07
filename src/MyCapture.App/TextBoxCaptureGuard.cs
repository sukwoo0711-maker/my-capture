using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MyCapture.App;

/// <summary>Keeps text boxes from retaining mouse capture after a left-button release.</summary>
/// <remarks>
/// <para>
/// With UseAdornerForTextboxSelectionRendering disabled, TextBoxView draws its own selection,
/// so a press that changes the selection leaves the view waiting for layout. WPF lays it out
/// on mouse down, but returns from mouse up without releasing capture if the view is invalid.
/// A release routed before the next layout pass can therefore send the next click to the box
/// (#235). Preview mouse up finishes that layout so WPF can do its usual release work: place
/// the caret deferred by a press on the selection, update the cursor and release capture.
/// </para>
/// <para>
/// The backstop uses the left-button-up sub-event, raised after WPF's mouse-up handling; a
/// MouseUp class handler on TextBox would run before that handling and pre-empt caret placement.
/// If the view still cannot lay out, the backstop only releases capture; WPF's other release
/// work does not run. PasswordBox also uses TextBoxView and needs the same guard. RichTextBox
/// uses a document text view that does not render its own selection and is not affected.
/// </para>
/// </remarks>
internal static class TextBoxCaptureGuard
{
    private static int _installed;

    internal static bool IsInstalled => Volatile.Read(ref _installed) != 0;

    /// <summary>Installs the guard for this process; later calls do nothing.</summary>
    internal static void Install()
    {
        if (Interlocked.Exchange(ref _installed, 1) != 0)
        {
            return;
        }

        foreach (Type owner in new[] { typeof(TextBox), typeof(PasswordBox) })
        {
            EventManager.RegisterClassHandler(owner, Mouse.PreviewMouseUpEvent,
                new MouseButtonEventHandler(OnPreviewMouseUp));
            EventManager.RegisterClassHandler(owner, UIElement.MouseLeftButtonUpEvent,
                new MouseButtonEventHandler(OnMouseLeftButtonUp), handledEventsToo: true);
        }
    }

    private static void OnPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && sender is UIElement box && box.IsMouseCaptured)
        {
            box.UpdateLayout();
        }
    }

    private static void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.RightButton != MouseButtonState.Released || sender is not UIElement box || !box.IsMouseCaptured) return;
        e.Handled = true;
        box.ReleaseMouseCapture();
    }
}
