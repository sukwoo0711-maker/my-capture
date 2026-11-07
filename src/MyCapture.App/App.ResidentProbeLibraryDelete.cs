using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using MyCapture.App.Diagnostics;
using MyCapture.App.Gallery;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Queue;
using MyCapture.Platform.Imaging;

namespace MyCapture.App;

/// <summary>
/// The resident probe's large library delete (<see cref="ResidentProbeLibraryDelete"/>).
/// </summary>
public partial class App
{
    /// <summary>
    /// Adds synthetic captures to the probe's own library, deletes them from the library window as
    /// one selection, then undoes the delete, through the product's window, removal worker and Windows
    /// Recycle Bin. Unlike every other step, this one acts outside the probe's data root: the captures
    /// pass through the user's Recycle Bin as a real delete does, and Undo brings each of them back.
    /// The probe never empties the Recycle Bin. A capture that does not come back fails the probe, and
    /// the report names the folders still missing; the Recycle Bin still holds them. As after any
    /// Undo in the app, the deletion's retired journal entries go there as one small folder. What the
    /// Recycle Bin held before and after the step is written with the run's machine state.
    /// </summary>
    private async Task ProbeLibraryDeleteAsync(ResidentProbeOptions probe, ResidentProbeRecorder recorder)
    {
        if (_queue is null) throw new InvalidOperationException("The library is unavailable.");
        recorder.MachineState(ResidentProbeLibraryDelete.RecycleBinState("before"), ResidentProbeLibraryDelete.RecycleBinContents());
        int count = ResidentProbeLibraryDelete.Count(probe.Quick);
        CaptureRecord[] seeded = SeedProbeLibraryCaptures(_queue, count);
        var deleted = new HashSet<Guid>(seeded.Select(record => record.Id));

        HandleGalleryRequested();
        await ProbeWaitAsync(() => _galleryWindow is { IsVisible: true }, 15_000, "library shown");
        GalleryWindow window = _galleryWindow!;
        bool DeletedCardShown() => window.RealizedCards()
            .Any(card => card.DataContext is GalleryItemViewModel item && deleted.Contains(item.Id));
        // The newest captures open the library: the selection is on screen when it is deleted.
        await ProbeWaitAsync(DeletedCardShown, 15_000, "probe captures shown");
        await Task.Delay(1500);
        recorder.Sample(ResidentProbeLibraryDelete.Scenario + " open", 0, $"{_queue.Count} captures");
        recorder.Note($"{ResidentProbeLibraryDelete.Scenario}: {count} captures, delete motion {(FluidMotion.AnimationsEnabled ? "on" : "off")}");

        string moved = UiText.Format("LibraryUndo_Moved", count);
        string restored = UiText.Format("LibraryUndo_Restored", count);
        string Status() => KoreanWordWrap.Strip(window.LibraryActionStatus.Text);
        double cardsGoneMs = -1, resultMs = -1, undoMs = -1;
        long deleteStarted = 0, undoStarted = 0;
        Task? undo = null;
        var failures = new List<Exception>();
        // How long the window went without a frame while each command ran: a window that waits on
        // the Recycle Bin shows its cards gone and its result in the same late frame.
        var deleteFrames = new ProbeFrameGaps();
        var undoFrames = new ProbeFrameGaps();
        EventHandler rendering = (_, _) =>
        {
            long frame = Stopwatch.GetTimestamp();
            if (cardsGoneMs < 0 && !DeletedCardShown()) cardsGoneMs = Stopwatch.GetElapsedTime(deleteStarted).TotalMilliseconds;
            if (resultMs < 0 && Status() == moved) resultMs = Stopwatch.GetElapsedTime(deleteStarted).TotalMilliseconds;
            if (undo is not null && undoMs < 0 && Status() == restored) undoMs = Stopwatch.GetElapsedTime(undoStarted).TotalMilliseconds;
            deleteFrames.Frame(frame);
            undoFrames.Frame(frame);
            if (cardsGoneMs >= 0 && resultMs >= 0) deleteFrames.Stop();
            if (undoMs >= 0) undoFrames.Stop();
        };

        Func<int, bool, bool>? confirm = window.ConfirmDelete;
        string deleteText, undoText;
        CompositionTarget.Rendering += rendering;
        try
        {
            // The probe answers the confirmation the user answered.
            window.ConfirmDelete = (_, _) => true;
            deleteStarted = Stopwatch.GetTimestamp();
            deleteFrames.Start(deleteStarted);
            Task delete = window.DeleteCapturesAsync(seeded.Select(record => record.Id).ToArray());
            // The one-at-a-time move took 0.34 s a capture; the limit only ends a hung Shell, and its
            // failure says where the captures are.
            if (!await ProbeTryWaitAsync(() => delete.IsCompleted, 600_000))
                throw new TimeoutException($"The probe delete of {count} captures did not finish; any it moved are in the Recycle Bin.");
            // From here nothing throws before Undo: a failed delete or a missing frame is reported,
            // never a reason to skip it.
            try { await delete; }
            catch (Exception ex) { failures.Add(ex); }
            deleteText = Status();
            await ProbeTryWaitAsync(() => cardsGoneMs >= 0 && (deleteText != moved || resultMs >= 0), 10_000);
            try { recorder.Sample(ResidentProbeLibraryDelete.Scenario + " deleted", 0, $"{_queue.Count} captures"); }
            catch (Exception ex) { failures.Add(ex); }

            // Undo whatever moved, whatever the result said: the probe leaves nothing in the Recycle Bin.
            await Task.Delay(1000);
            undoStarted = Stopwatch.GetTimestamp();
            undoFrames.Start(undoStarted);
            Task undoing = window.UndoLibraryAsync();
            undo = undoing;
            // The one-at-a-time restore took 3.2 s a capture; the probe waits for every one to return.
            if (!await ProbeTryWaitAsync(() => undoing.IsCompleted, 1_800_000))
                throw new TimeoutException($"The probe undo of {count} captures did not finish; the rest are in the Recycle Bin.");
            // A failed undo still leaves the check below to name what stayed in the Recycle Bin.
            try { await undoing; }
            catch (Exception ex) { failures.Add(ex); }
            undoText = Status();
            if (undoText == restored) await ProbeTryWaitAsync(() => undoMs >= 0, 10_000);
        }
        finally
        {
            CompositionTarget.Rendering -= rendering;
            window.ConfirmDelete = confirm;
            // Also when a command did not finish: the report names every probe capture not back in its folder.
            string[] missing = seeded
                .Where(record => _queue.Find(record.Id) is null
                    || !File.Exists(Path.Combine(_queue.GetDirectory(record), CaptureFileNames.Original)))
                .Select(record => _queue.GetDirectory(record))
                .ToArray();
            recorder.MachineState(ResidentProbeLibraryDelete.RecycleBinState("after"), ResidentProbeLibraryDelete.RecycleBinContents());
            if (missing.Length > 0)
                recorder.Fail(new InvalidOperationException(
                    $"{missing.Length} of {count} probe captures did not come back from the Recycle Bin; they can still be restored from there: "
                    + string.Join(", ", missing)));
        }

        recorder.Note($"{ResidentProbeLibraryDelete.Scenario}: delete {deleteFrames}; undo {undoFrames}");
        if (cardsGoneMs >= 0) recorder.Time(ResidentProbeLibraryDelete.CardsGoneTiming(count), cardsGoneMs);
        if (resultMs >= 0) recorder.Time(ResidentProbeLibraryDelete.ResultTiming(count), resultMs);
        if (undoMs >= 0) recorder.Time(ResidentProbeLibraryDelete.UndoTiming(count), undoMs);
        foreach (Exception failure in failures) recorder.Fail(failure);
        if (deleteText != moved)
            recorder.Fail(new InvalidOperationException($"The probe delete ended with \"{deleteText}\" instead of \"{moved}\"."));
        else if (undoText != restored)
            recorder.Fail(new InvalidOperationException($"The probe undo ended with \"{undoText}\" instead of \"{restored}\"."));
        else if (cardsGoneMs < 0 || resultMs < 0 || undoMs < 0)
            recorder.Fail(new InvalidOperationException(
                $"The probe did not see every result on screen (cards {cardsGoneMs:0} ms, result {resultMs:0} ms, undo {undoMs:0} ms)."));
        else
            foreach (string over in ResidentProbeLibraryDelete.OverBudget(new(count, cardsGoneMs, resultMs, undoMs)))
                recorder.Fail(new InvalidOperationException("Release budget exceeded: " + over));

        await Task.Delay(1000);
        recorder.Sample(ResidentProbeLibraryDelete.Scenario + " restored", 0, $"{_queue.Count} captures");
        window.Close();
        await ProbeWaitAsync(() => _galleryWindow is not { IsVisible: true }, 10_000, "library closed");
    }

    /// <summary>
    /// The longest time between rendered frames while a command runs, counting from the command,
    /// and when that wait ended: the time the window stood still.
    /// </summary>
    private sealed class ProbeFrameGaps
    {
        private long _started, _last;
        private int _frames;
        private double _longestMs, _endedMs;

        internal void Start(long started) => (_started, _last) = (started, started);

        internal void Frame(long frame)
        {
            if (_started == 0) return;
            double gap = Stopwatch.GetElapsedTime(_last, frame).TotalMilliseconds;
            if (gap > _longestMs) (_longestMs, _endedMs) = (gap, Stopwatch.GetElapsedTime(_started, frame).TotalMilliseconds);
            _last = frame;
            _frames++;
        }

        internal void Stop() => _started = 0;

        public override string ToString() => string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{_frames} frames, longest wait for a frame {_longestMs:0} ms, ending {_endedMs:0} ms after the command");
    }

    /// <summary>Waits like <see cref="ProbeWaitAsync"/> but reports a timeout instead of throwing it.</summary>
    private static async Task<bool> ProbeTryWaitAsync(Func<bool> done, int timeoutMs)
    {
        try
        {
            await ProbeWaitAsync(done, timeoutMs, "probe step");
            return true;
        }
        catch (TimeoutException) { return false; }
    }

    /// <summary>
    /// Small image captures, newest first, in the probe's own library: an original and a thumbnail
    /// each, already indexed for search so automatic OCR leaves them alone.
    /// </summary>
    private static CaptureRecord[] SeedProbeLibraryCaptures(CaptureQueue queue, int count)
    {
        byte[] png = ImageCodec.EncodePng(ResidentProbeImages.Pattern(320, 180, 11));
        DateTimeOffset newest = DateTimeOffset.Now;
        var records = new CaptureRecord[count];
        using (queue.SuspendEviction())
        {
            // Oldest first: each capture is inserted at the head, so the newest leads the library.
            for (int index = count - 1; index >= 0; index--)
            {
                DateTimeOffset created = newest.AddSeconds(-index);
                var record = new CaptureRecord
                {
                    Width = 320, Height = 180, Title = $"Resident probe delete {index + 1}",
                    CreatedAt = created, UpdatedAt = created, TotalBytes = png.Length * 2L,
                    OcrText = "Resident probe delete", OcrContentRevision = 0,
                };
                queue.Add(record);
                string directory = queue.GetDirectory(record);
                Directory.CreateDirectory(directory);
                File.WriteAllBytes(Path.Combine(directory, CaptureFileNames.Original), png);
                File.WriteAllBytes(Path.Combine(directory, CaptureFileNames.Thumbnail), png);
                records[index] = record;
            }
        }
        queue.Save();
        return records;
    }
}
