using MyCapture.Core.Localization;

namespace MyCapture.App.Tests;

/// <summary>Existing Korean caption contracts remain deterministic on English CI hosts.</summary>
public abstract class KoreanCaptionTest : IDisposable
{
    private readonly IDisposable _language = UiText.UseLanguage("ko-KR");
    public void Dispose() => _language.Dispose();
}
