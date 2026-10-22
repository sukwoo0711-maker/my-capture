using System.Collections;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class PenPointCollectionTests
{
    [Fact]
    public void GenericAndNonGenericMutationsAdvanceTheVersionAndNotifyOnce()
    {
        var pen = new PenAnnotation { Points = [new(1, 2), new(3, 4)] };
        var notifications = new List<string?>();
        pen.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
        void Check(Action mutation)
        {
            notifications.Clear();
            long before = pen.PointsVersion;
            mutation();
            Assert.True(pen.PointsVersion > before);
            Assert.Equal([nameof(PenAnnotation.Points), nameof(PenAnnotation.Bounds)], notifications);
        }

        IList<PointD> generic = pen.Points;
        IList untyped = pen.Points;
        Check(() => generic.Add(new(5, 6)));
        Check(() => generic.Insert(1, new(2, 3)));
        Check(() => generic[0] = new(7, 8));
        Check(() => generic.RemoveAt(0));
        Check(() => generic.Remove(new(2, 3)));
        Check(() => untyped.Add(new PointD(9, 10)));
        Check(() => untyped.Insert(0, new PointD(11, 12)));
        Check(() => untyped[0] = new PointD(13, 14));
        Check(() => untyped.RemoveAt(0));
        Check(() => untyped.Remove(new PointD(9, 10)));
        Check(generic.Clear);
        Check(() => untyped.Add(new PointD(1, 2)));
        Check(untyped.Clear);
        long unchanged = pen.PointsVersion;
        Assert.False(generic.Remove(new(99, 99)));
        Assert.Equal(unchanged, pen.PointsVersion);
    }

    [Fact]
    public void ListInputsAreCopiedAndDetachedCollectionsCannotNotifyThePen()
    {
        List<PointD> callerPoints = [new(1, 2), new(3, 4)];
        var pen = new PenAnnotation { Points = callerPoints };
        long before = pen.PointsVersion;
        callerPoints[0] = new(50, 60);
        Assert.Equal(new PointD(1, 2), pen.Points[0]);
        Assert.Equal(before, pen.PointsVersion);
        PenPointCollection detached = pen.Points;
        pen.Points = [new(10, 20), new(30, 40)];
        Assert.True(pen.PointsVersion > before);
        before = pen.PointsVersion;
        detached.Add(new(70, 80));
        Assert.Equal(before, pen.PointsVersion);
        Assert.Equal(2, pen.Points.Count);
    }

    [Fact]
    public void TransformsKeepOneNotificationPairAndUpdateEveryPoint()
    {
        var pen = new PenAnnotation { Points = [new(0, 0), new(5, 5), new(10, 10)] };
        var names = new List<string?>();
        pen.PropertyChanged += (_, e) => names.Add(e.PropertyName);
        long before = pen.PointsVersion;
        pen.Translate(3, 4);
        Assert.True(pen.PointsVersion > before);
        Assert.Equal([new PointD(3, 4), new(8, 9), new(13, 14)], pen.Points);
        Assert.Equal([nameof(PenAnnotation.Points), nameof(PenAnnotation.Bounds)], names);
        names.Clear();
        before = pen.PointsVersion;
        pen.SetBounds(new(20, 30, 40, 60));
        Assert.True(pen.PointsVersion > before);
        Assert.Equal([new PointD(20, 30), new(40, 60), new(60, 90)], pen.Points);
        Assert.Equal([nameof(PenAnnotation.Points), nameof(PenAnnotation.Bounds)], names);
    }

    [Fact]
    public void CloneAndJsonRoundTripKeepArraySchemaAndObserveLaterMutations()
    {
        var doc = AnnotationDocument.CreateFor(320, 240);
        var pen = new PenAnnotation { Points = [new(1, 2), new(3, 4), new(5, 6)] };
        doc.Add(pen);
        string json = doc.ToJson();
        Assert.DoesNotContain("pointsVersion", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"points\":[", json);
        var restored = Assert.IsType<PenAnnotation>(Assert.Single(AnnotationDocument.TryFromJson(json)!.Items));
        var clone = Assert.IsType<PenAnnotation>(pen.Clone());
        foreach (PenAnnotation copy in new[] { restored, clone })
        {
            Assert.Equal(pen.Points, copy.Points);
            Assert.NotSame(pen.Points, copy.Points);
            long before = copy.PointsVersion;
            copy.Points[0] = new(10, 20);
            Assert.True(copy.PointsVersion > before);
            Assert.Equal(new PointD(1, 2), pen.Points[0]);
        }
    }
}
