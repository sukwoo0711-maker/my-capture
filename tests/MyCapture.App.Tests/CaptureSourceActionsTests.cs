using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using MyCapture.App.Sources;
using MyCapture.Core.Capture;
using MyCapture.Core.Localization;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class CaptureSourceActionsTests
{
    [Fact]
    public async Task WebOpensTheFullValidatedAddressThroughInjectedLauncher()
    {
        var launched = new List<ProcessStartInfo>();
        var actions = Create(launched);
        CaptureProvenance source = CaptureProvenance.Create("https://example.com/page?token=kept#anchor");
        Assert.Empty(await actions.OpenAsync(source));
        ProcessStartInfo start = Assert.Single(launched);
        Assert.Equal(source.Url, start.FileName);
        Assert.True(start.UseShellExecute);
        Assert.Empty(await actions.OpenAsync(CaptureProvenance.Create("javascript:alert(1)")));
        Assert.Single(launched);
    }

    [Theory]
    [InlineData("budget.xlsx", true)]
    [InlineData("notes.txt", false)]
    [InlineData("script.cmd", false)]
    [InlineData("program.exe", false)]
    public async Task DocumentOpenKeepsExistingAssociationAndExplicitEditorRules(string name, bool shell)
    {
        string path = @"C:\Work\" + name;
        var launched = new List<ProcessStartInfo>();
        var actions = Create(launched, files: [path, @"C:\Windows\System32\notepad.exe"]);
        Assert.Empty(await actions.OpenAsync(CaptureProvenance.Create(documentPath: path)));
        ProcessStartInfo start = Assert.Single(launched);
        Assert.Equal(shell, start.UseShellExecute);
        Assert.Equal(shell ? path : @"C:\Windows\System32\notepad.exe", start.FileName);
        if (!shell) Assert.Equal(path, Assert.Single(start.ArgumentList));
    }

    [Fact]
    public async Task MissingDocumentReportsFailureAndOnlyExplicitRecoveryOpensExistingParent()
    {
        using var language = UiText.UseLanguage("ko");
        var launched = new List<ProcessStartInfo>();
        var actions = Create(launched, directories: [@"C:\Work"]);
        CaptureProvenance source = CaptureProvenance.Create(documentPath: @"C:\Work\gone.xlsx", appExecutablePath: @"C:\Office\excel.exe");
        Assert.Equal(UiText.Get("Library.Source.FileMissing"), await actions.OpenAsync(source));
        Assert.Equal(UiText.Get("Library.Source.FileMissing"), await actions.OpenLocationAsync(source));
        Assert.Empty(launched);
        Assert.Empty(await actions.OpenLocationAsync(source, allowMissingParent: true));
        ProcessStartInfo start = Assert.Single(launched);
        Assert.False(start.UseShellExecute);
        Assert.Equal(@"C:\Windows\explorer.exe", start.FileName);
        Assert.Equal(@"C:\Work", Assert.Single(start.ArgumentList));
    }

    [Fact]
    public async Task LocationSelectsExistingFileAndExplorerFolderOpensItself()
    {
        var launched = new List<ProcessStartInfo>();
        const string file = @"C:\Work\comma, space.xlsx";
        var actions = Create(launched, files: [file], directories: [@"C:\Work"]);
        Assert.Empty(await actions.OpenLocationAsync(CaptureProvenance.Create(documentPath: file, appExecutablePath: @"C:\Office\excel.exe")));
        Assert.Equal("/select,\"" + file + "\"", Assert.Single(launched).Arguments);
        Assert.Empty(await actions.OpenAsync(CaptureProvenance.Create(documentPath: @"C:\Work", appExecutablePath: @"C:\Windows\explorer.exe")));
        Assert.Equal(@"C:\Work", Assert.Single(launched[1].ArgumentList));
        Assert.Equal(CaptureSourceActions.SourceFolderTargetKind.Folder, actions.ResolveFolderTarget(@"C:\Work").Kind);
    }

    [Fact]
    public async Task AppLaunchRequiresExistingExecutableAndNeverRunsAnUnknownScript()
    {
        var launched = new List<ProcessStartInfo>();
        var actions = Create(launched, files: [@"C:\Apps\editor.exe", @"C:\Apps\script.cmd"]);
        Assert.Empty(await actions.OpenAsync(CaptureProvenance.Create(appName: "Editor", appExecutablePath: @"C:\Apps\editor.exe")));
        Assert.False(Assert.Single(launched).UseShellExecute);
        Assert.Equal(UiText.Get("Source.AppMissing"), await actions.OpenAsync(CaptureProvenance.Create(appName: "Missing")));
        Assert.Equal(UiText.Get("Source.AppMissing"), await actions.OpenAsync(CaptureProvenance.Create(appExecutablePath: @"C:\Apps\script.cmd")));
        Assert.Single(launched);
    }

    [Fact]
    public async Task FailuresBecomeExistingLocalizedStatusesAndCopyUsesOnlyInjectedClipboard()
    {
        using var language = UiText.UseLanguage("en");
        var copied = new List<string>();
        var actions = new CaptureSourceActions(_ => throw new Win32Exception(), _ => false, _ => false, copied.Add, @"C:\Windows");
        const string url = "https://example.com/?secret=1#anchor";
        Assert.Equal(UiText.Get("Library.SourcePage.OpenFailed"), await actions.OpenAsync(CaptureProvenance.Create(url)));
        Assert.Equal(UiText.Get("Library.SourcePage.Copied"), actions.CopyUrl(url));
        Assert.Equal(UiText.Get("Library.Source.PathCopied"), actions.CopyPath(@"C:\Work\spec.xlsx"));
        Assert.Equal(new[] { url, @"C:\Work\spec.xlsx" }, copied);
        Assert.Empty(actions.CopyPath("relative"));
        var blockedClipboard = new CaptureSourceActions(_ => { }, _ => false, _ => false, _ => throw new ExternalException(), @"C:\Windows");
        Assert.Equal(UiText.Get("Library.SourcePage.CopyFailed"), blockedClipboard.CopyUrl(url));
    }

    [Fact]
    public async Task InaccessibleFileSystemBecomesMissingWithoutLaunching()
    {
        var launched = new List<ProcessStartInfo>();
        var actions = new CaptureSourceActions(launched.Add, _ => throw new IOException(), _ => throw new UnauthorizedAccessException(), _ => { }, @"C:\Windows");
        Assert.Equal(UiText.Get("Library.Source.FileMissing"), await actions.OpenAsync(CaptureProvenance.Create(documentPath: @"C:\Work\spec.pdf")));
        Assert.Empty(launched);
    }

    [Fact]
    public async Task DocumentsReopenInTheCapturedDocumentAppWhileItExists()
    {
        const string code = @"C:\Apps\Code\Code.exe";
        const string script = @"C:\Work\setup.ps1";
        var launched = new List<ProcessStartInfo>();
        var actions = Create(launched, files: [code, script, @"C:\Windows\System32\notepad.exe"]);
        Assert.Empty(await actions.OpenAsync(CaptureProvenance.Create(documentPath: script, appExecutablePath: code)));
        ProcessStartInfo start = Assert.Single(launched);
        Assert.Equal(code, start.FileName);
        Assert.False(start.UseShellExecute);
        Assert.Equal(script, Assert.Single(start.ArgumentList));

        // The application was uninstalled since: the safe plan opens the script as text.
        var fallback = new List<ProcessStartInfo>();
        Assert.Empty(await Create(fallback, files: [script, @"C:\Windows\System32\notepad.exe"])
            .OpenAsync(CaptureProvenance.Create(documentPath: script, appExecutablePath: code)));
        Assert.Equal(@"C:\Windows\System32\notepad.exe", Assert.Single(fallback).FileName);
    }

    [Fact]
    public async Task UnknownAppsNeverReceiveTheDocumentPath()
    {
        const string shell = @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe";
        const string script = @"C:\Work\setup.ps1";
        var launched = new List<ProcessStartInfo>();
        var actions = Create(launched, files: [shell, script, @"C:\Windows\System32\notepad.exe"]);
        Assert.Empty(await actions.OpenAsync(CaptureProvenance.Create(documentPath: script, appExecutablePath: shell)));
        ProcessStartInfo start = Assert.Single(launched);
        Assert.Equal(@"C:\Windows\System32\notepad.exe", start.FileName);
        Assert.DoesNotContain(launched, plan => plan.FileName.Equals(shell, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PagesReopenInTheCapturedBrowserAndBrowserFilesInTheBrowser()
    {
        const string edge = @"C:\Edge\msedge.exe";
        var launched = new List<ProcessStartInfo>();
        var actions = Create(launched, files: [edge, @"C:\Work\Report.pdf"]);
        Assert.Empty(await actions.OpenAsync(CaptureProvenance.Create(url: "https://example.com/a?b=1", appExecutablePath: edge)));
        Assert.Equal(edge, launched[0].FileName);
        Assert.Equal("https://example.com/a?b=1", Assert.Single(launched[0].ArgumentList));

        Assert.Empty(await actions.OpenAsync(CaptureProvenance.Create(documentPath: @"C:\Work\Report.pdf", appExecutablePath: edge)));
        Assert.Equal(edge, launched[1].FileName);
        Assert.Equal(@"C:\Work\Report.pdf", Assert.Single(launched[1].ArgumentList));

        // The browser is gone: the default browser.
        var fallback = new List<ProcessStartInfo>();
        Assert.Empty(await Create(fallback).OpenAsync(CaptureProvenance.Create(url: "https://example.com/a", appExecutablePath: edge)));
        Assert.True(Assert.Single(fallback).UseShellExecute);
    }

    [Fact]
    public async Task StoreAppsOpenByTheirIdOnceAnUpdateRemovedTheRecordedExecutable()
    {
        const string id = "Microsoft.WindowsNotepad_8wekyb3d8bbwe!App";
        const string old = @"C:\Program Files\WindowsApps\Microsoft.WindowsNotepad_1.0.0.0_x64__8wekyb3d8bbwe\Notepad\Notepad.exe";
        var launched = new List<ProcessStartInfo>();
        var actions = Create(launched);
        Assert.Empty(await actions.OpenAsync(CaptureProvenance.Create(appName: "Notepad", appExecutablePath: old, appUserModelId: id)));
        ProcessStartInfo start = Assert.Single(launched);
        Assert.Equal(@"C:\Windows\explorer.exe", start.FileName);
        Assert.Equal(@"shell:AppsFolder\" + id, Assert.Single(start.ArgumentList));
        Assert.Equal(UiText.Get("Source.AppMissing"),
            await actions.OpenAsync(CaptureProvenance.Create(appName: "Notepad", appExecutablePath: old)));
        Assert.Single(launched);
    }

    private static CaptureSourceActions Create(List<ProcessStartInfo> launched, string[]? files = null, string[]? directories = null) =>
        new(launched.Add, path => files?.Contains(path, StringComparer.OrdinalIgnoreCase) == true,
            path => directories?.Contains(path, StringComparer.OrdinalIgnoreCase) == true, _ => { }, @"C:\Windows");
}
