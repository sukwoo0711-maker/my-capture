using MyCapture.Core.Primitives;

/// <summary>Small frame plans only. Rendered pixel buffers are never retained by a storyboard.</summary>
internal sealed class TutorialStoryboard
{
    internal const int MotionDelayCentiseconds = 4;
    private readonly List<TutorialFramePlan> _frames = [];
    internal IReadOnlyList<TutorialFramePlan> Frames => _frames;
    internal int DurationMs => _frames.Sum(frame => frame.DelayCentiseconds) * 10;

    internal TutorialStoryboard Hold(string segment, int centiseconds, PointD cursor)
    {
        _frames.Add(new TutorialFramePlan(segment, centiseconds, 1, cursor, false));
        return this;
    }

    internal TutorialStoryboard Motion(string segment, int frameCount, PointD from, PointD to, bool pressed = false)
    {
        for (int i = 1; i <= frameCount; i++)
        {
            double progress = (double)i / frameCount;
            double ease = SmoothStep(progress);
            _frames.Add(new TutorialFramePlan(segment, MotionDelayCentiseconds, progress,
                new PointD(from.X + (to.X - from.X) * ease, from.Y + (to.Y - from.Y) * ease), pressed));
        }
        return this;
    }

    internal static double SmoothStep(double progress) => progress * progress * (3 - 2 * progress);
}

internal readonly record struct TutorialFramePlan(string Segment, int DelayCentiseconds, double Progress, PointD Cursor, bool Pressed);
