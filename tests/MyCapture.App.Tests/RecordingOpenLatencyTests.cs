using System.Diagnostics;
using System.IO;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Recording;
using MyCapture.Core.Queue;
using MyCapture.Core.Recording;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Recording;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>Stop-to-editor diagnostics: stage wiring in the library and a durations-only log line.</summary>
public sealed class RecordingOpenLatencyTests
{
    [Fact]
    public async Task CompleteCapture_ChargesValidationThumbnailAndPublicationToTheirStages()
    {
        string root = OwnedTestDirectory.Create("mc-open-latency-");
        try
        {
            AppPaths paths = AppPaths.CreateForRoot(root);
            var queue = new CaptureQueue(paths, new QueueSettings(), NullLogger<CaptureQueue>.Instance);
            queue.Load();
            var library = new VideoLibraryService(queue, paths, NullLogger<VideoLibraryService>.Instance);
            using VideoCaptureWriteSession capture = library.BeginCapture(10);
            RecordingResult result = SyntheticDocumentClip.Encode(capture.StagingOutputPath, 160, 90, 10, 10);
            var latency = new RecordingOpenLatency(Stopwatch.GetTimestamp());

            _ = await library.CompleteCaptureAsync(capture, result, openLatency: latency);

            Assert.True(latency[RecordingOpenStage.Validate] > 0);
            Assert.True(latency[RecordingOpenStage.Thumbnail] > 0);
            Assert.True(latency[RecordingOpenStage.Publish] > 0);
            Assert.Equal(0, latency[RecordingOpenStage.Finalize]);
            Assert.Equal(0, latency[RecordingOpenStage.EditorOpen]);
            double stages = Enum.GetValues<RecordingOpenStage>().Sum(stage => latency[stage]);
            Assert.Equal(latency.TotalMs, stages, 6);
        }
        finally
        {
            OwnedTestDirectory.Delete(root);
        }
    }

    [Fact]
    public void OpenLatencyLog_CarriesOnlyDurations() => StaTestHost.Run(() =>
    {
        string root = OwnedTestDirectory.Create("mc-open-latency-log-");
        try
        {
            var loggers = new CapturingLoggerFactory();
            AppPaths paths = AppPaths.CreateForRoot(root);
            var queue = new CaptureQueue(paths, new QueueSettings(), NullLogger<CaptureQueue>.Instance);
            var library = new VideoLibraryService(queue, paths, NullLogger<VideoLibraryService>.Instance);
            var coordinator = new RegionRecordingCoordinator(
                new ScreenCaptureEngine(NullLogger<ScreenCaptureEngine>.Instance),
                paths,
                () => new RecordingSettings(),
                loggers,
                library);
            string privatePath = Path.Combine(root, "고객 회의 비밀", "source.mp4");
            var result = new RecordingResult(privatePath, 1000, 30, 30, 1920, 1080)
            {
                Performance = new RecordingPerformance(10, 20, 1, 30, 2, 6),
            };
            long now = 0;
            var latency = new RecordingOpenLatency(0, () => now, 1_000);
            foreach (RecordingOpenStage stage in new[]
                     {
                         RecordingOpenStage.Finalize, RecordingOpenStage.Validate, RecordingOpenStage.Publish,
                         RecordingOpenStage.Thumbnail, RecordingOpenStage.EditorOpen,
                     })
            {
                now += 100;
                latency.Mark(stage);
            }

            typeof(RegionRecordingCoordinator)
                .GetMethod("LogOpenLatency", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(coordinator, [latency, result]);

            (string message, IReadOnlyList<KeyValuePair<string, object?>> values) = Assert.Single(loggers.Entries);
            Assert.Contains("stop to editor 500ms", message, StringComparison.Ordinal);
            Assert.Contains("MP4 complete 6ms", message, StringComparison.Ordinal);
            Assert.DoesNotContain(root, message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("비밀", message, StringComparison.Ordinal);
            Assert.All(
                values.Where(pair => pair.Key != "{OriginalFormat}"),
                pair => Assert.IsType<double>(pair.Value));
        }
        finally
        {
            OwnedTestDirectory.Delete(root);
        }
    });

    private sealed class CapturingLoggerFactory : ILoggerFactory, ILogger
    {
        internal List<(string Message, IReadOnlyList<KeyValuePair<string, object?>> Values)> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => this;

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            IReadOnlyList<KeyValuePair<string, object?>> values =
                state as IReadOnlyList<KeyValuePair<string, object?>> ?? [];
            Entries.Add((formatter(state, exception), values));
        }
    }
}
