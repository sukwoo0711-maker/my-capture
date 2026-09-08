using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using Xunit;

namespace MyCapture.App.Tests;

internal static class InstallerSessionContract
{
    internal static async Task AssertAcceptedAsync(string root, string sessionPath, [CallerFilePath] string source = "")
    {
        // Execute the actual installer AST statement, with no extraction/install entry point.
        // Paths are data in the child environment, never interpolated into the command.
        const string command = """
            $ErrorActionPreference = 'Stop'
            $tokens = $null; $errors = $null
            $ast = [System.Management.Automation.Language.Parser]::ParseFile($env:MYCAPTURE_TEST_INSTALLER, [ref]$tokens, [ref]$errors)
            if ($errors.Count) { throw 'Installer parse failed.' }
            $guards = @($ast.FindAll({ param($node)
                $node -is [System.Management.Automation.Language.IfStatementAst] -and
                $node.Extent.Text.StartsWith('if (-not [string]::Equals((Split-Path -Parent $sessionDirectory)') -and
                $node.Extent.Text.Contains("'Invalid updater session path.'")
            }, $true))
            if ($guards.Count -ne 1) { throw 'Expected exactly one installer session path guard.' }
            function Throw-InstallerError($code, $message) { throw $message }
            $ExitUnsafePath = 10
            $updateRoot = $env:MYCAPTURE_TEST_UPDATE_ROOT
            $sessionPath = $env:MYCAPTURE_TEST_SESSION_PATH
            $sessionDirectory = Split-Path -Parent $sessionPath
            $guard = [scriptblock]::Create($guards[0].Extent.Text)
            & $guard
            # Prove that this is an effective validator, including the previously regressed name.
            $sessionDirectory = Join-Path $updateRoot ('update-' + ('a' * 32))
            $rejected = $false
            try { & $guard } catch { $rejected = $true }
            if (-not $rejected) { throw 'Installer accepted the versionless regression.' }
            Write-Output 'PATH_GUARD_PASS'
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
        info.Environment["MYCAPTURE_TEST_INSTALLER"] = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../../build/installer/install.ps1"));
        info.Environment["MYCAPTURE_TEST_UPDATE_ROOT"] = root;
        info.Environment["MYCAPTURE_TEST_SESSION_PATH"] = sessionPath;
        using var process = Process.Start(info) ?? throw new IOException("Installer contract process failed to start.");
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await process.WaitForExitAsync(timeout.Token);
            Assert.True(process.ExitCode == 0, await error);
            Assert.Contains("PATH_GUARD_PASS", await output, StringComparison.Ordinal);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }
}
