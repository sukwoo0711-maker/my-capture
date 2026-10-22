using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Editing;
using MyCapture.App.Themes;
using MyCapture.Core.Annotations;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using MyCapture.Core.Themes;
using MyCapture.Platform.Capture;
using Xunit;
using static MyCapture.App.Tests.EditorKeysTests;

namespace MyCapture.App.Tests;

public sealed class EditorChromeAndTextInspectorTests : KoreanCaptionTest
{
    [Theory]
    [InlineData(680, 460)]
    [InlineData(1280, 640)]
    public void AltMotionLeavesEditorLayoutCompactionAndViewportUnchanged(int width, int height) => StaTestHost.Run(() =>
    {
        using var preferences = new PreferencesScope(new AnnotationDefaults());
        Func<bool>? previous = ShortcutPresentation.ReadEnabled;
        try
        {
            ShortcutPresentation.ReadEnabled = () => false;
            using var host = EditorHost.Create(width, height);
            FluidMotion.AnimationsEnabledOverrideForTest = true;
            ShortcutRevealMotionTests.Pump(100);
            Border bar = Tree<Border>(host.Editor).Single(border => border.Name == "AnnotationCommandBar");
            var surface = Field<AnnotationEditorSurface>(host.Editor, "_surface");
            FrameworkElement body = host.Editor;
            var faces = Tree<Grid>(bar).Where(grid => grid.Name == "CommandButtonFace").ToArray();
            var commands = Tree<Button>(bar).Where(button => button.IsVisible).ToArray();
            Size[] faceSizes = faces.Select(face => face.RenderSize).ToArray();
            Size[] buttonSizes = commands.Select(button => button.RenderSize).ToArray();
            // Label visibility and command positions describe the chosen fitting tier.
            Visibility[] labels = Tree<TextBlock>(bar).Select(label => label.Visibility).ToArray();
            Point[] positions = commands.Select(button => button.TranslatePoint(default, bar)).ToArray();
            double dipPerPixel = surface.DipPerPixel;
            int sizeChanges = 0;
            host.Editor.LayoutUpdated += (_, _) =>
            {
                if (!faceSizes.SequenceEqual(faces.Select(face => face.RenderSize))
                    || !buttonSizes.SequenceEqual(commands.Select(button => button.RenderSize))) sizeChanges++;
            };
            void Check()
            {
                Assert.True(bar.IsMeasureValid && bar.IsArrangeValid);
                Assert.True(body.IsMeasureValid && body.IsArrangeValid);
                Assert.True(surface.IsMeasureValid && surface.IsArrangeValid);
                Assert.All(faces, face => Assert.True(face.IsMeasureValid && face.IsArrangeValid));
                Assert.Equal(faceSizes, faces.Select(face => face.RenderSize));
                Assert.Equal(buttonSizes, commands.Select(button => button.RenderSize));
                Assert.Equal(labels, Tree<TextBlock>(bar).Select(label => label.Visibility));
                Assert.Equal(positions, commands.Select(button => button.TranslatePoint(default, bar)));
                Assert.Equal(dipPerPixel, surface.DipPerPixel);
                Assert.Equal(0, sizeChanges);
            }
            foreach (bool revealed in new[] { true, false, true, false })
            {
                ShortcutPresentation.SetAltRevealed(host.Window, revealed);
                Check();
                ShortcutRevealMotionTests.Pump(45);
                Check();
                ShortcutRevealMotionTests.Pump(180);
                Check();
            }
        }
        finally
        {
            ShortcutPresentation.ReadEnabled = previous;
            ShortcutPresentation.Refresh();
        }
    });

    [Fact]
    public void UnselectedTextSettingsPersistAndApplyToTheNextCaption() => StaTestHost.Run(() =>
    {
        using var motion = new ShortcutMotionTestScope();
        using var preferences = new PreferencesScope(new AnnotationDefaults());
        string family;
        using (var host = EditorHost.Create())
        {
            SelectTextTool(host.Editor);
            Assert.Null(Controller(host.Editor).Selected);
            ComboBox fonts = Field<ComboBox>(host.Editor, "_textFontFamily");
            Assert.True(fonts.IsVisible);
            family = fonts.Items.Cast<FontChoice>().First(font => !font.IsMore && font.Value != "Malgun Gothic").Value;
            fonts.SelectedItem = fonts.Items.Cast<FontChoice>().Single(font => font.Value == family);
            Field<Slider>(host.Editor, "_fontSizeSlider").Value = 32;
            Toggle(host.Editor, "_textBold", true);
            Toggle(host.Editor, "_textBackground", true);
            Toggle(host.Editor, "_textOutline", false);
            Assert.Empty(Controller(host.Editor).Document.Items);
            Assert.False(Controller(host.Editor).CanUndo);
        }

        AnnotationDefaults persisted = preferences.Read();
        Assert.Equal(family, persisted.FontFamily);
        Assert.Equal(32, persisted.FontSize);
        Assert.True(persisted.TextBold);
        Assert.True(persisted.TextBackground);
        Assert.False(persisted.TextOutline);
        Assert.Contains(family, persisted.RecentFontFamilies);

        using var reopened = EditorHost.Create();
        SelectTextTool(reopened.Editor);
        Assert.Equal(family, Assert.IsType<FontChoice>(Field<ComboBox>(reopened.Editor, "_textFontFamily").SelectedItem).Value);
        Assert.Equal(32, Field<Slider>(reopened.Editor, "_fontSizeSlider").Value);
        Assert.True(Field<CheckBox>(reopened.Editor, "_textBold").IsChecked);
        Assert.True(Field<CheckBox>(reopened.Editor, "_textBackground").IsChecked);
        Assert.False(Field<CheckBox>(reopened.Editor, "_textOutline").IsChecked);

        reopened.Editor.SimulatePointerDown(reopened.Editor.ViewportPointOf(new PointD(60, 60)), 1, shift: false);
        TextBox input = Field<TextBox>(reopened.Editor, "_activeTextBox");
        Assert.True(Field<CheckBox>(reopened.Editor, "_textBold").IsChecked);
        Assert.True(Field<CheckBox>(reopened.Editor, "_textBackground").IsChecked);
        Assert.False(Field<CheckBox>(reopened.Editor, "_textOutline").IsChecked);
        input.Text = "새 텍스트";
        Invoke(reopened.Editor, "CommitActiveText");
        TextAnnotation caption = Assert.IsType<TextAnnotation>(Assert.Single(Controller(reopened.Editor).Document.Items));
        Assert.Equal(family, caption.FontFamily);
        Assert.Equal(32, caption.FontSize);
        Assert.True(caption.Bold);
        Assert.True(caption.Background.A > 0);
        Assert.False(caption.HasOutline);
        Assert.Equal(TextOutlineStyle.Consistent, caption.OutlineStyle);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FontPickerSharesRecommendationsAndPreviewsEachResolvedFamily(bool reusePreview) => StaTestHost.Run(() =>
    {
        using var motion = new ShortcutMotionTestScope();
        string[] installed = Fonts.SystemFontFamilies.Select(font => font.Source)
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        Assert.True(installed.Length >= 3);
        using var preferences = new PreferencesScope(new AnnotationDefaults
        {
            FontFamily = installed[^1],
            RecentFontFamilies = [installed[1], installed[0], installed[1]],
        });
        using var host = EditorHost.Create();
        SelectTextTool(host.Editor);
        ComboBox picker = Field<ComboBox>(host.Editor, "_textFontFamily");
        FontChoice[] choices = picker.Items.Cast<FontChoice>().ToArray();
        Assert.Equal(FontCatalog.ChoicesFor(installed[^1], FontCatalog.RecommendedFonts(UiText.Culture)), choices);
        Assert.Equal(choices.Length, choices.Select(font => font.Value).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.True(choices[^1].IsMore);

        Assert.NotNull(picker.ItemTemplate);
        ContentPresenter? preview = null;
        foreach (FontChoice family in choices)
        {
            // Apply the template through WPF so its factory is sealed before instantiation.
            if (preview is null || !reusePreview)
                preview = new ContentPresenter { ContentTemplate = picker.ItemTemplate };
            preview.Content = family;
            preview.Measure(new Size(240, 100));
            preview.Arrange(new Rect(preview.DesiredSize));
            TextBlock label = Assert.Single(Tree<TextBlock>(preview), text => text.Text == family.Label);
            Assert.Same(family.PreviewFamily, label.FontFamily);
        }
    });

    [Fact]
    public void SelectedFontChangeRefitsAndOneUndoRestoresTheFamilyAndBounds() => StaTestHost.Run(() =>
    {
        using var motion = new ShortcutMotionTestScope();
        using var preferences = new PreferencesScope(new AnnotationDefaults());
        using var host = EditorHost.Create();
        var controller = Controller(host.Editor);
        var caption = new TextAnnotation
        {
            Text = "Caption",
            FontFamily = "Malgun Gothic",
            Rect = new RectD(40, 40, 120, 40),
            AutoSize = true,
        };
        controller.Document.Add(caption);
        controller.SetSelected(caption);
        Assert.False(controller.CanUndo);
        ComboBox picker = Field<ComboBox>(host.Editor, "_textFontFamily");
        FontChoice next = picker.Items.Cast<FontChoice>().First(font => !font.IsMore && font.Value != caption.FontFamily);
        RectD original = caption.Rect;
        controller.TextFitter = text => new RectD(text.Rect.X, text.Rect.Y,
            text.FontFamily == next.Value ? 220 : 120, 40);
        int undoCount = controller.Undo.UndoCount;

        picker.SelectedItem = next;

        Assert.Equal(next.Value, caption.FontFamily);
        Assert.Equal(220, caption.Rect.Width);
        Assert.Equal(undoCount + 1, controller.Undo.UndoCount);
        Assert.True(controller.PerformUndo());
        Assert.Equal("Malgun Gothic", caption.FontFamily);
        Assert.Equal(original, caption.Rect);
        Assert.Equal("Malgun Gothic", Assert.IsType<FontChoice>(picker.SelectedItem).Value);
        Assert.True(controller.PerformRedo());
        Assert.Equal(next.Value, caption.FontFamily);
        Assert.Equal(220, caption.Rect.Width);
    });

    [Theory]
    [InlineData(1000, 640)]
    [InlineData(680, 460)]
    public void RailKeepsEveryAltRevealedHintAndHitTargetInsideItsBounds(int width, int height) => StaTestHost.Run(() =>
    {
        using var motion = new ShortcutMotionTestScope();
        using var preferences = new PreferencesScope(new AnnotationDefaults());
        Func<bool>? previous = ShortcutPresentation.ReadEnabled;
        try
        {
            ShortcutPresentation.ReadEnabled = () => false;
            ShortcutPresentation.Refresh();
            using var host = EditorHost.Create(width, height);
            Border rail = Tree<Border>(host.Editor).Single(border => border.Name == "AnnotationToolRail");
            Dictionary<EditorTool, ToggleButton> tools = Field<Dictionary<EditorTool, ToggleButton>>(host.Editor, "_toolButtons");
            Assert.Equal(Enum.GetValues<EditorTool>().Length - 1, tools.Count);
            foreach (ToggleButton tool in tools.Values)
            {
                ShortcutMotionTestScope.AssertState(Assert.Single(Tree<TextBlock>(tool), text =>
                    text.Text == AutomationProperties.GetAcceleratorKey(tool)), false);
                Grid face = Assert.IsType<Grid>(tool.Content);
                Viewbox glyph = Assert.Single(Tree<Viewbox>(tool));
                Rect bounds = glyph.TransformToAncestor(face).TransformBounds(new Rect(glyph.RenderSize));
                Assert.Equal(24, bounds.Width, 6);
                Assert.Equal(24, bounds.Height, 6);
                Assert.InRange(bounds.Left + bounds.Width / 2, 15.5, 16.5);
                Assert.InRange(bounds.Top + bounds.Height / 2, 15.5, 16.5);
                var path = Assert.Single(Tree<System.Windows.Shapes.Path>(glyph));
                Assert.Equal(1.6, path.StrokeThickness * glyph.Width / 20, 6);
                Rect ink = path.RenderedGeometry.Bounds;
                ink.Inflate(path.StrokeThickness / 2, path.StrokeThickness / 2);
                Assert.True(new Rect(face.RenderSize).Contains(path.TransformToAncestor(face).TransformBounds(ink)));
            }
            ShortcutPresentation.SetAltRevealed(host.Window, true);
            host.Window.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(host.Window.UpdateLayout));
            foreach (ToggleButton tool in tools.Values)
            {
                string key = AutomationProperties.GetAcceleratorKey(tool);
                Assert.False(string.IsNullOrWhiteSpace(key));
                Assert.Contains(key, Assert.IsType<string>(tool.ToolTip), StringComparison.Ordinal);
                Assert.True(tool.Focusable);
                Assert.True(tool.ActualWidth >= 36 && tool.ActualHeight >= 36);
                AssertInside(tool, rail);
                TextBlock hint = Assert.Single(Tree<TextBlock>(tool), text => text.Text == key);
                Assert.True(hint.IsVisible);
                AssertInside(hint, tool);
                Assert.Single(Tree<Viewbox>(tool));
                Grid face = Assert.IsType<Grid>(tool.Content);
                Viewbox glyph = Assert.Single(Tree<Viewbox>(tool));
                Rect glyphBounds = glyph.TransformToAncestor(face).TransformBounds(new Rect(glyph.RenderSize));
                Rect hintBounds = hint.TransformToAncestor(face).TransformBounds(new Rect(hint.RenderSize));
                Assert.Equal(18, glyphBounds.Width, 6);
                Assert.Equal(18, glyphBounds.Height, 6);
                Assert.False(glyphBounds.IntersectsWith(hintBounds));
                Assert.True(new Rect(face.RenderSize).Contains(glyphBounds));
                Assert.Null(tool.Template.FindName("Indicator", tool));
                Assert.NotNull(tool.Template.FindName("FocusRing", tool));
            }

            ToggleButton selected = Assert.Single(tools.Values, button => button.IsChecked == true);
            Border tile = Assert.IsType<Border>(selected.Template.FindName("CheckedLayer", selected));
            Assert.True(VisibleBrush(tile.Background));
            Assert.Equal(1, tile.Opacity);
            AssertInside(rail, host.Editor);
        }
        finally
        {
            ShortcutPresentation.ReadEnabled = previous;
            ShortcutPresentation.Refresh();
        }
    });

    [Fact]
    public void RailAndCommandBarHintsFollowAlwaysPreferenceOrAltImmediately() => StaTestHost.Run(() =>
    {
        using var motion = new ShortcutMotionTestScope();
        using var preferences = new PreferencesScope(new AnnotationDefaults());
        Func<bool>? previous = ShortcutPresentation.ReadEnabled;
        bool enabled = true;
        try
        {
            ShortcutPresentation.ReadEnabled = () => enabled;
            using var host = EditorHost.Create();
            var hints = new List<TextBlock>();
            foreach (string name in new[] { "AnnotationToolRail", "AnnotationCommandBar" })
            {
                Border container = Tree<Border>(host.Editor).Single(border => border.Name == name);
                int previousCount = hints.Count;
                foreach (ButtonBase button in Tree<ButtonBase>(container).Where(button => button.IsVisible))
                {
                    string key = AutomationProperties.GetAcceleratorKey(button);
                    if (string.IsNullOrEmpty(key)) continue;
                    ShortcutDefinition definition = ShortcutCatalog.All.First(entry =>
                        entry.Scope == ShortcutScope.Annotation && entry.Hint == key);
                    if (!ShortcutPresentation.ShowsCaption(definition)) continue;
                    hints.Add(Assert.Single(Tree<TextBlock>(button), text => text.Text == key));
                }
                Assert.True(hints.Count > previousCount, $"{name} has no captioned commands.");
            }
            Assert.True(hints.Count > Enum.GetValues<EditorTool>().Length);
            foreach (bool preference in new[] { true, false, true })
            {
                enabled = preference;
                ShortcutPresentation.Refresh();
                Assert.All(hints, hint => ShortcutMotionTestScope.AssertState(hint, preference));
                ShortcutPresentation.SetAltRevealed(host.Window, true);
                Assert.All(hints, hint => ShortcutMotionTestScope.AssertState(hint, true));
                ShortcutPresentation.SetAltRevealed(host.Window, false);
                Assert.All(hints, hint => ShortcutMotionTestScope.AssertState(hint, preference));
            }
        }
        finally
        {
            ShortcutPresentation.ReadEnabled = previous;
            ShortcutPresentation.Refresh();
        }
    });

    [Theory]
    [InlineData(1280, 640)]
    [InlineData(1000, 640)]
    [InlineData(680, 460)]
    public void CommandsKeepButtonAffordancesIconsAndBoundKeysAtBothWidths(int width, int height) => StaTestHost.Run(() =>
    {
        using var motion = new ShortcutMotionTestScope();
        using var preferences = new PreferencesScope(new AnnotationDefaults());
        using var host = EditorHost.Create(width, height);
        ShortcutPresentation.SetAltRevealed(host.Window, true);
        host.Window.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(host.Window.UpdateLayout));
        Border bar = Tree<Border>(host.Editor).Single(border => border.Name == "AnnotationCommandBar");
        Button[] commands = Tree<Button>(bar).Where(button => button.IsVisible).ToArray();
        Assert.True(commands.Length >= 10);
        foreach (Button command in commands)
        {
            AssertInside(command, bar);
            Assert.NotNull(command.ToolTip);
            Assert.NotEmpty(Tree<Viewbox>(command));
            Assert.Contains(Tree<Border>(command), border => border.IsVisible
                && (VisibleBrush(border.Background) || VisibleBrush(border.BorderBrush) && border.BorderThickness != new Thickness(0)));
            string key = AutomationProperties.GetAcceleratorKey(command);
            if (!string.IsNullOrEmpty(key))
            {
                TextBlock hint = Assert.Single(Tree<TextBlock>(command), text => text.Text == key);
                Assert.True(hint.IsVisible);
                AssertInside(hint, command);
            }

            Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(command)));
            Assert.Contains(Tree<TextBlock>(command), text => !string.IsNullOrWhiteSpace(text.Text) && text.Text != key);
            if ((bool)command.GetValue(CommandButtonPresentation.AlwaysIconProperty))
            {
                Assert.DoesNotContain(Tree<TextBlock>(command), text => text.IsVisible && !string.IsNullOrWhiteSpace(text.Text) && text.Text != key);
            }
            else if (width >= 960)
            {
                Assert.Contains(Tree<TextBlock>(command), text => text.IsVisible && !string.IsNullOrWhiteSpace(text.Text) && text.Text != key);
            }
        }
    });

    [Theory]
    [InlineData("ko-KR", 1280, 640)]
    [InlineData("en-US", 1280, 640)]
    [InlineData("ko-KR", 1000, 640)]
    [InlineData("ko-KR", 680, 460)]
    [InlineData("en-US", 1000, 640)]
    [InlineData("en-US", 680, 460)]
    public void EveryCommandKeepsItsHeightAndGapWithAltRevealedOrCollapsedHints(string language, int width, int height) => StaTestHost.Run(() =>
    {
        using var motion = new ShortcutMotionTestScope();
        using var culture = UiText.UseLanguage(language);
        using var preferences = new PreferencesScope(new AnnotationDefaults());
        Func<bool>? previous = ShortcutPresentation.ReadEnabled;
        try
        {
            ShortcutPresentation.ReadEnabled = () => false;
            using var host = EditorHost.Create(width, height);
            Border bar = Tree<Border>(host.Editor).Single(border => border.Name == "AnnotationCommandBar");
            foreach (bool crop in new[] { false, true })
            {
                if (crop) Assert.True(host.Editor.HandleShortcut(System.Windows.Input.Key.X, System.Windows.Input.ModifierKeys.None));
                foreach (bool show in new[] { true, false, true })
                {
                    ShortcutPresentation.SetAltRevealed(host.Window, show);
                    // Allow inherited bindings and the panel's queued refit to settle before measuring.
                    host.Window.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(host.Window.UpdateLayout));
                    Button[] commands = Tree<Button>(bar).Where(button => button.IsVisible).ToArray();
                    Assert.Equal(crop ? 5 : 10, commands.Length);
                    Assert.Contains(commands, button => !string.IsNullOrEmpty(AutomationProperties.GetAcceleratorKey(button)));
                    Assert.Contains(commands, button => string.IsNullOrEmpty(AutomationProperties.GetAcceleratorKey(button)));
                    foreach (Button button in commands)
                    {
                        Assert.Equal(44, button.ActualHeight, 2);
                        AssertInside(button, bar);
                        Grid face = Assert.IsType<Grid>(button.Content);
                        Assert.Equal("CommandButtonFace", face.Name);
                        RowDefinition hintRow = face.RowDefinitions[1];
                        Assert.Equal(new GridLength(14), hintRow.Height);
                        double scale = VisualTreeHelper.GetDpi(face).DpiScaleY;
                        string key = AutomationProperties.GetAcceleratorKey(button);
                        double hintHeight = CommandButtonPresentation.HintHeight;
                        Assert.Equal(Math.Round(hintHeight * scale), hintRow.ActualHeight * scale, 6);
                        if (!string.IsNullOrEmpty(key))
                        {
                            TextBlock hint = Assert.Single(Tree<TextBlock>(button), text => text.Text == key);
                            ShortcutMotionTestScope.AssertState(hint, show);
                            if (show) AssertInside(hint, button);
                        }
                        if (!show || string.IsNullOrEmpty(key))
                        {
                            FrameworkElement label = Assert.IsAssignableFrom<FrameworkElement>(face.Children[0]);
                            double center = label.TranslatePoint(new Point(0, label.ActualHeight / 2), button).Y;
                            Assert.InRange(center, button.ActualHeight / 2 - 1, button.ActualHeight / 2 + 1);
                        }
                    }

                    for (int first = 0; first < commands.Length; first++)
                    {
                        Rect firstBounds = commands[first].TransformToAncestor(bar).TransformBounds(new Rect(commands[first].RenderSize));
                        for (int second = first + 1; second < commands.Length; second++)
                        {
                            Rect secondBounds = commands[second].TransformToAncestor(bar).TransformBounds(new Rect(commands[second].RenderSize));
                            Assert.False(firstBounds.IntersectsWith(secondBounds), $"Commands overlap: {firstBounds}, {secondBounds}.");
                            if (Math.Abs(firstBounds.Top - secondBounds.Top) < 0.5)
                            {
                                double gap = Math.Max(firstBounds.Left, secondBounds.Left) - Math.Min(firstBounds.Right, secondBounds.Right);
                                Assert.True(gap >= 5.5, $"Command gap is {gap} at {language} {width}.");
                            }
                        }
                    }

                    if (!crop)
                    {
                        foreach (Button primaryTool in new[]
                        {
                            commands.Single(button => AutomationProperties.GetAutomationId(button) == "Capture410Crop"),
                            Field<Button>(host.Editor, "_redactButton"),
                            Field<Button>(host.Editor, "_copyTextButton"),
                        })
                        {
                            Assert.True(primaryTool.IsVisible);
                            string key = AutomationProperties.GetAcceleratorKey(primaryTool);
                            Assert.NotEmpty(Tree<Viewbox>(primaryTool));
                            Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(primaryTool)));
                            Assert.Contains(key, Assert.IsType<string>(primaryTool.ToolTip));
                            Assert.Contains(Tree<TextBlock>(primaryTool), text => !string.IsNullOrWhiteSpace(text.Text) && text.Text != key);
                            if (width >= 960)
                                Assert.Contains(Tree<TextBlock>(primaryTool), text => text.IsVisible
                                    && !string.IsNullOrWhiteSpace(text.Text) && text.Text != key);
                        }
                    }
                }
            }
        }
        finally
        {
            ShortcutPresentation.ReadEnabled = previous;
            ShortcutPresentation.Refresh();
        }
    });

    [Fact]
    public void TextInspectorVisualEvidenceUsesOnlySyntheticContent() => StaTestHost.Run(() =>
    {
        using var motion = new ShortcutMotionTestScope();
        using var preferences = new PreferencesScope(new AnnotationDefaults());
        var document = AnnotationDocument.CreateFor(640, 360);
        ColorRgba[] colors = [ColorRgba.FromRgb(0xEF, 0x44, 0x44), ColorRgba.FromRgb(0xFA, 0xCC, 0x15),
            ColorRgba.White, ColorRgba.FromRgb(0x12, 0x12, 0x12)];
        for (int index = 0; index < colors.Length; index++)
        {
            document.Add(new TextAnnotation
            {
                Text = "기록을 남기는 텍스트",
                FontSize = 30,
                Foreground = colors[index],
                Rect = new RectD(40, 35 + index * 75, 500, 55),
                HasOutline = true,
                OutlineStyle = TextOutlineStyle.Consistent,
            });
        }

        using var host = EditorHost.Create(document: document);
        Controller(host.Editor).SetSelected(document.Items[0]);
        host.Window.UpdateLayout();
        SaveEvidence(host.Editor, "text-inspector.png");
        ShortcutPresentation.SetAltRevealed(host.Window, true);
        host.Window.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(host.Window.UpdateLayout));
        SaveEvidence(host.Editor, "text-inspector-alt.png");
        ShortcutPresentation.SetAltRevealed(host.Window, false);
        host.Window.Width = 680;
        host.Window.Height = 460;
        host.Window.UpdateLayout();
        SaveEvidence(host.Editor, "text-inspector-compact.png");
        ShortcutPresentation.SetAltRevealed(host.Window, true);
        host.Window.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() => { }));
        host.Window.UpdateLayout();
        SaveEvidence(host.Editor, "text-inspector-compact-alt.png");
    });

    private static void SelectTextTool(AnnotationEditorControl editor) =>
        Field<Dictionary<EditorTool, ToggleButton>>(editor, "_toolButtons")[EditorTool.Text]
            .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    private static void Toggle(AnnotationEditorControl editor, string field, bool value)
    {
        CheckBox control = Field<CheckBox>(editor, field);
        Assert.True(control.IsVisible);
        control.IsChecked = value;
        control.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    }

    private static bool VisibleBrush(Brush? brush) => brush is SolidColorBrush { Color.A: > 0, Opacity: > 0 };

    private static void AssertInside(FrameworkElement element, FrameworkElement parent)
    {
        Point origin = element.TranslatePoint(new Point(), parent);
        Assert.True(origin.X >= -0.5 && origin.Y >= -0.5
            && origin.X + element.ActualWidth <= parent.ActualWidth + 0.5
            && origin.Y + element.ActualHeight <= parent.ActualHeight + 0.5,
            $"{element.GetType().Name} ({element.Name}) at {origin}, {element.ActualWidth}×{element.ActualHeight} exceeds {parent.Name}, {parent.ActualWidth}×{parent.ActualHeight}.");
    }

    private static IEnumerable<T> Tree<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        foreach (DependencyObject child in Descendants(root))
        {
            if (child is T item) yield return item;
        }
    }

    private static void SaveEvidence(FrameworkElement editor, string name)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "MyCapture.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        string directory = Path.Combine(root.FullName, "artifacts", "editor-ui");
        Directory.CreateDirectory(directory);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(editor.ActualWidth), (int)Math.Ceiling(editor.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(editor);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream output = File.Create(Path.Combine(directory, name));
        encoder.Save(output);
        Assert.True(output.Length > 0);
    }

    private sealed class PreferencesScope : IDisposable
    {
        private readonly Func<AnnotationDefaults>? _read = AnnotationEditorPreferences.Read;
        private readonly Action<AnnotationDefaults>? _write = AnnotationEditorPreferences.Write;
        private readonly Func<bool>? _sourceTitle = AnnotationEditorPreferences.ReadShowSourceWindowTitle;
        private string _json;

        internal PreferencesScope(AnnotationDefaults defaults)
        {
            _json = JsonSerializer.Serialize(defaults);
            AnnotationEditorPreferences.Read = Read;
            AnnotationEditorPreferences.Write = value => _json = JsonSerializer.Serialize(value);
            AnnotationEditorPreferences.ReadShowSourceWindowTitle = () => false;
        }

        internal AnnotationDefaults Read() => JsonSerializer.Deserialize<AnnotationDefaults>(_json)!;

        public void Dispose()
        {
            AnnotationEditorPreferences.Read = _read;
            AnnotationEditorPreferences.Write = _write;
            AnnotationEditorPreferences.ReadShowSourceWindowTitle = _sourceTitle;
        }
    }

    private sealed class EditorHost : IDisposable
    {
        private readonly bool? _animations;
        private EditorHost(Window window, AnnotationEditorControl editor, bool? animations)
        {
            Window = window;
            Editor = editor;
            _animations = animations;
        }
        internal Window Window { get; }
        internal AnnotationEditorControl Editor { get; }

        internal static EditorHost Create(int width = 1000, int height = 640, AnnotationDocument? document = null)
        {
            bool? animations = FluidMotion.AnimationsEnabledOverrideForTest;
            FluidMotion.AnimationsEnabledOverrideForTest = false;
            BitmapSource bitmap = Solid(640, 360, 0x90);
            var region = new RectD(0, 0, 640, 360);
            var editor = new AnnotationEditorControl(new FrozenFrame(bitmap, region, null, 0), region, bitmap, document, null);
            ResourceDictionary resources = SystemHighContrastThemeTests.LoadTheme();
            ThemeService.ApplyResources(AppTheme.Midnight, resources, systemHighContrast: false);
            var window = new Window
            {
                Content = editor,
                Resources = resources,
                Width = width,
                Height = height,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                Left = -4000,
                Top = -4000,
                ShowActivated = false,
                ShowInTaskbar = false,
            };
            window.Show();
            window.UpdateLayout();
            return new EditorHost(window, editor, animations);
        }

        public void Dispose()
        {
            Window.Close();
            FluidMotion.AnimationsEnabledOverrideForTest = _animations;
        }
    }
}
