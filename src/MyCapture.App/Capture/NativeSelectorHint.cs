using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using MyCapture.App.Themes;

namespace MyCapture.App.Capture;

/// <summary>
/// The hint chip under the mode bar ("드래그: 영역 캡처  ·  Esc: 취소"). The resource strings are parsed,
/// never changed: segments are split on "  ·  ", each is "Key: action" or "Key · action", and the
/// keyboard keys in a key part become key caps while mouse words stay plain text. Toasts are plain.
/// </summary>
/// <remarks>
/// Key caps take the Micro size because they hold only Latin key names; every word, Korean
/// included, keeps the Caption size. The chip draws a one-pixel edge only when its window has no
/// DWM border (<see cref="Outlined"/>).
/// </remarks>
internal sealed partial class NativeSelectorHint : Border
{
    /// <summary>Chip height, DIPs.</summary>
    internal const double ChipHeight = 30;
    /// <summary>Space from the chip's edges to its first and last word, DIPs.</summary>
    internal const double PaddingX = 12;
    /// <summary>Key cap height, DIPs.</summary>
    internal const double KeycapHeight = 18;
    /// <summary>Space between a key cap and the words beside it, DIPs.</summary>
    internal const double KeycapGap = 6;
    /// <summary>Space on each side of the dot between two segments, DIPs.</summary>
    internal const double SegmentGap = 8;
    internal const string SegmentSeparator = "  ·  ";

    private readonly StackPanel _row = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly IReadOnlyDictionary<string, Brush> _palette;
    private readonly FontFamily _ui;
    private readonly List<Border> _keycaps = [];
    private double _scale = 1;
    private bool _outlined = true;
    private string _text = string.Empty;
    private bool _plain;

    internal NativeSelectorHint(IReadOnlyDictionary<string, Brush> palette)
    {
        _palette = palette;
        _ui = Application.Current?.TryFindResource("Font.Ui") as FontFamily ?? new FontFamily("Segoe UI");
        Height = ChipHeight;
        Padding = new Thickness(PaddingX, 0, PaddingX, 0);
        Background = palette["CaptureGuide.ChipSurface"];
        BorderBrush = palette["CaptureGuide.ChipBorder"];
        BorderThickness = new Thickness(1);
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;
        SnapsToDevicePixels = true;
        Child = _row;
    }

    /// <summary>The text shown, exactly as the resource or toast gave it.</summary>
    internal string Text => _text;

    /// <summary>The key-cap texts shown, in order.</summary>
    internal IReadOnlyList<string> KeycapsForTest => _keycaps.Select(cap => ((TextBlock)cap.Child).Text).ToArray();

    /// <summary>Every word shown as plain text, in order.</summary>
    internal IReadOnlyList<TextBlock> WordsForTest => _row.Children.OfType<TextBlock>().ToArray();

    internal IReadOnlyList<Border> KeycapBordersForTest => _keycaps;

    /// <summary>True while the chip draws its own edge (no DWM border around its window).</summary>
    internal bool Outlined
    {
        get => _outlined;
        set { if (_outlined == value) return; _outlined = value; ApplyEdges(); }
    }

    /// <summary>Keeps hairlines one physical pixel wide on the monitor the chip is on.</summary>
    internal void SetScale(double scale)
    {
        if (!(scale > 0) || _scale == scale) return;
        _scale = scale;
        ApplyEdges();
    }

    /// <summary>Shows <paramref name="text"/>; a toast (<paramref name="plain"/>) is never parsed into key caps.</summary>
    internal bool Show(string text, bool plain = false)
    {
        if (string.Equals(_text, text, StringComparison.Ordinal) && _plain == plain) return false;
        _text = text;
        _plain = plain;
        AutomationProperties.SetName(this, text);
        _row.Children.Clear();
        _keycaps.Clear();
        if (plain)
        {
            _row.Children.Add(Word(text));
            InvalidateMeasure();
            return true;
        }
        string[] segments = text.Split(SegmentSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (int index = 0; index < segments.Length; index++)
        {
            if (index > 0) _row.Children.Add(Dot(SegmentGap));
            AddSegment(segments[index]);
        }
        // New children mark only the row dirty until a layout pass reaches it; a direct Measure of
        // the chip, which the selector makes to size its window, must not return the old width.
        InvalidateMeasure();
        return true;
    }

    private void AddSegment(string segment)
    {
        // "Key: action" first; a "Key · action" segment keeps its dot.
        int colon = segment.IndexOf(": ", StringComparison.Ordinal);
        int dot = segment.IndexOf(" · ", StringComparison.Ordinal);
        if (colon > 0 && (dot < 0 || colon < dot))
        {
            bool endsInKey = AddKeys(segment[..colon]);
            // A key cap already sets the key apart; a word keeps its colon.
            if (endsInKey) AddWord(segment[(colon + 2)..], KeycapGap);
            else
            {
                AppendColon();
                AddWord(segment[(colon + 2)..], Space);
            }
            return;
        }
        if (dot > 0)
        {
            AddKeys(segment[..dot]);
            _row.Children.Add(Dot(KeycapGap));
            AddWord(segment[(dot + 3)..], 0);
            return;
        }
        AddKeys(segment);
    }

    // The width of a space in the Caption size, so words keep their own spacing.
    private const double Space = 3.5;

    /// <summary>Adds a key part: key caps for keyboard keys, plain words for the rest. True when it ends in a key cap.</summary>
    private bool AddKeys(string keys)
    {
        int position = 0;
        bool endsInKey = false;
        foreach (Match match in KeyPattern().Matches(keys))
        {
            string before = keys[position..match.Index].Trim();
            if (before.Length > 0) AddWord(before, LeadingGap(keycap: false));
            AddKeycap(match.Value.Replace(" ", "", StringComparison.Ordinal));
            position = match.Index + match.Length;
            endsInKey = true;
        }
        string rest = keys[position..].Trim();
        if (rest.Length > 0)
        {
            AddWord(rest, LeadingGap(keycap: false));
            endsInKey = false;
        }
        return endsInKey;
    }

    // Nothing after a dot (it carries its own spacing), a cap's gap beside a cap, a space between words.
    private double LeadingGap(bool keycap) => _row.Children.Count == 0 ? 0
        : _row.Children[^1] is TextBlock { Tag: DotTag } ? 0
        : keycap || _row.Children[^1] is Border ? KeycapGap : Space;

    private void AddWord(string text, double leading)
    {
        if (text.Length == 0) return;
        TextBlock word = Word(text);
        word.Margin = new Thickness(_row.Children.Count == 0 ? 0 : leading, 0, 0, 0);
        _row.Children.Add(word);
    }

    private void AppendColon()
    {
        if (_row.Children.Count > 0 && _row.Children[^1] is TextBlock word) word.Text += ":";
    }

    private TextBlock Word(string text) => new()
    {
        Text = text, FontFamily = _ui, FontSize = TypeScale.Caption, FontWeight = FontWeights.Medium,
        Foreground = _palette["CaptureGuide.Text"], VerticalAlignment = VerticalAlignment.Center,
    };

    private const string DotTag = "dot";

    private TextBlock Dot(double gap) => new()
    {
        Text = "·", Tag = DotTag, FontFamily = _ui, FontSize = TypeScale.Caption, FontWeight = FontWeights.Medium,
        // Full secondary ink: at 60% the 12-DIP dot all but vanished on the chip (design review, round 2).
        Foreground = _palette["CaptureGuide.MutedText"], Margin = new Thickness(gap, 0, gap, 0),
        VerticalAlignment = VerticalAlignment.Center,
    };

    private void AddKeycap(string key)
    {
        var cap = new Border
        {
            Height = KeycapHeight, Padding = new Thickness(5, 0, 5, 0), CornerRadius = new CornerRadius(4),
            Background = _palette["CaptureGuide.KeycapSurface"], BorderBrush = _palette["CaptureGuide.ChipHairline"],
            VerticalAlignment = VerticalAlignment.Center, SnapsToDevicePixels = true,
            Margin = new Thickness(LeadingGap(keycap: true), 0, 0, 0),
            Child = new TextBlock
            {
                Text = key, FontFamily = _ui, FontSize = TypeScale.Micro, FontWeight = FontWeights.SemiBold,
                Foreground = _palette["CaptureGuide.Text"], VerticalAlignment = VerticalAlignment.Center,
            },
        };
        cap.BorderThickness = new Thickness(1 / _scale);
        _keycaps.Add(cap);
        _row.Children.Add(cap);
    }

    private void ApplyEdges()
    {
        BorderThickness = new Thickness(_outlined ? 1 / _scale : 0);
        foreach (Border cap in _keycaps) cap.BorderThickness = new Thickness(1 / _scale);
    }

    /// <summary>
    /// Keyboard keys a hint names: Esc, Shift, Enter, Tab, Space, Ctrl/Alt/Shift/Win combinations,
    /// F1–F12, PgUp/PgDn and the arrows. Mouse words (드래그, 클릭, 휠, Drag, Click, Wheel) never match.
    /// </summary>
    [GeneratedRegex(@"(?:(?:Ctrl|Alt|Shift|Win)\s*\+\s*)+(?:F(?:1[0-2]|[1-9])|[A-Z0-9]\b|Esc|Enter|Tab|Space|PgUp|PgDn|[←↑→↓])"
        + @"|\b(?:Esc|Shift|Enter|Tab|Space|Ctrl|Alt|PgUp|PgDn|F(?:1[0-2]|[1-9]))\b|[←↑→↓]", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();
}
