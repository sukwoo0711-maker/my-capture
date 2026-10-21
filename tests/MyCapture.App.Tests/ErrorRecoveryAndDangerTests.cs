using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.App.Diagnostics;
using MyCapture.App.Gallery;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Queue;
using MyCapture.Core.Themes;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// Shared error and confirmation behaviour: a full disk or a locked file says what to do,
/// failures offer the next step, and destructive actions share one danger style.
/// </summary>
public sealed class ErrorRecoveryAndDangerTests
{
    [Theory]
    [InlineData("ko-KR")]
    [InlineData("en-US")]
    public void StorageFailuresSayWhatToDo(string language)
    {
        using var scope = UiText.UseLanguage(language);
        var diskFull = new IOException(@"There is not enough space on the disk. : 'D:\Captures\review.png'", UserFacingError.DiskFullHResult);
        Assert.Equal(UiText.Format("Dialog.DiskFull", "D:"), ThemedDialog.RecoveryMessage(diskFull));
        Assert.Contains("D:", ThemedDialog.RecoveryMessage(diskFull));
        Assert.DoesNotContain("review.png", ThemedDialog.RecoveryMessage(diskFull));
        // The caller's location wins over a path in the message; a share root names a network drive.
        Assert.Equal(UiText.Format("Dialog.DiskFull", "E:"), ThemedDialog.RecoveryMessage(diskFull, @"E:\Exports\a.png"));
        Assert.Equal(UiText.Format("Dialog.DiskFull", @"\\nas\share"),
            ThemedDialog.RecoveryMessage(new IOException("full", UserFacingError.HandleDiskFullHResult), @"\\nas\share\captures\a.png"));
        Assert.Equal(UiText.Get("Dialog.DiskFullUnknownDrive"),
            ThemedDialog.RecoveryMessage(new IOException("full", UserFacingError.HandleDiskFullHResult)));
        // An encoder's COM error inside a task still reads as a full disk.
        Assert.Equal(UiText.Get("Dialog.DiskFullUnknownDrive"),
            ThemedDialog.RecoveryMessage(new AggregateException(new COMException("WIC", UserFacingError.DiskFullHResult))));

        Assert.Equal(UiText.Get("Dialog.InUse"), ThemedDialog.RecoveryMessage(new IOException("busy", UserFacingError.SharingViolationHResult)));
        Assert.Equal(UiText.Get("Dialog.InUse"), ThemedDialog.RecoveryMessage(new IOException("locked", UserFacingError.LockViolationHResult)));
        Assert.Equal(UiText.Get("Dialog.Access"), ThemedDialog.RecoveryMessage(new UnauthorizedAccessException(@"Access to the path 'C:\x' is denied.")));
        Assert.Equal(UiText.Get("Dialog.Storage"), ThemedDialog.RecoveryMessage(new IOException("private-detail")));
        Assert.Equal(UiText.Get("Dialog.Unexpected"), ThemedDialog.RecoveryMessage(new InvalidOperationException("x")));
        // A save keeps the storage wording even when the cause is not an I/O exception.
        Assert.Equal(UiText.Get("Dialog.Storage"), ThemedDialog.SaveFailureMessage(new InvalidOperationException("encoder")));
        Assert.Equal(UiText.Get("Dialog.InUse"), ThemedDialog.SaveFailureMessage(new IOException("busy", UserFacingError.SharingViolationHResult)));
    }

    [Fact]
    public void ARealSharingViolationReadsAsFileInUse()
    {
        string folder = Directory.CreateTempSubdirectory("mc-inuse-").FullName;
        string path = Path.Combine(folder, "locked.png");
        try
        {
            using var holder = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            IOException error = Assert.ThrowsAny<IOException>(() => new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None).Dispose());
            Assert.Equal(StorageFailure.InUse, UserFacingError.ClassifyStorage(error));
            Assert.Equal(path, UserFacingError.FindPath(error));
            Assert.Equal(folder, ThemedDialog.SaveLocationFor(error));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void SaveLocationIsOfferedOnlyForStorageFailuresWithAnExistingFolder()
    {
        string folder = Directory.CreateTempSubdirectory("mc-location-").FullName;
        try
        {
            string file = Path.Combine(folder, "a.png");
            Assert.Equal(folder, ThemedDialog.SaveLocationFor(new IOException("x", UserFacingError.DiskFullHResult), file));
            Assert.Equal(folder, ThemedDialog.SaveLocationFor(new UnauthorizedAccessException($"Access to the path '{file}' is denied.")));
            Assert.Equal(folder, ThemedDialog.SaveLocationFor(new IOException("x"), folder));
            Assert.Null(ThemedDialog.SaveLocationFor(new InvalidOperationException($"'{file}'"), file));
            Assert.Null(ThemedDialog.SaveLocationFor(new IOException("x"), Path.Combine(folder, "missing", "a.png")));
            Assert.Null(ThemedDialog.SaveLocationFor(new IOException("no path")));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Theory]
    [InlineData("ko-KR")]
    [InlineData("en-US")]
    public void ASaveErrorOffersOpenSaveLocationAndStaysOpen(string language) => StaTestHost.Run(() =>
    {
        using var scope = UiText.UseLanguage(language);
        string folder = Directory.CreateTempSubdirectory("mc-open-").FullName;
        Action<string> previous = ThemedDialog.OpenFolder;
        var opened = new List<string>();
        try
        {
            ThemedDialog.OpenFolder = opened.Add;
            var error = new IOException($"There is not enough space on the disk. : '{Path.Combine(folder, "a.png")}'", UserFacingError.DiskFullHResult);
            ThemedDialog dialog = ThemedDialog.CreateError(UiText.Get("Dialog.AppFailure"), error);
            Show(dialog);
            Button[] buttons = ActionButtons(dialog);
            Assert.Equal([UiText.Get("Dialog.OpenSaveLocation"), UiText.Get("Dialog.Close")], buttons.Select(SurfaceShortcutHintTests.PrimaryCaption));
            Assert.True(buttons[1].IsDefault);
            Assert.Same(dialog.FindResource("Button.Secondary"), buttons[0].Style);
            buttons[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal([folder], opened);
            Assert.True(dialog.IsVisible, "opening the folder keeps the message in view");
            Assert.Equal(UiText.Format("Dialog.DiskFull", Path.GetPathRoot(folder)!.TrimEnd('\\')), Strip(BodyText(dialog)));
            SaveEvidence(dialog, $"error-diskfull-{language}");

            // When Explorer cannot start, the dialog says so instead of failing silently.
            ThemedDialog.OpenFolder = _ => throw new System.ComponentModel.Win32Exception(2);
            buttons[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            TextBlock status = Descendants(dialog).OfType<TextBlock>().Single(block => Strip(block.Text) == UiText.Get("Dialog.OpenSaveLocationFailed"));
            Assert.Equal(Visibility.Visible, status.Visibility);
            dialog.Close();

            // A failure that is not about storage has no location to open.
            ThemedDialog other = ThemedDialog.CreateError("Title", new InvalidOperationException("x"));
            Show(other);
            Assert.Equal([UiText.Get("Dialog.Close")], ActionButtons(other).Select(SurfaceShortcutHintTests.PrimaryCaption));
            other.Close();
        }
        finally
        {
            ThemedDialog.OpenFolder = previous;
            Directory.Delete(folder, recursive: true);
        }
    });

    [Theory]
    [InlineData("ko-KR")]
    [InlineData("en-US")]
    public void UnexpectedErrorsOfferReportAProblem(string language) => StaTestHost.Run(() =>
    {
        using var scope = UiText.UseLanguage(language);
        Action<string> previous = SupportDiagnostics.OpenUri;
        var opened = new List<string>();
        try
        {
            SupportDiagnostics.OpenUri = opened.Add;
            foreach ((string name, ThemedDialog dialog) in new[] { ("error-unhandled", ThemedDialog.CreateUnhandled(new InvalidOperationException("boom"))),
                ("error-startup", ThemedDialog.CreateStartupFailure(new InvalidOperationException("boom"))) })
            {
                Show(dialog);
                Button[] buttons = ActionButtons(dialog);
                Assert.Equal([UiText.Get("Dialog.ReportProblem"), UiText.Get("Dialog.Close")], buttons.Select(SurfaceShortcutHintTests.PrimaryCaption));
                Assert.True(buttons[1].IsDefault && buttons[1].IsCancel);
                // The message tells the reader to attach the details to the report the button opens.
                Assert.Contains(UiText.Get("Dialog.ReportProblem"), Strip(BodyText(dialog)), StringComparison.OrdinalIgnoreCase);
                buttons[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.True(dialog.IsVisible, "the details stay available to copy into the report");
                SaveEvidence(dialog, $"{name}-{language}");
                dialog.Close();
            }

            Assert.Equal([SupportDiagnostics.IssueUri, SupportDiagnostics.IssueUri], opened);
            SupportDiagnostics.OpenUri = _ => throw new InvalidOperationException("no browser");
            Assert.False(SupportDiagnostics.TryOpenIssuePage());
        }
        finally { SupportDiagnostics.OpenUri = previous; }
    });

    [Theory]
    [InlineData(AppTheme.Daylight)]
    [InlineData(AppTheme.Midnight)]
    [InlineData(AppTheme.Glass)]
    public void DangerButtonsShowDangerTextAndOutlineInEveryPaletteAndContrastTheme(AppTheme theme) => SystemHighContrastThemeTests.Simulate(() =>
    {
        ResourceDictionary resources = SystemHighContrastThemeTests.LoadTheme();
        ThemeService.ApplyResources(theme, resources, systemHighContrast: false);
        var button = new Button { Content = "Delete", Resources = resources };
        button.Style = (Style)resources["Button.Danger"];
        button.ApplyTemplate();
        var chrome = (Border)button.Template.FindName("Chrome", button);
        var hover = (Border)button.Template.FindName("HoverLayer", button);
        Color danger = ((SolidColorBrush)resources["State.Danger"]).Color;
        Assert.Equal(danger, ((SolidColorBrush)button.Foreground).Color);
        Assert.Equal(danger, ((SolidColorBrush)chrome.BorderBrush).Color);
        // Hovering keeps the outline red instead of the neutral strong border.
        Assert.Equal(danger, ((SolidColorBrush)hover.BorderBrush).Color);
        Assert.Equal(new Thickness(1), chrome.BorderThickness);
        // The caption is readable text on the button fill (WCAG 4.5:1).
        Color fill = ((SolidColorBrush)chrome.Background).Color;
        Assert.True(Contrast(danger, fill) >= 4.5, $"{theme}: danger text contrast {Contrast(danger, fill):0.00}");

        ThemeService.ApplyResources(theme, resources, systemHighContrast: true);
        Color text = SystemHighContrastThemeTests.NightSky[SystemColorRole.WindowText];
        Assert.Equal(text, ((SolidColorBrush)button.Foreground).Color);
        Assert.Equal(text, ((SolidColorBrush)chrome.BorderBrush).Color);
    });

    [Theory]
    [InlineData("ko-KR")]
    [InlineData("en-US")]
    public void FolderDeletionUsesTheSharedDangerStyle(string language) => StaTestHost.Run(() =>
    {
        using var scope = UiText.UseLanguage(language);
        var dialog = new GalleryFolderDeletionDialog("Review", new CaptureFolderDeletionPreview(3, 2, 4, 1));
        Show(dialog);
        Assert.Same(dialog.FindResource("Button.Danger"), dialog.DeleteButton.Style);
        Assert.Equal(DependencyProperty.UnsetValue, dialog.DeleteButton.ReadLocalValue(Control.ForegroundProperty));
        SaveEvidence(dialog, $"folder-delete-{language}");
        dialog.Close();

        var recycle = new ThemedDialog(LibraryPermanentDeletion.RecyclePrompt(1, entireLibrary: false), UiText.Get("LibrarySelection_Delete"),
            MessageBoxButton.OKCancel, MessageBoxResult.Cancel, okLabel: UiText.Get("LibrarySelection_MoveToRecycleBin"), destructive: true);
        Show(recycle);
        Assert.Equal(language == "ko-KR" ? "선택한 1개 항목을 휴지통으로 이동하시겠습니까?" : "Move 1 selected item to the Recycle Bin?", Strip(BodyText(recycle)));
        SaveEvidence(recycle, $"delete-confirm-{language}");
        recycle.Close();

        var permanent = new ThemedDialog(UiText.Format("LibrarySelection_RecycleUnavailableMessage", 2), UiText.Get("LibrarySelection_RecycleUnavailableTitle"),
            MessageBoxButton.OKCancel, MessageBoxResult.Cancel, okLabel: UiText.Get("LibrarySelection_PermanentDelete"), destructive: true);
        Show(permanent);
        Assert.Same(permanent.FindResource("Button.Danger"), ActionButtons(permanent)[0].Style);
        SaveEvidence(permanent, $"permanent-delete-{language}");
        permanent.Close();

        var clear = new ThemedDialog(LibraryPermanentDeletion.RecyclePrompt(24, entireLibrary: true), UiText.Get("LibrarySelection_Delete"),
            MessageBoxButton.OKCancel, MessageBoxResult.Cancel, okLabel: UiText.Get("LibrarySelection_MoveToRecycleBin"), destructive: true);
        Show(clear);
        SaveEvidence(clear, $"clear-library-{language}");
        clear.Close();
    });

    private static string Strip(string text) => KoreanWordWrap.Strip(text);

    private static string BodyText(ThemedDialog dialog) =>
        ((StackPanel)((ScrollViewer)dialog.Content).Content).Children.OfType<TextBlock>().ElementAt(1).Text;

    private static Button[] ActionButtons(Window dialog) =>
        Descendants(dialog).OfType<WrapPanel>().Last().Children.OfType<Button>().ToArray();

    private static void Show(Window dialog)
    {
        LoadTheme(dialog);
        FluidMotion.SetWindowEntrance(dialog, false);
        dialog.WindowStartupLocation = WindowStartupLocation.Manual;
        dialog.Left = -10000;
        dialog.Top = -10000;
        dialog.ShowActivated = false;
        dialog.ShowInTaskbar = false;
        dialog.Show();
        dialog.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(dialog.UpdateLayout));
    }

    private static void LoadTheme(Window window) => Ocr410Fixture.LoadTheme(window);

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (DependencyObject descendant in Descendants(child)) yield return descendant;
        }
    }

    private static double Contrast(Color first, Color second)
    {
        double a = Luminance(first) + 0.05, b = Luminance(second) + 0.05;
        return Math.Max(a, b) / Math.Min(a, b);
        static double Luminance(Color color) => 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
        static double Channel(byte value)
        {
            double c = value / 255d;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
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
        using FileStream output = File.Create(Path.Combine(root, name + ".png"));
        encoder.Save(output);
    }
}
