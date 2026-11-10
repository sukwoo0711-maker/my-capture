using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace MyCapture.App.Gallery;

internal sealed partial class GalleryWindow
{
    internal int DetailsContentCreationCount { get; private set; }

    private void InitializeDeferredDetails()
    {
        _viewModel.PropertyChanged += OnDetailsSelectionChanged;
        Closed += OnDeferredDetailsClosed;
        // A window constructed with a selection still creates its visible details immediately.
        EnsureSelectedDetails();
    }

    private void OnDetailsSelectionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(GalleryViewModel.SingleSelectedTile))
            EnsureSelectedDetails();
    }

    private void EnsureSelectedDetails()
    {
        if (_viewModel.SingleSelectedTile is null || GalleryDetailsHost.Child is not null) return;
        Action<GalleryOpenTiming>? observer = OpenTimingObserverForTest;
        long started = observer is null ? 0 : System.Diagnostics.Stopwatch.GetTimestamp();
        bool completed = false;
        try
        {
            var template = (DataTemplate)FindResource("Gallery.SelectedDetailsContent");
            var content = (Grid)template.LoadContent();
            // Template names keep their own ElementName scope. Also preserve the window's
            // established FindName contract once the controls exist. ControlTemplate names
            // such as Chrome/FocusRing belong to their own scopes and are not registered here.
            string[] names = ["GalleryDetailsContent", "SelectedDetailThumbnail", "DetailsEditButton",
                "DetailsFloatButton", "DetailsCopyButton", "GalleryDetailsScroll", "SourcePageLabel",
                "SourcePageToggle", "SourcePageValue", "SourcePathLabel", "SourcePathToggle", "SourcePathValue"];
            var named = new FrameworkElement[names.Length];
            for (int index = 0; index < names.Length; index++)
                named[index] = LogicalTreeHelper.FindLogicalNode(content, names[index]) as FrameworkElement
                    ?? throw new InvalidOperationException($"Missing selected-details element: {names[index]}.");
            for (int index = 0; index < names.Length; index++) RegisterName(names[index], named[index]);
            // Keep the original root binding, visibility, layout, event handlers and accessibility.
            // The single instance remains attached across clear/multi-selection and hide/show.
            GalleryDetailsHost.Child = content;
            DetailsContentCreationCount++;
            completed = true;
        }
        finally
        {
            if (observer is not null)
                observer(new GalleryOpenTiming("details-create",
                    System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds, Completed: completed));
        }
    }

    private void OnDeferredDetailsClosed(object? sender, EventArgs e)
    {
        CancelSelectedDetailsAudio();
        _viewModel.PropertyChanged -= OnDetailsSelectionChanged;
        Closed -= OnDeferredDetailsClosed;
    }
}
