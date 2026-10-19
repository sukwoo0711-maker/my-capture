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

    /// <summary>
    /// A product string. Captions name buttons, commands, menus and results with the words the
    /// product shows (its UiText keys), so a renamed command renames the caption with it.
    /// </summary>
    protected static string Ui(string key) => UiText.Get(key);

    /// <summary>The key the product shows beside a command (ShortcutCatalog).</summary>
    protected static string Hint(MyCapture.App.ShortcutScope scope, string command) => MyCapture.App.ShortcutCatalog.Get(scope, command).Hint;

    /// <summary>The fixed keys the tutorial window shows for this feature (TutorialCatalog).</summary>
    protected string FeatureKeys => MyCapture.App.TutorialCatalog.Get(Id).FixedKeys?.Invoke()
        ?? throw new InvalidOperationException($"{Id} has no fixed keys.");

    /// <summary>The product's accessible command name also covers icon-only buttons.</summary>
    protected static string Label(System.Windows.Controls.ContentControl control)
    {
        string name = System.Windows.Automation.AutomationProperties.GetName(control);
        if (!string.IsNullOrWhiteSpace(name)) return name;
        string label = control.Content switch
        {
            string text => text,
            System.Windows.DependencyObject content => string.Join(" ", OffscreenWindow.Descendants(content)
                .OfType<System.Windows.Controls.TextBlock>().Where(block => block.Visibility == System.Windows.Visibility.Visible)
                .Select(block => block.Text).Where(text => !string.IsNullOrWhiteSpace(text))),
            _ => string.Empty,
        };
        return !string.IsNullOrWhiteSpace(label) ? label
            : throw new InvalidOperationException("The control has no command name.");
    }

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
