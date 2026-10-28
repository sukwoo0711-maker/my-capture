using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using MyCapture.App.Editing;
using MyCapture.Core.Annotations;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using MyCapture.Core.Undo;
using Xunit;
using static MyCapture.App.Tests.EditorKeysTests;

namespace MyCapture.App.Tests;

public sealed class ArrowHeadChoiceTests
{
    [Theory]
    [InlineData(ArrowHeadStyle.End, false, true)]
    [InlineData(ArrowHeadStyle.Both, true, true)]
    [InlineData(ArrowHeadStyle.None, false, false)]
    public void ChoiceMapsToNewArrowAndSelectionWithSingleUndo(ArrowHeadStyle style, bool start, bool end)
    {
        var document = AnnotationDocument.CreateFor(200, 100);
        var undo = new UndoStack();
        var controller = new AnnotationEditorController(document, undo) { Tool = EditorTool.Arrow };
        controller.ApplyArrowHeads(style);
        controller.PointerDown(new PointD(10, 10));
        controller.PointerUp(new PointD(70, 30));
        var first = Assert.IsType<PolylineAnnotation>(Assert.Single(document.Items));
        Assert.Equal(start, first.HeadAtStart);
        Assert.Equal(end, first.HeadAtEnd);
        var second = PolylineAnnotation.CreateArrow(new PointD(10, 50), new PointD(70, 70), ColorRgba.Black, 3);
        document.Add(second);
        controller.SelectAll();
        int before = undo.UndoCount;
        controller.ApplyArrowHeads(ArrowHeadStyle.Both);
        Assert.Equal(before + 1, undo.UndoCount);
        Assert.All(document.Items.Cast<PolylineAnnotation>(), line => { Assert.True(line.HeadAtStart); Assert.True(line.HeadAtEnd); });
        Assert.True(controller.PerformUndo());
        Assert.Equal(start, first.HeadAtStart);
        Assert.Equal(end, first.HeadAtEnd);
        Assert.False(second.HeadAtStart);
        Assert.True(second.HeadAtEnd);
    }

    [Theory]
    [InlineData("ko-KR", 680)]
    [InlineData("en-US", 680)]
    [InlineData("ko-KR", 1000)]
    [InlineData("en-US", 1000)]
    public void ChoicePersistsAndPropertyGroupsFitAfterWrapping(string language, int width) => StaTestHost.Run(() =>
    {
        using var culture = UiText.UseLanguage(language);
        Func<AnnotationDefaults>? previousRead = AnnotationEditorPreferences.Read;
        Action<AnnotationDefaults>? previousWrite = AnnotationEditorPreferences.Write;
        var settings = new AppSettings();
        try
        {
            AnnotationEditorPreferences.Read = () => settings.Annotation;
            AnnotationEditorPreferences.Write = value => settings.Annotation = value;
            using (var host = EditorWindowHost.Create())
            {
                host.Window.Resources = SystemHighContrastThemeTests.LoadTheme();
                host.Window.Width = width;
                Assert.True(host.Editor.HandleShortcut(System.Windows.Input.Key.A, System.Windows.Input.ModifierKeys.None));
                host.Window.UpdateLayout();
                var button = Descendants(host.Editor).OfType<ToggleButton>().Single(b => AutomationProperties.GetAutomationId(b) == "AnnotationArrowHeadNone");
                button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.True(button.IsChecked);
                Assert.Equal(ArrowHeadStyle.None, settings.Annotation.ArrowHeads);
                var group = Descendants(host.Editor).OfType<StackPanel>().Single(p => p.Name == "AnnotationArrowHeadProperties");
                var bar = Descendants(host.Editor).OfType<PropertyGroupsPanel>().Single();
                Assert.True(group.ActualWidth <= bar.ActualWidth);
                Assert.True(group.TranslatePoint(default, bar).X + group.ActualWidth <= bar.ActualWidth + 1);
                Assert.Equal(3, group.Children.OfType<StackPanel>().Single().Children.Count);
            }
            settings = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(new SettingsDraft(settings.DeepClone()).ToAppSettings()))!;
            Assert.Equal(ArrowHeadStyle.None, settings.Annotation.ArrowHeads);
            using var reopened = EditorWindowHost.Create();
            var controller = Controller(reopened.Editor);
            controller.Tool = EditorTool.Arrow;
            controller.PointerDown(new PointD(10, 10));
            controller.PointerUp(new PointD(80, 10));
            var line = Assert.IsType<PolylineAnnotation>(Assert.Single(controller.Document.Items));
            Assert.False(line.HeadAtStart);
            Assert.False(line.HeadAtEnd);
            DraftSnapshot? draft = reopened.Editor.CaptureDraftAsync().GetAwaiter().GetResult();
            Assert.NotNull(draft);
            var draftLine = Assert.IsType<PolylineAnnotation>(Assert.Single(AnnotationDocument.TryFromJson(draft.Document)!.Items));
            Assert.False(draftLine.HeadAtStart);
            Assert.False(draftLine.HeadAtEnd);
            reopened.Window.ConfirmClose = () => EditorCloseChoice.DontSave;
        }
        finally
        {
            AnnotationEditorPreferences.Read = previousRead;
            AnnotationEditorPreferences.Write = previousWrite;
        }
    });

    [Fact]
    public void HeadlessLineRetainsBoundsHitTestingSerializationAndRenderedPixels() => StaTestHost.Run(() =>
    {
        var document = AnnotationDocument.CreateFor(100, 70);
        var line = PolylineAnnotation.CreateLine(new PointD(15, 35), new PointD(85, 35), ColorRgba.Black, 4);
        document.Add(line);
        Assert.Equal(new RectD(15, 35, 70, 0), line.Bounds);
        Assert.Equal(0, line.DistanceTo(new PointD(40, 35)));
        Assert.True(line.DistanceTo(new PointD(40, 60)) > 10);
        var restored = JsonSerializer.Deserialize<AnnotationDocument>(JsonSerializer.Serialize(CaptureCommitService.CreatePersistenceSnapshot(document)))!;
        var recovered = Assert.IsType<PolylineAnnotation>(Assert.Single(restored.Items));
        Assert.False(recovered.HeadAtStart);
        Assert.False(recovered.HeadAtEnd);
        Assert.Equal(line.Id, recovered.Id);
        var bitmap = CaptureCommitService.Flatten(Solid(100, 70, 0xFF), restored, new Dictionary<string, System.Windows.Media.Imaging.BitmapSource>());
        byte[] pixels = new byte[100 * 70 * 4];
        bitmap.CopyPixels(pixels, 400, 0);
        Assert.Equal(0, pixels[(35 * 100 + 45) * 4]);
        Assert.Equal(255, pixels[(28 * 100 + 76) * 4]);
        recovered.HeadAtEnd = true;
        var arrow = CaptureCommitService.Flatten(Solid(100, 70, 0xFF), restored, new Dictionary<string, System.Windows.Media.Imaging.BitmapSource>());
        byte[] arrowPixels = new byte[pixels.Length];
        arrow.CopyPixels(arrowPixels, 400, 0);
        Assert.False(pixels.SequenceEqual(arrowPixels));
    });
}
