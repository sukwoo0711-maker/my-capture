using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using MyCapture.App.Settings;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Settings;
using Xunit;

namespace MyCapture.App.Tests;

public sealed partial class SettingsThemePreviewTests
{
    [Fact]
    public Task Phase3Settings_GeneralRowsAlignValuesAndWrappedDescriptions() =>
        RunIsolated(nameof(Phase3Settings_GeneralRowsAlignValuesAndWrappedDescriptions), () =>
        {
            Application app = CreateThemedApplication();
            try
            {
            foreach (string language in new[] { "ko-KR", "en-US" })
            {
                using var culture = UiText.UseLanguage(language);
                WithPhase3Settings((window, tabs) =>
                {
                    foreach ((string _, double width, double height) in SettingsDensitySizes)
                    {
                        window.Width = width;
                        window.Height = height;
                        tabs.SelectedItem = window.FindName("GeneralTab");
                        Drain(window);
                        var page = Assert.IsType<ScrollViewer>(((TabItem)tabs.SelectedItem).Content);
                        SettingsField[] rows = Descendants(page).OfType<SettingsField>().ToArray();
                        Assert.Equal(2, rows.Length);
                        AssertSettingsInputsAreCompact(page, language);
                        double? firstValueLeft = null;
                        foreach (SettingsField row in rows)
                        {
                            var label = Assert.IsType<TextBlock>(row.Template.FindName("FieldLabel", row));
                            var value = Assert.IsType<ComboBox>(row.Content);
                            var note = Assert.IsType<ContentPresenter>(row.Template.FindName("FieldNote", row));
                            Rect valueBounds = BoundsIn(value, page);
                            Assert.Equal(FontWeights.Normal, label.FontWeight);
                            Assert.Equal(firstValueLeft ?? valueBounds.Left, valueBounds.Left, 0.5);
                            firstValueLeft = valueBounds.Left;
                            Assert.Equal(valueBounds.Left, BoundsIn(note, page).Left, 0.5);
                            Assert.True(BoundsIn(note, page).Top >= valueBounds.Bottom);
                            Assert.All(Descendants(note).OfType<TextBlock>(), text => Assert.Equal(TextWrapping.Wrap, text.TextWrapping));
                        }
                        AssertNoClippingOrOverlap(page, language);
                    }
                    var navigation = Assert.IsType<Border>(tabs.Template.FindName("CategoryNavigation", tabs));
                    var content = Assert.IsType<Border>(tabs.Template.FindName("SettingsContentPlane", tabs));
                    Assert.Null(navigation.Background);
                    Assert.Null(content.Background);
                    Assert.Equal(default, content.BorderThickness);
                    Assert.Equal(default, content.CornerRadius);
                });
            }
            }
            finally { app.Shutdown(); }
        });

    [Fact]
    public Task Phase3Settings_CategorySwitchIsImmediateAndInterruptibleWithReducedMotion() =>
        RunIsolated(nameof(Phase3Settings_CategorySwitchIsImmediateAndInterruptibleWithReducedMotion), () =>
            WithPhase3Settings((window, tabs) =>
            {
                foreach (bool motion in new[] { true, false })
                {
                    tabs.SelectedItem = window.FindName("CaptureTab");
                    FluidMotion.AnimationsEnabledOverrideForTest = motion;
                    tabs.SelectedItem = window.FindName("GeneralTab");
                    var general = Assert.IsType<ScrollViewer>(tabs.SelectedContent);
                    Assert.Same(window.FindName("GeneralTab"), tabs.SelectedItem);
                    Assert.Equal(1d, general.GetAnimationBaseValue(UIElement.OpacityProperty));
                    Assert.Equal(motion, general.HasAnimatedProperties);
                    Assert.Equal(Transform.Identity.Value, general.RenderTransform.Value);
                    tabs.SelectedItem = window.FindName("EditingTab");
                    Assert.False(general.HasAnimatedProperties);
                    Assert.Equal(1, general.Opacity);
                    var editing = Assert.IsType<ScrollViewer>(tabs.SelectedContent);
                    Assert.Equal(motion, editing.HasAnimatedProperties);
                    Assert.Same(window.FindName("EditingTab"), tabs.SelectedItem);
                    Assert.Equal(1d, editing.GetAnimationBaseValue(UIElement.OpacityProperty));
                }
                Assert.Equal(TimeSpan.FromMilliseconds(250), FluidMotion.DeliberateDuration.TimeSpan);
            }));

    [Fact]
    public Task Phase3Settings_FoldAndComboUseSharedMotionAndSnapWhenReduced() =>
        RunIsolated(nameof(Phase3Settings_FoldAndComboUseSharedMotionAndSnapWhenReduced), () =>
            WithPhase3Settings((window, tabs) =>
            {
                foreach (bool motion in new[] { true, false })
                {
                    FluidMotion.AnimationsEnabledOverrideForTest = motion;
                    tabs.SelectedItem = window.FindName("CaptureTab");
                    Drain(window);
                    var fold = Assert.IsType<Expander>(window.FindName("CaptureControlsExpander"));
                    fold.IsExpanded = true;
                    var content = Assert.IsType<ContentPresenter>(fold.Template.FindName("ExpandSite", fold));
                    Assert.True(fold.IsExpanded);
                    Assert.Equal(motion, content.HasAnimatedProperties);
                    Assert.Equal(1d, content.GetAnimationBaseValue(UIElement.OpacityProperty));
                    fold.IsExpanded = false;
                    Assert.False(content.HasAnimatedProperties);
                    tabs.SelectedItem = window.FindName("GeneralTab");
                    Drain(window);
                    var combo = Assert.IsType<ComboBox>(window.FindName("LanguageSelector"));
                    combo.IsDropDownOpen = true;
                    var popup = Assert.IsType<Popup>(combo.Template.FindName("PART_Popup", combo));
                    Assert.True(popup.IsOpen);
                    Assert.Equal(motion, popup.Child.HasAnimatedProperties);
                    Assert.Equal(1d, popup.Child.GetAnimationBaseValue(UIElement.OpacityProperty));
                    combo.IsDropDownOpen = false;
                    Drain(window);
                    Assert.False(popup.IsOpen);
                    Assert.False(popup.Child.HasAnimatedProperties);
                }
                Assert.Equal(TimeSpan.FromMilliseconds(167), FluidMotion.NormalDuration.TimeSpan);
                Assert.Equal(TimeSpan.FromMilliseconds(83), FluidMotion.FastDuration.TimeSpan);
            }));

    [Fact]
    public Task Phase3Settings_NarrowShortcutRowsWrapAndKeepKeyboardFocusOrder() =>
        RunIsolated(nameof(Phase3Settings_NarrowShortcutRowsWrapAndKeepKeyboardFocusOrder), () =>
            WithPhase3Settings((window, tabs) =>
            {
                window.Width = 780;
                window.Height = 560;
                tabs.SelectedItem = window.FindName("ShortcutsTab");
                Drain(window);
                var page = Assert.IsType<ScrollViewer>(tabs.SelectedContent);
                var field = Descendants(page).OfType<SettingsField>().First();
                var recorder = Assert.IsType<HotkeyRecorder>(field.Content);
                var controls = Assert.IsType<WrapPanel>(recorder.Children[0]);
                Button[] buttons = controls.Children.OfType<Button>().Where(button => button.IsVisible).ToArray();
                Assert.Equal(3, buttons.Length);
                Assert.All(buttons, button => Assert.True(button.Focusable && button.IsTabStop));
                Assert.False(field.Focusable || field.IsTabStop);
                Assert.True(buttons[0].Focus());
                Assert.True(buttons[0].MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)));
                Assert.True(buttons[1].IsKeyboardFocused);
                AssertNoClippingOrOverlap(page, "compact shortcuts");
                Assert.True(window.HandleSearchShortcut(Key.F, ModifierKeys.Control));
                var search = Assert.IsType<TextBox>(tabs.Template.FindName("SettingsSearchBox", tabs));
                Assert.True(search.IsKeyboardFocused);
            }));

    [Fact]
    public Task Phase3Settings_NarrowStoragePathsKeepActionsBelowValuesAndOutsideGuides() =>
        RunIsolated(nameof(Phase3Settings_NarrowStoragePathsKeepActionsBelowValuesAndOutsideGuides), () =>
        {
            using var culture = UiText.UseLanguage("en-US");
            WithPhase3Settings((window, tabs) =>
            {
                window.Width = 780;
                window.Height = 560;
                tabs.SelectedItem = window.FindName("StorageTab");
                Drain(window);
                var page = Assert.IsType<ScrollViewer>(tabs.SelectedContent);
                foreach (string property in new[] { "QuickSaveDirectoryOverride", "CapturesDirectoryOverride" })
                {
                    var input = Assert.IsType<TextBox>(window.FindSettingControl(property));
                    var field = Assert.IsType<SettingsField>(input.Parent);
                    var note = Assert.IsType<ContentPresenter>(field.Template.FindName("FieldNote", field));
                    var actions = Assert.Single(Descendants(note).OfType<WrapPanel>());
                    Button[] buttons = actions.Children.OfType<Button>().ToArray();
                    Assert.Equal(property == "CapturesDirectoryOverride" ? 2 : 1, buttons.Length);
                    Assert.True(BoundsIn(actions, page).Top >= BoundsIn(input, page).Bottom);
                    Assert.Equal(BoundsIn(input, page).Left, BoundsIn(actions, page).Left, 0.5);
                    Assert.All(buttons, button => Assert.True(button.Focusable && button.IsTabStop));
                }
                AssertNoClippingOrOverlap(page, "compact English storage paths");
            });
        });

    private static void WithPhase3Settings(Action<SettingsWindow, TabControl> action)
    {
        bool ownsApplication = Application.Current is null;
        Application app = Application.Current ?? CreateThemedApplication();
        bool? oldMotion = FluidMotion.AnimationsEnabledOverrideForTest;
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        SettingsWindow window = CreateSettingsUxWindow(new AppSettings { General = { Theme = "glass" } }).Window;
        try
        {
            window.Show();
            Drain(window);
            action(window, Assert.IsType<TabControl>(window.FindName("CategoryTabs")));
        }
        finally
        {
            window.CloseForExit();
            FluidMotion.AnimationsEnabledOverrideForTest = oldMotion;
            if (ownsApplication) app.Shutdown();
        }
    }
}
