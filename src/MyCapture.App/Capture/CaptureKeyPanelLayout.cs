using System.Windows;

namespace MyCapture.App.Capture;

/// <summary>Corner switching uses rectangles only; moving the pointer never measures the panel.</summary>
internal static class CaptureKeyPanelLayout
{
    internal const double Clearance = 48;
    internal const double Margin = 12;

    internal static Rect Place(Rect work, Size panel, Point pointer, Rect? highlight,
        Rect? bar, Rect? magnifier, Rect? sizeChip, double scale, ref bool right, Rect? hoverTab = null)
    {
        double margin = Margin * scale;
        double y = Math.Max(work.Top, work.Bottom - margin - panel.Height);
        Rect left = new(work.Left + margin, y, panel.Width, panel.Height);
        Rect other = new(Math.Max(work.Left, work.Right - margin - panel.Width), y, panel.Width, panel.Height);
        bool leftHard = Hard(left), rightHard = Hard(other);
        bool leftCrowded = leftHard || Near(left), rightCrowded = rightHard || Near(other);
        if (right ? rightCrowded && !leftCrowded : leftCrowded && !rightCrowded) right = !right;
        else if (right ? rightHard && !leftHard : leftHard && !rightHard) right = !right;
        // On a tiny screen both corners can be occupied by essential feedback. Preserve that feedback.
        if ((right ? rightHard : leftHard) || panel.Width + 2 * margin > work.Width || panel.Height + 2 * margin > work.Height)
            return Rect.Empty;
        return right ? other : left;

        bool Near(Rect candidate)
        {
            candidate.Inflate(Clearance * scale, Clearance * scale);
            return candidate.Contains(pointer) || Intersects(candidate, highlight) || Intersects(candidate, hoverTab);
        }
        bool Hard(Rect candidate) => Intersects(candidate, bar) || Intersects(candidate, magnifier) || Intersects(candidate, sizeChip);
        static bool Intersects(Rect candidate, Rect? obstacle) => obstacle is { IsEmpty: false } r && candidate.IntersectsWith(r);
    }
}
