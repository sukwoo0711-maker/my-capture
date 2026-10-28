using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Editing;
using MyCapture.App.Media;
using MyCapture.App.Recording;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using MyCapture.Core.Storage;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Recording;
using Xunit;
using static MyCapture.App.Tests.VideoEditorUxTrimTests;

namespace MyCapture.App.Tests;

public sealed class EditorFileSizeTests
{
    [Fact]
    public void FileLengthReadRunsOffTheCallerAndOmitsUnavailableFacts() => StaTestHost.Run(() =>
    {
        int caller = Environment.CurrentManagedThreadId;
        Assert.Equal(1234, MediaFileSizeReader.ReadAsync("fixture.png", CancellationToken.None, _ =>
        {
            Assert.NotEqual(caller, Environment.CurrentManagedThreadId);
            return 1234;
        }).GetAwaiter().GetResult());
        Assert.Null(MediaFileSizeReader.ReadAsync(null, CancellationToken.None, _ => throw new Exception("No path must not read")).GetAwaiter().GetResult());
        Assert.Null(MediaFileSizeReader.ReadAsync("fixture.png", CancellationToken.None, _ => throw new IOException()).GetAwaiter().GetResult());
        Assert.Null(MediaFileSizeReader.ReadAsync("fixture.png", CancellationToken.None, _ => throw new UnauthorizedAccessException()).GetAwaiter().GetResult());
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => MediaFileSizeReader.ReadAsync("fixture.png", cancelled.Token,
            _ => throw new Exception("Cancelled request must not read")).GetAwaiter().GetResult());
    });

    [Theory]
    [InlineData("ko-KR")]
    [InlineData("en-US")]
    public void ImageSizeLoadsAfterOpeningAndStaysWithCanvasChanges(string language) => StaTestHost.Run(() =>
    {
        using var culture = UiText.UseLanguage(language);
        AnnotationEditorWindow window = CreateImageEditor();
        var pending = new TaskCompletionSource<long?>();
        int reads = 0;
        window.Editor.SourceFileSizeReader = (_, _) => { reads++; return pending.Task; };
        window.Editor.SourceFilePath = "source.png";
        Assert.Equal(0, reads);
        try
        {
            window.Show();
            Assert.Equal(1, reads);
            Assert.False(window.Editor.SourceFileSizeReadTask.IsCompleted);
            Assert.Equal("720 × 300 px", Dimensions(window.Editor));
            pending.SetResult(2_516_582);
            Finish(window.Editor.SourceFileSizeReadTask);
            Assert.Equal("720 × 300 px · 2.4 MB", Dimensions(window.Editor));
            // History and draft restoration both refresh this same dimensions presenter.
            typeof(AnnotationEditorControl).GetMethod("OnHistoryChanged", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window.Editor, null);
            Assert.Equal("720 × 300 px · 2.4 MB", Dimensions(window.Editor));
            Button capture = Descendants(window).OfType<Button>().Single(button => button.Name == "NewCapture");
            Button info = Descendants(window).OfType<Button>().Single(button => button.Name == "MediaInfo");
            Assert.Same(capture.Style, info.Style);
            Assert.Equal(capture.Height, info.Height);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChangingOrUnloadingAnImageCancelsItsReadAndRejectsLateValues(bool closeOwner) => StaTestHost.Run(() =>
    {
        AnnotationEditorWindow window = CreateImageEditor();
        var reads = new List<TaskCompletionSource<long?>>();
        var tokens = new List<CancellationToken>();
        window.Editor.SourceFileSizeReader = (_, token) =>
        {
            tokens.Add(token);
            var read = new TaskCompletionSource<long?>();
            reads.Add(read);
            return read.Task;
        };
        window.Editor.SourceFilePath = "first.png";
        try
        {
            window.Show();
            Task first = window.Editor.SourceFileSizeReadTask;
            window.Editor.SourceFilePath = "second.png";
            Assert.True(tokens[0].IsCancellationRequested);
            reads[1].SetResult(2048);
            Finish(window.Editor.SourceFileSizeReadTask);
            Assert.Equal("720 × 300 px · 2 KB", Dimensions(window.Editor));
            reads[0].SetResult(4_194_304);
            Finish(first);
            Assert.Equal("720 × 300 px · 2 KB", Dimensions(window.Editor));
            window.Editor.SourceFilePath = null;
            Assert.Equal("720 × 300 px", Dimensions(window.Editor));
            Task unloaded = window.Editor.SourceFileSizeReadTask;
            if (closeOwner) window.Close();
            else window.Editor.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            Assert.True(tokens[2].IsCancellationRequested);
            reads[2].SetResult(1024);
            Finish(unloaded);
            Assert.Equal("720 × 300 px", Dimensions(window.Editor));
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData("ko-KR", 25_480_397L, "24.3 MB")]
    [InlineData("en-US", 0L, "0 B")]
    [InlineData("ko-KR", null, null)]
    public void VideoFactsGainSizeAfterOpeningWithoutChangingTheExistingDetailsLocation(string language, long? bytes, string? size) => StaTestHost.Run(() =>
    {
        using var culture = UiText.UseLanguage(language);
        VideoEditorWindow editor = CreateVideoEditor();
        var pending = new TaskCompletionSource<long?>();
        int reads = 0;
        editor.SourceFileSizeReader = (_, _) => { reads++; return pending.Task; };
        Assert.Equal(0, reads);
        string expected = UiText.Format("VideoEditorUx_HeaderDetails", "0:10.000", 640, 360, 30);
        try
        {
            editor.Show();
            Assert.Equal(1, reads);
            Assert.False(editor.SourceFileSizeReadTask.IsCompleted);
            Assert.Equal(expected, Field<TextBlock>(editor, "_identityDetails").Text);
            pending.SetResult(bytes);
            Finish(editor.SourceFileSizeReadTask);
            if (size is not null) expected += " · " + size;
            Assert.Equal(expected, Field<TextBlock>(editor, "_identityDetails").Text);
            Assert.StartsWith(expected + Environment.NewLine, Assert.IsType<string>(Field<Border>(editor, "_positionChip").ToolTip));
            Assert.DoesNotContain(Descendants(editor), child => ReferenceEquals(child, Field<TextBlock>(editor, "_identityDetails")));
        }
        finally { editor.Close(); }
    });

    [Fact]
    public void ClosingVideoCancelsTheSizeReadAndIgnoresLateCompletion() => StaTestHost.Run(() =>
    {
        VideoEditorWindow editor = CreateVideoEditor();
        var pending = new TaskCompletionSource<long?>();
        CancellationToken token = default;
        editor.SourceFileSizeReader = (_, cancellation) => { token = cancellation; return pending.Task; };
        try
        {
            editor.Show();
            string before = Field<TextBlock>(editor, "_identityDetails").Text;
            editor.Close();
            Assert.True(token.IsCancellationRequested);
            pending.SetResult(1_048_576);
            Finish(editor.SourceFileSizeReadTask);
            Assert.Equal(before, Field<TextBlock>(editor, "_identityDetails").Text);
        }
        finally { editor.Close(); }
    });

    private static AnnotationEditorWindow CreateImageEditor()
    {
        var image = EditorKeysTests.Solid(720, 300, 0x30);
        var region = new RectD(0, 0, image.PixelWidth, image.PixelHeight);
        var editor = new AnnotationEditorWindow(new FrozenFrame(image, region, null, 0), region, image)
        {
            Left = -4000, Top = -4000, Width = 1000, Height = 640,
            WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false, ShowInTaskbar = false,
            Resources = SystemHighContrastThemeTests.LoadTheme(),
        };
        return editor;
    }

    private static VideoEditorWindow CreateVideoEditor()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "file-size-in-memory");
        var recording = new RecordingResult(Path.Combine(root, "source.mp4"), 10_000, 30, 300, 640, 360);
        var editor = new VideoEditorWindow(recording, AppPaths.CreateForRoot(root), NullLoggerFactory.Instance)
        {
            Left = -4000, Top = -4000, Width = 920, Height = 680,
            WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false, ShowInTaskbar = false,
        };
        LoadTheme(editor);
        return editor;
    }

    private static string Dimensions(AnnotationEditorControl editor) => Field<TextBlock>(editor, "_dimensionsText").Text;

    private static void Finish(Task task)
    {
        var timeout = Stopwatch.StartNew();
        while (!task.IsCompleted && timeout.Elapsed < TimeSpan.FromSeconds(5))
        {
            Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
            Thread.Sleep(1);
        }
        Assert.True(task.IsCompleted);
        task.GetAwaiter().GetResult();
    }
}
