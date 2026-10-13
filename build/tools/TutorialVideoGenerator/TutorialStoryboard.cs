using MyCapture.Core.Primitives;

namespace MyCapture.TutorialGenerator;

/// <summary>
/// A clip as a list of frame plans at 25 fps. A hold is one plan repeated for its duration (it is
/// rendered once); a motion is one plan per frame with an eased pointer path. Plans are small: the
/// rendered pixels never accumulate.
/// </summary>
internal sealed class TutorialStoryboard
{
    internal const int Fps = 25;
    private readonly List<TutorialFramePlan> _frames = [];

    internal TutorialStoryboard(PointD start) => End = start;

    internal IReadOnlyList<TutorialFramePlan> Frames => _frames;

    internal int TotalFrames => _frames.Sum(frame => frame.Repeat);

    internal double DurationSeconds => TotalFrames / (double)Fps;

    /// <summary>Where the pointer is after the last plan.</summary>
    internal PointD End { get; private set; }

    internal TutorialStoryboard Hold(string segment, double seconds, PointD? cursor = null)
    {
        PointD at = cursor ?? End;
        _frames.Add(new TutorialFramePlan(segment, Math.Max(1, (int)Math.Round(seconds * Fps)), 1, at, false, 0, 1));
        End = at;
        return this;
    }

    /// <summary>Moves the pointer from its current position, eased; the frame count follows the distance.</summary>
    internal TutorialStoryboard Move(string segment, PointD to, int? frames = null, bool pressed = false)
    {
        double distance = Math.Sqrt(Math.Pow(to.X - End.X, 2) + Math.Pow(to.Y - End.Y, 2));
        int count = frames ?? Math.Clamp((int)Math.Ceiling(distance / 24), 8, 28);
        return Motion(segment, count, End, to, pressed);
    }

    /// <summary>A click in place: the press shows for a few frames with an expanding ring.</summary>
    internal TutorialStoryboard Click(string segment, int frames = 6) => Motion(segment, frames, End, End, pressed: true);

    internal TutorialStoryboard Motion(string segment, int frameCount, PointD from, PointD to, bool pressed = false)
    {
        for (int i = 1; i <= frameCount; i++)
        {
            double progress = (double)i / frameCount;
            _frames.Add(new TutorialFramePlan(segment, 1, progress, Stage.Lerp(from, to, Stage.Ease(progress)), pressed, i - 1, frameCount));
        }

        End = to;
        return this;
    }

    /// <summary>Pads the clip with a final hold so it runs at least <paramref name="seconds"/>.</summary>
    internal TutorialStoryboard Finish(string segment, double seconds = 11)
    {
        double remaining = Math.Max(1.6, seconds - DurationSeconds);
        return Hold(segment, remaining);
    }
}

/// <summary>One rendered picture shown for <see cref="Repeat"/> frames.</summary>
internal readonly record struct TutorialFramePlan(string Segment, int Repeat, double Progress, PointD Cursor, bool Pressed, int Step, int Steps)
{
    internal bool IsFirst => Step == 0;

    internal bool IsLast => Step == Steps - 1;
}
