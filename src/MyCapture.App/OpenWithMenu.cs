using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using MyCapture.Platform.Shell;
using MyCapture.App.Themes;
using System.IO;

namespace MyCapture.App;

/// <summary>Lazy, per-opening shell recommendations shared by the library and capture receipt.</summary>
internal sealed class OpenWithMenu : IDisposable
{
    internal static string FailureMessage(Exception error)
    {
        string? reason = UserFacingError.GetMessage(error);
        reason ??= error is FileNotFoundException or DirectoryNotFoundException
            ? UiText.Get("Library.Source.FileMissing")
            : UserFacingError.ClassifyStorage(error) switch
            {
                StorageFailure.DiskFull => UiText.Get("Folder410.ExportDiskFull"),
                StorageFailure.InUse => UiText.Get("Dialog.InUse"),
                StorageFailure.AccessDenied => UiText.Get("OpenWith.AccessDenied"),
                _ => UiText.Get("OpenWith.Unavailable"),
            };
        return UiText.Format("OpenWith.Failed", reason);
    }

    private readonly MenuItem _item;
    private readonly Func<IOpenWithHandlerSource> _source;
    private readonly Func<string> _extension;
    private readonly Func<Func<string, Task>, Task> _launch;
    private readonly Func<IntPtr> _owner;
    private readonly TimeSpan _budget;
    private CancellationTokenSource? _query;
    private IOpenWithHandlerSet? _handlers;
    private int _generation;
    private bool _launching;

    internal OpenWithMenu(MenuItem item, ContextMenu menu, Func<IOpenWithHandlerSource> source,
        Func<string> extension, Func<Func<string, Task>, Task> launch, Func<IntPtr> owner, TimeSpan? budget = null)
    {
        _item = item; _source = source; _extension = extension; _launch = launch; _owner = owner;
        _budget = budget ?? OpenWithService.QueryBudget;
        item.Header = UiText.Get("OpenWith.Menu");
        AutomationProperties.SetName(item, UiText.Get("OpenWith.Menu"));
        Placeholder();
        item.SubmenuOpened += async (_, e) =>
        {
            if (!ReferenceEquals(e.OriginalSource, item)) return;
            await LoadAsync();
        };
        item.SubmenuClosed += (_, e) => { if (ReferenceEquals(e.OriginalSource, item)) QueueReset(); };
        menu.Closed += (_, _) => QueueReset();
    }

    private void Placeholder()
    {
        ReplaceItems([new MenuItem { Header = UiText.Get("OpenWith.Loading"), IsEnabled = false }]);
    }

    private void ReplaceItems(IEnumerable<object> items)
    {
        object[] previous = _item.Items.Cast<object>().ToArray();
        // Never transiently empty an open MenuItem: WPF would coerce IsSubmenuOpen to false.
        foreach (object item in items) _item.Items.Add(item);
        foreach (object item in previous) _item.Items.Remove(item);
    }

    private void QueueReset()
    {
        int generation = _generation;
        _ = _item.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
        {
            // WPF can close a popup before raising the selected item's Click event.
            if (generation == _generation) Reset();
        }));
    }

    internal async Task LoadAsync()
    {
        if (_launching) return;
        Reset();
        if (!_item.IsEnabled) return;
        int generation = _generation;
        var cancellation = _query = new CancellationTokenSource();
        IOpenWithHandlerSet? result = null;
        try
        {
            Task<IOpenWithHandlerSet>? query = null;
            try
            {
                query = _source().QueryAsync(_extension(), cancellation.Token);
                result = await query.WaitAsync(_budget, cancellation.Token);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                cancellation.Cancel();
                // A provider may ignore cancellation. Release its late result, without publishing it.
                if (query is not null) _ = ReleaseLateAsync(query);
            }
            if (generation != _generation) { result?.Dispose(); return; }
            _handlers = result;
            var items = new List<object>();
            foreach (IOpenWithHandler handler in result?.Handlers ?? [])
            {
                var command = new MenuItem { Header = new TextBlock { Text = handler.Name }, Focusable = _item.Focusable };
                AutomationProperties.SetName(command, handler.Name);
                if (handler.Icon is { } icon)
                    command.Icon = new Image { Source = icon, Width = 16, Height = 16 };
                command.Click += async (_, _) => await LaunchAsync(handler.InvokeAsync);
                items.Add(command);
            }
            if (items.Count > 0) items.Add(new Separator());
            var choose = new MenuItem { Header = UiText.Get("OpenWith.Choose"), Focusable = _item.Focusable };
            AutomationProperties.SetName(choose, UiText.Get("OpenWith.Choose"));
            choose.Click += async (_, _) =>
            {
                IOpenWithHandlerSource source = _source();
                IntPtr owner = _owner();
                await LaunchAsync(path => source.ChooseAsync(path, owner));
            };
            items.Add(choose);
            ReplaceItems(items);
        }
        finally
        {
            if (ReferenceEquals(_query, cancellation)) _query = null;
            cancellation.Dispose();
        }
    }

    private static async Task ReleaseLateAsync(Task<IOpenWithHandlerSet> query)
    {
        try { (await query.ConfigureAwait(false)).Dispose(); }
        catch (Exception error) when (error is not OutOfMemoryException) { }
    }

    private async Task LaunchAsync(Func<string, Task> invoke)
    {
        if (_launching) return;
        // Click runs before WPF closes the parent. The selected handler stays alive through export
        // and invocation, even though closing the menu releases all other per-opening state.
        _launching = true;
        try { await _launch(invoke); }
        finally { _launching = false; Reset(); }
    }

    internal void Reset()
    {
        _generation++;
        _query?.Cancel();
        if (!_launching) { _handlers?.Dispose(); _handlers = null; }
        Placeholder();
    }

    public void Dispose() => Reset();
}
