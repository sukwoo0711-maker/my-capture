using MyCapture.Core.Recording;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class WgcBorderPolicyTests
{
    [Theory]
    [InlineData(false, false, false, false, false, true, false, "unavailable (pre-Windows 11)")]
    [InlineData(false, false, true, false, false, true, false, "unavailable (pre-Windows 11)")]
    [InlineData(false, true, false, false, false, true, false, "unavailable (pre-Windows 11)")]
    [InlineData(false, true, true, false, false, true, false, "unavailable (pre-Windows 11)")]
    [InlineData(true, false, false, false, true, true, true, "not requested (border forced on)")]
    [InlineData(true, false, true, false, false, true, true, "unavailable (borderless access API missing)")]
    [InlineData(true, true, false, false, true, true, true, "not requested (border forced on)")]
    [InlineData(true, true, true, true, true, false, true, "pending")]
    public void Resolve_NeverTouchesMissingApis(bool property, bool access, bool borderless,
        bool request, bool set, bool required, bool read, string diagnostic)
    {
        WgcBorderPlan plan = WgcBorderPolicy.Resolve(property, access, borderless);
        Assert.Equal(new WgcBorderPlan(request, set, required, read, diagnostic), plan);
    }
}
