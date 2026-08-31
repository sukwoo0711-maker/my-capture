using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using MyCapture.Core.Undo;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class AnnotationDocumentTests
{
    private static AnnotationDocument BuildPopulatedDocument()
    {
        AnnotationDocument doc = AnnotationDocument.CreateFor(1920, 1080);

        doc.Add(new RectangleAnnotation
        {
            Rect = new RectD(10, 20, 100, 50),
            Stroke = ColorRgba.FromRgb(0xEF, 0x44, 0x44),
            StrokeThickness = 4,
            CornerRadius = 6,
        });

        doc.Add(new EllipseAnnotation
        {
            Rect = new RectD(200, 100, 80, 80),
            Fill = new ColorRgba(0x40, 0x3B, 0x82, 0xF6),
        });

        doc.Add(PolylineAnnotation.CreateArrow(
            new PointD(300, 300), new PointD(500, 380),
            ColorRgba.FromRgb(0x22, 0xC5, 0x5E), thickness: 5));

        doc.Add(new PenAnnotation
        {
            Points = [new(0, 0), new(5, 8), new(11, 3)],
            StrokeThickness = 2,
            IsHighlighter = true,
        });

        doc.Add(new TextAnnotation
        {
            Rect = new RectD(600, 400, 240, 40),
            Text = "확인 필요",
            FontSize = 22,
            Bold = true,
            RotationDegrees = 15,
        });

        doc.Add(new ImageAnnotation
        {
            AssetFileName = "asset-1.png",
            Rect = new RectD(900, 500, 160, 90),
            SourceWidth = 320,
            SourceHeight = 180,
        });

        return doc;
    }

    [Fact]
    public void ToJson_ThenTryFromJson_PreservesEveryAnnotationType()
    {
        AnnotationDocument original = BuildPopulatedDocument();

        AnnotationDocument? restored = AnnotationDocument.TryFromJson(original.ToJson());

        Assert.NotNull(restored);
        Assert.Equal(original.Items.Count, restored.Items.Count);
        Assert.Equal(1920, restored.CanvasWidth);
        Assert.Equal(1080, restored.CanvasHeight);

        Assert.Collection(
            restored.Items,
            i => Assert.IsType<RectangleAnnotation>(i),
            i => Assert.IsType<EllipseAnnotation>(i),
            i => Assert.IsType<PolylineAnnotation>(i),
            i => Assert.IsType<PenAnnotation>(i),
            i => Assert.IsType<TextAnnotation>(i),
            i => Assert.IsType<ImageAnnotation>(i));
    }

    [Fact]
    public void RoundTrip_PreservesGeometryAndStyleDetails()
    {
        AnnotationDocument original = BuildPopulatedDocument();
        AnnotationDocument restored = AnnotationDocument.TryFromJson(original.ToJson())!;

        var rect = (RectangleAnnotation)restored.Items[0];
        Assert.Equal(new RectD(10, 20, 100, 50), rect.Rect);
        Assert.Equal(4, rect.StrokeThickness);
        Assert.Equal(6, rect.CornerRadius);
        Assert.Equal(ColorRgba.FromRgb(0xEF, 0x44, 0x44), rect.Stroke);

        var ellipse = (EllipseAnnotation)restored.Items[1];
        Assert.True(ellipse.HasFill);
        Assert.Equal(0x40, ellipse.Fill.A);

        var arrow = (PolylineAnnotation)restored.Items[2];
        Assert.True(arrow.HeadAtEnd);
        Assert.False(arrow.HeadAtStart);
        Assert.Equal(2, arrow.Points.Count);
        Assert.Equal(new PointD(500, 380), arrow.Points[1]);

        var pen = (PenAnnotation)restored.Items[3];
        Assert.True(pen.IsHighlighter);
        Assert.Equal(3, pen.Points.Count);

        var text = (TextAnnotation)restored.Items[4];
        Assert.Equal("확인 필요", text.Text);
        Assert.True(text.Bold);
        Assert.Equal(22, text.FontSize);
        Assert.Equal(15, text.RotationDegrees);

        var image = (ImageAnnotation)restored.Items[5];
        Assert.Equal("asset-1.png", image.AssetFileName);
        Assert.Equal(320, image.SourceWidth);
    }

    [Fact]
    public void RoundTrip_PreservesPaintOrderAfterReordering()
    {
        AnnotationDocument doc = BuildPopulatedDocument();
        AnnotationItem bottom = doc.Items[0];

        doc.BringToFront(bottom);
        Assert.Same(bottom, doc.Items[^1]);

        AnnotationDocument restored = AnnotationDocument.TryFromJson(doc.ToJson())!;

        // Order is restored from the persisted ZIndex mirror, so the reordered item
        // is still on top.
        Assert.IsType<RectangleAnnotation>(restored.Items[^1]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ not json")]
    [InlineData("[]")]
    public void TryFromJson_ReturnsNullRatherThanThrowing(string? json)
    {
        Assert.Null(AnnotationDocument.TryFromJson(json));
    }

    [Fact]
    public void HitTest_ReturnsTopmostOverlappingItem()
    {
        AnnotationDocument doc = AnnotationDocument.CreateFor(500, 500);

        var lower = new RectangleAnnotation
        {
            Rect = new RectD(0, 0, 200, 200),
            Fill = new ColorRgba(0xFF, 0, 0, 0),
        };
        var upper = new RectangleAnnotation
        {
            Rect = new RectD(50, 50, 100, 100),
            Fill = new ColorRgba(0xFF, 0, 0, 0),
        };

        doc.Add(lower);
        doc.Add(upper);

        Assert.Same(upper, doc.HitTest(new PointD(100, 100), tolerance: 2));
    }

    [Fact]
    public void HitTest_OutlineOnlyShapeDoesNotSwallowClicksInItsInterior()
    {
        AnnotationDocument doc = AnnotationDocument.CreateFor(500, 500);

        // Unfilled rectangle drawn around some content: clicking the middle should
        // not select it, otherwise it becomes impossible to reach annotations placed
        // inside it.
        doc.Add(new RectangleAnnotation { Rect = new RectD(0, 0, 200, 200) });

        Assert.Null(doc.HitTest(new PointD(100, 100), tolerance: 2));
        Assert.NotNull(doc.HitTest(new PointD(0, 100), tolerance: 4));
    }

    [Fact]
    public void Clone_ProducesIndependentItemsWithFreshIdentities()
    {
        AnnotationDocument original = BuildPopulatedDocument();
        AnnotationDocument clone = original.Clone();

        Assert.Equal(original.Items.Count, clone.Items.Count);
        Assert.NotSame(original.Items[0], clone.Items[0]);
        Assert.NotEqual(original.Items[0].Id, clone.Items[0].Id);

        ((RectangleAnnotation)clone.Items[0]).Rect = new RectD(0, 0, 1, 1);
        Assert.Equal(new RectD(10, 20, 100, 50), ((RectangleAnnotation)original.Items[0]).Rect);
    }

    [Fact]
    public void SetBounds_OnPolylineScalesEveryVertex()
    {
        var arrow = PolylineAnnotation.CreateArrow(
            new PointD(0, 0), new PointD(100, 50), ColorRgba.Black, 3);

        arrow.SetBounds(new RectD(0, 0, 200, 100));

        Assert.Equal(new PointD(0, 0), arrow.Points[0]);
        Assert.Equal(new PointD(200, 100), arrow.Points[1]);
    }

    [Fact]
    public void SetBounds_ToleratesCollapsedBoundsWithoutProducingNaN()
    {
        var arrow = PolylineAnnotation.CreateArrow(
            new PointD(0, 0), new PointD(100, 50), ColorRgba.Black, 3);

        // Dragging a resize handle past the opposite edge collapses the box.
        arrow.SetBounds(new RectD(30, 40, 0, 0));

        Assert.All(arrow.Points, p =>
        {
            Assert.True(double.IsFinite(p.X));
            Assert.True(double.IsFinite(p.Y));
        });
    }

    [Fact]
    public void PenAnnotation_SimplifyInPlaceRemovesRedundantPoints()
    {
        var pen = new PenAnnotation
        {
            StrokeThickness = 6,
            Points = [.. Enumerable.Range(0, 200).Select(i => new PointD(i, 0))],
        };

        pen.SimplifyInPlace();

        Assert.Equal(2, pen.Points.Count);
    }

    [Fact]
    public void PenAnnotation_OptsOutOfInteractiveResize()
    {
        Assert.False(new PenAnnotation().SupportsResize);
        Assert.True(new RectangleAnnotation().SupportsResize);
    }
}

public sealed class UndoStackTests
{
    private static (AnnotationDocument Document, UndoStack Stack) CreateEditor() =>
        (AnnotationDocument.CreateFor(800, 600), new UndoStack());

    [Fact]
    public void Execute_AppliesTheCommandAndEnablesUndo()
    {
        (AnnotationDocument doc, UndoStack stack) = CreateEditor();
        var rect = new RectangleAnnotation { Rect = new RectD(0, 0, 10, 10) };

        stack.Execute(new AddAnnotationCommand(doc, rect));

        Assert.Single(doc.Items);
        Assert.True(stack.CanUndo);
        Assert.False(stack.CanRedo);
    }

    [Fact]
    public void UndoThenRedo_RestoresTheItemAtItsOriginalPaintPosition()
    {
        (AnnotationDocument doc, UndoStack stack) = CreateEditor();

        var first = new RectangleAnnotation { Rect = new RectD(0, 0, 10, 10) };
        var second = new EllipseAnnotation { Rect = new RectD(20, 20, 10, 10) };
        stack.Execute(new AddAnnotationCommand(doc, first));
        stack.Execute(new AddAnnotationCommand(doc, second));

        stack.Execute(new RemoveAnnotationCommand(doc, first));
        Assert.Single(doc.Items);

        stack.Undo();

        Assert.Equal(2, doc.Items.Count);
        Assert.Same(first, doc.Items[0]);   // restored underneath, not on top
        Assert.Same(second, doc.Items[1]);
    }

    [Fact]
    public void NewEditAfterUndo_DiscardsTheRedoBranch()
    {
        (AnnotationDocument doc, UndoStack stack) = CreateEditor();

        stack.Execute(new AddAnnotationCommand(doc, new RectangleAnnotation()));
        stack.Undo();
        Assert.True(stack.CanRedo);

        stack.Execute(new AddAnnotationCommand(doc, new EllipseAnnotation()));

        Assert.False(stack.CanRedo);
    }

    [Fact]
    public void ConsecutiveTransformsOfTheSameItemCollapseIntoOneStep()
    {
        (AnnotationDocument doc, UndoStack stack) = CreateEditor();
        var rect = new RectangleAnnotation { Rect = new RectD(0, 0, 10, 10) };
        doc.Add(rect);

        // A drag produces one command per mouse-move; the user expects one Ctrl+Z.
        for (int i = 1; i <= 20; i++)
        {
            RectD before = rect.Rect;
            var after = new RectD(i, i, 10, 10);
            rect.SetBounds(after);
            stack.Push(new TransformAnnotationCommand(rect, before, after));
        }

        Assert.Equal(1, stack.UndoCount);

        stack.Undo();

        Assert.Equal(new RectD(0, 0, 10, 10), rect.Rect);
    }

    [Fact]
    public void TransformsOfDifferentItemsDoNotCollapse()
    {
        (AnnotationDocument doc, UndoStack stack) = CreateEditor();
        var a = new RectangleAnnotation { Rect = new RectD(0, 0, 10, 10) };
        var b = new RectangleAnnotation { Rect = new RectD(0, 0, 10, 10) };
        doc.Add(a);
        doc.Add(b);

        stack.Push(new TransformAnnotationCommand(a, a.Rect, new RectD(5, 5, 10, 10)));
        stack.Push(new TransformAnnotationCommand(b, b.Rect, new RectD(9, 9, 10, 10)));

        Assert.Equal(2, stack.UndoCount);
    }

    [Fact]
    public void MergingIsTimeGatedSoDeliberateSeparateEditsStaySeparate()
    {
        var stack = new UndoStack(mergeWindow: TimeSpan.Zero);
        var rect = new RectangleAnnotation { Rect = new RectD(0, 0, 10, 10) };

        stack.Push(new TransformAnnotationCommand(rect, rect.Rect, new RectD(1, 1, 10, 10)));
        stack.Push(new TransformAnnotationCommand(rect, rect.Rect, new RectD(2, 2, 10, 10)));

        Assert.Equal(2, stack.UndoCount);
    }

    [Fact]
    public void PropertyChangeCommand_UndoRestoresThePreviousValue()
    {
        (AnnotationDocument doc, UndoStack stack) = CreateEditor();
        var rect = new RectangleAnnotation { StrokeThickness = 3 };
        doc.Add(rect);

        stack.Execute(new PropertyChangeCommand<RectangleAnnotation, double>(
            rect, "두께", static (item, value) => item.StrokeThickness = value, 3, 12));

        Assert.Equal(12, rect.StrokeThickness);

        stack.Undo();
        Assert.Equal(3, rect.StrokeThickness);

        stack.Redo();
        Assert.Equal(12, rect.StrokeThickness);
    }

    [Fact]
    public void BeginBatch_GroupsMultipleRemovalsIntoOneUndoStep()
    {
        (AnnotationDocument doc, UndoStack stack) = CreateEditor();

        var items = new List<AnnotationItem>();
        for (int i = 0; i < 5; i++)
        {
            var item = new RectangleAnnotation { Rect = new RectD(i * 10, 0, 5, 5) };
            doc.Add(item);
            items.Add(item);
        }

        using (stack.BeginBatch("선택 삭제"))
        {
            foreach (AnnotationItem item in items)
            {
                stack.Execute(new RemoveAnnotationCommand(doc, item));
            }
        }

        Assert.Empty(doc.Items);
        Assert.Equal(1, stack.UndoCount);

        stack.Undo();
        Assert.Equal(5, doc.Items.Count);
    }

    [Fact]
    public void UndoAll_ReversesEverythingAndStaysRedoable()
    {
        (AnnotationDocument doc, UndoStack stack) = CreateEditor();

        for (int i = 0; i < 4; i++)
        {
            stack.Execute(new AddAnnotationCommand(doc, new RectangleAnnotation()));
        }

        stack.UndoAll();

        Assert.Empty(doc.Items);
        Assert.False(stack.CanUndo);
        Assert.Equal(4, stack.RedoCount);
    }

    [Fact]
    public void DepthLimit_DropsTheOldestSteps()
    {
        var stack = new UndoStack(depthLimit: 3, mergeWindow: TimeSpan.Zero);
        AnnotationDocument doc = AnnotationDocument.CreateFor(10, 10);

        for (int i = 0; i < 10; i++)
        {
            stack.Execute(new AddAnnotationCommand(doc, new RectangleAnnotation()));
        }

        Assert.Equal(3, stack.UndoCount);
    }

    [Fact]
    public void Changed_FiresForEveryHistoryMutation()
    {
        (AnnotationDocument doc, UndoStack stack) = CreateEditor();
        int events = 0;
        stack.Changed += (_, _) => events++;

        stack.Execute(new AddAnnotationCommand(doc, new RectangleAnnotation()));
        stack.Undo();
        stack.Redo();
        stack.Clear();

        Assert.Equal(4, events);
    }
}

public sealed class CaptureQueueTests
{
    private static CaptureQueue CreateQueue(TempWorkspace workspace, QueueSettings? limits = null) =>
        new(workspace.Paths, limits ?? new QueueSettings(), NullLogger<CaptureQueue>.Instance);

    /// <summary>
    /// Creates a record and the minimal on-disk footprint the queue expects.
    /// </summary>
    private static CaptureRecord AddCapture(
        CaptureQueue queue, TempWorkspace workspace, DateTimeOffset createdAt, long bytes, bool pinned = false)
    {
        var record = new CaptureRecord
        {
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
            Width = 800,
            Height = 600,
            TotalBytes = bytes,
            IsPinned = pinned,
            RelativeDirectory = CaptureQueue.BuildRelativeDirectory(Guid.NewGuid(), createdAt),
        };

        string dir = Path.Combine(workspace.Paths.CapturesRoot, record.RelativeDirectory);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, CaptureFileNames.Original), new byte[16]);

        queue.Add(record);
        return record;
    }

    [Fact]
    public void Add_PutsNewestFirst()
    {
        using var workspace = new TempWorkspace();
        CaptureQueue queue = CreateQueue(workspace);

        DateTimeOffset t0 = DateTimeOffset.Now;
        CaptureRecord older = AddCapture(queue, workspace, t0.AddMinutes(-10), 100);
        CaptureRecord newer = AddCapture(queue, workspace, t0, 100);

        Assert.Same(newer, queue.Records[0]);
        Assert.Same(older, queue.Records[1]);
    }

    [Fact]
    public void Eviction_EnforcesTheItemCapByDroppingTheOldest()
    {
        using var workspace = new TempWorkspace();
        var limits = new QueueSettings { MaxItems = 5, MaxBytes = long.MaxValue };
        CaptureQueue queue = CreateQueue(workspace, limits);

        DateTimeOffset t0 = DateTimeOffset.Now.AddHours(-1);
        var evicted = new List<Guid>();
        queue.Evicted += (_, e) => evicted.Add(e.Record.Id);

        var all = new List<CaptureRecord>();
        for (int i = 0; i < 8; i++)
        {
            all.Add(AddCapture(queue, workspace, t0.AddMinutes(i), 10));
        }

        Assert.Equal(5, queue.Count);
        Assert.Equal(3, evicted.Count);

        // The three oldest went, the five newest stayed.
        Assert.Equal([all[0].Id, all[1].Id, all[2].Id], evicted);
        Assert.Same(all[^1], queue.Records[0]);
    }

    [Fact]
    public void Eviction_EnforcesTheByteCapIndependentlyOfItemCount()
    {
        using var workspace = new TempWorkspace();
        var limits = new QueueSettings { MaxItems = 1000, MaxBytes = 500 };
        CaptureQueue queue = CreateQueue(workspace, limits);

        DateTimeOffset t0 = DateTimeOffset.Now.AddHours(-1);
        for (int i = 0; i < 10; i++)
        {
            AddCapture(queue, workspace, t0.AddMinutes(i), 100);
        }

        Assert.True(queue.TotalBytes <= 500);
        Assert.Equal(5, queue.Count);
    }

    [Fact]
    public void Eviction_NeverRemovesPinnedRecords()
    {
        using var workspace = new TempWorkspace();
        var limits = new QueueSettings { MaxItems = 3, MaxBytes = long.MaxValue };
        CaptureQueue queue = CreateQueue(workspace, limits);

        DateTimeOffset t0 = DateTimeOffset.Now.AddHours(-1);

        // The oldest record is pinned, so it must survive even though FIFO would
        // remove it first.
        CaptureRecord pinned = AddCapture(queue, workspace, t0, 10, pinned: true);
        for (int i = 1; i <= 6; i++)
        {
            AddCapture(queue, workspace, t0.AddMinutes(i), 10);
        }

        Assert.Equal(3, queue.Count);
        Assert.Contains(pinned, queue.Records);
    }

    [Fact]
    public void Eviction_StopsAndReportsWhenPinsAloneExceedTheCap()
    {
        using var workspace = new TempWorkspace();
        var limits = new QueueSettings { MaxItems = 2, MaxBytes = long.MaxValue };
        CaptureQueue queue = CreateQueue(workspace, limits);

        DateTimeOffset t0 = DateTimeOffset.Now.AddHours(-1);
        for (int i = 0; i < 5; i++)
        {
            AddCapture(queue, workspace, t0.AddMinutes(i), 10, pinned: true);
        }

        // Honouring the cap would mean deleting something the user explicitly kept,
        // so the queue stays over capacity and says so.
        Assert.Equal(5, queue.Count);
        Assert.True(queue.IsOverCapacityDueToPins);
    }

    [Fact]
    public void Eviction_ReportsPinPressureWhenThreePinsLeaveOneUsableUnpinnedCaptureOverTheItemCap()
    {
        using var workspace = new TempWorkspace();
        var limits = new QueueSettings { MaxItems = 3, MaxBytes = long.MaxValue };
        CaptureQueue queue = CreateQueue(workspace, limits);

        DateTimeOffset t0 = DateTimeOffset.Now.AddHours(-1);
        CaptureRecord firstPin = AddCapture(queue, workspace, t0, 10, pinned: true);
        CaptureRecord secondPin = AddCapture(queue, workspace, t0.AddMinutes(1), 10, pinned: true);
        CaptureRecord thirdPin = AddCapture(queue, workspace, t0.AddMinutes(2), 10, pinned: true);
        CaptureRecord usable = AddCapture(queue, workspace, t0.AddMinutes(3), 10);

        Assert.Equal(4, queue.Count);
        Assert.All(new[] { firstPin, secondPin, thirdPin }, pin => Assert.Contains(pin, queue.Records));
        Assert.Contains(usable, queue.Records);
        Assert.False(usable.IsPinned);
        Assert.True(queue.IsOverCapacityDueToPins);
    }

    [Fact]
    public void EvictionLease_DefersNewHeadEvictionUntilTheOlderLeaseCanBeReleased()
    {
        using var workspace = new TempWorkspace();
        var limits = new QueueSettings { MaxItems = 1, MaxBytes = long.MaxValue };
        CaptureQueue queue = CreateQueue(workspace, limits);
        DateTimeOffset now = DateTimeOffset.Now;
        CaptureRecord active = AddCapture(queue, workspace, now.AddMinutes(-1), 10);
        var evicted = new List<Guid>();
        queue.Evicted += (_, args) => evicted.Add(args.Record.Id);
        CaptureRecord newest;

        using (queue.AcquireEvictionLease(active.Id))
        {
            newest = AddCapture(queue, workspace, now, 10);

            Assert.Equal(2, queue.Count);
            Assert.Contains(active, queue.Records);
            Assert.Contains(newest, queue.Records);
            Assert.Empty(evicted);
        }

        Assert.Single(queue.Records);
        Assert.DoesNotContain(active, queue.Records);
        Assert.Same(newest, queue.Records[0]);
        Assert.Equal([active.Id], evicted);
    }

    [Fact]
    public void TogglePin_OnAnOverCapacityQueueAllowsEvictionToResume()
    {
        using var workspace = new TempWorkspace();
        var limits = new QueueSettings { MaxItems = 2, MaxBytes = long.MaxValue };
        CaptureQueue queue = CreateQueue(workspace, limits);

        DateTimeOffset t0 = DateTimeOffset.Now.AddHours(-1);
        CaptureRecord oldest = AddCapture(queue, workspace, t0, 10, pinned: true);
        AddCapture(queue, workspace, t0.AddMinutes(1), 10, pinned: true);
        AddCapture(queue, workspace, t0.AddMinutes(2), 10, pinned: true);

        Assert.True(queue.IsOverCapacityDueToPins);

        queue.TogglePin(oldest.Id);

        Assert.Equal(2, queue.Count);
        Assert.DoesNotContain(oldest, queue.Records);
    }

    [Fact]
    public void UpdateByteCount_KeepsTheRunningTotalAccurate()
    {
        using var workspace = new TempWorkspace();
        CaptureQueue queue = CreateQueue(workspace);

        CaptureRecord record = AddCapture(queue, workspace, DateTimeOffset.Now, 100);
        Assert.Equal(100, queue.TotalBytes);

        // Annotating a capture adds a rendered PNG and a layer file.
        queue.UpdateByteCount(record.Id, 450);

        Assert.Equal(450, queue.TotalBytes);
        Assert.Equal(450, record.TotalBytes);
    }

    [Fact]
    public void EvictionLease_ProtectsRecordAcrossByteCountEnforcement()
    {
        using var workspace = new TempWorkspace();
        var limits = new QueueSettings { MaxItems = 100, MaxBytes = 250 };
        CaptureQueue queue = CreateQueue(workspace, limits);
        DateTimeOffset now = DateTimeOffset.Now;
        CaptureRecord edited = AddCapture(queue, workspace, now.AddMinutes(-2), 100);
        CaptureRecord other = AddCapture(queue, workspace, now.AddMinutes(-1), 100);
        var evicted = new List<Guid>();
        queue.Evicted += (_, args) => evicted.Add(args.Record.Id);

        using (queue.AcquireEvictionLease(edited.Id))
        {
            queue.UpdateByteCount(edited.Id, 300);

            Assert.Contains(edited, queue.Records);
            Assert.Contains(other, queue.Records);
            Assert.Equal(2, queue.Count);
            Assert.Empty(evicted);
        }

        // Releasing the edit makes both records eligible. The recent edited generation survives,
        // while the older disposable capture is evicted; an oversized last capture is retained.
        Assert.Single(queue.Records);
        Assert.Same(edited, queue.Records[0]);
        Assert.Equal([other.Id], evicted);
    }

    [Fact]
    public void Eviction_UsesRecentEditActivityInsteadOfCreationOrder()
    {
        using var workspace = new TempWorkspace();
        var limits = new QueueSettings { MaxItems = 2, MaxBytes = long.MaxValue };
        CaptureQueue queue = CreateQueue(workspace, limits);
        DateTimeOffset now = DateTimeOffset.Now;
        CaptureRecord createdFirst = AddCapture(queue, workspace, now.AddMinutes(-3), 10);
        CaptureRecord untouched = AddCapture(queue, workspace, now.AddMinutes(-2), 10);

        // Re-editing the oldest-created record makes it the most recently used capture.
        createdFirst.UpdatedAt = now;
        CaptureRecord newest = AddCapture(queue, workspace, now.AddMinutes(-1), 10);

        Assert.Contains(createdFirst, queue.Records);
        Assert.Contains(newest, queue.Records);
        Assert.DoesNotContain(untouched, queue.Records);
    }

    [Fact]
    public void SaveThenLoad_RestoresTheQueueAcrossRestart()
    {
        using var workspace = new TempWorkspace();
        CaptureQueue queue = CreateQueue(workspace);

        DateTimeOffset t0 = DateTimeOffset.Now.AddHours(-1);
        CaptureRecord pinned = AddCapture(queue, workspace, t0, 100, pinned: true);
        AddCapture(queue, workspace, t0.AddMinutes(5), 250);
        queue.Save();

        CaptureQueue reloaded = CreateQueue(workspace);
        reloaded.Load();

        Assert.Equal(2, reloaded.Count);
        Assert.Equal(350, reloaded.TotalBytes);
        Assert.Contains(reloaded.Records, r => r.Id == pinned.Id && r.IsPinned);
    }

    [Fact]
    public void Load_DropsRecordsWhoseImageFileHasBeenDeletedByHand()
    {
        using var workspace = new TempWorkspace();
        CaptureQueue queue = CreateQueue(workspace);

        CaptureRecord ghost = AddCapture(queue, workspace, DateTimeOffset.Now, 100);
        AddCapture(queue, workspace, DateTimeOffset.Now, 100);
        queue.Save();

        File.Delete(Path.Combine(
            workspace.Paths.CapturesRoot, ghost.RelativeDirectory, CaptureFileNames.Original));

        CaptureQueue reloaded = CreateQueue(workspace);
        reloaded.Load();

        Assert.Equal(1, reloaded.Count);
        Assert.DoesNotContain(reloaded.Records, r => r.Id == ghost.Id);
    }

    [Fact]
    public void Load_RebuildsTheIndexFromSidecarMetadataWhenTheIndexIsUnreadable()
    {
        using var workspace = new TempWorkspace();
        CaptureQueue queue = CreateQueue(workspace);

        DateTimeOffset t0 = DateTimeOffset.Now.AddHours(-1);
        for (int i = 0; i < 3; i++)
        {
            CaptureRecord record = AddCapture(queue, workspace, t0.AddMinutes(i), 100);
            queue.SaveRecordMeta(record);
        }

        queue.Save();

        // Destroy both the index and its backup, leaving only the sidecars.
        File.WriteAllText(workspace.Paths.IndexFile, "{ corrupt");
        string backup = workspace.Paths.IndexFile + Storage.AtomicFile.BackupSuffix;
        if (File.Exists(backup))
        {
            File.Delete(backup);
        }

        CaptureQueue reloaded = CreateQueue(workspace);
        reloaded.Load();

        Assert.Equal(3, reloaded.Count);
        Assert.Equal(300, reloaded.TotalBytes);
    }

    [Fact]
    public void Remove_RaisesEvictedSoTheCallerCanDeleteFiles()
    {
        using var workspace = new TempWorkspace();
        CaptureQueue queue = CreateQueue(workspace);

        CaptureRecord record = AddCapture(queue, workspace, DateTimeOffset.Now, 100);

        string? reason = null;
        queue.Evicted += (_, e) => reason = e.Reason;

        Assert.True(queue.Remove(record.Id));

        Assert.Equal("manual", reason);
        Assert.Equal(0, queue.Count);
        Assert.Equal(0, queue.TotalBytes);
    }

    [Fact]
    public void UpdateLimits_AppliesTheNewCapImmediately()
    {
        using var workspace = new TempWorkspace();
        CaptureQueue queue = CreateQueue(workspace, new QueueSettings { MaxItems = 100 });

        DateTimeOffset t0 = DateTimeOffset.Now.AddHours(-1);
        for (int i = 0; i < 10; i++)
        {
            AddCapture(queue, workspace, t0.AddMinutes(i), 10);
        }

        queue.UpdateLimits(new QueueSettings { MaxItems = 4, MaxBytes = long.MaxValue });

        Assert.Equal(4, queue.Count);
    }
}
