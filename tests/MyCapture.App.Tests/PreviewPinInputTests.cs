using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.App.Pinning;
using MyCapture.App.Preview;
using MyCapture.Core.Settings;
using MyCapture.Platform.Shell;
using Xunit;

namespace MyCapture.App.Tests;

// These tests use fake clipboard ports and registrations; never the system clipboard or RegisterHotKey.
public sealed class PreviewPinInputTests : KoreanCaptionTest
{
    [Fact]
    public async Task F3RegistrationRoutesOnlyItsOwnMessageAndDisposesOnlyItsLease()
    {
        var registrar = new FakeRegistrar(true);
        int requests = 0;
        var input = new PreviewPinInput(registrar, () => { requests++; return Task.FromResult(PasteResult.Pinned); });
        input.RegisterShortcut();
        Assert.Equal("F3", input.ShortcutText);
        Assert.Empty(input.ShortcutNotice);
        Assert.Equal(PreviewPinInput.Primary, Assert.Single(registrar.Attempts));
        Assert.False(input.HandleMessage(0x1234, PreviewPinInput.HotkeyId));
        Assert.False(input.HandleMessage(PreviewPinInput.WmHotkey, PreviewPinInput.HotkeyId + 1));
        Assert.True(input.HandleMessage(PreviewPinInput.WmHotkey, PreviewPinInput.HotkeyId));
        await input.LastPinTask;
        Assert.Equal(1, requests);
        Assert.Equal("핀 열림", input.StatusText);
        input.Dispose();
        input.Dispose();
        Assert.Equal(PreviewPinInput.HotkeyId, Assert.Single(registrar.Released));
        Assert.False(input.HandleMessage(PreviewPinInput.WmHotkey, PreviewPinInput.HotkeyId));
    }

    [Fact]
    public void F3ConflictUsesFallbackAndRetryCanAcquireF3()
    {
        var registrar = new FakeRegistrar(false, true, true);
        using var input = new PreviewPinInput(registrar, () => Task.FromResult(PasteResult.Pinned));
        input.RegisterShortcut();
        Assert.Equal(new[] { PreviewPinInput.Primary, PreviewPinInput.Fallback }, registrar.Attempts);
        Assert.Equal("Ctrl+Shift+F3", input.ShortcutText);
        Assert.Equal("Store판 MyCapture를 종료하면 F3을 쓸 수 있습니다", input.ShortcutNotice);
        Assert.Equal(1409, input.PrimaryRegistrationError);
        Assert.Equal(0, input.FallbackRegistrationError);
        input.RegisterShortcut();
        Assert.Equal("F3", input.ShortcutText);
        Assert.Empty(input.ShortcutNotice);
        Assert.Single(registrar.Released);
    }

    [Fact]
    public async Task BothConflictsAreExplicitAndManualPinStillWorks()
    {
        var registrar = new FakeRegistrar(false, false);
        int requests = 0;
        using var input = new PreviewPinInput(registrar, () => { requests++; return Task.FromResult(PasteResult.Pinned); });
        input.RegisterShortcut();
        Assert.Equal("없음", input.ShortcutText);
        Assert.Contains(PreviewPinInput.F3UnavailableMessage, input.ShortcutNotice);
        Assert.Contains("Ctrl+Shift+F3도 사용할 수 없습니다", input.ShortcutNotice);
        Assert.Equal(1409, input.FallbackRegistrationError);
        Assert.False(input.HandleMessage(PreviewPinInput.WmHotkey, PreviewPinInput.HotkeyId));
        await input.PinClipboardAsync();
        Assert.Equal(1, requests);
        Assert.Empty(registrar.Released);
    }

    [Fact]
    public async Task RepeatHotkeyDuringReadDoesNotCreateDuplicatePins()
    {
        var completion = new TaskCompletionSource<PasteResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        int requests = 0;
        using var input = new PreviewPinInput(new FakeRegistrar(true), () => { requests++; return completion.Task; });
        input.RegisterShortcut();
        Assert.True(input.HandleMessage(PreviewPinInput.WmHotkey, PreviewPinInput.HotkeyId));
        Task first = input.LastPinTask;
        Assert.True(input.IsBusy);
        Assert.Equal("클립보드 읽는 중", input.StatusText);
        Assert.True(input.HandleMessage(PreviewPinInput.WmHotkey, PreviewPinInput.HotkeyId));
        Assert.Equal(1, requests);
        completion.SetResult(PasteResult.Pinned);
        await first;
        Assert.False(input.IsBusy);
        Assert.Equal("핀 열림", input.StatusText);
    }

    [Theory]
    [InlineData((int)PasteResult.ClipboardBusy, "클립보드를 읽을 수 없습니다. 다시 시도하십시오.")]
    [InlineData((int)PasteResult.NoSupportedContent, "텍스트나 이미지를 복사한 후 다시 시도하십시오.")]
    public async Task ClipboardFailuresStayVisibleAndCanBeRetried(int result, string message)
    {
        int attempts = 0;
        using var input = new PreviewPinInput(new FakeRegistrar(), () => Task.FromResult(++attempts == 1 ? (PasteResult)result : PasteResult.Pinned));
        await input.PinClipboardAsync();
        Assert.Equal(message, input.StatusText);
        Assert.False(input.IsBusy);
        await input.PinClipboardAsync();
        Assert.Equal("핀 열림", input.StatusText);
    }

    [Fact]
    public async Task ExpectedReadFailureReleasesBusyState()
    {
        using var input = new PreviewPinInput(new FakeRegistrar(), () => throw new InvalidOperationException("Synthetic read error"));
        await input.PinClipboardAsync();
        Assert.NotNull(input.LastError);
        Assert.False(input.IsBusy);
        Assert.Equal("핀을 열 수 없습니다. 다시 시도하십시오.", input.StatusText);
    }

    [Fact]
    public async Task ClosingDuringClipboardReadDropsLateContent()
    {
        bool active = true;
        int reads = 0;
        var completion = new TaskCompletionSource<ClipboardImageReader.PinReadAttempt>(TaskCreationOptions.RunContinuationsAsynchronously);
        var clipboard = new PreviewClipboardAccess(
            () => { reads++; return completion.Task; },
            (_, _, _, _, _) => throw new InvalidOperationException("No image copy expected"),
            _ => throw new InvalidOperationException("No text copy expected"));
        Task<ClipboardImageReader.PinReadAttempt> read = clipboard.ReadWhileActiveAsync(() => active);
        active = false;
        var image = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[4], 4);
        image.Freeze();
        completion.SetResult(ClipboardImageReader.PinReadAttempt.Success(PinContent.FromImage(image)));
        Assert.Null((await read).Content);
        await clipboard.ReadWhileActiveAsync(() => active);
        Assert.Equal(1, reads);
    }

    [Fact]
    public Task FakeClipboardFlowsThroughManagerAndCopiesExactSourceWithAndWithoutImage() => RunAsync(async () =>
    {
        const string source = "# 배포\r\n\r\n- [x] 확인\r\n\r\n```ts\r\nconst timeoutMs = 5000;\r\n```\r\n";
        var image = BitmapSource.Create(240, 120, 96, 96, PixelFormats.Bgra32, null, new byte[240 * 120 * 4], 240 * 4);
        image.Freeze();
        var content = PinContent.FromMarkdown(image, source);
        string? textCopy = null;
        var imageCopy = new TaskCompletionSource<(BitmapSource Image, string? Source)>();
        int reads = 0;
        var clipboard = new PreviewClipboardAccess(
            () => { reads++; return Task.FromResult(ClipboardImageReader.PinReadAttempt.Success(content)); },
            (bitmap, _, text, _, _) => { imageCopy.TrySetResult((bitmap, text)); return Task.FromResult(true); },
            text => { textCopy = text; return Task.FromResult(true); });
        PinManager manager = clipboard.CreateManager(() => new PinSettings(), saveService: null);
        manager.ShowPinForTest = static (_, _) => { };
        manager.ActivatePinForTest = static _ => { };
        using var input = new PreviewPinInput(new FakeRegistrar(true), () => manager.PasteFromClipboardAsync(clipboard.Read));
        try
        {
            input.RegisterShortcut();
            input.HandleMessage(PreviewPinInput.WmHotkey, PreviewPinInput.HotkeyId);
            await input.LastPinTask;
            var pin = Assert.Single(manager.SnapshotOpenPinWindows());
            Assert.Equal(1, reads);
            Assert.True(pin.PinnedContent.IsMarkdown);
            Assert.Equal(source, pin.OriginalText);
            pin.SimulateCtrlDoubleClickForTest();
            Assert.True(await manager.LastTextCopyOperationForTest!);
            Assert.Equal(source, textCopy);
            Assert.False(imageCopy.Task.IsCompleted);
            pin.HandleKeyDown(new KeyEventArgs(Keyboard.PrimaryDevice, new MemoryPresentationSource(), 0, Key.C)
                { RoutedEvent = Keyboard.KeyDownEvent }, ModifierKeys.Control);
            Assert.True(imageCopy.Task.IsCompleted, "Ctrl+C must request an image together with the exact source.");
            var copied = await imageCopy.Task;
            Assert.Equal(source, copied.Source);
            Assert.True(copied.Image.IsFrozen);
        }
        finally { manager.CloseAll(); }
    });

    [Fact]
    public Task ControlWindowHasStatusAndShortcutWithoutInputTabsOrCopyBuffer() => RunAsync(() =>
    {
        using var input = new PreviewPinInput(new FakeRegistrar(false, true), () => Task.FromResult(PasteResult.Pinned));
        input.RegisterShortcut();
        var window = new PinPreviewWindow(input);
        try
        {
            var descendants = Descendants(window).ToArray();
            Assert.Empty(descendants.OfType<TextBox>());
            Assert.Empty(descendants.OfType<TabControl>());
            Assert.Contains(descendants.OfType<TextBlock>(), text => text.Text.Contains("Ctrl+Shift+F3"));
            Assert.Contains(descendants.OfType<TextBlock>(), text => text.Text == PreviewPinInput.F3UnavailableMessage);
            Assert.Contains(descendants.OfType<Button>(), button => Equals(button.Content, "클립보드 핀"));
            Assert.Contains(descendants.OfType<Button>(), button => Equals(button.Content, "종료"));
        }
        finally { window.Close(); }
        return Task.CompletedTask;
    });

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        foreach (object child in LogicalTreeHelper.GetChildren(parent))
        {
            if (child is not DependencyObject dependency) continue;
            yield return dependency;
            foreach (DependencyObject descendant in Descendants(dependency)) yield return descendant;
        }
    }

    private sealed class MemoryPresentationSource : PresentationSource
    {
        public override Visual RootVisual { get; set; } = new DrawingVisual();
        public override bool IsDisposed => false;
        protected override CompositionTarget GetCompositionTargetCore() => null!;
    }

    private sealed class FakeRegistrar(params bool[] outcomes) : IHotkeyRegistrar
    {
        private readonly Queue<bool> _outcomes = new(outcomes);
        internal List<Hotkey> Attempts { get; } = [];
        internal List<int> Released { get; } = [];
        public bool TryRegister(int id, Hotkey hotkey, out int errorCode, out string errorMessage)
        {
            Assert.Equal(PreviewPinInput.HotkeyId, id);
            Attempts.Add(hotkey);
            bool succeeded = _outcomes.Dequeue();
            errorCode = succeeded ? 0 : 1409;
            errorMessage = succeeded ? string.Empty : "Synthetic conflict";
            return succeeded;
        }
        public void Unregister(int id) => Released.Add(id);
    }

    private static Task RunAsync(Func<Task> action)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread thread = WpfTestThread.Create(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await action(); completion.TrySetResult(true); }
                catch (Exception error) { completion.TrySetException(error); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); }
            }));
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
