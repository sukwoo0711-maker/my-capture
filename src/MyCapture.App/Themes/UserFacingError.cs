using System.Runtime.CompilerServices;

namespace MyCapture.App.Themes;

/// <summary>Marks only messages authored and localized by this application for display.</summary>
internal static class UserFacingError
{
    private sealed record Message(string Text);
    private static readonly ConditionalWeakTable<Exception, Message> Messages = new();
    internal static T Mark<T>(T error) where T : Exception
    {
        Messages.AddOrUpdate(error, new Message(error.Message));
        return error;
    }
    internal static string? GetMessage(Exception error) =>
        Messages.TryGetValue(error, out Message? value) ? value.Text : null;
}
