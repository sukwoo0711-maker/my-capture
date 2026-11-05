using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Ocr;
using MyCapture.App.Gallery;
using MyCapture.App.Themes;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using Xunit;

namespace MyCapture.App.Tests;

public sealed partial class SettingsThemePreviewTests
{
    [Fact]
    public Task LibraryProjection_AppCreatesEagerWindowAndReusesItBeforeShow() =>
        RunIsolated(nameof(LibraryProjection_AppCreatesEagerWindowAndReusesItBeforeShow), () =>
        {
            Application application = CreateLibraryMotionApplication();
            using ProbeLibraryServices services = ProjectionServices();
            using ServiceProvider logging = new ServiceCollection()
                .AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance)
                .AddSingleton<ILogger<GalleryWindow>>(NullLogger<GalleryWindow>.Instance)
                .BuildServiceProvider();
            // Exercise the actual App factory without starting the resident shell or user stores,
            // as CaptureLibraryFirstTests isolates its real App completion handlers.
            var shell = (App)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(App));
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic;
            void Set(string name, object value)
            {
                var field = typeof(App).GetField(name, flags);
                Assert.NotNull(field);
                field.SetValue(shell, value);
            }
            Set("_services", logging);
            Set("_queue", services.Queue);
            Set("_capturePaths", services.Paths);
            Set("_galleryController", services.Controller);
            Set("_reeditLoader", services.Reedit);
            Set("_commit", services.Commit);
            Set("_videoLibrary", services.Videos);
            Set("_ocrPresenter", services.Presenter);
            Set("_settings", services.Settings);
            Set("_automaticIndexer", services.Indexing);
            Set("_privacyRedactionService", new EagerProjectionNoPrivacy());
            var notices = typeof(App).GetField("_engagementNotices", flags);
            Assert.NotNull(notices);
            notices.SetValue(shell, Activator.CreateInstance(notices.FieldType));
            var ensure = typeof(App).GetMethod("EnsureGalleryWindow", flags);
            Assert.NotNull(ensure);
            GalleryWindow? window = null;
            GalleryViewModel? model = null;
            try
            {
                DateTimeOffset now = DateTimeOffset.Now;
                CaptureRecord first = AddLibraryFolderCapture(services.Queue, now.AddMinutes(-1), "eager first", Colors.Blue);
                window = Assert.IsType<GalleryWindow>(ensure.Invoke(shell, null));
                model = Assert.IsType<GalleryViewModel>(window.DataContext);
                PrepareProjectionWindow(window);
                Assert.False(window.IsVisible);
                Assert.False(model.NeedsInitialRefresh);
                Assert.Equal(first.Id, Assert.Single(model.Groups.SelectMany(group => group.Items)).Id);
                Assert.NotEmpty(model.Rows);
                model.Select(first.Id);
                Assert.Equal(first.Id, Assert.IsType<GalleryItemViewModel>(model.SingleSelectedTile).Id);
                Assert.Same(window, ensure.Invoke(shell, null));

                // Show remains authoritative when storage changes after eager construction.
                CaptureRecord latest = AddLibraryFolderCapture(services.Queue, now, "eager newest", Colors.Green);
                Assert.DoesNotContain(model.Groups.SelectMany(group => group.Items), tile => tile.Id == latest.Id);
                window.ShowGallery();
                SettleGalleryLayout(window);
                Assert.Equal(new[] { latest.Id, first.Id }, model.Groups.SelectMany(group => group.Items).Select(tile => tile.Id));
                Assert.Equal(first.Id, Assert.IsType<GalleryItemViewModel>(model.SingleSelectedTile).Id);
                Assert.Same(window, ensure.Invoke(shell, null));
            }
            finally
            {
                try
                {
                    if (window is not null)
                    {
                        GalleryItemViewModel[] tiles = services.Queue.Records.Select(record => model?.FindTile(record.Id))
                            .OfType<GalleryItemViewModel>().ToArray();
                        window.CloseForExit();
                        PumpLibraryUntil(window, () => tiles.All(tile => tile.PendingThumbnailLoad.IsCompleted),
                            "owned App gallery thumbnail tasks finish");
                        foreach (GalleryItemViewModel tile in tiles) tile.PendingThumbnailLoad.GetAwaiter().GetResult();
                    }
                }
                finally { services.Presenter.Dispose(); application.Shutdown(); }
                // Preserve the synthetic fixture; no filesystem cleanup.
            }
        });

    private sealed class EagerProjectionNoPrivacy : IPrivacyRedactionService
    {
        public bool IsAvailable => false;
        public Task<PrivacyRedactionResult> FindAsync(System.Windows.Media.Imaging.BitmapSource bitmap,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The projection regression never searches private text.");
    }

    [Fact]
    public void LibraryProjection_PublicConstructorStillSupportsImmediateSelection()
    {
        var queue = ProjectionQueue();
        var record = new CaptureRecord { Title = "synthetic eager selection" };
        queue.Add(record);
        var model = new GalleryViewModel(new GalleryController(queue, NullLogger<GalleryController>.Instance),
            _ => throw new InvalidOperationException("Projection must not read a thumbnail."), 96);
        Assert.False(model.NeedsInitialRefresh);
        Assert.Equal(record.Id, Assert.Single(model.Groups.SelectMany(group => group.Items)).Id);
        Assert.NotEmpty(model.Rows);
        model.Select(record.Id);
        Assert.Equal(record.Id, Assert.IsType<GalleryItemViewModel>(model.SingleSelectedTile).Id);
    }

    [Fact]
    public void LibraryProjection_FailedInitialRefreshCanRetryWithoutPublishingCompletion()
    {
        var queue = ProjectionQueue();
        var record = new CaptureRecord { Title = "synthetic retry" };
        queue.Add(record);
        bool fail = true;
        var expected = new InvalidOperationException("Synthetic projection clock failure.");
        var model = GalleryViewModel.CreateForWindow(new GalleryController(queue, NullLogger<GalleryController>.Instance),
            _ => throw new InvalidOperationException("Projection must not read a thumbnail."), 96,
            clock: () => fail ? throw expected : DateTimeOffset.Now);
        Assert.True(model.NeedsInitialRefresh);
        Assert.Empty(model.Groups);
        Assert.Same(expected, Assert.Throws<InvalidOperationException>(model.Refresh));
        Assert.True(model.NeedsInitialRefresh);
        fail = false;
        model.Refresh();
        Assert.False(model.NeedsInitialRefresh);
        Assert.Equal(record.Id, Assert.Single(model.Groups.SelectMany(group => group.Items)).Id);
        model.Select(record.Id);
        Assert.Equal(record.Id, Assert.IsType<GalleryItemViewModel>(model.SingleSelectedTile).Id);
    }

    [Fact]
    public Task LibraryProjection_ShowGalleryUsesLatestDataAndRetainsHiddenSelection() =>
        VerifyInitialProjectionWindow(nameof(LibraryProjection_ShowGalleryUsesLatestDataAndRetainsHiddenSelection), directShow: false);

    [Fact]
    public Task LibraryProjection_DirectShowUsesLatestDataAndRetainsHiddenSelection() =>
        VerifyInitialProjectionWindow(nameof(LibraryProjection_DirectShowUsesLatestDataAndRetainsHiddenSelection), directShow: true);

    private Task VerifyInitialProjectionWindow(string name, bool directShow) => RunIsolated(name, () =>
    {
        Application application = CreateLibraryMotionApplication();
        using ProbeLibraryServices services = ProjectionServices();
        try
        {
            DateTimeOffset now = DateTimeOffset.Now;
            CaptureRecord filed = AddLibraryFolderCapture(services.Queue, now.AddMinutes(-1), "before constructor", Colors.Blue);
            services.EnsureWindow(deferInitialProjection: true);
            GalleryWindow window = services.Window!;
            GalleryViewModel model = services.ViewModel!;
            PrepareProjectionWindow(window);
            Assert.True(model.NeedsInitialRefresh);
            Assert.Empty(model.Groups);
            Assert.Empty(model.Rows);

            // Actual queue changes occur after construction, before either supported first-show route.
            filed.Title = "filed latest title";
            filed.OcrText = "synthetic latest OCR";
            CaptureFolder folder = services.Queue.CreateFolderAsync("synthetic new folder").GetAwaiter().GetResult();
            services.Queue.MoveCapturesAsync([filed.Id], folder.Id).GetAwaiter().GetResult();
            CaptureRecord loose = AddLibraryFolderCapture(services.Queue, now, "keep latest", Colors.Green);
            if (directShow) window.Show(); else window.ShowGallery();
            SettleGalleryLayout(window);
            Assert.False(model.NeedsInitialRefresh);
            var eager = new GalleryViewModel(services.Controller, _ => "unused-synthetic-thumbnail", 96);
            Assert.Equal(eager.Groups.SelectMany(group => group.Items).Select(tile => tile.Id),
                model.Groups.SelectMany(group => group.Items).Select(tile => tile.Id));
            Assert.Equal(eager.FolderRoots.Select(node => (node.FolderId, node.Name, node.Count)),
                model.FolderRoots.Select(node => (node.FolderId, node.Name, node.Count)));
            Assert.Equal(eager.ReachableCount, model.ReachableCount);
            Assert.Equal(eager.SummaryText, model.SummaryText);
            Assert.Equal(loose.Id, Assert.Single(model.Groups.SelectMany(group => group.Items)).Id);
            Assert.Contains(model.FolderRoots, node => node.FolderId == folder.Id && node.Count == 1);

            model.Filter = GalleryFilter.Images;
            model.SearchQuery = "keep";
            model.Select(loose.Id);
            SettleGalleryLayout(window);
            var details = Assert.IsType<Border>(window.FindName("GalleryDetailsHost")).Child;
            Assert.NotNull(details);
            Assert.Equal(1, window.DetailsContentCreationCount);
            window.Hide();
            CaptureRecord later = AddLibraryFolderCapture(services.Queue, now.AddSeconds(1), "keep hidden arrival", Colors.Red);
            window.RefreshFromQueue(later.Id);
            Assert.DoesNotContain(model.Groups.SelectMany(group => group.Items), tile => tile.Id == later.Id);
            if (directShow) window.Show(); else window.ShowGallery();
            SettleGalleryLayout(window);
            Assert.Equal(GalleryFilter.Images, model.Filter);
            Assert.Equal("keep", model.SearchQuery);
            Assert.Equal(new[] { later.Id, loose.Id }, model.Groups.SelectMany(group => group.Items).Select(tile => tile.Id));
            Assert.Equal(loose.Id, Assert.IsType<GalleryItemViewModel>(model.SingleSelectedTile).Id);
            Assert.Same(details, Assert.IsType<Border>(window.FindName("GalleryDetailsHost")).Child);
            Assert.Equal(1, window.DetailsContentCreationCount);
        }
        finally { CloseProjectionFixture(services, application); }
    });

    [Fact]
    public Task LibraryProjection_EmptyDirectShowThenHiddenArrivalRendersCurrentQueue() =>
        RunIsolated(nameof(LibraryProjection_EmptyDirectShowThenHiddenArrivalRendersCurrentQueue), () =>
        {
            Application application = CreateLibraryMotionApplication();
            using ProbeLibraryServices services = ProjectionServices();
            try
            {
                services.EnsureWindow(deferInitialProjection: true);
                GalleryWindow window = services.Window!;
                GalleryViewModel model = services.ViewModel!;
                PrepareProjectionWindow(window);
                Assert.True(model.NeedsInitialRefresh);
                window.Show();
                SettleGalleryLayout(window);
                Assert.False(model.NeedsInitialRefresh);
                Assert.True(model.QueueIsEmpty);
                Assert.True(model.IsEmpty);
                Assert.Empty(model.Rows);
                Assert.Null(Assert.IsType<Border>(window.FindName("GalleryDetailsHost")).Child);
                window.Hide();
                CaptureRecord record = AddLibraryFolderCapture(services.Queue, DateTimeOffset.Now, "synthetic first arrival", Colors.Blue);
                window.RefreshFromQueue(record.Id);
                window.ShowGallery();
                SettleGalleryLayout(window);
                Assert.False(model.IsEmpty);
                Assert.False(model.QueueIsEmpty);
                Assert.Equal(record.Id, Assert.Single(model.Groups.SelectMany(group => group.Items)).Id);
                Assert.Equal(0, window.DetailsContentCreationCount);
            }
            finally { CloseProjectionFixture(services, application); }
        });

    private static CaptureQueue ProjectionQueue() => new(ProjectionPaths(), new QueueSettings(), NullLogger<CaptureQueue>.Instance);

    private static ProbeLibraryServices ProjectionServices()
    {
        AppPaths paths = ProjectionPaths();
        paths.EnsureCreated();
        var settings = new AppSettings();
        settings.Queue.ImageRetentionHours = CaptureRetention.NeverHours;
        return ProbeLibraryServices.Initialize(paths, settings);
    }

    private static AppPaths ProjectionPaths()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "MyCapture.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        string path = Path.Combine(root.FullName, "artifacts", "library-initial-projection-tests", Guid.NewGuid().ToString("N"));
        Assert.False(Directory.Exists(path));
        return AppPaths.CreateForRoot(path);
    }

    private static void PrepareProjectionWindow(GalleryWindow window)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.Left = -10000;
        window.Top = -10000;
        FluidMotion.SetWindowEntrance(window, false);
    }

    private static void CloseProjectionFixture(ProbeLibraryServices services, Application application)
    {
        try
        {
            if (services.Window is { } window)
            {
                GalleryItemViewModel[] tiles = services.Queue.Records.Select(record => services.ViewModel?.FindTile(record.Id))
                    .OfType<GalleryItemViewModel>().ToArray();
                window.CloseForExit();
                PumpLibraryUntil(window, () => tiles.All(tile => tile.PendingThumbnailLoad.IsCompleted), "owned thumbnail tasks finish");
                foreach (GalleryItemViewModel tile in tiles) tile.PendingThumbnailLoad.GetAwaiter().GetResult();
            }
        }
        finally { services.Presenter.Dispose(); application.Shutdown(); }
        // Preserve every synthetic fixture; this test performs no filesystem cleanup.
    }
}
