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
}
