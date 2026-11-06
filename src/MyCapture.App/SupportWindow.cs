using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using MyCapture.App.Diagnostics;
using MyCapture.App.Themes;
using MyCapture.Core.Capture;
using MyCapture.Core.Settings;
using MyCapture.Platform.Display;

namespace MyCapture.App;

internal interface ISupportActions
{
    void CopyText(string text);
    bool TryOpenUri(string uri);
    string? ChooseBundlePath(Window owner) => SupportActions.ChooseDiagnosticsBundlePath(owner);
    void SaveBundle(string path, SupportSubmission submission) =>
        DiagnosticBundle.Write(path, submission.Body, ErrorReporting.FileLog, ErrorReporting.Exits);
    bool TryShowInFolder(string path) => SupportActions.ShowInFolder(path);
}

internal sealed class SupportActions : ISupportActions
{
    public void CopyText(string text) => ClipboardWriter.SetText(text);
    public bool TryOpenUri(string uri) => SupportDiagnostics.TryOpenUri(uri);

    internal static string? ChooseDiagnosticsBundlePath(Window owner)
    {
        var dialog = new SaveFileDialog
        {
            Title = UiText.Get("About_DiagnosticsBundle"),
            Filter = UiText.Get("About_DiagnosticsBundleFilter"),
            DefaultExt = ".zip", AddExtension = true, OverwritePrompt = true,
            FileName = $"MyCapture-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip",
        };
        return dialog.ShowDialog(owner) == true ? dialog.FileName : null;
    }

    internal static bool ShowInFolder(string path)
    {
        try
        {
            string explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            ProcessStartInfo? start = SourceDocument.CreateOpenFolderStartInfo(path, explorer, selectFile: true);
            if (start is null) return false;
            using Process? process = Process.Start(start);
            return process is not null;
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        { return false; }
    }
}

/// <summary>Explicit, previewable contact details; creating the window performs no external action.</summary>
internal sealed class SupportWindow : Window
{
    private readonly ISupportActions _actions;
    private readonly Func<Window, SupportDiagnosticSnapshot> _snapshot;
    private readonly TextBlock _status;
    private readonly TextBlock _accounts;
    private readonly ErrorReport? _report;
    internal TextBox DiagnosticsBox { get; }
    internal TextBox MessageBox { get; }
    internal TextBox? ErrorSummaryBox { get; }
    internal Button BundleButton { get; }
    internal Button StoreReviewButton { get; }
    internal Button CloseButton { get; }
    internal string StatusText => KoreanWordWrap.Strip(_status.Text);

    internal SupportWindow(ISupportActions? actions = null, Func<Window, SupportDiagnosticSnapshot>? snapshot = null,
        ErrorReport? report = null)
    {
        _actions = actions ?? new SupportActions();
        _snapshot = snapshot ?? SupportDiagnosticSnapshot.Capture;
        _report = report;
        Title = UiText.Get(report is null ? "Support_Title" : "Support_ReportError");
        Width = 680; MinWidth = 480; SizeToContent = SizeToContent.Height;
        ApplyHeightLimits(SystemParameters.WorkArea.Height);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        StandardWindowTheme.Apply(this);
        SetResourceReference(BackgroundProperty, "Surface.Raised");
        SetResourceReference(ForegroundProperty, "Text.Primary");
        var content = new StackPanel { Margin = new Thickness(24) };
        content.Children.Add(new TextBlock { Text = Title, FontSize = TypeScale.Subtitle, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 20) });
        content.Children.Add(Label("Support_Diagnostics"));
        DiagnosticsBox = CreateTextInput("Support_Diagnostics", 156);
        DiagnosticsBox.IsReadOnly = true;
        content.Children.Add(DiagnosticsBox);
        content.Children.Add(Label("Support_Message"));
        MessageBox = CreateTextInput("Support_Message", 136);
        MessageBox.MaxLength = 20000;
        content.Children.Add(MessageBox);
        if (report is not null)
        {
            content.Children.Add(Label("Support_ErrorSummary"));
            ErrorSummaryBox = CreateTextInput("Support_ErrorSummary", 200);
            ErrorSummaryBox.MaxLength = 20000;
            ErrorSummaryBox.Text = report.FormatSummary();
            content.Children.Add(ErrorSummaryBox);
            var hint = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = TypeScale.Caption,
                Margin = new Thickness(0, 0, 0, 16) };
            hint.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
            KoreanWordWrap.Show(hint, UiText.Get("Support_ErrorSummaryHint"));
            content.Children.Add(hint);
        }
        _accounts = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = TypeScale.Caption, Margin = new Thickness(0, 0, 0, 16) };
        _accounts.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
        content.Children.Add(_accounts);
        _status = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = TypeScale.Caption, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 12) };
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        content.Children.Add(_status);
        var actionsRow = new WrapPanel();
        actionsRow.Children.Add(ActionButton("Support_Copy", CopyDiagnostics));
        actionsRow.Children.Add(ActionButton("Support_GitHub", OpenGitHub));
        BundleButton = ActionButton("About_DiagnosticsBundle", () => _ = SaveBundleAsync());
        // Settings already offers the bundle beside its support button; only an error report adds it here.
        BundleButton.Visibility = report is null ? Visibility.Collapsed : Visibility.Visible;
        actionsRow.Children.Add(BundleButton);
        StoreReviewButton = ActionButton("Support_StoreReview", OpenStoreReview);
        actionsRow.Children.Add(StoreReviewButton);
        content.Children.Add(actionsRow);
        CloseButton = ActionButton("Dialog.Close", Close);
        CloseButton.HorizontalAlignment = HorizontalAlignment.Right;
        CloseButton.Margin = new Thickness(0, 12, 0, 0);
        CloseButton.ToolTip = UiText.Get("Support_CloseTip");
        content.Children.Add(CloseButton);
        Content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
        RefreshDiagnostics();
        SourceInitialized += (_, _) => RefreshHeightLimits();
        Loaded += (_, _) => RefreshDiagnostics(); // The HWND now identifies the current monitor's DPI.
    }

    internal void RefreshDiagnostics()
    {
        SupportDiagnosticSnapshot value = _snapshot(this);
        DiagnosticsBox.Text = SupportReport.Format(value);
        bool storeReview = value.Packaged && _report is null;
        StoreReviewButton.Visibility = storeReview ? Visibility.Visible : Visibility.Collapsed;
        KoreanWordWrap.Show(_accounts, UiText.Get(storeReview ? "Support_Accounts" : "Support_GitHubAccount"));
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        RefreshHeightLimits();
        RefreshDiagnostics();
    }

    internal static (double MinHeight, double MaxHeight) HeightLimits(double workAreaHeightDip)
    {
        double maximum = Math.Max(1, (double.IsFinite(workAreaHeightDip) ? workAreaHeightDip : 580) - 40);
        return (Math.Min(540, maximum), maximum);
    }

    private void ApplyHeightLimits(double workAreaHeightDip)
    {
        (double minimum, double maximum) = HeightLimits(workAreaHeightDip);
        // Lower the minimum first so moving to a shorter monitor never leaves conflicting bounds.
        MinHeight = minimum;
        MaxHeight = maximum;
    }

    private void RefreshHeightLimits()
    {
        nint handle = new WindowInteropHelper(this).Handle;
        if (handle == 0) { ApplyHeightLimits(SystemParameters.WorkArea.Height); return; }
        var monitor = MonitorEnumerator.GetFromWindow(handle);
        double scale = double.IsFinite(monitor.ScaleFactor) && monitor.ScaleFactor > 0 ? monitor.ScaleFactor : 1;
        ApplyHeightLimits(monitor.WorkArea.Height / scale);
    }

    private static TextBlock Label(string key) => new()
    {
        Text = UiText.Get(key), FontSize = TypeScale.Body, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6),
    };

    private static TextBox CreateTextInput(string key, double height)
    {
        var box = new TextBox { Height = height, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontSize = TypeScale.Body, Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(0, 0, 0, 16) };
        box.SetResourceReference(BackgroundProperty, "Surface.Sunken");
        box.SetResourceReference(ForegroundProperty, "Text.Primary");
        box.SetResourceReference(BorderBrushProperty, "Border.Control");
        box.SetResourceReference(TextBox.CaretBrushProperty, "Text.Primary");
        AutomationProperties.SetName(box, UiText.Get(key));
        return box;
    }

    private static Button ActionButton(string key, Action action)
    {
        var button = new Button { Content = UiText.Get(key), MinHeight = 36, Margin = new Thickness(0, 0, 8, 8) };
        button.SetResourceReference(StyleProperty, "Button.Secondary");
        AutomationProperties.SetName(button, UiText.Get(key));
        button.Click += (_, _) => action();
        return button;
    }

    internal void CopyDiagnostics()
    {
        try
        {
            RefreshDiagnostics();
            _actions.CopyText(Submission().Body);
            SetStatus("Support_Copied");
        }
        catch (Exception error) when (error is System.Runtime.InteropServices.ExternalException or InvalidOperationException)
        { SetStatus("Support_CopyFailed"); }
    }

    internal void OpenGitHub()
    {
        RefreshDiagnostics();
        string url = SupportReport.IssueUrl(Submission(), out bool trimmed);
        SetStatus(_actions.TryOpenUri(url)
            ? trimmed ? "Support_GitHubOpenedTrimmed" : "Support_GitHubOpened" : "Support_OpenFailed");
    }

    internal SupportSubmission Submission() =>
        new(DiagnosticsBox.Text, MessageBox.Text, ErrorSummaryBox?.Text ?? string.Empty, _report);

    internal async Task SaveBundleAsync()
    {
        if (!BundleButton.IsEnabled || BundleButton.Visibility != Visibility.Visible) return;
        BundleButton.IsEnabled = false;
        try
        {
            string? path = _actions.ChooseBundlePath(this);
            if (path is null) return;
            RefreshDiagnostics();
            SupportSubmission submission = Submission();
            await Task.Run(() => _actions.SaveBundle(path, submission));
            SetStatus(_actions.TryShowInFolder(path) ? "Support_BundleSaved" : "Support_BundleSavedFolderFailed");
        }
        catch (Exception)
        {
            // The preview stays intact so another destination or a retry keeps the person's edits.
            SetStatus("Support_BundleFailed");
        }
        finally { BundleButton.IsEnabled = true; }
    }

    internal void OpenStoreReview()
    {
        RefreshDiagnostics();
        if (StoreReviewButton.Visibility != Visibility.Visible) return;
        SetStatus(_actions.TryOpenUri(StoreRatingPolicy.ReviewUri) ? "Support_StoreOpened" : "StoreRating_OpenFailed");
    }

    private void SetStatus(string key)
    {
        KoreanWordWrap.Show(_status, UiText.Get(key));
        _status.Visibility = Visibility.Visible;
    }

    internal static void ShowSupport() => new SupportWindow().Show();
    internal static void ShowErrorReport(ErrorReport report) => new SupportWindow(report: report).Show();
}
