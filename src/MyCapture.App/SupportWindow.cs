using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MyCapture.App.Diagnostics;
using MyCapture.App.Themes;
using MyCapture.Core.Settings;

namespace MyCapture.App;

internal interface ISupportActions
{
    void CopyText(string text);
    bool TryOpenUri(string uri);
}

internal sealed class SupportActions : ISupportActions
{
    public void CopyText(string text) => Clipboard.SetText(text);
    public bool TryOpenUri(string uri) => SupportDiagnostics.TryOpenUri(uri);
}

/// <summary>Explicit, previewable contact details; creating the window performs no external action.</summary>
internal sealed class SupportWindow : Window
{
    private readonly ISupportActions _actions;
    private readonly Func<Window, SupportDiagnosticSnapshot> _snapshot;
    private readonly TextBlock _status;
    private readonly TextBlock _accounts;
    internal TextBox DiagnosticsBox { get; }
    internal TextBox MessageBox { get; }
    internal Button StoreReviewButton { get; }
    internal Button CloseButton { get; }
    internal string StatusText => _status.Text;

    internal SupportWindow(ISupportActions? actions = null, Func<Window, SupportDiagnosticSnapshot>? snapshot = null)
    {
        _actions = actions ?? new SupportActions();
        _snapshot = snapshot ?? SupportDiagnosticSnapshot.Capture;
        Title = UiText.Get("Support_Title");
        Width = 680; Height = 680; MinWidth = 480; MinHeight = 540;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        StandardWindowTheme.Apply(this);
        SetResourceReference(BackgroundProperty, "Surface.Raised");
        SetResourceReference(ForegroundProperty, "Text.Primary");
        var content = new StackPanel { Margin = new Thickness(24) };
        content.Children.Add(new TextBlock { Text = Title, FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 20) });
        content.Children.Add(Label("Support_Diagnostics"));
        DiagnosticsBox = CreateTextInput("Support_Diagnostics", 156);
        DiagnosticsBox.IsReadOnly = true;
        content.Children.Add(DiagnosticsBox);
        content.Children.Add(Label("Support_Message"));
        MessageBox = CreateTextInput("Support_Message", 136);
        MessageBox.MaxLength = 20000;
        content.Children.Add(MessageBox);
        _accounts = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 0, 0, 16) };
        _accounts.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
        content.Children.Add(_accounts);
        _status = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 12) };
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        content.Children.Add(_status);
        var actionsRow = new WrapPanel();
        actionsRow.Children.Add(ActionButton("Support_Copy", CopyDiagnostics));
        actionsRow.Children.Add(ActionButton("Support_GitHub", OpenGitHub));
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
        Loaded += (_, _) => RefreshDiagnostics(); // The HWND now identifies the current monitor's DPI.
    }

    private void RefreshDiagnostics()
    {
        SupportDiagnosticSnapshot value = _snapshot(this);
        DiagnosticsBox.Text = SupportReport.Format(value);
        StoreReviewButton.Visibility = value.Packaged ? Visibility.Visible : Visibility.Collapsed;
        _accounts.Text = UiText.Get(value.Packaged ? "Support_Accounts" : "Support_GitHubAccount");
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        RefreshDiagnostics();
    }

    private static TextBlock Label(string key) => new()
    {
        Text = UiText.Get(key), FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6),
    };

    private static TextBox CreateTextInput(string key, double height)
    {
        var box = new TextBox { Height = height, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontSize = 13, Padding = new Thickness(10, 6, 10, 6),
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
            _actions.CopyText(SupportReport.Body(DiagnosticsBox.Text, MessageBox.Text));
            SetStatus("Support_Copied");
        }
        catch (Exception error) when (error is System.Runtime.InteropServices.ExternalException or InvalidOperationException)
        { SetStatus("Support_CopyFailed"); }
    }

    internal void OpenGitHub()
    {
        RefreshDiagnostics();
        SetStatus(_actions.TryOpenUri(SupportReport.IssueUrl(DiagnosticsBox.Text, MessageBox.Text))
            ? "Support_GitHubOpened" : "Support_OpenFailed");
    }

    internal void OpenStoreReview()
    {
        RefreshDiagnostics();
        if (StoreReviewButton.Visibility != Visibility.Visible) return;
        SetStatus(_actions.TryOpenUri(StoreRatingPolicy.ReviewUri) ? "Support_StoreOpened" : "StoreRating_OpenFailed");
    }

    private void SetStatus(string key)
    {
        _status.Text = UiText.Get(key);
        _status.Visibility = Visibility.Visible;
    }

    internal static void ShowSupport() => new SupportWindow().Show();
}
