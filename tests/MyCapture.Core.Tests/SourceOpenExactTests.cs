using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using MyCapture.Core.Capture;
using MyCapture.Core.Queue;
using MyCapture.Core.Serialization;
using MyCapture.Core.Storage;
using Xunit;

namespace MyCapture.Core.Tests;

/// <summary>Reopening a source in the application it was captured from, and how the record keeps it.</summary>
public sealed class SourceOpenExactTests
{
    private const string StoreApp = "Microsoft.WindowsNotepad_8wekyb3d8bbwe!App";
    private static bool Exists(string _) => true;
    private static bool Missing(string _) => false;

    [Theory]
    [InlineData(@"C:\Apps\Code\Code.exe", @"C:\Work\main.cs")]
    [InlineData(@"C:\Windows\System32\notepad.exe", @"C:\Work\setup.ps1")]
    [InlineData(@"C:\Apps\Notepad++\notepad++.exe", @"C:\Work\build.cmd")]
    [InlineData(@"C:\Office\WINWORD.EXE", @"C:\Work\Plan.docx")]
    [InlineData(@"C:\Office\EXCEL.EXE", @"C:\Work\Budget.csv")]
    [InlineData(@"C:\Office\POWERPNT.EXE", @"C:\Work\Deck.pptx")]
    [InlineData(@"C:\Adobe\Acrobat.exe", @"C:\Work\Report.pdf")]
    [InlineData(@"C:\Adobe\AcroRd32.exe", @"C:\Work\Report.pdf")]
    [InlineData(@"C:\Apps\SumatraPDF.exe", @"C:\Work\Report.pdf")]
    [InlineData(@"C:\Apps\FoxitPDFReader.exe", @"C:\Work\Report.pdf")]
    [InlineData(@"C:\Hnc\Hwp.exe", @"C:\Work\공문.hwpx")]
    [InlineData(@"C:\Edge\msedge.exe", @"C:\Work\Report.pdf")]
    [InlineData(@"C:\Chrome\chrome.exe", @"C:\Work\page.html")]
    public void KnownDocumentAppsReceiveOnlyThePathAsOneArgument(string app, string document)
    {
        ProcessStartInfo start = SourceAppLaunch.CreateDocument(app, document, Exists)!;
        Assert.Equal(app, start.FileName);
        Assert.False(start.UseShellExecute);
        Assert.Equal([document], start.ArgumentList);
        Assert.Empty(start.Arguments);
    }

    [Fact]
    public void VisualStudioOpensTheFileForEditingInTheRunningInstance()
    {
        ProcessStartInfo start = SourceAppLaunch.CreateDocument(@"C:\VS\Common7\IDE\devenv.exe", @"C:\Work\main.cs", Exists)!;
        Assert.Equal(["/edit", @"C:\Work\main.cs"], start.ArgumentList);
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\cmd.exe", @"C:\Work\run.cmd")]
    [InlineData(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", @"C:\Work\setup.ps1")]
    [InlineData(@"C:\Python\python.exe", @"C:\Work\tool.py")]
    [InlineData(@"C:\Windows\explorer.exe", @"C:\Work\Plan.docx")]
    [InlineData(@"C:\Apps\unknown.exe", @"C:\Work\notes.txt")]
    [InlineData(@"C:\Apps\Code\Code.cmd", @"C:\Work\main.cs")]
    [InlineData("", @"C:\Work\notes.txt")]
    public void ShellsInterpretersAndUnknownAppsNeverReceiveAPath(string app, string document) =>
        Assert.Null(SourceAppLaunch.CreateDocument(app, document, Exists));

    [Theory]
    [InlineData(@"C:\Office\WINWORD.EXE", @"C:\Work\tool.ps1")]
    [InlineData(@"C:\Office\EXCEL.EXE", @"C:\Work\setup.exe")]
    [InlineData(@"C:\Edge\msedge.exe", @"C:\Work\run.bat")]
    [InlineData(@"C:\Edge\msedge.exe", @"C:\Work\link.lnk")]
    [InlineData(@"C:\Hnc\Hwp.exe", @"C:\Work\Report.pdf")]
    public void ViewersReceiveOnlyTheirOwnDocumentTypes(string app, string document) =>
        Assert.Null(SourceAppLaunch.CreateDocument(app, document, Exists));

    [Fact]
    public void AMissingCapturedAppFallsBackToTheSafePlan()
    {
        Assert.Null(SourceAppLaunch.CreateDocument(@"C:\Office\WINWORD.EXE", @"C:\Work\Plan.docx", Missing));
        // The fallback keeps today's rules: associations for known formats, the text editor for scripts.
        ProcessStartInfo docx = SourceDocument.CreateOpenStartInfo(@"C:\Work\Plan.docx", @"C:\Windows\System32\notepad.exe",
            path => !path.EndsWith("WINWORD.EXE", StringComparison.OrdinalIgnoreCase), @"C:\Office\WINWORD.EXE")!;
        Assert.True(docx.UseShellExecute);
        Assert.Equal(@"C:\Work\Plan.docx", docx.FileName);
        ProcessStartInfo script = SourceDocument.CreateOpenStartInfo(@"C:\Work\setup.ps1", @"C:\Windows\System32\notepad.exe",
            Exists, @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe")!;
        Assert.Equal(@"C:\Windows\System32\notepad.exe", script.FileName);
        Assert.False(script.UseShellExecute);
        Assert.Equal([@"C:\Work\setup.ps1"], script.ArgumentList);
    }

    [Fact]
    public void TheCapturedEditorOpensAScriptAsTextNotTheDefaultEditor()
    {
        ProcessStartInfo start = SourceDocument.CreateOpenStartInfo(@"C:\Work\setup.ps1", @"C:\Windows\System32\notepad.exe",
            Exists, @"C:\Apps\Code\Code.exe")!;
        Assert.Equal(@"C:\Apps\Code\Code.exe", start.FileName);
        Assert.Equal([@"C:\Work\setup.ps1"], start.ArgumentList);
    }

    [Fact]
    public void AddressesOpenInTheCapturedBrowserWhileItExists()
    {
        ProcessStartInfo captured = SourceAppLaunch.CreateUrl("https://example.com/docs?a=1#b", @"C:\Edge\msedge.exe", Exists)!;
        Assert.Equal(@"C:\Edge\msedge.exe", captured.FileName);
        Assert.False(captured.UseShellExecute);
        Assert.Equal(["https://example.com/docs?a=1#b"], captured.ArgumentList);

        foreach (ProcessStartInfo fallback in new[]
        {
            SourceAppLaunch.CreateUrl("https://example.com/docs", @"C:\Edge\msedge.exe", Missing)!,
            SourceAppLaunch.CreateUrl("https://example.com/docs", @"C:\Apps\Code\Code.exe", Exists)!,
            SourceAppLaunch.CreateUrl("https://example.com/docs", null)!,
        })
        {
            Assert.True(fallback.UseShellExecute);
            Assert.Equal("https://example.com/docs", fallback.FileName);
            Assert.Empty(fallback.ArgumentList);
        }
        Assert.Null(SourceAppLaunch.CreateUrl("javascript:alert(1)", @"C:\Edge\msedge.exe", Exists));
        Assert.Null(SourceAppLaunch.CreateUrl("--disable-web-security", @"C:\Edge\msedge.exe", Exists));
    }

    [Fact]
    public void AppsStartWithoutArgumentsAndStoreAppsByTheirIdOnceTheExecutableIsGone()
    {
        const string versioned = @"C:\Program Files\WindowsApps\Microsoft.WindowsNotepad_11.2410.21.0_x64__8wekyb3d8bbwe\Notepad\Notepad.exe";
        ProcessStartInfo direct = SourceAppLaunch.CreateApp(versioned, StoreApp, @"C:\Windows\explorer.exe", Exists)!;
        Assert.Equal(versioned, direct.FileName);
        Assert.Empty(direct.ArgumentList);

        ProcessStartInfo store = SourceAppLaunch.CreateApp(versioned, StoreApp, @"C:\Windows\explorer.exe", Missing)!;
        Assert.Equal(@"C:\Windows\explorer.exe", store.FileName);
        Assert.False(store.UseShellExecute);
        Assert.Equal([@"shell:AppsFolder\" + StoreApp], store.ArgumentList);

        Assert.Null(SourceAppLaunch.CreateApp(versioned, null, @"C:\Windows\explorer.exe", Missing));
        Assert.Null(SourceAppLaunch.CreateApp(versioned, "not an id", @"C:\Windows\explorer.exe", Missing));
    }

    [Theory]
    [InlineData(StoreApp, StoreApp)]
    [InlineData("  " + StoreApp + " ", StoreApp)]
    [InlineData("Microsoft.WindowsNotepad_8wekyb3d8bbwe", "")]
    [InlineData("Microsoft.WindowsNotepad_8wekyb3d8bbwe!App!Two", "")]
    [InlineData("Microsoft.WindowsNotepad_8wekyb3d8bbwe!App\" /x", "")]
    [InlineData(@"..\..\Windows!App", "")]
    [InlineData("Notepad_short!App", "")]
    [InlineData("", "")]
    public void PackagedAppIdsKeepOnlyTheirOwnShape(string value, string expected) =>
        Assert.Equal(expected, CaptureSourceText.NormalizeAppUserModelId(value));

    [Fact]
    public void ProvenanceOpensAnAppByItsIdAndSaysWhenOnlyTheAppIsKnown()
    {
        CaptureProvenance store = CaptureProvenance.Create(appName: "Notepad", appUserModelId: StoreApp);
        Assert.Equal(CaptureProvenanceKind.App, store.Kind);
        Assert.True(store.CanOpen);
        Assert.True(store.IsAppOnly);
        Assert.True(store.HasAppIdentity);
        Assert.Equal(StoreApp, CaptureProvenance.FromSource(CaptureSourceDetails.Create("Notepad", null, null, StoreApp)).AppUserModelId);

        CaptureProvenance named = CaptureProvenance.Create(appName: "Tool");
        Assert.False(named.CanOpen);
        Assert.False(named.HasAppIdentity);

        CaptureProvenance document = CaptureProvenance.Create(documentPath: @"C:\Work\a.txt", appExecutablePath: @"C:\Windows\notepad.exe");
        Assert.False(document.IsAppOnly);
        Assert.True(document.HasAppIdentity);
    }

    [Theory]
    [InlineData("file:///C:/Work/Report.pdf", @"C:\Work\Report.pdf")]
    [InlineData("file:///C:/Work/%EB%B3%B4%EA%B3%A0%EC%84%9C.pdf", @"C:\Work\보고서.pdf")]
    [InlineData("file:///C:/Work/My%20Report.pdf", @"C:\Work\My Report.pdf")]
    [InlineData("file://server/share/Report.pdf", @"\\server\share\Report.pdf")]
    [InlineData("https://example.com/Report.pdf", "")]
    [InlineData("file://user@server/share/a.pdf", "")]
    [InlineData("edge://settings", "")]
    [InlineData("", "")]
    public void BrowserFileAddressesBecomeLocalPaths(string address, string expected) =>
        Assert.Equal(expected, SourcePageUrl.LocalFilePath(address));

    [Fact]
    public void BrowserDocumentsKeepWebPagesAndCanonicalFileAddresses()
    {
        Assert.Equal("https://example.com/a", SourcePageUrl.NormalizeDocument("https://example.com/a"));
        Assert.Equal("file:///C:/Work/My%20Report.pdf", SourcePageUrl.NormalizeDocument("file:///C:/Work/My%20Report.pdf"));
        Assert.Empty(SourcePageUrl.NormalizeDocument("chrome://downloads"));
        // The page address field itself still refuses files.
        Assert.Empty(SourcePageUrl.Normalize("file:///C:/Work/My%20Report.pdf"));
    }

    [Fact]
    public void ABrowserFileIsAddedOnlyWhereNoDocumentIsKnown()
    {
        var edge = CaptureSourceDetails.Create("Microsoft Edge", @"C:\Edge\msedge.exe", null);
        CaptureSourceDetails withFile = CaptureSourceDetails.WithDocument(edge, @"C:\Work\Report.pdf", CaptureDocumentOrigin.Browser)!;
        Assert.Equal(@"C:\Work\Report.pdf", withFile.DocumentPath);
        Assert.Equal(CaptureDocumentOrigin.Browser, withFile.DocumentOrigin);
        Assert.Same(withFile, CaptureSourceDetails.WithDocument(withFile, @"C:\Other.pdf", CaptureDocumentOrigin.Browser));
        Assert.Same(edge, CaptureSourceDetails.WithDocument(edge, "", CaptureDocumentOrigin.Browser));
        Assert.True(SourceDocument.IsFileSource(@"C:\Edge\msedge.exe"));
    }

    [Fact]
    public void NewSourceFieldsRoundTripAndOlderRecordsStillLoad()
    {
        var record = new CaptureRecord { Width = 10, Height = 10 };
        record.SetSourceDetails(CaptureSourceDetails.Create("Notepad",
            @"C:\Program Files\WindowsApps\Microsoft.WindowsNotepad_1.0.0.0_x64__8wekyb3d8bbwe\Notepad\Notepad.exe",
            @"C:\Work\notes.txt", StoreApp, CaptureDocumentOrigin.Recent));
        foreach (JsonSerializerOptions options in new[] { JsonDefaults.Compact, JsonDefaults.Readable })
        {
            CaptureRecord loaded = JsonSerializer.Deserialize<CaptureRecord>(JsonSerializer.Serialize(record, options), options)!;
            Assert.Equal(StoreApp, loaded.SourceAppUserModelId);
            Assert.Equal(CaptureDocumentOrigin.Recent, loaded.SourceDocumentOrigin);
            Assert.Equal(record.SourceDetails, loaded.SourceDetails);
            Assert.True(loaded.HasSameValuesAs(record));
        }

        // Absent while empty, so records without them keep their old shape.
        var plain = new CaptureRecord();
        plain.SetSourceDetails(CaptureSourceDetails.Create("Tool", @"C:\Apps\tool.exe", null));
        JsonObject written = JsonNode.Parse(JsonSerializer.Serialize(plain, JsonDefaults.Compact))!.AsObject();
        Assert.False(written.ContainsKey("sourceAppUserModelId"));
        Assert.False(written.ContainsKey("sourceDocumentOrigin"));

        // An older build's record (no new fields) loads with them empty.
        CaptureRecord legacy = JsonSerializer.Deserialize<CaptureRecord>(
            "{\"sourceAppName\":\"Tool\",\"sourceAppPath\":\"C:\\\\Apps\\\\tool.exe\"}", JsonDefaults.Compact)!;
        Assert.Equal(string.Empty, legacy.SourceDetails.AppUserModelId);
        Assert.Equal(string.Empty, legacy.SourceDetails.DocumentOrigin);

        // A record carrying the new fields still loads where they are unknown properties.
        JsonObject future = JsonNode.Parse(JsonSerializer.Serialize(record, JsonDefaults.Compact))!.AsObject();
        future["sourceSomethingLater"] = "x";
        Assert.NotNull(JsonSerializer.Deserialize<CaptureRecord>(future.ToJsonString(), JsonDefaults.Compact));
    }

    [Fact]
    public void AnUnknownOriginIsDroppedAndAnOriginNeedsADocument()
    {
        Assert.Empty(CaptureSourceDetails.Create("Tool", @"C:\Apps\tool.exe", @"C:\Work\a.txt", null, "guess").DocumentOrigin);
        Assert.Empty(CaptureSourceDetails.Create("Tool", @"C:\Apps\tool.exe", null, null, CaptureDocumentOrigin.Recent).DocumentOrigin);
    }

    [Theory]
    [InlineData("notepad.exe", "Notes.txt - Notepad", new[] { "Notes.txt" })]
    [InlineData("notepad.exe", "*Notes.txt - 메모장", new[] { "Notes.txt" })]
    [InlineData("Code.exe", "● main.cs - Project - Visual Studio Code", new[] { "main.cs" })]
    [InlineData("notepad++.exe", "C:\\Work\\a.md - Notepad++", new string[0])]
    [InlineData("Hwp.exe", "공문.hwpx - 한글", new[] { "공문.hwpx" })]
    [InlineData("excel.exe", "Budget - Excel", new string[0])]
    [InlineData("excel.exe", "Book1 - Excel", new string[0])]
    [InlineData("notepad.exe", "제목 없음 - 메모장", new string[0])]
    [InlineData("other.exe", "Notes.txt - Other", new string[0])]
    public void TitleFileNamesNeedAKnownAppAndAnExtension(string app, string title, string[] expected) =>
        Assert.Equal(expected, SourceDocument.TitleFileNames(app, title));

    [Fact]
    public void RecentMatchNeedsOneExistingTargetWrittenAfterTheProcessStarted()
    {
        DateTime started = new(2026, 11, 12, 1, 0, 0, DateTimeKind.Utc);
        RecentDocumentLink fresh = new(@"C:\Work\notes.txt", started.AddMinutes(5));
        string[] names = ["notes.txt"];

        Assert.Equal(@"C:\Work\notes.txt", RecentDocumentMatch.Choose(names, [fresh], started, Exists));
        // The same target through two shortcuts is still one file.
        Assert.Equal(@"C:\Work\notes.txt", RecentDocumentMatch.Choose(names,
            [fresh, fresh with { WrittenUtc = started.AddDays(-3) }], started, Exists));
        // Two different files with the name, even when one shortcut is old: ambiguous.
        Assert.Empty(RecentDocumentMatch.Choose(names,
            [fresh, new RecentDocumentLink(@"D:\Old\notes.txt", started.AddDays(-30))], started, Exists));
        // A shortcut from before the process started belongs to another session.
        Assert.Empty(RecentDocumentMatch.Choose(names, [fresh with { WrittenUtc = started.AddSeconds(-1) }], started, Exists));
        Assert.Empty(RecentDocumentMatch.Choose(names, [fresh], started, Missing));
        Assert.Empty(RecentDocumentMatch.Choose(names, [fresh], null, Exists));
        Assert.Empty(RecentDocumentMatch.Choose([], [fresh], started, Exists));
        Assert.Empty(RecentDocumentMatch.Choose(["other.txt"], [fresh], started, Exists));
        Assert.Empty(RecentDocumentMatch.Choose(names, [new RecentDocumentLink("notes.txt", started.AddMinutes(1))], started, Exists));
    }

    [Fact]
    public void SourceIconsLiveBesideTheModelsNeverInTheRoamingRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "mc-paths");
        Assert.Equal(Path.Combine(root, "source-icons"), AppPaths.CreateForRoot(root).SourceIconsRoot);
        AppPaths package = AppPaths.CreateForPackage(@"C:\Pkg\LocalState", @"C:\Pkg\LocalCache", null);
        Assert.Equal(@"C:\Pkg\LocalCache\source-icons", package.SourceIconsRoot);
        Assert.Equal(package.SourceIconsRoot, package.WithCapturesRoot(@"D:\Captures").SourceIconsRoot);
    }
}
