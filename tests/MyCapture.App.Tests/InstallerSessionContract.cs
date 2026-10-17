using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using MyCapture.App.Updates;
using Xunit;

namespace MyCapture.App.Tests;

internal static class InstallerSessionContract
{
    internal static async Task AssertAcceptedAsync(string root, string sessionPath, UpdateVersion version, [CallerFilePath] string source = "")
    {
        string configuration = typeof(InstallerSessionContract).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration;
        string setup = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../../src/MyCapture.Setup/bin",
            configuration, "net48/MyCaptureSetup.exe"));
        Assert.True(File.Exists(setup), $"Build the current net48 installer before running its contract: {setup}");
        string installRoot = Path.Combine(Path.GetDirectoryName(root)!, "install");
        string token = Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(sessionPath, JsonSerializer.Serialize(new
        {
            Token = token, Version = version.ToNormalizedString(), InstallRoot = installRoot, ParentId = int.MaxValue,
        }));
        // Windows PowerShell hosts the shipped net48 assembly on .NET Framework. Invoke only
        // validation and receipt serialization: no installer entry point, process stop or shell writes.
        const string command = """
            $ErrorActionPreference = 'Stop'
            [void][Reflection.Assembly]::LoadFrom($env:MYCAPTURE_TEST_SETUP)
            $manifest = [MyCapture.Setup.PackageManifest]::new()
            $manifest.Version = $env:MYCAPTURE_TEST_VERSION
            $file = [MyCapture.Setup.FileRecord]::new(); $file.Path = 'MyCapture.exe'
            $manifest.Files = @($file)
            $options = [MyCapture.Setup.Options]::Parse(@('/quiet', '--no-shell-integration', '--install-root', $env:MYCAPTURE_TEST_INSTALL_ROOT), $env:MYCAPTURE_TEST_UPDATE_ROOT)
            $log = [MyCapture.Setup.SetupLog]::new((Join-Path $env:MYCAPTURE_TEST_UPDATE_ROOT 'contract.log'), $env:MYCAPTURE_TEST_UPDATE_ROOT, $true, $false)
            $installer = [MyCapture.Setup.Installer]::new($options, $env:MYCAPTURE_TEST_UPDATE_ROOT, $log)
            $validate = [MyCapture.Setup.Installer].GetMethod('ValidateUpdate', [Reflection.BindingFlags]'Instance,NonPublic')
            if ($null -eq $validate) { throw 'Current installer session validation method is missing.' }
            function Validate-Session([string]$path, [int]$expected) {
                $env:MYCAPTURE_UPDATE_SESSION = $path
                $actual = 0
                try { [void]$validate.Invoke($installer, @($manifest)) }
                catch {
                    $failure = $_.Exception.GetBaseException()
                    if ($failure -isnot [MyCapture.Setup.SetupException]) { throw }
                    $actual = $failure.Code
                }
                if ($actual -ne $expected) { throw "Session validation expected $expected, got $actual for $path" }
            }
            $session = $env:MYCAPTURE_TEST_SESSION_PATH
            Validate-Session $session 0
            $root = $env:MYCAPTURE_TEST_UPDATE_ROOT
            foreach ($name in @(('update-' + ('a' * 32)), ('update-4.2.1-win10.8-' + ('a' * 32)), ('update-4.2.1-' + ('A' * 32)))) {
                Validate-Session (Join-Path (Join-Path $root $name) 'update-session.json') 12
            }
            Validate-Session (Join-Path $root 'update-session.json') 12
            Validate-Session (Join-Path (Split-Path -Parent $session) 'other.json') 12
            $original = [IO.File]::ReadAllText($session)
            foreach ($fault in @('Token', 'Version', 'ParentId')) {
                $candidate = $original | ConvertFrom-Json
                if ($fault -eq 'Token') { $candidate.Token = 'bad' }
                if ($fault -eq 'Version') { $candidate.Version = '0.0.0' }
                if ($fault -eq 'ParentId') { $candidate.ParentId = 0 }
                [IO.File]::WriteAllText($session, ($candidate | ConvertTo-Json))
                Validate-Session $session 14
            }
            [IO.File]::WriteAllText($session, $original)
            Validate-Session $session 0
            $receipt = [MyCapture.Setup.InstallReceipt]::new()
            $receipt.Token = $env:MYCAPTURE_TEST_TOKEN; $receipt.Version = $manifest.Version
            $receipt.ExitCode = 17; $receipt.InstallRoot = $env:MYCAPTURE_TEST_INSTALL_ROOT; $receipt.LogPath = $log.Path
            $receiptPath = Join-Path (Split-Path -Parent $session) 'install-result.json'
            $receipt.Write($receiptPath)
            Validate-Session $session 12
            Write-Output 'SETUP_CONTRACT_PASS'
            """;
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(command)) })
            info.ArgumentList.Add(argument);
        info.Environment["MYCAPTURE_TEST_SETUP"] = setup;
        info.Environment["MYCAPTURE_TEST_UPDATE_ROOT"] = root;
        info.Environment["MYCAPTURE_TEST_SESSION_PATH"] = sessionPath;
        info.Environment["MYCAPTURE_TEST_INSTALL_ROOT"] = installRoot;
        info.Environment["MYCAPTURE_TEST_VERSION"] = version.ToNormalizedString();
        info.Environment["MYCAPTURE_TEST_TOKEN"] = token;
        using var process = Process.Start(info) ?? throw new IOException("Installer contract process failed to start.");
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await process.WaitForExitAsync(timeout.Token);
            Assert.True(process.ExitCode == 0, await error);
            Assert.Contains("SETUP_CONTRACT_PASS", await output, StringComparison.Ordinal);
            using JsonDocument receipt = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(Path.GetDirectoryName(sessionPath)!, "install-result.json")));
            Assert.Equal(token, receipt.RootElement.GetProperty("Token").GetString());
            Assert.Equal(version.ToNormalizedString(), receipt.RootElement.GetProperty("Version").GetString());
            Assert.Equal(17, receipt.RootElement.GetProperty("ExitCode").GetInt32());
            Assert.Equal(installRoot, receipt.RootElement.GetProperty("InstallRoot").GetString());
            Assert.Equal(Path.Combine(root, "contract.log"), receipt.RootElement.GetProperty("LogPath").GetString());
            // The package cleanup owns downloads/session input, not the installer's receipt.
            TestRecycleBin.DeleteFile(Path.Combine(Path.GetDirectoryName(sessionPath)!, "install-result.json"));
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }
}
