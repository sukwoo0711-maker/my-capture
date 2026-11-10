using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Xml.Linq;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class WindowsProjectionReleaseContractTests
{
    [Theory]
    [InlineData("x64", "Release", null, "true")]
    [InlineData("x86", "Release", null, "true")]
    [InlineData("x64", "Release", "false", "false")]
    [InlineData("x86", "Release", "false", "false")]
    [InlineData("x64", "Debug", null, "false")]
    [InlineData("x86", "Debug", null, "false")]
    public void ReleaseProfile_OnlyLinksProjectionAndHonorsDiagnosticOverride(
        string architecture, string configuration, string? trimOverride, string expected)
    {
        Dictionary<string, string> properties = EvaluateProperties(configuration, architecture, trimOverride);
        Assert.Equal(expected, properties["MyCaptureTrimWindowsProjection"]);
        Assert.Equal("false", properties["PublishTrimmed"]);
        Assert.Equal("true", properties["SelfContained"]);
        Assert.Equal("true", properties["PublishReadyToRun"]);
        Assert.Equal("false", properties["PublishSingleFile"]);
        Assert.Equal("win-" + architecture, properties["RuntimeIdentifier"]);
    }

    [Theory]
    [InlineData("Release")]
    [InlineData("Debug")]
    public void OrdinaryBuild_LeavesProjectionTrimmingDisabled(string configuration)
    {
        Dictionary<string, string> properties = EvaluateProperties(configuration, null, null);
        Assert.Equal("false", properties["MyCaptureTrimWindowsProjection"]);
        Assert.NotEqual("true", properties["PublishTrimmed"]);
    }

    [Fact]
    public void StoreAndInteropBoundaryTypes_KeepEveryMember()
    {
        XDocument descriptor = XDocument.Load(PathFor("build/WindowsProjectionRoots.xml"));
        XElement projection = Assert.Single(descriptor.Root!.Elements("assembly"));
        Assert.Equal("Microsoft.Windows.SDK.NET", (string?)projection.Attribute("fullname"));
        string[] required =
        [
            "Windows.Services.Store.StoreContext", "Windows.Services.Store.StorePackageUpdate",
            "Windows.Services.Store.StorePackageUpdateResult", "Windows.Services.Store.StorePackageUpdateStatus",
            "Windows.Services.Store.StorePackageUpdateState", "Windows.Services.Store.StoreRateAndReviewResult",
            "Windows.Services.Store.StoreRateAndReviewStatus", "Windows.ApplicationModel.Package",
            "Windows.ApplicationModel.PackageId", "Windows.ApplicationModel.AppInstance",
            "Windows.ApplicationModel.StartupTask", "Windows.ApplicationModel.StartupTaskState",
            "Windows.ApplicationModel.Activation.IActivatedEventArgs", "Windows.ApplicationModel.Activation.ActivationKind",
            "Windows.Graphics.Capture.GraphicsCaptureItem", "Windows.Graphics.DirectX.Direct3D11.IDirect3DDevice",
            "Windows.Graphics.DirectX.Direct3D11.IDirect3DSurface", "Windows.Foundation.IMemoryBufferReference",
        ];
        foreach (string name in required)
        {
            XElement type = Assert.Single(projection.Elements("type"), element => (string?)element.Attribute("fullname") == name);
            Assert.Equal("all", (string?)type.Attribute("preserve"));
        }
        Assert.DoesNotContain(projection.Elements("type"), element => (string?)element.Attribute("fullname") == "*");
    }

    [Fact]
    public void PackagedRunner_UsesPublishedModuleIdentityAndVerifiesUnknownBytes()
    {
        string runner = File.ReadAllText(PathFor("build/run-packaged-self-tests.ps1"));
        // A caller's false switch cannot bypass a detected trimmed or unclassifiable DLL.
        Assert.Contains("$projectionDetection = Get-WindowsProjectionVerification -Root $publish", runner, StringComparison.Ordinal);
        Assert.Contains("$VerifyWindowsProjection = [bool]$VerifyWindowsProjection -or $projectionDetection.RequiresVerification", runner, StringComparison.Ordinal);
        Assert.Contains("RequiresVerification = $true; State = 'unknown'", runner, StringComparison.Ordinal);
        Assert.Contains("tools\\projection-identity.cs", runner, StringComparison.Ordinal);
        Assert.Contains("$detection.RequiresVerification = $identity.RequiresVerification", runner, StringComparison.Ordinal);
        Assert.DoesNotContain("ReflectionOnlyLoad", runner, StringComparison.Ordinal);
        Assert.Contains("runtimepack\\.Microsoft\\.Windows\\.SDK\\.NET\\.Ref/", runner, StringComparison.Ordinal);
        Assert.Contains("$result.WindowsProjection = $projectionEvidence", runner, StringComparison.Ordinal);
        Assert.Contains("foreach ($number in 1..4)", runner, StringComparison.Ordinal);
        Assert.Contains("capture backend requested=WindowsGraphicsCapture active=WindowsGraphicsCapture", runner, StringComparison.Ordinal);
        Assert.Contains("copied=[1-9][0-9]*", runner, StringComparison.Ordinal);
        Assert.Contains("$resultLines.Count -eq 1", runner, StringComparison.Ordinal);
        Assert.Contains("$process.WaitForExit($TimeoutSeconds * 1000)", runner, StringComparison.Ordinal);
    }

    [Fact]
    public void ProjectionMetadata_IdenticalAssemblyIdentityIsReadTwiceWithoutLoading()
    {
        string sdk = Path.Combine(AppContext.BaseDirectory, "Microsoft.Windows.SDK.NET.dll");
        Assert.True(File.Exists(sdk));
        using JsonDocument result = CompareModuleIdentities(sdk, sdk, 0);
        Assert.Equal("original-sdk", result.RootElement.GetProperty("State").GetString());
        Assert.False(result.RootElement.GetProperty("RequiresVerification").GetBoolean());
        Assert.Equal(result.RootElement.GetProperty("PublishedMvid").GetString(), result.RootElement.GetProperty("OriginalMvid").GetString());
    }

    [Fact]
    public void ProjectionMetadata_DifferentModulesRequireVerification()
    {
        string sdk = Path.Combine(AppContext.BaseDirectory, "Microsoft.Windows.SDK.NET.dll");
        string app = Path.Combine(AppContext.BaseDirectory, "MyCapture.dll");
        Assert.True(File.Exists(sdk));
        Assert.True(File.Exists(app));
        using JsonDocument result = CompareModuleIdentities(sdk, app, 0);
        Assert.Equal("trimmed-or-modified", result.RootElement.GetProperty("State").GetString());
        Assert.True(result.RootElement.GetProperty("RequiresVerification").GetBoolean());
        Assert.NotEqual(result.RootElement.GetProperty("PublishedMvid").GetString(), result.RootElement.GetProperty("OriginalMvid").GetString());
    }

    [Fact]
    public void ProjectionMetadata_InvalidInputRequiresVerificationAndFails()
    {
        string sdk = Path.Combine(AppContext.BaseDirectory, "Microsoft.Windows.SDK.NET.dll");
        using JsonDocument result = CompareModuleIdentities(sdk, PathFor("Directory.Build.props"), 2);
        Assert.Equal("unknown", result.RootElement.GetProperty("State").GetString());
        Assert.True(result.RootElement.GetProperty("RequiresVerification").GetBoolean());
    }

    private static JsonDocument CompareModuleIdentities(string published, string original, int expectedExit)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = RepositoryRoot(), UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
        };
        string[] arguments = ["run", "--file", PathFor("build/tools/projection-identity.cs"),
            "--artifacts-path", Path.Combine(Path.GetTempPath(), "mycapture-projection-identity-tests", Guid.NewGuid().ToString("N")),
            "--verbosity", "quiet", "--", published, original];
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Projection metadata reader did not start.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60000))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            Assert.Fail("Projection metadata reader exceeded 60 seconds.");
        }
        string output = stdout.GetAwaiter().GetResult();
        string errors = stderr.GetAwaiter().GetResult();
        Assert.True(process.ExitCode == expectedExit, $"Metadata reader exit {process.ExitCode}, expected {expectedExit}: {output}\n{errors}");
        return JsonDocument.Parse(output);
    }

    [Fact]
    public void LinkerAcquisition_IsPinnedAndDoesNotEnableApplicationTrimming()
    {
        XDocument targets = XDocument.Load(PathFor("build/WindowsProjectionTrim.targets"));
        XElement properties = Assert.Single(targets.Root!.Elements("PropertyGroup"));
        string version = properties.Element("MyCaptureProjectionLinkerVersion")!.Value;
        byte[] digest = Convert.FromBase64String(properties.Element("MyCaptureProjectionLinkerPackageSha512")!.Value);
        Assert.Equal("10.0.12", version);
        Assert.Equal(64, digest.Length);
        string prepare = File.ReadAllText(PathFor("build/prepare-projection-linker.ps1"));
        Assert.Contains("$actualSha512 -cne $expectedSha512", prepare, StringComparison.Ordinal);
        Assert.True(prepare.IndexOf("$actualSha512 -cne $expectedSha512", StringComparison.Ordinal)
            < prepare.IndexOf("Expand-Archive -LiteralPath", StringComparison.Ordinal));
        Assert.Contains("$env:MYCAPTURE_PROJECTION_LINKER_PATH = $cachedLinker", prepare, StringComparison.Ordinal);
        Assert.DoesNotContain("PackageReference", targets.Descendants().Select(element => element.Name.LocalName));
        Assert.DoesNotContain(targets.Descendants(), element => element.Name.LocalName is "PublishTrimmed" or "EnableTrimAnalyzer");
    }

    private static Dictionary<string, string> EvaluateProperties(string configuration, string? architecture, string? trimOverride)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = RepositoryRoot(), UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
        };
        start.ArgumentList.Add("msbuild");
        start.ArgumentList.Add(PathFor("src/MyCapture.App/MyCapture.App.csproj"));
        start.ArgumentList.Add("-nologo");
        start.ArgumentList.Add("-p:Configuration=" + configuration);
        if (architecture is not null) start.ArgumentList.Add("-p:PublishProfile=win-" + architecture + "-self-contained");
        if (trimOverride is not null) start.ArgumentList.Add("-p:MyCaptureTrimWindowsProjection=" + trimOverride);
        start.ArgumentList.Add("-getProperty:MyCaptureTrimWindowsProjection,PublishTrimmed,SelfContained,PublishReadyToRun,PublishSingleFile,RuntimeIdentifier");
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("MSBuild property evaluation did not start.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60000))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            Assert.Fail("MSBuild property evaluation exceeded 60 seconds.");
        }
        string output = stdout.GetAwaiter().GetResult();
        string errors = stderr.GetAwaiter().GetResult();
        Assert.True(process.ExitCode == 0, $"MSBuild exit {process.ExitCode}: {output}\n{errors}");
        using JsonDocument json = JsonDocument.Parse(output);
        return json.RootElement.GetProperty("Properties").EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.GetString() ?? string.Empty, StringComparer.Ordinal);
    }

    private static string PathFor(string path) => Path.Combine(RepositoryRoot(), path.Replace('/', Path.DirectorySeparatorChar));

    private static string RepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Directory.Build.props"))
                && Directory.Exists(Path.Combine(directory.FullName, "build"))) return directory.FullName;
        throw new InvalidOperationException("Repository root was not found above the test output directory.");
    }
}
