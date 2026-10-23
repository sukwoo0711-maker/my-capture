using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MyCapture.App.Diagnostics;
using MyCapture.App.Themes;
using MyCapture.Core.Themes;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// The shared buttons come in three tiers (filled, bordered, borderless). Disabling a button
/// only fades it; its fill, border and border thickness stay those of its tier.
/// </summary>
public sealed partial class SettingsThemePreviewTests
{
    private enum ButtonTier { Filled, Bordered, Borderless, Danger }

    private static readonly ThemeColor DarkMicaBackdrop = ThemeColor.Rgb(0x20, 0x20, 0x20);
    private static readonly ThemeColor LightMicaBackdrop = ThemeColor.Rgb(0xF3, 0xF3, 0xF3);

    [Fact]
    public Task SettingsUx_ButtonTiersKeepTheirShapeWhenDisabledInEveryTheme() =>
        RunIsolated(nameof(SettingsUx_ButtonTiersKeepTheirShapeWhenDisabledInEveryTheme), VerifyButtonTiers);

    [Fact]
    public Task SettingsUx_NewInstallHidesKeyCaptionsAndTooltipsKeepTheKey() =>
        RunIsolated(nameof(SettingsUx_NewInstallHidesKeyCaptionsAndTooltipsKeepTheKey), VerifyKeyCaptionDefaults);

    private static void VerifyKeyCaptionDefaults()
    {
        using var motion = new ShortcutMotionTestScope();
        using var language = MyCapture.Core.Localization.UiText.UseLanguage("ko-KR");
        foreach ((MyCapture.Core.Settings.AppSettings settings, bool expected) in new[]
        {
            (MyCapture.Core.Settings.NewInstallDefaults.Create(), false),
            (new MyCapture.Core.Settings.AppSettings(), false),
            (new MyCapture.Core.Settings.AppSettings { General = { ShowShortcutHints = false } }, false),
            (new MyCapture.Core.Settings.AppSettings { General = { AlwaysShowShortcutHints = true } }, true),
        })
        {
            ShortcutPresentation.ReadEnabled = () => settings.General.AlwaysShowShortcutHints;
            ShortcutPresentation.Refresh();
            // Only the explicit always-show preference reveals command captions without Alt.
            var button = new Button { Content = "Rotate", ToolTip = "Rotate left" };
            ShortcutPresentation.Attach(button, ShortcutScope.Annotation, "RotateLeft");
            TextBlock caption = Assert.IsType<Grid>(button.Content).Children.OfType<TextBlock>().Last();
            Assert.Equal("Q", caption.Text);
            ShortcutMotionTestScope.AssertState(caption, expected);
            ShortcutPresentation.SetAltRevealed(button, true);
            ShortcutMotionTestScope.AssertState(caption, true);
            ShortcutPresentation.SetAltRevealed(button, false);
            ShortcutMotionTestScope.AssertState(caption, expected);
            // The key stays discoverable either way: tooltip and accelerator property.
            Assert.Equal("Rotate left (Q)", button.ToolTip);
            Assert.Equal("Q", System.Windows.Automation.AutomationProperties.GetAcceleratorKey(button));
        }
        ShortcutPresentation.ReadEnabled = null;
    }

    private static void VerifyButtonTiers()
    {
        Application application = CreateThemedApplication();
        var table = new StringBuilder("theme,style,tier,enabled label contrast,disabled label contrast,border contrast" + Environment.NewLine);
        try
        {
            var samples = new (string Style, ButtonTier Tier, string? Command)[]
            {
                ("Button.Primary", ButtonTier.Filled, null),
                ("Button.Secondary", ButtonTier.Bordered, null),
                ("Button.Compact", ButtonTier.Bordered, null),
                ("Button.Ghost", ButtonTier.Borderless, null),
                ("Button.GhostCompact", ButtonTier.Borderless, null),
                ("Button.Danger", ButtonTier.Danger, null),
                ("Button.Icon", ButtonTier.Borderless, null),
                // Editor buttons that carry a key caption keep the same rule. Only single keys are
                // printed under a command, so the samples use Q, W and Del.
                ("Button.Ghost", ButtonTier.Borderless, "RotateLeft"),
                ("Button.Secondary", ButtonTier.Bordered, "RotateRight"),
                ("Button.Primary", ButtonTier.Filled, "Delete"),
            };

            foreach (AppTheme theme in SettingsUxEvidenceThemes)
            {
                ThemeService.Apply(theme);
                // The palette actually applied to the application resources.
                ThemeColor Token(string key) => Theme(ColorOf((Brush)application.FindResource(key)));
                var panel = new StackPanel { Margin = new Thickness(12) };
                var pairs = new List<(string Style, ButtonTier Tier, Button Enabled, Button Disabled)>();
                foreach ((string style, ButtonTier tier, string? command) in samples)
                {
                    Button Make(bool enabled)
                    {
                        var button = new Button { Content = "Sample", IsEnabled = enabled, Margin = new Thickness(2) };
                        button.Style = (Style)application.FindResource(style);
                        if (command is not null) ShortcutPresentation.Attach(button, ShortcutScope.Annotation, command);
                        panel.Children.Add(button);
                        return button;
                    }
                    pairs.Add((style, tier, Make(true), Make(false)));
                }

                var surface = new Border { Child = panel };
                surface.SetResourceReference(Border.BackgroundProperty, "Surface.Raised");
                var window = new Window
                {
                    Content = surface, SizeToContent = SizeToContent.WidthAndHeight, WindowStyle = WindowStyle.None,
                    WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000,
                    ShowActivated = false, ShowInTaskbar = false,
                };
                FluidMotion.SetWindowEntrance(window, false);
                try
                {
                    window.Show();
                    Drain(window);
                    foreach ((string style, ButtonTier tier, Button enabled, Button disabled) in pairs)
                    {
                        string label = $"{theme} {style}{(enabled.Content is Panel ? " + key caption" : string.Empty)}";
                        Border on = Assert.IsType<Border>(enabled.Template.FindName("Chrome", enabled));
                        Border off = Assert.IsType<Border>(disabled.Template.FindName("Chrome", disabled));

                        ThemeColor page = PageSurface(theme, Token("Surface.Raised"), Token("Surface.Base"));
                        Assert.Equal(on.BorderThickness, off.BorderThickness);
                        Assert.Equal(1, enabled.Opacity, 3);
                        double disabledFilledRatio = double.NaN;
                        if (tier == ButtonTier.Filled)
                        {
                            // Like WinUI's disabled accent button the fill stays but turns neutral
                            // with disabled text: a faded accent fill left its ink caption and
                            // shortcut near 2:1 on dark themes. It must stay readable (3:1).
                            Assert.Equal(Wpf(Token("Surface.Overlay")), ColorOf(off.Background));
                            Assert.Equal(Wpf(Token("Surface.Overlay")), ColorOf(off.BorderBrush));
                            Assert.Equal(1, disabled.Opacity, 3);
                            Assert.Equal(Wpf(Token("Text.Disabled")), ColorOf(disabled.Foreground));
                            disabledFilledRatio = ThemeContrast.Ratio(Theme(ColorOf(disabled.Foreground)), Over(Theme(ColorOf(off.Background)), page));
                            Assert.True(disabledFilledRatio >= 3, $"{label}: disabled label contrast {disabledFilledRatio:0.00}");
                        }
                        else
                        {
                            // Same shape: fill, border colour and thickness do not change when disabled.
                            Assert.True(Same(on.Background, off.Background), $"{label}: disabled fill changed");
                            Assert.True(Same(on.BorderBrush, off.BorderBrush), $"{label}: disabled border changed");
                            // Only fades, by the one shared amount.
                            Assert.Equal(0.46, disabled.Opacity, 3);
                            Assert.True(Same(enabled.Foreground, disabled.Foreground), $"{label}: disabled label colour changed");
                        }

                        Color fill = ColorOf(on.Background);
                        Color border = ColorOf(on.BorderBrush);
                        switch (tier)
                        {
                            case ButtonTier.Filled:
                                Assert.Equal(Wpf(Token("Accent.Default")), fill);
                                break;
                            case ButtonTier.Bordered:
                                // Control outlines have their own token: Border.Subtle is decorative
                                // and too faint on glass to mark a button.
                                Assert.Equal(Wpf(Token("Border.Control")), border);
                                Assert.Equal(Wpf(Token("Surface.Overlay")), fill);
                                break;
                            case ButtonTier.Danger:
                                // Destructive commands: the bordered shape with danger text and outline.
                                Assert.Equal(Wpf(Token("State.Danger")), border);
                                Assert.Equal(Wpf(Token("State.Danger")), ColorOf(enabled.Foreground));
                                Assert.Equal(Wpf(Token("Surface.Overlay")), fill);
                                break;
                            default:
                                Assert.Equal(0, fill.A);
                                Assert.Equal(0, border.A);
                                break;
                        }

                        // Contrast of the label (and the border of the bordered tier) on the page surface.
                        ThemeColor background = tier == ButtonTier.Borderless ? page : Over(Theme(fill), page);
                        ThemeColor text = Theme(ColorOf(enabled.Foreground));
                        double enabledRatio = ThemeContrast.Ratio(text, background);
                        double disabledRatio = tier == ButtonTier.Filled
                            ? disabledFilledRatio
                            : ThemeContrast.Ratio(Fade(text, page), Fade(background, page));
                        bool outlined = tier is ButtonTier.Bordered or ButtonTier.Danger;
                        double borderRatio = outlined ? ThemeContrast.Ratio(Over(Theme(border), page), page) : double.NaN;
                        Assert.True(enabledRatio >= 4.5, $"{label}: label contrast {enabledRatio:0.00}");
                        if (outlined)
                            Assert.True(borderRatio >= 3.0, $"{label}: outline contrast {borderRatio:0.00}");
                        Assert.True(disabledRatio < enabledRatio, $"{label}: disabled label is not dimmer");
                        table.AppendLine(string.Create(CultureInfo.InvariantCulture,
                            $"{AppThemeNames.ToSetting(theme)},{style}{(enabled.Content is Panel ? "+key" : string.Empty)},{tier},{enabledRatio:0.00},{disabledRatio:0.00},{borderRatio:0.00}"));
                    }
                }
                finally
                {
                    window.Close();
                }
            }

            Assert.Equal(0.46, Assert.IsType<double>(application.FindResource("Button.DisabledOpacity")), 2);
            Console.WriteLine(table.ToString());
            string? requested = Environment.GetEnvironmentVariable("MYCAPTURE_SETTINGS_UX_EVIDENCE");
            if (!string.IsNullOrWhiteSpace(requested))
                File.WriteAllText(DiagnosticOutputPaths.Child(DiagnosticOutputPaths.Create(requested), "button-tier-contrast.csv"), table.ToString());
        }
        finally
        {
            application.Shutdown();
        }

        static Color ColorOf(Brush brush) => Assert.IsType<SolidColorBrush>(brush).Color;
        static bool Same(Brush first, Brush second) => ColorOf(first) == ColorOf(second);
        static Color Wpf(ThemeColor color) => Color.FromArgb(color.A, color.R, color.G, color.B);
        static ThemeColor Theme(Color color) => ThemeColor.Argb(color.A, color.R, color.G, color.B);
        static ThemeColor Over(ThemeColor top, ThemeColor bottom) => ThemeContrast.Composite(top, bottom);
        static ThemeColor Fade(ThemeColor color, ThemeColor under) =>
            ThemeContrast.Composite(ThemeColor.Argb((byte)Math.Round(color.A * 0.46), color.R, color.G, color.B), under);
        static ThemeColor PageSurface(AppTheme theme, ThemeColor raised, ThemeColor @base)
        {
            ThemeColor backdrop = theme == AppTheme.GlassLight ? LightMicaBackdrop : DarkMicaBackdrop;
            return raised.A == 0xFF ? raised : ThemeContrast.Composite(raised, Over(@base, backdrop));
        }
    }
}
