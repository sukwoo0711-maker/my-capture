using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Settings;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using Xunit;

namespace MyCapture.App.Tests;

public sealed partial class SettingsThemePreviewTests
{
    [Fact]
    public Task EditorBlurStrengthField_BindsValidatesAndAppliesWithTheAnnotationDefaults() =>
        RunIsolated(nameof(EditorBlurStrengthField_BindsValidatesAndAppliesWithTheAnnotationDefaults), VerifyEditorBlurStrengthField);

    private static void VerifyEditorBlurStrengthField()
    {
        using var language = UiText.UseLanguage("ko-KR");
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{name}.xaml"),
            });
        string root = TestRecycleBin.CreateTempSubdirectory("mc-blur-settings-").FullName;
        var store = new SettingsStore(AppPaths.CreateForRoot(root), NullLogger<SettingsStore>.Instance);
        AppSettings saved = new();
        store.Save(saved);
        ThemeService.ApplyFromSettings(saved.General.Theme);
        var window = new SettingsWindow(() => saved, candidate =>
        {
            store.Save(candidate);
            saved = candidate;
            return new SettingsApplyResult(true, true, true, false, []);
        }, NullLogger.Instance)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000, Top = -10000, Width = 940, Height = 700,
            ShowActivated = false, ShowInTaskbar = false,
        };
        FluidMotion.SetWindowEntrance(window, false);
        try
        {
            window.Show();
            Assert.True(window.NavigateToSetting(nameof(SettingsDraft.BlurRadius)));
            Drain(window);

            TextBox field = Descendants(window).OfType<TextBox>()
                .Single(box => box.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path.Path == nameof(SettingsDraft.BlurRadius));
            Assert.True(field.IsVisible);
            Assert.Equal(UiText.Get("SettingsStructure_BlurStrength"), AutomationProperties.GetName(field));
            Assert.Equal("16", field.Text);

            field.Text = "65";
            Drain(window);
            Assert.True(Assert.IsType<SettingsDraft>(window.DataContext).HasErrors);

            field.Text = "40";
            Drain(window);
            SettingsWindow.ApplyCommand.Execute(null, window);
            Assert.Equal(40, saved.Annotation.BlurRadius);
            Assert.Equal(40, store.Load().Annotation.BlurRadius);
        }
        finally
        {
            window.CloseForExit();
            application.Shutdown();
        }
    }
}
