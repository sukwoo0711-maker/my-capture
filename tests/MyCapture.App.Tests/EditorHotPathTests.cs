using System.Globalization;
using System.Windows.Media;
using MyCapture.App.Editing;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using MyCapture.Core.Undo;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class EditorHotPathTests
{
    [Fact]
    public void InkGeometryIsReusedUntilEveryKindOfPointMutation() => StaTestHost.Run(() =>
    {
        var doc = AnnotationDocument.CreateFor(320, 240);
        var pen = new PenAnnotation { Points = [new(10, 10), new(20, 20), new(30, 30), new(40, 40)] };
        doc.Add(pen);
        var renderer = new AnnotationRenderer(new AnnotationImageStore());
        StreamGeometry Paint() => InkGeometry(renderer, doc);
        StreamGeometry previous = Paint();
        Assert.Same(previous, Paint());
        void Check(Action mutation)
        {
            long version = pen.PointsVersion;
            mutation();
            Assert.True(pen.PointsVersion > version);
            StreamGeometry next = Paint();
            Assert.NotSame(previous, next);
            Assert.True(next.IsFrozen);
            Assert.Same(next, Paint());
            Assert.Equal(InkGeometry(new AnnotationRenderer(new AnnotationImageStore()), doc).ToString(), next.ToString());
            previous = next;
        }

        Check(pen.SimplifyInPlace);
        Check(() => pen.Points.Add(new(50, 20)));
        Check(() => pen.Points.Insert(1, new(15, 40)));
        Check(() => pen.Points[1] = new(15, 50));
        Check(() => pen.Points.RemoveAt(1));
        Check(() => pen.Points = [new(5, 8), new(40, 80), new(90, 20)]);
        Check(() => pen.Translate(3, 7));
        Check(() => pen.SetBounds(new(20, 30, 150, 90)));
        Check(() => AnnotationDocumentRotator.Rotate(doc, 1));
        var undo = new UndoStack();
        Check(() => undo.Execute(new TranslateAnnotationsCommand([pen], 4, 5)));
        Check(() => Assert.True(undo.Undo()));
        Check(() => Assert.True(undo.Redo()));
        Check(() => undo.Execute(new ReplacePointsCommand(pen, pen.Points, [new(2, 3), new(30, 40)])));
        Check(() => Assert.True(undo.Undo()));
        Check(() => Assert.True(undo.Redo()));
        Check(() =>
        {
            var restored = Assert.IsType<PenAnnotation>(Assert.Single(AnnotationDocument.TryFromJson(doc.ToJson())!.Items));
            pen.Points = restored.Points;
        });
        pen.StrokeThickness = 9;
        pen.IsHighlighter = true;
        Assert.Same(previous, Paint());
    });

    [Fact]
    public void StepNumberTracksEditsFlagsCollectionChangesAndUndoRedo()
    {
        var doc = AnnotationDocument.CreateFor(320, 240);
        var undo = new UndoStack();
        var controller = new AnnotationEditorController(doc, undo);
        Assert.Equal(1, controller.NextStepNumber);
        TextAnnotation first = controller.AddStepNumber(new(10, 10));
        Assert.Equal(2, controller.NextStepNumber);
        TextAnnotation second = controller.AddStepNumber(new(30, 30));
        Assert.Equal(3, controller.NextStepNumber);
        Assert.True(undo.Undo());
        Assert.Equal(2, controller.NextStepNumber);
        Assert.True(undo.Redo());
        Assert.Equal(3, controller.NextStepNumber);
        second.Text = "8";
        Assert.Equal(9, controller.NextStepNumber);
        second.IsStepNumber = false;
        Assert.Equal(2, controller.NextStepNumber);
        second.IsStepNumber = true;
        Assert.Equal(9, controller.NextStepNumber);
        doc.Items[1] = new TextAnnotation { IsStepNumber = true, Text = "12" };
        Assert.Equal(13, controller.NextStepNumber);
        long revision = doc.StepNumberRevision;
        second.Text = "99"; // Replaced item is detached.
        first.Foreground = ColorRgba.Black;
        controller.SetSelected(first);
        Assert.Equal(revision, doc.StepNumberRevision);
        Assert.Equal(13, controller.NextStepNumber);
        doc.Items.Clear();
        Assert.Equal(1, controller.NextStepNumber);
        revision = doc.StepNumberRevision;
        first.Text = "99";
        Assert.Equal(revision, doc.StepNumberRevision);
    }

    [Fact]
    public void StepNumberHandlesPrepopulatedCollectionsDuplicatesCloneAndJson()
    {
        var badge = new TextAnnotation { IsStepNumber = true, Text = "7" };
        var doc = new AnnotationDocument { CanvasWidth = 320, CanvasHeight = 240, Items = [badge, badge] };
        var controller = new AnnotationEditorController(doc, new UndoStack());
        Assert.Equal(8, controller.NextStepNumber);
        doc.Items.RemoveAt(0);
        badge.Text = "8";
        Assert.Equal(9, controller.NextStepNumber);
        doc.Items.Move(0, 0);
        badge.Text = "9";
        Assert.Equal(10, controller.NextStepNumber);
        foreach (AnnotationDocument copy in new[] { doc.Clone(), AnnotationDocument.TryFromJson(doc.ToJson())! })
        {
            var copiedController = new AnnotationEditorController(copy, new UndoStack());
            Assert.Equal(10, copiedController.NextStepNumber);
            Assert.IsType<TextAnnotation>(copy.Items[0]).Text = "15";
            Assert.Equal(16, copiedController.NextStepNumber);
        }
        doc.Items.Clear();
        long revision = doc.StepNumberRevision;
        badge.Text = "20";
        Assert.Equal(revision, doc.StepNumberRevision);
    }

    [Fact]
    public void StepNumberRetainsCultureSensitiveParsingWhenNumberFormatChanges()
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
            CultureInfo.CurrentCulture = culture;
            var doc = AnnotationDocument.CreateFor(320, 240);
            doc.Add(new TextAnnotation { IsStepNumber = true, Text = "p7" });
            var controller = new AnnotationEditorController(doc, new UndoStack());
            Assert.Equal(1, controller.NextStepNumber);
            culture.NumberFormat.PositiveSign = "p";
            Assert.Equal(8, controller.NextStepNumber);
            culture.NumberFormat.PositiveSign = "+";
            culture.NumberFormat.NegativeSign = "p";
            Assert.Equal(-6, controller.NextStepNumber);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void SelectionBoundsReadsEachItemOnceAndFollowsLaterChanges()
    {
        var doc = AnnotationDocument.CreateFor(320, 240);
        var left = new CountingBoundsAnnotation { Rect = new(-10, 5, 30, 40) };
        var right = new CountingBoundsAnnotation { Rect = new(30, -20, 50, 10) };
        doc.Add(left);
        doc.Add(right);
        var controller = new AnnotationEditorController(doc, new UndoStack());
        Assert.Null(controller.SelectionBounds);
        controller.SelectAll();
        left.Reads = right.Reads = 0;
        Assert.Equal(new RectD(-10, -20, 90, 65), controller.SelectionBounds);
        Assert.Equal(1, left.Reads);
        Assert.Equal(1, right.Reads);
        right.Translate(100, 0);
        Assert.Equal(new RectD(-10, -20, 190, 65), controller.SelectionBounds);
    }

    private static StreamGeometry InkGeometry(AnnotationRenderer renderer, AnnotationDocument doc)
    {
        var drawing = new DrawingGroup();
        using (DrawingContext context = drawing.Open()) renderer.Render(context, doc, 1);
        return Assert.Single(Geometries(drawing).OfType<StreamGeometry>());
    }

    private static IEnumerable<Geometry> Geometries(Drawing drawing)
    {
        if (drawing is GeometryDrawing geometry) yield return geometry.Geometry;
        if (drawing is DrawingGroup group)
            foreach (Drawing child in group.Children)
                foreach (Geometry item in Geometries(child)) yield return item;
    }

    private sealed class CountingBoundsAnnotation : AnnotationItem
    {
        internal RectD Rect;
        internal int Reads;
        public override string DisplayName => "Test bounds";
        public override RectD Bounds { get { Reads++; return Rect; } }
        public override void Translate(double dx, double dy) => Rect = new(Rect.X + dx, Rect.Y + dy, Rect.Width, Rect.Height);
        public override void SetBounds(RectD bounds) => Rect = bounds;
        public override double DistanceTo(PointD point) => 0;
        public override AnnotationItem Clone() => new CountingBoundsAnnotation { Rect = Rect };
    }
}
