using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Gallery;
using MyCapture.Core.Localization;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Platform.Shell;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class OpenWithMenuTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public Task RecommendationsKeepShellOrderNamesIconsAndLastChooseCommand(int count) => OpenWithTestSupport.RunStaAsync(async () =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        var source = new OpenWithTestSource(count);
        var parent = new ContextMenu();
        var item = new MenuItem();
        parent.Items.Add(item);
        using var menu = new OpenWithMenu(item, parent, () => source, () => ".png", _ => Task.CompletedTask, () => IntPtr.Zero);
        Assert.Equal(0, source.Queries);
        parent.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
        Assert.Equal(0, source.Queries);
        await menu.LoadAsync();
        Assert.Equal(1, source.Queries);
        Assert.Equal(".png", source.Extensions.Single());
        MenuItem[] commands = item.Items.OfType<MenuItem>().ToArray();
        Assert.Equal(source.Set.Handlers.Select(handler => handler.Name).Append(UiText.Get("OpenWith.Choose")),
            commands.Select(OpenWithTestSupport.Label));
        Assert.Equal(count > 0 ? 1 : 0, item.Items.OfType<Separator>().Count());
        Assert.Equal(UiText.Get("OpenWith.Choose"), ((MenuItem)item.Items[^1]).Header);
        for (int index = 0; index < count; index++)
        {
            Assert.Equal(source.Set.Handlers[index].Name, AutomationProperties.GetName(commands[index]));
            Assert.Same(source.Set.Handlers[index].Icon, Assert.IsType<Image>(commands[index].Icon).Source);
        }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task QueryFailureKeepsChooseAvailableWhetherThrownOrFaulted(bool synchronous) => OpenWithTestSupport.RunStaAsync(async () =>
    {
        var source = new OpenWithTestSource(0)
        {
            Query = _ => synchronous ? throw new InvalidOperationException("synthetic query failure")
                : Task.FromException<IOpenWithHandlerSet>(new InvalidOperationException("synthetic query failure")),
        };
        var parent = new ContextMenu(); var item = new MenuItem(); parent.Items.Add(item);
        using var menu = new OpenWithMenu(item, parent, () => source, () => ".png", _ => Task.CompletedTask, () => IntPtr.Zero);
        await menu.LoadAsync();
        Assert.Equal(UiText.Get("OpenWith.Choose"), OpenWithTestSupport.Label(Assert.IsType<MenuItem>(Assert.Single(item.Items))));
        Assert.Equal(1, source.Queries);
    });

    [Fact]
    public Task ProgramNameUnderscoreIsDisplayedLiterallyAndAnnouncedUnchanged() => OpenWithTestSupport.RunStaAsync(async () =>
    {
        var source = new OpenWithTestSource(0) { Set = new OpenWithTestSet([new OpenWithTestHandler("Photo_Editor", null)]) };
        var parent = new ContextMenu(); var item = new MenuItem(); parent.Items.Add(item);
        using var menu = new OpenWithMenu(item, parent, () => source, () => ".png", _ => Task.CompletedTask, () => IntPtr.Zero);
        await menu.LoadAsync();
        var command = (MenuItem)item.Items[0];
        Assert.Equal("Photo_Editor", OpenWithTestSupport.Label(command));
        Assert.Equal("Photo_Editor", AutomationProperties.GetName(command));
        Assert.Null(command.Icon);
        // The shared template recognizes access keys for ordinary strings; a TextBlock preserves shell names.
        Assert.Equal("Photo_Editor", Assert.IsType<TextBlock>(command.Header).Text);
    });

    [Fact]
    public Task SubmenuOpenedShowsLoadingThenResultsAndClosedOpeningQueriesAgain() => OpenWithTestSupport.RunStaAsync(async () =>
    {
        var pending = new TaskCompletionSource<IOpenWithHandlerSet>();
        var source = new OpenWithTestSource(1) { Query = _ => pending.Task };
        var parent = new ContextMenu();
        var item = new MenuItem(); parent.Items.Add(item);
        using var menu = new OpenWithMenu(item, parent, () => source, () => ".mp4", _ => Task.CompletedTask, () => IntPtr.Zero);
        parent.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
        Assert.Equal(0, source.Queries);
        item.RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuOpenedEvent, item));
        Assert.Equal(1, source.Queries);
        MenuItem loading = Assert.IsType<MenuItem>(Assert.Single(item.Items));
        Assert.False(loading.IsEnabled);
        Assert.Equal(UiText.Get("OpenWith.Loading"), loading.Header);
        pending.SetResult(source.Set);
        await OpenWithTestSupport.WaitUntilAsync(() => item.Items.Count == 3);
        item.RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuClosedEvent, item));
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.True(source.Set.Disposed);
        source.Set = new OpenWithTestSet([new OpenWithTestHandler("Newly installed sample", null)]);
        source.Query = null;
        item.RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuOpenedEvent, item));
        Assert.Equal(2, source.Queries);
        Assert.Equal("Newly installed sample", OpenWithTestSupport.Label((MenuItem)item.Items[0]));
    });

    [Fact]
    public Task DeadlineShowsOnlyChooseAndDisposesLateResults() => OpenWithTestSupport.RunStaAsync(async () =>
    {
        var pending = new TaskCompletionSource<IOpenWithHandlerSet>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new OpenWithTestSource(2) { Query = _ => pending.Task };
        var parent = new ContextMenu(); var item = new MenuItem(); parent.Items.Add(item);
        using var menu = new OpenWithMenu(item, parent, () => source, () => ".png", _ => Task.CompletedTask, () => IntPtr.Zero,
            budget: TimeSpan.FromMilliseconds(30));
        Task loading = menu.LoadAsync();
        Assert.False(Assert.IsType<MenuItem>(Assert.Single(item.Items)).IsEnabled);
        await loading.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(UiText.Get("OpenWith.Choose"), Assert.IsType<MenuItem>(Assert.Single(item.Items)).Header);
        Assert.True(source.LastCancellation.IsCancellationRequested);
        pending.SetResult(source.Set);
        await OpenWithTestSupport.WaitUntilAsync(() => source.Set.Disposed);
        Assert.Equal(UiText.Get("OpenWith.Choose"), Assert.IsType<MenuItem>(Assert.Single(item.Items)).Header);
    });

    [Fact]
    public Task ClosingDuringQueryCannotPublishOldHandlersOnNextOpening() => OpenWithTestSupport.RunStaAsync(async () =>
    {
        var old = new OpenWithTestSet([new OpenWithTestHandler("Old result", null)]);
        var pending = new TaskCompletionSource<IOpenWithHandlerSet>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new OpenWithTestSource(0) { Query = _ => pending.Task };
        var parent = new ContextMenu(); var item = new MenuItem(); parent.Items.Add(item);
        using var menu = new OpenWithMenu(item, parent, () => source, () => ".png", _ => Task.CompletedTask, () => IntPtr.Zero);
        Task first = menu.LoadAsync();
        menu.Reset();
        source.Query = null;
        await menu.LoadAsync();
        pending.SetResult(old);
        await first;
        await OpenWithTestSupport.WaitUntilAsync(() => old.Disposed);
        Assert.Equal(2, source.Queries);
        Assert.Equal(UiText.Get("OpenWith.Choose"), Assert.IsType<MenuItem>(Assert.Single(item.Items)).Header);
    });

    [Fact]
    public Task CloseBeforeClickKeepsHandlerUntilExportAndInvocationFinish() => OpenWithTestSupport.RunStaAsync(async () =>
    {
        var source = new OpenWithTestSource(1);
        var export = new TaskCompletionSource<string>();
        var parent = new ContextMenu(); var item = new MenuItem(); parent.Items.Add(item);
        using var menu = new OpenWithMenu(item, parent, () => source, () => ".png",
            async invoke => await invoke(await export.Task), () => IntPtr.Zero);
        await menu.LoadAsync();
        var selected = (MenuItem)item.Items[0];
        // The order used by WPF for a leaf activation: popup closure precedes dispatched Click.
        parent.RaiseEvent(new RoutedEventArgs(ContextMenu.ClosedEvent));
        selected.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.False(source.Set.Disposed);
        Assert.Empty(source.InvokedPaths);
        export.SetResult("synthetic-export.png");
        await OpenWithTestSupport.WaitUntilAsync(() => source.Set.Disposed);
        Assert.Equal(["synthetic-export.png"], source.InvokedPaths);
    });

    [Fact]
    public Task ChooseUsesThePreparedCopyExactlyOnce() => OpenWithTestSupport.RunStaAsync(async () =>
    {
        var source = new OpenWithTestSource(0);
        var parent = new ContextMenu(); var item = new MenuItem(); parent.Items.Add(item);
        using var menu = new OpenWithMenu(item, parent, () => source, () => ".png",
            invoke => invoke("synthetic-export.png"), () => new IntPtr(42));
        await menu.LoadAsync();
        Assert.IsType<MenuItem>(Assert.Single(item.Items)).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Equal(["synthetic-export.png"], source.ChosenPaths);
        Assert.Equal(new IntPtr(42), source.ChooseOwner);
    });

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, false, false)]
    [InlineData(true, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(true, false, true, false)]
    [InlineData(false, false, false, true)]
    [InlineData(true, false, false, true)]
    public async Task ProgramEditsOnlyExportedCopyAndPreservesStoreBytesAndTimestamp(bool video, bool renderedVideo, bool titled, bool foldered)
    {
        // Retained fixture: root is owned by this test process and stays under the run's TMP.
        string root = OwnedTestDirectory.Create("MyCapture-openwith-copy-");
        var paths = AppPaths.CreateForRoot(Path.Combine(root, "store"));
        var queue = new CaptureQueue(paths, new QueueSettings(), NullLogger<CaptureQueue>.Instance);
        var record = new CaptureRecord { Id = Guid.NewGuid(), MediaKind = video ? CaptureMediaKind.Video : CaptureMediaKind.Image };
        if (titled) record.Title = "Synthetic capture";
        if (foldered) record.FolderId = (await queue.CreateFolderAsync("Synthetic folder")).Id;
        queue.Add(record);
        Directory.CreateDirectory(queue.GetDirectory(record));
        string stored = queue.GetFilePath(record, video ? CaptureFileNames.VideoSource : CaptureFileNames.Rendered);
        byte[] original = [1, 4, 9, 16, 25];
        File.WriteAllBytes(stored, original);
        string selectedSource = stored;
        if (renderedVideo)
        {
            selectedSource = queue.GetFilePath(record, CaptureFileNames.VideoRendered);
            File.WriteAllBytes(selectedSource, [36, 49, 64]);
        }
        var stamps = new Dictionary<string, (byte[] Bytes, DateTime Timestamp)>();
        foreach (string source in new[] { stored, selectedSource }.Distinct())
        {
            File.SetLastWriteTimeUtc(source, new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc));
            stamps.Add(source, (File.ReadAllBytes(source), File.GetLastWriteTimeUtc(source)));
        }
        string staging = Path.Combine(root, "export");
        var exporter = new GalleryDragExportService(queue, staging, () => DateTimeOffset.UnixEpoch);
        string exported = await exporter.PrepareOpenWithAsync(record);
        Assert.StartsWith(Path.GetFullPath(staging) + Path.DirectorySeparatorChar, exported, StringComparison.OrdinalIgnoreCase);
        Assert.False(exported.StartsWith(Path.GetFullPath(Path.Combine(root, "store")) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        if (!titled && !foldered)
            Assert.Equal(video ? "MyCapture_19700101_000000.mp4" : "MyCapture_19700101_000000.png", Path.GetFileName(exported));
        Assert.True(File.Exists(exported));
        Assert.Equal(stamps[selectedSource].Bytes, File.ReadAllBytes(exported));
        var fakeProgram = new OpenWithTestHandler("Synthetic editor", null, path => { File.WriteAllBytes(path, [100, 101]); return Task.CompletedTask; });
        await fakeProgram.InvokeAsync(exported);
        Assert.Equal(new byte[] { 100, 101 }, File.ReadAllBytes(exported));
        foreach ((string path, var before) in stamps)
        {
            Assert.Equal(before.Bytes, File.ReadAllBytes(path));
            Assert.Equal(before.Timestamp, File.GetLastWriteTimeUtc(path));
        }
    }

    [Fact]
    public async Task RealShellEnumerationForPicturesAndVideoReturnsWithoutLaunching()
    {
        var source = new OpenWithService();
        foreach (string extension in new[] { ".png", ".mp4" })
        {
            // The service budget is 700 ms; 100 ms admits continuation scheduling, never a second probe.
            using IOpenWithHandlerSet result = await source.QueryAsync(extension, CancellationToken.None)
                .WaitAsync(OpenWithService.QueryBudget + TimeSpan.FromMilliseconds(100));
            Assert.NotNull(result);
        }
    }
}

internal sealed class OpenWithTestSource : IOpenWithHandlerSource
{
    internal OpenWithTestSource(int count)
    {
        Set = new OpenWithTestSet(Enumerable.Range(0, count).Select(index => (IOpenWithHandler)new OpenWithTestHandler(
            $"Sample app {count - index}", OpenWithTestSupport.Image(), path =>
            { InvokedPaths.Add(path); return Invoke?.Invoke(path) ?? Task.CompletedTask; })).ToArray());
    }
    internal OpenWithTestSet Set { get; set; }
    internal Func<CancellationToken, Task<IOpenWithHandlerSet>>? Query { get; set; }
    internal Func<string, Task>? Invoke { get; set; }
    internal Func<string, Task>? Choose { get; set; }
    internal List<string> InvokedPaths { get; } = [];
    internal List<string> ChosenPaths { get; } = [];
    internal List<string> Extensions { get; } = [];
    internal int Queries { get; private set; }
    internal IntPtr ChooseOwner { get; private set; }
    internal CancellationToken LastCancellation { get; private set; }
    public Task<IOpenWithHandlerSet> QueryAsync(string extension, CancellationToken cancellationToken)
    {
        Queries++; Extensions.Add(extension); LastCancellation = cancellationToken;
        return Query?.Invoke(cancellationToken) ?? Task.FromResult<IOpenWithHandlerSet>(Set);
    }
    public Task ChooseAsync(string path, IntPtr owner)
    { ChosenPaths.Add(path); ChooseOwner = owner; return Choose?.Invoke(path) ?? Task.CompletedTask; }
}

internal sealed class OpenWithTestSet(IReadOnlyList<IOpenWithHandler> handlers) : IOpenWithHandlerSet
{
    public IReadOnlyList<IOpenWithHandler> Handlers => handlers;
    internal bool Disposed { get; private set; }
    public void Dispose() => Disposed = true;
}

internal sealed record OpenWithTestHandler(string Name, ImageSource? Icon, Func<string, Task>? Action = null) : IOpenWithHandler
{
    public Task InvokeAsync(string path) => Action?.Invoke(path) ?? Task.CompletedTask;
}

internal static class OpenWithTestSupport
{
    internal static string Label(MenuItem item) => item.Header is TextBlock text ? text.Text : (string)item.Header;
    internal static BitmapSource Image()
    {
        byte[] pixels = new byte[160 * 100 * 4];
        for (int index = 0; index < pixels.Length; index += 4)
        { pixels[index] = 0x80; pixels[index + 1] = 0x60; pixels[index + 2] = 0x30; pixels[index + 3] = 0xff; }
        var image = BitmapSource.Create(160, 100, 96, 96, PixelFormats.Bgra32, null, pixels, 160 * 4);
        image.Freeze(); return image;
    }

    internal static async Task WaitUntilAsync(Func<bool> ready)
    {
        var clock = Stopwatch.StartNew();
        while (!ready()) { Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3)); await Task.Delay(10); }
    }

    internal static void PumpUntil(Func<bool> ready)
    {
        var clock = Stopwatch.StartNew();
        while (!ready())
        {
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5));
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
    }

    internal static Task RunStaAsync(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = WpfTestThread.Create(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            try
            {
                Task work = action();
                _ = work.ContinueWith(_ => dispatcher.BeginInvokeShutdown(DispatcherPriority.Send),
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                Dispatcher.Run(); work.GetAwaiter().GetResult(); completion.TrySetResult();
            }
            catch (Exception error) { completion.TrySetException(error); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task;
    }
}
