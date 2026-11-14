using System.Diagnostics;
using System.Xml.Linq;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class ReleaseSizeBudgetContractTests
{
    private static string Root()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Directory.Build.props"))) return directory.FullName;
        throw new InvalidOperationException("Repository root not found.");
    }

    [Theory]
    [InlineData("win-x64")]
    [InlineData("win-x86")]
    [InlineData("win-arm64")]
    public void ReleaseBudget_HasPositiveInstalledPortableAndProjectionLimits(string runtime)
    {
        var document = XDocument.Load(Path.Combine(Root(), "build/ReleaseSizeBudgets.props"));
        var group = Assert.Single(document.Root!.Elements("PropertyGroup"),
            value => value.Attribute("Condition")?.Value == $"'$(RuntimeIdentifier)' == '{runtime}'");
        long installed = long.Parse(Assert.Single(group.Elements("MyCaptureInstalledSizeBudgetBytes")).Value);
        long portable = long.Parse(Assert.Single(group.Elements("MyCapturePortableSizeBudgetBytes")).Value);
        long projection = long.Parse(Assert.Single(group.Elements("MyCaptureProjectionSizeBudgetBytes")).Value);
        Assert.InRange(projection, 1, 40_000_000); // Every full SDK projection exceeds 50 MB.
        Assert.InRange(portable, 1, installed);
        Assert.True(projection < installed);
    }

    [Theory]
    [InlineData("MyCaptureInstalledSizeBudgetBytes", "installed-size")]
    [InlineData("MyCaptureProjectionSizeBudgetBytes", "Windows projection")]
    public async Task ReleasePublish_ActuallyFailsWhenItsBudgetIsLowered(string property, string diagnostic)
    {
        // Exercise the actual AfterTargets=Publish task, not a reimplementation of its arithmetic.
        // A tiny payload keeps this contract test independent of runtime packs and network restore.
        string fixture = Directory.CreateTempSubdirectory("mycapture-release-budget-").FullName;
        string publish = Directory.CreateDirectory(Path.Combine(fixture, "publish")).FullName;
        File.WriteAllBytes(Path.Combine(publish, "Microsoft.Windows.SDK.NET.dll"), new byte[64]);
        File.WriteAllBytes(Path.Combine(publish, "payload.bin"), new byte[128]);
        string harness = Path.Combine(fixture, "budget.proj");
        new XDocument(new XElement("Project",
            new XElement("PropertyGroup",
                new XElement("Configuration", "Release"),
                new XElement("RuntimeIdentifier", "win-x64"),
                new XElement("SelfContained", "true"),
                new XElement("MyCaptureTrimWindowsProjection", "true"),
                new XElement("PublishDir", publish)),
            new XElement("Import", new XAttribute("Project", Path.Combine(Root(), "build/ReleasePackaging.targets"))),
            new XElement("Target", new XAttribute("Name", "CopyModelsPublish")),
            new XElement("Target", new XAttribute("Name", "Publish")))).Save(harness);

        var success = await Run(harness);
        Assert.True(success.ExitCode == 0, success.Output);
        var failure = await Run(harness, $"-p:{property}=1");
        Assert.NotEqual(0, failure.ExitCode);
        Assert.Contains(diagnostic + " budget exceeded", failure.Output, StringComparison.Ordinal);
        Assert.Contains("deliberately update build/ReleaseSizeBudgets.props", failure.Output, StringComparison.Ordinal);
        // Preserve this small fixture as diagnostic evidence under the host-managed temporary root.
    }

    [Fact]
    public void ReleasePackaging_ChecksInstalledAndPortableBudgetsBeforeInstallerConstruction()
    {
        string package = File.ReadAllText(Path.Combine(Root(), "build/package.ps1"));
        string store = File.ReadAllText(Path.Combine(Root(), "build/package-store.ps1"));
        string check = "Assert-ReleaseSizeBudget -PublishRoot $publish -Architecture $Architecture -PortableZip $portable";
        Assert.Contains("Assert-ReleaseSizeBudget -PublishRoot $publish -Architecture $Architecture", package, StringComparison.Ordinal);
        Assert.Contains(check, package, StringComparison.Ordinal);
        Assert.True(package.IndexOf(check, StringComparison.Ordinal) < package.IndexOf("$payload = Join-Path $stage", StringComparison.Ordinal));
        Assert.Contains("Assert-ReleaseSizeBudget -PublishRoot $PublishDir -Architecture x64", store, StringComparison.Ordinal);
        string script = File.ReadAllText(Path.Combine(Root(), "build/release-size-budget.ps1"));
        Assert.Contains("ReleaseSizeBudgets.props", script, StringComparison.Ordinal);
        Assert.Contains("$zip.Length -gt $limits.MyCapturePortableSizeBudgetBytes", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReleasePublish_RejectsStaleDebuggerFiles()
    {
        string fixture = Directory.CreateTempSubdirectory("mycapture-debugger-budget-").FullName;
        File.WriteAllBytes(Path.Combine(fixture, "Microsoft.Windows.SDK.NET.dll"), new byte[64]);
        string harness = Path.Combine(fixture, "debuggers.proj");
        new XDocument(new XElement("Project",
            new XElement("Import", new XAttribute("Project", Path.Combine(Root(), "build/ReleasePackaging.targets"))),
            new XElement("Target", new XAttribute("Name", "Check"),
                new XElement("CheckMyCapturePublishSize", new XAttribute("PublishRoot", fixture),
                    new XAttribute("Runtime", "win-x64"), new XAttribute("InstalledLimit", "1000000"),
                    new XAttribute("ProjectionLimit", "1000000"))))).Save(harness);
        foreach (string name in new[] { "mscordaccore.dll", "mscordaccore_amd64_amd64_10.0.1226.42308.dll", "mscordbi.dll", "Microsoft.DiaSymReader.Native.amd64.dll",
            "mscordaccore_arm64_arm64_10.0.1226.42308.dll", "Microsoft.DiaSymReader.Native.arm64.dll" })
        {
            File.WriteAllBytes(Path.Combine(fixture, name), new byte[1]);
            var failure = await Run(harness, "-t:Check");
            Assert.NotEqual(0, failure.ExitCode);
            Assert.Contains("Debugger-only file remains", failure.Output, StringComparison.Ordinal);
            Assert.Contains(name, failure.Output, StringComparison.Ordinal);
        }
    }

    private static async Task<(int ExitCode, string Output)> Run(string project, string? argument = null)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Root(), UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
        };
        string target = argument?.StartsWith("-t:", StringComparison.Ordinal) == true ? argument : "-t:Publish";
        foreach (string value in new[] { "msbuild", project, target, "-m:1", "-nr:false", "-nologo" }) start.ArgumentList.Add(value);
        if (argument is not null && argument != target) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("MSBuild did not start.");
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> errors = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
        }
        catch (TimeoutException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw;
        }
        return (process.ExitCode, await output + await errors);
    }
}
