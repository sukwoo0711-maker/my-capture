using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using MyCapture.App.Gallery;
using MyCapture.App.Recording;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Queue;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// Every ordinary dialog ends with the same action row: the confirming action first and the
/// dismissal last, 8 DIPs apart, each at least 88 x 36. Enter picks the filled default, which is
/// never a destructive command; Esc always cancels.
/// </summary>
public sealed class DialogActionOrderTests
{
    [Theory]
    [InlineData("ko-KR")]
    [InlineData("en-US")]
    public void ConfirmingActionComesBeforeCancelInEveryOrdinaryDialog(string language) => StaTestHost.Run(() =>
    {
        using var scope = UiText.UseLanguage(language);
        string cancel = UiText.Get("Library.Action.Cancel");
        var record = new CaptureRecord { Title = "Review sample", Width = 4, Height = 3 };
        Verify(new GalleryOrganizationDialog(record, (_, _) => Task.FromResult(true)),
            [UiText.Get("Library.Action.Save"), cancel], defaultIndex: 0, language);
        Verify(GalleryFolderEditDialog.ForName(string.Empty, create: true, _ => Task.CompletedTask),
            [UiText.Get("Folder410.Create"), cancel], defaultIndex: 0, language);
        Verify(GalleryFolderEditDialog.ForName("Review", create: false, _ => Task.CompletedTask),
            [UiText.Get("Library.Action.Save"), cancel], defaultIndex: 0, language);
        Verify(new TimedTextOverlayDialog(5000, 0),
            [UiText.Get("Text_D9F974C95F68"), UiText.Get("Text_BE876433993A")], defaultIndex: 0, language);
        Verify(new ThemedDialog("Body", "Title", MessageBoxButton.OKCancel, MessageBoxResult.OK),
            [UiText.Get("Dialog.OK"), UiText.Get("Dialog.Cancel")], defaultIndex: 0, language);
        // Like a Windows Yes/No message box, No is not bound to Esc; the order still leads with Yes.
        Verify(new ThemedDialog("Body", "Title", MessageBoxButton.YesNo, MessageBoxResult.Yes),
            [UiText.Get("Dialog.Yes"), UiText.Get("Dialog.No")], defaultIndex: 0, language, escCancels: false);
        Verify(new ThemedDialog("Body", "Title", MessageBoxButton.YesNoCancel, MessageBoxResult.Yes),
            [UiText.Get("Dialog.Yes"), UiText.Get("Dialog.No"), UiText.Get("Dialog.Cancel")], defaultIndex: 0, language);
    });

    [Theory]
    [InlineData("ko-KR")]
    [InlineData("en-US")]
    public void DestructiveConfirmationsLeadWithTheActionButEnterCancels(string language) => StaTestHost.Run(() =>
    {
        using var scope = UiText.UseLanguage(language);
        // The library's delete confirmation and the folder deletion dialog.
        Verify(new ThemedDialog("Delete?", "Delete", MessageBoxButton.OKCancel, MessageBoxResult.Cancel),
            [UiText.Get("Dialog.OK"), UiText.Get("Dialog.Cancel")], defaultIndex: 1, language);
        var folder = new GalleryFolderDeletionDialog("Review", new CaptureFolderDeletionPreview(3, 2, 4, 1));
        Verify(folder, [UiText.Get("Folder410.Delete"), UiText.Get("Library.Action.Cancel")], defaultIndex: 1, language,
            shown => Assert.True(folder.CancelButton.IsKeyboardFocused || !shown.IsActive,
                $"{language}: the folder deletion dialog must open with focus on cancel"));
    });

    private static void Verify(Window dialog, string[] captions, int defaultIndex, string language,
        Action<Window>? whileShown = null, bool escCancels = true)
    {
        LoadTheme(dialog);
        // Final layout at once: the entrance fade would otherwise be caught mid-way in the evidence.
        FluidMotion.SetWindowEntrance(dialog, false);
        dialog.WindowStartupLocation = WindowStartupLocation.Manual;
        dialog.Left = -10000;
        dialog.Top = -10000;
        dialog.ShowActivated = false;
        dialog.ShowInTaskbar = false;
        string label = $"{language}/{dialog.GetType().Name}";
        try
        {
            dialog.Show();
            dialog.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(dialog.UpdateLayout));
            WrapPanel row = Descendants(dialog).OfType<WrapPanel>()
                .Single(panel => panel.Children.Count == captions.Length && panel.Children.OfType<Button>().Count() == captions.Length);
            Button[] buttons = row.Children.OfType<Button>().ToArray();
            Assert.Equal(captions, buttons.Select(button => button.Content as string));
            Assert.Equal(HorizontalAlignment.Right, row.HorizontalAlignment);

            // Esc is the last button; Enter is the one filled default.
            Assert.Equal(escCancels ? [captions.Length - 1] : [], Enumerable.Range(0, buttons.Length).Where(index => buttons[index].IsCancel));
            Assert.Equal([defaultIndex], Enumerable.Range(0, buttons.Length).Where(index => buttons[index].IsDefault));
            for (int index = 0; index < buttons.Length; index++)
                Assert.Same(dialog.FindResource(index == defaultIndex ? "Button.Primary" : "Button.Secondary"), buttons[index].Style);

            for (int index = 0; index < buttons.Length; index++)
            {
                Button button = buttons[index];
                Assert.True(button.ActualWidth >= ThemedDialog.ActionMinWidth - 0.5, $"{label}: '{button.Content}' is {button.ActualWidth:0.#} wide");
                Assert.InRange(button.ActualHeight, ThemedDialog.ActionHeight - 0.5, ThemedDialog.ActionHeight + 2.5);
                Rect bounds = button.TransformToAncestor(dialog).TransformBounds(new Rect(button.RenderSize));
                var client = (FrameworkElement)VisualTreeHelper.GetChild(dialog, 0);
                Assert.True(bounds.Left >= 0 && bounds.Right <= client.ActualWidth + 0.5, $"{label}: '{button.Content}' leaves the dialog at {bounds}");
                if (index == 0) continue;
                Rect previous = buttons[index - 1].TransformToAncestor(dialog).TransformBounds(new Rect(buttons[index - 1].RenderSize));
                if (Math.Abs(previous.Top - bounds.Top) > 0.5) continue; // wrapped onto another line
                Assert.Equal(ThemedDialog.ActionSpacing, bounds.Left - previous.Right, 1);
            }

            whileShown?.Invoke(dialog);
            SaveEvidence(dialog, $"dialog-{dialog.GetType().Name}-{captions[0]}-{language}.png");
        }
        finally
        {
            dialog.Close();
        }
    }

    private static void LoadTheme(Window window)
    {
        XElement? combined = null;
        foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
        {
            using Stream source = typeof(DialogActionOrderTests).Assembly.GetManifestResourceStream($"ThemeFixture.{name}.xaml")!;
            XElement dictionary = XDocument.Load(source).Root!;
            combined ??= new XElement(dictionary.Name);
            foreach (XAttribute declaration in dictionary.Attributes().Where(attribute => attribute.IsNamespaceDeclaration))
            {
                string value = declaration.Value;
                if (value.StartsWith("clr-namespace:MyCapture.", StringComparison.Ordinal) && !value.Contains(";assembly=", StringComparison.Ordinal))
                    value += ";assembly=MyCapture";
                combined.SetAttributeValue(declaration.Name, value);
            }
            foreach (XElement entry in dictionary.Elements()) combined.Add(new XElement(entry));
        }
        window.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Parse(combined!.ToString(SaveOptions.DisableFormatting)));
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (DependencyObject descendant in Descendants(child)) yield return descendant;
        }
    }

    /// <summary>Opt-in: set MYCAPTURE_DIALOG_EVIDENCE to a folder to keep a PNG of each dialog.</summary>
    private static void SaveEvidence(Window dialog, string name)
    {
        string? root = Environment.GetEnvironmentVariable("MYCAPTURE_DIALOG_EVIDENCE");
        if (string.IsNullOrWhiteSpace(root)) return;
        Directory.CreateDirectory(root);
        var client = (FrameworkElement)VisualTreeHelper.GetChild(dialog, 0);
        var pixels = new RenderTargetBitmap((int)Math.Ceiling(client.ActualWidth), (int)Math.Ceiling(client.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        pixels.Render(client);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(pixels));
        string safe = string.Concat(name.Select(character => Path.GetInvalidFileNameChars().Contains(character) || character == ' ' ? '_' : character));
        using FileStream output = File.Create(Path.Combine(root, safe));
        encoder.Save(output);
    }
}
