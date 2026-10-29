using MyCapture.Core.Settings;

namespace MyCapture.App;

/// <summary>One update offer per installation and revision, only after the shell is idle.</summary>
internal static class PracticeOffer
{
    internal const int CurrentRevision = 1;

    internal static bool IsEligible(AppSettings settings, bool firstRun) =>
        !firstRun && settings.Hotkeys.CoreShortcutsAsked
        && settings.General.PracticeRevisionOffered < CurrentRevision;

    internal static bool TryShow(AppSettings settings, bool firstRun, bool idle,
        Func<bool> show, Action save)
    {
        if (!idle || !IsEligible(settings, firstRun) || !show()) return false;
        MarkPresented(settings, save);
        return true;
    }

    internal static void MarkPresented(AppSettings settings, Action save)
    {
        int previous = settings.General.PracticeRevisionOffered;
        if (previous >= CurrentRevision) return;
        settings.General.PracticeRevisionOffered = CurrentRevision;
        try { save(); }
        catch
        {
            settings.General.PracticeRevisionOffered = previous;
            throw;
        }
    }
}
