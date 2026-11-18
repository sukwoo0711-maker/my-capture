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
        // The linker is a framework-dependent tool started with the SDK's own dotnet. It cannot start
        // on an older runtime patch than its own, so the pin is the runtime of the SDK in global.json
        // (SDK 10.0.400 carries runtime 10.0.11). When global.json moves, move this pair together.
        using (JsonDocument global = JsonDocument.Parse(File.ReadAllText(PathFor("global.json"))))
            Assert.Equal("10.0.400", global.RootElement.GetProperty("sdk").GetProperty("version").GetString());
        Assert.Equal("10.0.11", version);
        Assert.Equal(64, digest.Length);
        string prepare = File.ReadAllText(PathFor("build/prepare-projection-linker.ps1"));
        Assert.Contains("$actualSha512 -cne $expectedSha512", prepare, StringComparison.Ordinal);
        Assert.True(prepare.IndexOf("$actualSha512 -cne $expectedSha512", StringComparison.Ordinal)
            < prepare.IndexOf("Expand-Archive -LiteralPath", StringComparison.Ordinal));
        // A pack already in the NuGet cache is used only when NuGet's own record of its hash is the pin.
        Assert.Contains("-and $cachedSha512 -ceq $expectedSha512) {", prepare, StringComparison.Ordinal);
        Assert.True(prepare.IndexOf("-and $cachedSha512 -ceq $expectedSha512) {", StringComparison.Ordinal)
            < prepare.IndexOf("$env:MYCAPTURE_PROJECTION_LINKER_PATH = $cachedLinker", StringComparison.Ordinal));
        Assert.Contains("$env:MYCAPTURE_PROJECTION_LINKER_PATH = $cachedLinker", prepare, StringComparison.Ordinal);
        Assert.DoesNotContain("PackageReference", targets.Descendants().Select(element => element.Name.LocalName));
        Assert.DoesNotContain(targets.Descendants(), element => element.Name.LocalName is "PublishTrimmed" or "EnableTrimAnalyzer");
        // A plain profile publish gets the pinned pack from its own restore: downloaded, never
        // referenced, and compared with the pin before the linker runs.
        XElement download = Assert.Single(targets.Descendants("PackageDownload"));
        Assert.Equal("Microsoft.NET.ILLink.Tasks", (string?)download.Attribute("Include"));
        Assert.Equal("[$(MyCaptureProjectionLinkerVersion)]", (string?)download.Attribute("Version"));
        Assert.Contains(targets.Descendants("Error"), error => ((string?)error.Attribute("Condition") ?? string.Empty)
            .Contains("'$(_ProjectionLinkerPackSha512)' != '$(MyCaptureProjectionLinkerPackageSha512)'", StringComparison.Ordinal));
        // Restore may leave the pack in a fallback folder instead of the packages folder.
        Assert.Contains(targets.Descendants("_ProjectionPackageFolder"),
            folder => (string?)folder.Attribute("Include") == "$(NuGetPackageFolders)");
        // A linker newer than the SDK's runtime is refused by name instead of dying with exit 150.
        Assert.Contains(targets.Descendants("Error"), error => ((string?)error.Attribute("Condition") ?? string.Empty)
            .Contains("VersionGreaterThan('$(MyCaptureProjectionLinkerVersion)', '$(BundledNETCoreAppPackageVersion)')", StringComparison.Ordinal));
    }

    [Fact]
    public void PinnedLinker_IsNotNewerThanTheRuntimeOfTheSdkThatRunsThePublish()
    {
        using JsonDocument evaluation = Evaluate("Release", "x64", null, null,
            "-getProperty:MyCaptureProjectionLinkerVersion,BundledNETCoreAppPackageVersion");
        JsonElement properties = evaluation.RootElement.GetProperty("Properties");
        var linker = Version.Parse(properties.GetProperty("MyCaptureProjectionLinkerVersion").GetString()!);
        string bundled = properties.GetProperty("BundledNETCoreAppPackageVersion").GetString()!;
        // A preview SDK reports "10.0.0-rc.1…"; only the numeric part decides whether the tool can start.
        var runtime = Version.Parse(bundled.Split('-')[0]);
        Assert.True(linker <= runtime, $"ILLink {linker} cannot start on the SDK's runtime {bundled}.");
        Assert.Equal(runtime.Major, linker.Major);
    }

    [Theory]
    [InlineData("Release", "x64", null, null, true)]
    [InlineData("Release", "x86", null, null, true)]
    [InlineData("Release", "x86", "false", null, false)]
    [InlineData("Debug", "x86", null, null, false)]
    [InlineData("Release", null, null, null, false)]
    [InlineData("Release", "x86", null, "prepared-linker", false)]
    public void PinnedLinkerPack_IsRestoredOnlyByAPlainTrimmedProfilePublish(
        string configuration, string? architecture, string? trimOverride, string? preparedLinker, bool expected)
    {
        // CI and a developer publish with the profile alone; build/package.ps1 prepares the linker
        // itself, and ordinary builds and tests never ask for the pack.
        using JsonDocument evaluation = Evaluate(configuration, architecture, trimOverride, preparedLinker, "-getItem:PackageDownload");
        string[] downloads = evaluation.RootElement.GetProperty("Items").GetProperty("PackageDownload").EnumerateArray()
            .Select(item => item.GetProperty("Identity").GetString() + " " + item.GetProperty("Version").GetString()).ToArray();
        if (expected) Assert.Equal(["Microsoft.NET.ILLink.Tasks [10.0.11]"], downloads);
        else Assert.Empty(downloads);
    }

    private static Dictionary<string, string> EvaluateProperties(string configuration, string? architecture, string? trimOverride)
    {
        using JsonDocument json = Evaluate(configuration, architecture, trimOverride, null,
            "-getProperty:MyCaptureTrimWindowsProjection,PublishTrimmed,SelfContained,PublishReadyToRun,PublishSingleFile,RuntimeIdentifier");
        return json.RootElement.GetProperty("Properties").EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.GetString() ?? string.Empty, StringComparer.Ordinal);
    }

    private static JsonDocument Evaluate(string configuration, string? architecture, string? trimOverride, string? preparedLinker, string query)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = RepositoryRoot(), UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
        };
        // The packaging script exports this variable; an inherited value must not decide the result.
        if (preparedLinker is null) start.Environment.Remove("MYCAPTURE_PROJECTION_LINKER_PATH");
        else start.Environment["MYCAPTURE_PROJECTION_LINKER_PATH"] = preparedLinker;
        start.ArgumentList.Add("msbuild");
        start.ArgumentList.Add(PathFor("src/MyCapture.App/MyCapture.App.csproj"));
        start.ArgumentList.Add("-nologo");
        start.ArgumentList.Add("-p:Configuration=" + configuration);
        if (architecture is not null) start.ArgumentList.Add("-p:PublishProfile=win-" + architecture + "-self-contained");
        if (trimOverride is not null) start.ArgumentList.Add("-p:MyCaptureTrimWindowsProjection=" + trimOverride);
        start.ArgumentList.Add(query);
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
        return JsonDocument.Parse(output);
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
