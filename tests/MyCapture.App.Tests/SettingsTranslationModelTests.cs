using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Settings;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Settings;
using MyCapture.Core.Themes;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class SettingsTranslationModelTests
{
    [Fact]
    public Task ShowsTheModelSizeAndDeletesItOnlyAfterConfirmation() => SettingsThemePreviewTests.RunIsolated(
        nameof(ShowsTheModelSizeAndDeletesItOnlyAfterConfirmation), () =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{name}.xaml"),
            });
        ThemeService.Apply(AppTheme.Glass);
        long bytes = 246_256_561;
        int deletes = 0;
        var window = new SettingsWindow(() => new AppSettings(), _ => new SettingsApplyResult(true, true, true, false, []),
            NullLogger.Instance)
        {
            DeleteTranslationModel = _ =>
            {
                deletes++;
                bytes = 0;
                return Task.CompletedTask;
            },
            TranslationModelBytes = () => bytes,
        };
        try
        {
            var status = (TextBlock)window.FindName("TranslationModelStatus");
            var delete = (Button)window.FindName("TranslationModelDelete");
            Assert.Equal(UiText.Format("Settings_TranslationInstalled", "247"), KoreanWordWrap.Strip(status.Text));
            Assert.True(delete.IsEnabled);

            long? asked = null;
            window.ConfirmDeleteTranslationModel = size =>
            {
                asked = size;
                return false;
            };
            delete.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Equal(246_256_561, asked);
            Assert.Equal(0, deletes);

            window.ConfirmDeleteTranslationModel = _ => true;
            delete.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));

            Assert.Equal(1, deletes);
            Assert.Equal(UiText.Get("Settings_TranslationNotInstalled"), KoreanWordWrap.Strip(status.Text));
            Assert.False(delete.IsEnabled);
            Assert.Equal(UiText.Get("Settings_TranslationDeleted"), KoreanWordWrap.Strip(((TextBlock)window.FindName("SettingsStatus")).Text));
        }
        finally
        {
            window.CloseForExit();
            application.Shutdown();
        }
    }, typeof(SettingsTranslationModelTests));

    [Fact]
    public Task WithoutADownloadedModelDeleteStaysOff() => SettingsThemePreviewTests.RunIsolated(
        nameof(WithoutADownloadedModelDeleteStaysOff), () =>
    {
        using var language = UiText.UseLanguage("en-US");
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{name}.xaml"),
            });
        var window = new SettingsWindow(() => new AppSettings(), _ => new SettingsApplyResult(true, true, true, false, []),
            NullLogger.Instance)
        {
            DeleteTranslationModel = _ => throw new InvalidOperationException("Nothing to delete."),
            TranslationModelBytes = () => 0,
        };
        try
        {
            Assert.Equal("Not installed", KoreanWordWrap.Strip(((TextBlock)window.FindName("TranslationModelStatus")).Text));
            Assert.False(((Button)window.FindName("TranslationModelDelete")).IsEnabled);
            Assert.Equal(UiText.Format("About_PrivacyTranslation", MyCapture.Ocr.Translation.TranslationModelCatalog.TotalMegabytes),
                KoreanWordWrap.Strip(((TextBlock)window.FindName("PrivacyTranslationText")).Text));
        }
        finally
        {
            window.CloseForExit();
            application.Shutdown();
        }
    }, typeof(SettingsTranslationModelTests));
}
