using Xunit;
namespace MyCapture.Setup.Tests;

public sealed class X86ArchitectureTests
{
    [Theory]
    [InlineData("x86", "win-x86", true)]
    [InlineData("x64", "win-x64", true)]
    [InlineData("x86", "win-x64", false)]
    [InlineData("x64", "win-x86", false)]
    [InlineData("arm64", "win-arm64", false)]
    public void ManifestRequiresMatchingSupportedArchitecture(string architecture, string runtime, bool valid)
    {
        var manifest = SetupContractsTests.Manifest();
        manifest.Architecture = architecture;
        manifest.Runtime = runtime;
        if (valid) manifest.Validate();
        else SetupContractsTests.Code(14, manifest.Validate);
    }

    [Theory]
    [InlineData("x86", 19045, 0x014c, 0)]
    [InlineData("x86", 19044, 0x014c, 10)]
    [InlineData("x64", 19045, 0x014c, 11)]
    [InlineData("x86", 19045, 0x8664, 0)]
    [InlineData("x64", 19045, 0x8664, 0)]
    [InlineData("x86", 19045, 0xaa64, 11)]
    [InlineData("x86", 22000, 0xaa64, 0)]
    [InlineData("x64", 22000, 0xaa64, 0)]
    [InlineData("other", 22000, 0x8664, 11)]
    public void HostChecksPackageArchitecture(string architecture, int build, int native, int error)
    {
        void Validate() => WindowsHost.Validate(true, 10, build, "Client", (ushort)native, 19045, architecture);
        if (error == 0) Validate();
        else SetupContractsTests.Code(error, Validate);
    }
}
