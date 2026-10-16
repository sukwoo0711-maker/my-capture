using Xunit;

namespace MyCapture.Setup.Tests
{
    public sealed class SkeletonTests
    {
        [Fact]
        public void RunsOnNetFramework() => Assert.StartsWith(".NET Framework", System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);
    }
}
