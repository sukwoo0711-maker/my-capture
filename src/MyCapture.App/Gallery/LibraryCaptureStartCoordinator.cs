namespace MyCapture.App.Gallery;

/// <summary>Serializes library capture commands behind confirmation that the library has disappeared.</summary>
internal sealed class LibraryCaptureStartCoordinator(Func<Task> hideAndConfirm, Action restore)
{
    private bool _preparing;

    internal async Task StartAsync(Action start)
    {
        if (_preparing) return;
        _preparing = true;
        try
        {
            await hideAndConfirm();
            start();
        }
        catch
        {
            restore();
            throw;
        }
        finally
        {
            _preparing = false;
        }
    }
}
