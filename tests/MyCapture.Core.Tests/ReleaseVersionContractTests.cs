using System;
using System.IO;
using System.Xml.Linq;
using Xunit;

namespace MyCapture.Core.Tests;

/// <summary>
/// Keeps the 5.4.0 stable version and its Windows binary version aligned across MSBuild, packaging
/// and installer validation.
/// </summary>
public sealed class ReleaseVersionContractTests
{
    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Directory.Build.props"))
                && Directory.Exists(Path.Combine(dir.FullName, "src"))
                && Directory.Exists(Path.Combine(dir.FullName, "build")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            $"Repository root was not found above '{AppContext.BaseDirectory}'.");
    }

    private static string Read(string relativePath) => File.ReadAllText(
        Path.Combine(RepositoryRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar)));

    [Fact]
    public void BuildDefaults_AreTheStableReleaseBinaryVersion()
    {
        XDocument props = XDocument.Parse(Read("Directory.Build.props"));
        XDocument manifest = XDocument.Parse(Read("src/MyCapture.App/app.manifest"));

        Assert.Equal("5.4.0", Assert.Single(props.Descendants("Version")).Value);
        Assert.Equal("5.4.0.0", Assert.Single(props.Descendants("FileVersion")).Value);
        Assert.Equal("5.4.0.0", Assert.Single(props.Descendants("AssemblyVersion")).Value);

        XElement identity = Assert.Single(
            manifest.Descendants(),
            element => element.Name.LocalName == "assemblyIdentity");
        Assert.Equal("5.4.0.0", identity.Attribute("version")?.Value);
    }

    [Fact]
    public void PackageAndInstaller_DefaultToTheGaVersion_AndStillAcceptAPrerelease()
    {
        string package = Read("build/package.ps1");
        string installer = Read("src/MyCapture.Setup/Contracts.cs");
        string hostile = Read("build/tests/installer-hostile-tests.ps1");

        // The shipping default is the GA version. A release candidate must never be the default
        // again, otherwise an operator who forgets -Version publishes a prerelease by accident.
        Assert.Contains("[string]$Version = '5.4.0'", package, StringComparison.Ordinal);
        Assert.Contains("[string]$Version = '5.4.0',", hostile, StringComparison.Ordinal);
        Assert.DoesNotContain("$Version = '1.7.0-rc.1'", package, StringComparison.Ordinal);
        Assert.DoesNotContain("$Version = '1.7.0-rc.1'", hostile, StringComparison.Ordinal);

        // A prerelease must still parse, so a future RC needs no script edit. The Windows binary
        // version stays numeric because Win32 version resources cannot carry a prerelease label.
        Assert.Contains("$semVerPattern", package, StringComparison.Ordinal);
        Assert.Contains("$baseVersion", package, StringComparison.Ordinal);
        Assert.Contains("$binaryVersion = \"$baseVersion.0\"", package, StringComparison.Ordinal);
        Assert.Contains("does not match the release version declared in Directory.Build.props", package, StringComparison.Ordinal);
        Assert.Contains("SemVer core", package, StringComparison.Ordinal);
        Assert.DoesNotContain("numeric SemVer", package, StringComparison.Ordinal);

        Assert.Contains("SemVerPattern", installer, StringComparison.Ordinal);
        Assert.Contains("$versionMatch", hostile, StringComparison.Ordinal);
        Assert.Contains("$baseVersion", hostile, StringComparison.Ordinal);
        Assert.Contains("$binaryVersion", hostile, StringComparison.Ordinal);
        Assert.Contains("Assert-Equal $baseVersion $sourceVersion", hostile, StringComparison.Ordinal);
        Assert.Contains("Assert-Equal $binaryVersion $sourceFileVersion", hostile, StringComparison.Ordinal);
        Assert.Contains("Assert-Equal $Version (($dllVersionInfo.ProductVersion -split '\\+')[0])", hostile, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseManifest_RecordsTheExactGitSourceCommit()
    {
        string package = Read("build/package.ps1");

        Assert.Contains("rev-parse --verify 'HEAD^{commit}'", package, StringComparison.Ordinal);
        Assert.Contains("-p:SourceRevisionId=$sourceCommit", package, StringComparison.Ordinal);
        Assert.Contains("SchemaVersion = 3", package, StringComparison.Ordinal);
        Assert.Contains("SourceCommit = $sourceCommit", package, StringComparison.Ordinal);
        Assert.Contains("SourceTreeClean = $sourceTreeClean", package, StringComparison.Ordinal);
        Assert.Contains("releaseRoundTrip.SourceCommit", package, StringComparison.Ordinal);
        Assert.Contains("New-FileRecord $releaseManifestOutput", package, StringComparison.Ordinal);
        Assert.Contains("Refusing to package a dirty Git worktree", package, StringComparison.Ordinal);
        Assert.Contains("Release source changed during packaging", package, StringComparison.Ordinal);
    }

    [Fact]
    public void Package_IncludesProjectAndDotNetLicenseNotices()
    {
        string package = Read("build/package.ps1");
        string notices = Read("THIRD-PARTY-NOTICES.md");

        Assert.Contains("$projectLicense = Join-Path $repo 'LICENSE'", package, StringComparison.Ordinal);
        Assert.Contains("$thirdPartyIndex = Join-Path $repo 'THIRD-PARTY-NOTICES.md'", package, StringComparison.Ordinal);
        Assert.Contains("DOTNET-LICENSE.txt", package, StringComparison.Ordinal);
        Assert.Contains("DOTNET-THIRD-PARTY-NOTICES.txt", package, StringComparison.Ordinal);
        Assert.Contains("Microsoft.Extensions.DependencyInjection 10.0.11", notices, StringComparison.Ordinal);
        Assert.Contains("xUnit.net 2.9.3", notices, StringComparison.Ordinal);
        Assert.Contains("does not bundle FFmpeg", notices, StringComparison.Ordinal);
    }

    [Fact]
    public void BrandIcons_ShipEveryWindowsAndTrayResolution()
    {
        int[] expectedSizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
        string assets = Path.Combine(RepositoryRoot(), "src", "MyCapture.App", "Assets");

        foreach (string fileName in new[]
                 {
                     "app.ico",
                     "tray-idle.ico",
                     "tray-capturing.ico",
                     "tray-busy.ico",
                     "tray-error.ico",
                 })
        {
            byte[] bytes = File.ReadAllBytes(Path.Combine(assets, fileName));
            Assert.True(bytes.Length > 6, $"{fileName} is not a complete ICO file.");
            Assert.Equal((ushort)0, BitConverter.ToUInt16(bytes, 0));
            Assert.Equal((ushort)1, BitConverter.ToUInt16(bytes, 2));
            int count = BitConverter.ToUInt16(bytes, 4);
            Assert.Equal(expectedSizes.Length, count);

            int[] actualSizes = Enumerable.Range(0, count)
                .Select(index => bytes[6 + (index * 16)] is 0
                    ? 256
                    : bytes[6 + (index * 16)])
                .ToArray();
            Assert.Equal(expectedSizes, actualSizes);

            for (int index = 0; index < count; index++)
            {
                int entry = 6 + (index * 16);
                uint length = BitConverter.ToUInt32(bytes, entry + 8);
                uint offset = BitConverter.ToUInt32(bytes, entry + 12);
                Assert.True(length > 0, $"{fileName} has an empty {actualSizes[index]} px entry.");
                Assert.True(offset + length <= bytes.Length,
                    $"{fileName} has an out-of-range {actualSizes[index]} px entry.");
            }
        }

        string package = Read("build/package.ps1");
        string installer = Read("src/MyCapture.Setup/Payload.cs");
        Assert.Contains("'tray-error.ico'", package, StringComparison.Ordinal);
        Assert.Contains("\"tray-error.ico\"", installer, StringComparison.Ordinal);
    }

    [Fact]
    public void PackagedSelfTestRunner_RequiresExitZeroAndAnExactPassFromAllEightTests()
    {
        string runner = Read("build/run-packaged-self-tests.ps1");
        string definition = Read("build/packaged-validation.ps1");

        Assert.Contains(". (Join-Path $PSScriptRoot 'packaged-validation.ps1')", runner, StringComparison.Ordinal);
        Assert.Contains("$tests = @(Get-PackagedSelfTests)", runner, StringComparison.Ordinal);
        Assert.Contains("function Get-PackagedSelfTests", definition, StringComparison.Ordinal);

        string[] switches =
        [
            "--selftest-capture",
            "--selftest-shell",
            "--selftest-advanced",
            "--selftest-settings",
            "--selftest-ocr",
            "--selftest-recording",
            "--selftest-video-editor",
            "--selftest-hotfix-ux",
        ];

        foreach (string commandLineSwitch in switches)
        {
            Assert.Contains(commandLineSwitch, definition, StringComparison.Ordinal);
        }

        Assert.Contains("$exitCode -ne 0", runner, StringComparison.Ordinal);
        Assert.Contains("$processHandle = $process.Handle", runner, StringComparison.Ordinal);
        Assert.Contains("$null -eq $process.ExitCode", runner, StringComparison.Ordinal);
        Assert.Contains("[string]::Equals($resultLines[0], 'RESULT: PASS', [StringComparison]::Ordinal)", runner, StringComparison.Ordinal);
        Assert.Contains("$resultLines.Count -eq 1", runner, StringComparison.Ordinal);
        Assert.Contains("$passCount -ne $tests.Count", runner, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionExtensions_AreAlignedWithTheNetTenServicingLine()
    {
        string packages = Read("Directory.Packages.props");

        foreach (string package in new[]
                 {
                     "Microsoft.Extensions.DependencyInjection",
                     "Microsoft.Extensions.Logging",
                     "Microsoft.Extensions.Logging.Debug",
                 })
        {
            Assert.Contains($"Include=\"{package}\" Version=\"10.0.11\"", packages, StringComparison.Ordinal);
        }

        Assert.Contains("Include=\"xunit\" Version=\"2.9.3\"", packages, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallerPayload_HashesAndShipsTheBilingualStartupHelp()
    {
        string package = Read("build/package.ps1");
        string installer = Read("src/MyCapture.Setup/Contracts.cs");
        string[] names =
        [
            "MyCaptureSetup.exe", "MyCaptureSetup.exe.config", "SETUP-HELP.txt",
        ];
        string packagedNames = package.Split("$bootstrapNames = @(", StringSplitOptions.None)[1]
            .Split(')')[0];
        foreach (string name in names)
        {
            Assert.Contains($"'{name}'", packagedNames, StringComparison.Ordinal);
            Assert.Contains($"\"{name}\"", installer, StringComparison.Ordinal);
        }

        Assert.Contains("%FILE4%=", package, StringComparison.Ordinal);
        Assert.Contains("FILE4=SETUP-HELP.txt", package, StringComparison.Ordinal);
        byte[] helpBytes = File.ReadAllBytes(Path.Combine(
            RepositoryRoot(), "build", "installer", "SETUP-HELP.txt"));
        Assert.True(helpBytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }));
        string help = Read("build/installer/SETUP-HELP.txt");
        Assert.StartsWith("MyCapture 설치 도움말", help, StringComparison.Ordinal);
        Assert.Contains("MyCapture Setup help", help, StringComparison.Ordinal);
        Assert.Contains("MyCapture-<version>-win-x64-portable.zip", help, StringComparison.Ordinal);
        Assert.Contains("https://apps.microsoft.com/detail/9PB33MC095VR", help, StringComparison.Ordinal);
        Assert.Contains("%TEMP%\\MyCapture-Install-", help, StringComparison.Ordinal);
        Assert.Contains(".NET Framework 4.8", package, StringComparison.Ordinal);
        Assert.Contains("AppLocker or WDAC", package, StringComparison.Ordinal);
        Assert.Contains("Exit 22 is retired", package, StringComparison.Ordinal);
        Assert.Contains("%TEMP%\\MyCapture-Setup-Help.txt", package, StringComparison.Ordinal);
        Assert.Contains("AppLaunched=MyCaptureSetup.exe", package, StringComparison.Ordinal);
        Assert.Contains("AdminQuietInstCmd=MyCaptureSetup.exe /quiet", package, StringComparison.Ordinal);
        Assert.Contains("UserQuietInstCmd=MyCaptureSetup.exe /quiet", package, StringComparison.Ordinal);
        Assert.DoesNotContain("install.cmd", package, StringComparison.Ordinal);
        Assert.DoesNotContain("install.ps1", package, StringComparison.Ordinal);
        string setupRoot = Path.Combine(RepositoryRoot(), "src", "MyCapture.Setup");
        foreach (string source in Directory.EnumerateFiles(setupRoot, "*.cs", SearchOption.TopDirectoryOnly))
        {
            string text = File.ReadAllText(source);
            foreach (string forbidden in new[] { "powershell.exe", "pwsh", "cmd.exe", "-EncodedCommand", "-Command", "WScript.Shell" })
                Assert.DoesNotContain(forbidden, text, StringComparison.OrdinalIgnoreCase);
        }
        foreach (string forbidden in new[] { "install.ps1", "install.cmd", "uninstall.ps1", "uninstall.cmd", "uninstall-cleanup.ps1" })
            Assert.DoesNotContain(forbidden, packagedNames, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallerBootstrapSimulation_RunsThroughTheCheckedWorkflowHelper()
    {
        string workflow = Read(".github/workflows/packaged-validation.yml");
        Assert.True(File.Exists(Path.Combine(
            RepositoryRoot(), "build", "tests", "installer-bootstrap-tests.ps1")));
        Assert.Contains(
            @"Invoke-CheckedTest .\build\tests\installer-bootstrap-tests.ps1",
            workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void PackagedValidation_RunsForEveryReleaseTagAndOnDemand()
    {
        // It ran only for a retired v1.8.0 branch, so no release from 4.0.0 to 4.5.2 was validated.
        string workflow = Read(".github/workflows/packaged-validation.yml").Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Contains("  push:\n    tags:\n      - 'v*'\n  workflow_dispatch:\n", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("branches:", workflow, StringComparison.Ordinal);
        Assert.Contains("$env:GITHUB_REF_NAME -cne \"v$version\"", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void PublishedGitHubReleases_MustIncludeTheTwelveRequiredX64AndX86Assets()
    {
        string required = Read("build/required-release-assets.ps1");
        string verify = Read("build/verify-github-release.ps1");
        string publish = Read("build/publish-github-release.ps1");
        string workflow = Read(".github/workflows/release-assets.yml");
        string packaged = Read(".github/workflows/packaged-validation.yml");

        foreach (string name in new[]
                 {
                     "MyCapture-$Version-win-x64-setup.exe",
                     "MyCapture-$Version-win-x64-portable.zip",
                     "installer-manifest.json",
                     "release-manifest.json",
                     "SHA256SUMS.txt",
                     "README-OFFLINE.txt",
                     "MyCapture-$Version-win-x86-setup.exe",
                     "MyCapture-$Version-win-x86-portable.zip",
                     "installer-manifest-win-x86.json",
                     "release-manifest-win-x86.json",
                     "SHA256SUMS-win-x86.txt",
                     "README-OFFLINE-win-x86.txt",
                     // Required only when a release carries the native ARM64 build (-IncludeArm64).
                     "MyCapture-$Version-win-arm64-setup.exe",
                     "MyCapture-$Version-win-arm64-portable.zip",
                     "installer-manifest-win-arm64.json",
                     "release-manifest-win-arm64.json",
                     "SHA256SUMS-win-arm64.txt",
                     "README-OFFLINE-win-arm64.txt",
                 })
        {
            Assert.Contains(name, required, StringComparison.Ordinal);
        }

        Assert.Contains("Get-RequiredReleaseAssetNames", verify, StringComparison.Ordinal);
        Assert.Contains("Get-RequiredReleaseAssetNames -Version $version -IncludeX86:$IncludeX86", verify, StringComparison.Ordinal);
        Assert.Contains("Get-RequiredReleaseAssetNames -Version $Version -IncludeX86:$IncludeX86", publish, StringComparison.Ordinal);
        Assert.Contains("-Tag $tag -Repository $Repository -IncludeX86:$IncludeX86", publish, StringComparison.Ordinal);
        Assert.Contains("-IncludeX86:$IncludeX86 -IncludeArm64:$IncludeArm64)", verify, StringComparison.Ordinal);
        Assert.Contains("-IncludeX86:$IncludeX86 -IncludeArm64:$IncludeArm64)", publish, StringComparison.Ordinal);
        Assert.Contains("-Tag $tag -Repository $Repository -IncludeX86:$IncludeX86 -IncludeArm64:$IncludeArm64", publish, StringComparison.Ordinal);
        Assert.Contains("Join-Path $artifactRoot 'win-arm64'", publish, StringComparison.Ordinal);
        Assert.Contains("releases/tags/", verify, StringComparison.Ordinal);
        Assert.Contains("NO_COLOR", verify, StringComparison.Ordinal);
        Assert.Contains(".draft", verify, StringComparison.Ordinal);
        Assert.Contains("missing required assets", verify, StringComparison.Ordinal);
        Assert.Contains("Refusing to publish", publish, StringComparison.Ordinal);
        Assert.Contains("verify-github-release.ps1", publish, StringComparison.Ordinal);

        Assert.Contains("release:", workflow.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("types: [published]", workflow, StringComparison.Ordinal);
        Assert.Contains("verify-github-release.ps1", workflow, StringComparison.Ordinal);
        string verificationCommand = Assert.Single(
            workflow.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries),
            line => line.TrimStart().StartsWith(@".\build\verify-github-release.ps1 ", StringComparison.Ordinal));
        Assert.EndsWith(" -IncludeX86", verificationCommand.Trim(), StringComparison.Ordinal);
        Assert.Contains("name: Verify twelve required GitHub release assets (x64 and x86)", workflow, StringComparison.Ordinal);
        Assert.Contains("permissions:\n  contents: read", workflow.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.DoesNotContain(": write", workflow, StringComparison.Ordinal);
        Assert.Contains("actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1", workflow, StringComparison.Ordinal);

        Assert.Contains("MyCapture-${{ env.PACKAGE_VERSION }}-win-x64-setup.exe", packaged, StringComparison.Ordinal);
        Assert.Contains("SHA256SUMS.txt", packaged, StringComparison.Ordinal);
        Assert.Contains("release-manifest.json", packaged, StringComparison.Ordinal);
    }

    [Fact]
    public void ContinuousIntegration_UsesOnlyImmutableActionsAndLeastTokenPermissions()
    {
        string workflow = Read(".github/workflows/ci.yml");
        string dependabot = Read(".github/dependabot.yml");

        string[] actions = workflow
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.TrimStart().StartsWith("uses:", StringComparison.Ordinal))
            .ToArray();
        // Four jobs (build and tests, x86, native ARM64, Recycle Bin canary), each with the same three actions.
        Assert.Equal(12, actions.Length);
        Assert.Equal(
            Enumerable.Repeat(new[]
            {
                "actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1",
                "actions/setup-dotnet@a98b56852c35b8e3190ac28c8c2271da59106c68",
                "actions/upload-artifact@cf430e030ddbb5b0abf93d22962f4752f3646cd9",
            }, 4).SelectMany(job => job).ToArray(),
            actions.Select(action => action.Trim()["uses:".Length..]
                .Split([' ', '#'], StringSplitOptions.RemoveEmptyEntries)[0]).ToArray());
        foreach (string action in actions)
        {
            int at = action.IndexOf('@');
            Assert.True(at >= 0, $"Action is not pinned: {action}");
            string sha = action[(at + 1)..].Split([' ', '#'], StringSplitOptions.RemoveEmptyEntries)[0];
            Assert.Equal(40, sha.Length);
            Assert.All(sha, character => Assert.True(Uri.IsHexDigit(character), $"Invalid action SHA: {sha}"));
        }

        Assert.DoesNotContain("uses: actions/checkout@v", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("uses: actions/setup-dotnet@v", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("uses: actions/upload-artifact@v", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("pull_request_target", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain(": write", workflow, StringComparison.Ordinal);
        Assert.Contains("permissions:\n  contents: read", workflow.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("timeout-minutes: 75", workflow, StringComparison.Ordinal);
        Assert.Contains("persist-credentials: false", workflow, StringComparison.Ordinal);

        Assert.Contains("package-ecosystem: nuget", dependabot, StringComparison.Ordinal);
        Assert.Contains("package-ecosystem: github-actions", dependabot, StringComparison.Ordinal);
        Assert.Contains("interval: weekly", dependabot, StringComparison.Ordinal);
        Assert.Contains("version-update:semver-patch", dependabot, StringComparison.Ordinal);
    }

    [Fact]
    public void ContinuousIntegration_RunsTheNativeArm64BuildOnAnArm64Runner()
    {
        string workflow = Read(".github/workflows/ci.yml").Replace("\r\n", "\n", StringComparison.Ordinal);
        int start = workflow.IndexOf("\n  arm64-native:\n", StringComparison.Ordinal);
        Assert.True(start >= 0, "The native ARM64 job is missing.");
        int end = workflow.IndexOf("\n  recycle-bin-canary:\n", start, StringComparison.Ordinal);
        string job = end > start ? workflow[start..end] : workflow[start..];

        // x64 runners can build arm64 but cannot start it; only this runner executes the result.
        Assert.Contains("runs-on: windows-11-arm", job, StringComparison.Ordinal);
        Assert.Contains("$env:PROCESSOR_ARCHITECTURE -cne 'ARM64'", job, StringComparison.Ordinal);
        foreach (string project in new[] { "MyCapture.Core.Tests", "MyCapture.App.Tests" })
        {
            Assert.Contains(
                $"dotnet test tests/{project}/{project}.csproj -c Release -p:Platform=arm64 --no-build",
                job, StringComparison.Ordinal);
        }

        // The setup is x64 in the arm64 package, so its tests stay x64 and run under emulation.
        Assert.Contains(
            "dotnet test tests/MyCapture.Setup.Tests/MyCapture.Setup.Tests.csproj -c Release --no-build",
            job, StringComparison.Ordinal);
        Assert.Contains(
            "-r win-arm64 --self-contained true -p:Platform=arm64 -p:PublishProfile=win-arm64-self-contained",
            job, StringComparison.Ordinal);
        Assert.Contains("Assert-PublishArchitecture -PublishRoot", job, StringComparison.Ordinal);
        Assert.Contains(
            @".\build\run-packaged-self-tests.ps1 -PublishRoot artifacts/ci-arm64/publish",
            job, StringComparison.Ordinal);

        // The whole suite runs natively. The single exclusion is named, and nothing may fail quietly.
        Assert.Equal(2, job.Split("FullyQualifiedName", StringSplitOptions.None).Length);
        Assert.Contains(
            "--filter \"FullyQualifiedName!~GitHubUpdateServiceTests.DownloadedSessionPassesActualInstallerPathGuard\"",
            job, StringComparison.Ordinal);
        Assert.DoesNotContain("continue-on-error", job, StringComparison.Ordinal);

        // A read-only desktop description runs before the packaged self-tests and the suites, so a
        // failure can be told apart from the image (render tier, software effects, foreground owner,
        // input locale, long paths and Recycle Bin, capture and encoder availability).
        int probe = job.IndexOf("dotnet run --file build/tools/runner-desktop-probe.cs", StringComparison.Ordinal);
        Assert.True(probe >= 0, "The ARM64 runner desktop probe is missing.");
        Assert.True(probe < job.IndexOf(@".\build\run-packaged-self-tests.ps1", StringComparison.Ordinal));
        // The image's own windows (sign-in prompt, the wsl.exe terminal, then the Search flyout) cover
        // every fixture window, so they are closed before the desktop is described and before
        // anything is put on screen. Only the two named processes are stopped, in one place; the
        // Search flyout is dismissed with Escape because its host restarts and shows it again.
        int prompt = job.IndexOf("Stop-Process -Id $prompt.Id", StringComparison.Ordinal);
        Assert.True(prompt >= 0 && prompt < probe, "The image's windows must be closed before the desktop is described.");
        Assert.Equal(3, job.Split("foreach ($name in 'WWAHost', 'WindowsTerminal')", StringSplitOptions.None).Length);
        Assert.Equal(2, job.Split("Stop-Process", StringSplitOptions.None).Length);
        int escape = job.IndexOf("SendKeys('{ESC}')", StringComparison.Ordinal);
        Assert.True(escape > prompt && escape < probe, "The Search flyout is dismissed after the prompt has gone and before the description.");
        Assert.Contains("tests/MyCapture.App.Tests/bin/arm64/Release/net10.0-windows10.0.22000.0", job[probe..], StringComparison.Ordinal);
        foreach (string evidence in new[] { "LongPathsEnabled", "BitBucket", "Get-WinUserLanguageList", "GraphicsCaptureSession", "WWAHost.exe" })
            Assert.Contains(evidence, job, StringComparison.Ordinal);
        string tool = Read("build/tools/runner-desktop-probe.cs");
        foreach (string property in new[] { "#:property PlatformTarget=AnyCPU", "#:property UseAppHost=false" })
            Assert.Contains(property, tool, StringComparison.Ordinal);
    }
}
