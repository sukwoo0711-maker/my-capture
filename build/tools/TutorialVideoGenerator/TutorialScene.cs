using System.Windows.Media;
using MyCapture.Core.Primitives;

namespace MyCapture.TutorialGenerator;

/// <summary>
/// One feature's clip. The constructor builds the storyboard and the product UI it needs; Draw is
/// called once per frame plan, in order, on the generator's STA thread. Product state may change
/// between calls (that is how a click takes effect); rendered pixels must not be retained.
/// </summary>
internal abstract class TutorialScene : IDisposable
{
    protected TutorialScene(string id, string language)
    {
        Id = id;
        Korean = language == "ko";
    }

    internal string Id { get; }

    internal bool Korean { get; }

    internal TutorialStoryboard Story { get; set; } = new(new PointD(880, 440));

    /// <summary>What the clip shows and which parts are real product UI; recorded in the receipt.</summary>
    internal abstract string Provenance { get; }

    /// <summary>Draws the whole logical 960×540 frame: scene, caption strip, pointer and click ring.</summary>
    internal abstract void Draw(DrawingContext dc, TutorialFramePlan frame);

    protected string L(string korean, string english) => Korean ? korean : english;

    /// <summary>Common finish: caption strip, click ring and the pointer on top.</summary>
    protected static void Overlay(DrawingContext dc, TutorialFramePlan frame, string caption, string key,
        Stage.PointerKind pointer = Stage.PointerKind.Arrow, bool showPointer = true)
    {
        if (frame.Pressed) Stage.DrawClick(dc, frame.Cursor, frame.Progress);
        if (showPointer) Stage.DrawPointer(dc, frame.Cursor, pointer);
        Stage.DrawCaption(dc, caption, key);
    }

    public virtual void Dispose()
    {
    }
}
