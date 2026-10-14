using MyCapture.Core.Capture;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class SourceDocumentTests
{
    [Theory]
    [InlineData("EXCEL.EXE", "Budget.xlsx - Excel", @"C:\Work\Budget.xlsx")]
    [InlineData("winword.exe", "Plan - Word", @"C:\Work\Plan.docx")]
    [InlineData("powerpnt.exe", "Slides.pptx - PowerPoint", @"C:\Work\Slides.pptx")]
    [InlineData("notepad.exe", "*Notes.txt - Notepad", @"C:\Work\Notes.txt")]
    [InlineData("AcroRd32.exe", "Report.pdf - Adobe Acrobat Reader", @"C:\Work\Report.pdf")]
    [InlineData("SumatraPDF.exe", "Report.pdf - SumatraPDF", @"C:\Work\Report.pdf")]
    [InlineData("Code.exe", "main.cs - Project - Visual Studio Code", @"C:\Work\main.cs")]
    public void MatchesOnlyExplicitPathsForTheCapturedDocument(string app, string title, string path) =>
        Assert.Equal(path, SourceDocument.ResolvePath(app, title, [path]));

    [Fact]
    public void FullPathTitleWorksWithoutAutomation()
    {
        const string path = @"C:\Work\Notes.txt";
        Assert.Equal(path, SourceDocument.ResolvePath("notepad.exe", path + " - Notepad", []));
    }

    [Fact]
    public void MissingAmbiguousAndUnrelatedPropertiesStayUnknown()
    {
        Assert.Empty(SourceDocument.ResolvePath("excel.exe", "Budget.xlsx - Excel", []));
        Assert.Empty(SourceDocument.ResolvePath("excel.exe", "Budget.xlsx - Excel", [@"C:\Other.xlsx"]));
        Assert.Empty(SourceDocument.ResolvePath("excel.exe", "Budget.xlsx - Excel", [@"C:\Budget.xlsx", @"D:\Budget.xlsx"]));
        Assert.Empty(SourceDocument.ResolvePath("browser.exe", "Budget.xlsx - Excel", [@"C:\Budget.xlsx"]));
        Assert.Empty(SourceDocument.ResolvePath("excel.exe", "Budget.xlsx - Excel", ["Budget.xlsx", "https://host/Budget.xlsx"]));
    }

    [Theory]
    [InlineData("excel.exe", "통합 문서1 - Excel", true)]
    [InlineData("excel.exe", "Book12 - Excel", true)]
    [InlineData("winword.exe", "문서1 - Word", true)]
    [InlineData("powerpnt.exe", "Presentation1 - PowerPoint", true)]
    [InlineData("notepad.exe", "*제목 없음 - 메모장", true)]
    [InlineData("code.exe", "Untitled-1 - Visual Studio Code", true)]
    [InlineData("code.exe", "Untitled-1 - MyProject - Visual Studio Code", true)]
    [InlineData("code.exe", "● Untitled-1 - MyProject - Visual Studio Code", true)]
    [InlineData("code.exe", "제목 없음-2 - 프로젝트 - Visual Studio Code", true)]
    [InlineData("code.exe", "● 제목 없음-2 - 프로젝트 - Visual Studio Code", true)]
    [InlineData("code.exe", "*Untitled-3 — MyProject — Visual Studio Code", true)]
    [InlineData("code.exe", "• 제목 없음-3 – 프로젝트 – Visual Studio Code", true)]
    [InlineData("code.exe", "Untitled-1.txt - MyProject - Visual Studio Code", false)]
    [InlineData("code.exe", "● 제목 없음-2.cs - 프로젝트 - Visual Studio Code", false)]
    [InlineData("code.exe", "Untitled-1-notes - MyProject - Visual Studio Code", false)]
    [InlineData("code.exe", "main.cs - Untitled-1 - Visual Studio Code", false)]
    [InlineData("excel.exe", "Book1.xlsx - Excel", false)]
    [InlineData("excel.exe", "Budget.xlsx - Excel", false)]
    [InlineData("other.exe", "Book1 - Excel", false)]
    public void UnsavedRequiresAKnownAppAndAnUntitledName(string app, string title, bool expected) =>
        Assert.Equal(expected, SourceDocument.IsUnsaved(app, title));

    [Fact]
    public void MiddleShorteningKeepsTheRootAndFilename()
    {
        const string path = @"C:\Users\test\Documents\Long project directory\2026\Budget.xlsx";
        Assert.Equal(path, SourceDocument.ShortenPath(path, path.Length));
        string shortPath = SourceDocument.ShortenPath(path, 35);
        Assert.StartsWith(@"C:\", shortPath);
        Assert.EndsWith("Budget.xlsx", shortPath);
        Assert.Contains("…", shortPath);
        Assert.True(shortPath.Length <= 35);
        Assert.Throws<ArgumentOutOfRangeException>(() => SourceDocument.ShortenPath(path, 4));
    }

    [Fact]
    public void MiddleShorteningDoesNotSplitSurrogates()
    {
        string path = @"C:\" + string.Concat(Enumerable.Repeat("\U0001F600", 30)) + ".txt";
        for (int length = 5; length < path.Length; length++)
        {
            string shortened = SourceDocument.ShortenPath(path, length);
            Assert.True(shortened.Length <= length);
            for (int index = 0; index < shortened.Length; index++)
            {
                if (char.IsHighSurrogate(shortened[index])) Assert.True(char.IsLowSurrogate(shortened[++index]));
                else Assert.False(char.IsLowSurrogate(shortened[index]));
            }
        }
    }

    [Fact]
    public void ExistenceChecksAnActualFileWithoutCreatingFixtures()
    {
        string path = typeof(SourceDocumentTests).Assembly.Location;
        Assert.True(SourceDocument.FileExists(path));
        Assert.False(SourceDocument.FileExists(path + ".missing-" + Guid.NewGuid().ToString("N")));
    }

    [Fact]
    public void ExistenceIsRecheckedAndInvalidPathsNeverReachTheFilesystem()
    {
        int calls = 0;
        bool present = true;
        bool Exists(string path) { calls++; Assert.Equal(@"C:\Work\Budget.xlsx", path); return present; }
        Assert.True(SourceDocument.FileExists(@"C:\Work\Budget.xlsx", Exists));
        present = false;
        Assert.False(SourceDocument.FileExists(@"C:\Work\Budget.xlsx", Exists));
        Assert.False(SourceDocument.FileExists("relative.xlsx", Exists));
        Assert.False(SourceDocument.FileExists("file:///C:/Work/Budget.xlsx", Exists));
        Assert.Equal(2, calls);
        Assert.False(SourceDocument.FileExists(@"C:\Work\Budget.xlsx", _ => throw new UnauthorizedAccessException()));
        Assert.False(SourceDocument.FileExists(@"C:\Work\Budget.xlsx", _ => throw new IOException()));
    }

    [Theory]
    [InlineData(@"C:\Work\Budget,Q4.xlsx", "/select,\"C:\\Work\\Budget,Q4.xlsx\"")]
    [InlineData(@"C:\Work files\Budget Q4.xlsx", "/select,\"C:\\Work files\\Budget Q4.xlsx\"")]
    [InlineData(@"\\server\share\Budget.xlsx", "/select,\"\\\\server\\share\\Budget.xlsx\"")]
    [InlineData(@"C:\자료\예산.xlsx", "/select,\"C:\\자료\\예산.xlsx\"")]
    [InlineData(@"\\server\공유 자료\예산, 4분기.xlsx", "/select,\"\\\\server\\공유 자료\\예산, 4분기.xlsx\"")]
    public void FolderLaunchPlanQuotesTheSelectedFileForExplorer(string path, string arguments)
    {
        const string explorer = @"C:\Windows\explorer.exe";
        var start = SourceDocument.CreateOpenFolderStartInfo(path, explorer, selectFile: true);
        Assert.NotNull(start);
        Assert.Equal(explorer, start.FileName);
        Assert.False(start.UseShellExecute);
        Assert.Equal(arguments, start.Arguments);
        Assert.Empty(start.ArgumentList);
    }

    [Fact]
    public void FolderLaunchPlanKeepsFolderOpeningWithoutSelection()
    {
        const string path = @"C:\자료, Work files";
        const string explorer = @"C:\Windows\explorer.exe";
        var start = SourceDocument.CreateOpenFolderStartInfo(path, explorer, selectFile: false);
        Assert.NotNull(start);
        Assert.Equal(explorer, start.FileName);
        Assert.False(start.UseShellExecute);
        Assert.Equal(path, Assert.Single(start.ArgumentList));
        Assert.Empty(start.Arguments);
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative.xlsx")]
    [InlineData("/select,C:\\Work\\Budget.xlsx")]
    [InlineData("C:\\Work\\Budget\",Q4.xlsx")]
    [InlineData("file:///C:/Work/Budget.xlsx")]
    public void FolderLaunchPlanRejectsInvalidPaths(string path)
    {
        Assert.Null(SourceDocument.CreateOpenFolderStartInfo(path, @"C:\Windows\explorer.exe", selectFile: true));
        Assert.Null(SourceDocument.CreateOpenFolderStartInfo(path, @"C:\Windows\explorer.exe", selectFile: false));
    }

    [Theory]
    [InlineData("cleanup.cmd")]
    [InlineData("cleanup.bat")]
    [InlineData("cleanup.CMD")]
    [InlineData("cleanup.ps1")]
    [InlineData("cleanup.vbs")]
    [InlineData("cleanup.js")]
    [InlineData("cleanup.exe")]
    [InlineData("cleanup.lnk")]
    [InlineData("cleanup.unknown")]
    [InlineData("cleanup")]
    [InlineData("notes.txt")]
    public void LaunchPlanUsesAnExplicitEditorForNonDocumentTypes(string name)
    {
        const string editor = @"C:\Windows\System32\notepad.exe";
        string path = @"C:\Work & notes\" + name;
        var start = SourceDocument.CreateOpenStartInfo(path, editor, candidate => candidate == editor);
        Assert.NotNull(start);
        Assert.Equal(editor, start.FileName);
        Assert.False(start.UseShellExecute);
        Assert.Equal(path, Assert.Single(start.ArgumentList));
        Assert.Empty(start.Arguments);
    }

    [Theory]
    [InlineData("Budget.xlsx")]
    [InlineData("Plan.docx")]
    [InlineData("Slides.pptx")]
    [InlineData("Report.PDF")]
    public void LaunchPlanUsesTheOpenVerbForOrdinaryDocuments(string name)
    {
        string path = @"C:\Work\" + name;
        var start = SourceDocument.CreateOpenStartInfo(path, string.Empty, _ => throw new InvalidOperationException());
        Assert.NotNull(start);
        Assert.Equal(path, start.FileName);
        Assert.True(start.UseShellExecute);
        Assert.Equal("open", start.Verb);
        Assert.Empty(start.ArgumentList);
    }

    [Theory]
    [InlineData("cleanup.cmd")]
    [InlineData("cleanup.bat")]
    public void LaunchPlanDeclinesScriptsWhenTheEditorIsMissing(string name)
    {
        Assert.Null(SourceDocument.CreateOpenStartInfo(@"C:\Work\" + name,
            @"C:\Windows\System32\notepad.exe", _ => false));
        Assert.Null(SourceDocument.CreateOpenStartInfo(@"C:\Work\" + name,
            "notepad.exe", _ => throw new InvalidOperationException()));
    }

    [Theory]
    [InlineData("relative.cmd")]
    [InlineData("file:///C:/Work/cleanup.cmd")]
    [InlineData("/open cleanup.cmd")]
    public void LaunchPlanRejectsInvalidSourcePaths(string path) =>
        Assert.Null(SourceDocument.CreateOpenStartInfo(path, @"C:\Windows\System32\notepad.exe",
            _ => throw new InvalidOperationException()));
}
