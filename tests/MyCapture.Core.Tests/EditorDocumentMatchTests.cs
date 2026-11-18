using MyCapture.Core.Capture;
using Xunit;

namespace MyCapture.Core.Tests;

/// <summary>Rules for the path a code editor's selected tab names.</summary>
public sealed class EditorDocumentMatchTests
{
    private const string Alpha = @"C:\Work\My Project\alpha.cs";
    private static readonly string[] Existing = [Alpha, @"C:\Work\beta.md", @"D:\Other\alpha.cs"];
    private static bool Exists(string path) => Existing.Contains(path, StringComparer.OrdinalIgnoreCase);

    [Theory]
    [InlineData(@"C:\Work\My Project\alpha.cs")]
    [InlineData(@"C:\Work\My Project\alpha.cs • Modified")]
    [InlineData(@"alpha.cs, C:\Work\My Project\alpha.cs, preview")]
    [InlineData("\"C:\\Work\\My Project\\alpha.cs\"")]
    public void ATabLabelNamesItsExistingFile(string label) =>
        Assert.Equal(Alpha, EditorDocumentMatch.Choose(["alpha.cs"], [label], Exists));

    [Fact]
    public void TheTitleNarrowsSeveralSelectedTabsAndAnythingAmbiguousGivesNothing()
    {
        Assert.Equal(Alpha, EditorDocumentMatch.Choose(["alpha.cs"], [@"C:\Work\beta.md", Alpha], Exists));
        Assert.Empty(EditorDocumentMatch.Choose(["alpha.cs"], [Alpha, @"D:\Other\alpha.cs"], Exists));
        Assert.Empty(EditorDocumentMatch.Choose(["alpha.cs"], [@"C:\Work\beta.md"], Exists));
        // Without a file name in the title only one selected path qualifies.
        Assert.Equal(Alpha, EditorDocumentMatch.Choose([], [Alpha, null, "alpha.cs"], Exists));
        Assert.Empty(EditorDocumentMatch.Choose([], [Alpha, @"C:\Work\beta.md"], Exists));
    }

    [Theory]
    [InlineData("alpha.cs")]
    [InlineData(@"C:\Work\missing.cs")]
    [InlineData(@"\\server\share\alpha.cs")]
    [InlineData(@"C:\Work\My Project")]
    [InlineData(@"xC:\Work\My Project\alpha.cs")]
    public void NamesFoldersSharesAndMissingFilesGiveNothing(string label) =>
        // The check is File.Exists, which is false for a folder.
        Assert.Empty(EditorDocumentMatch.Choose([], [label], Exists));

    [Fact]
    public void ALongLabelIsNotSearchedAndOneLabelChecksFewPaths()
    {
        int checks = 0;
        string many = string.Join(" ", Enumerable.Repeat(@"C:\a b c d e f g h i j k l m n o p q r s t u v w x y z", 20));
        Assert.Empty(EditorDocumentMatch.Choose([], [many], _ => { checks++; return false; }));
        Assert.True(checks <= EditorDocumentMatch.MaxPathsPerText * EditorDocumentMatch.MaxCutsPerPath);
        checks = 0;
        Assert.Empty(EditorDocumentMatch.Choose([], [new string('x', 3000) + " " + Alpha], _ => { checks++; return true; }));
        Assert.Equal(0, checks);
    }

    [Theory]
    [InlineData(@"C:\Apps\Microsoft VS Code\Code.exe", true)]
    [InlineData(@"C:\Apps\Microsoft VS Code Insiders\Code - Insiders.exe", true)]
    [InlineData(@"C:\Apps\Cursor\Cursor.exe", false)]
    [InlineData(@"C:\Windows\notepad.exe", false)]
    [InlineData(null, false)]
    public void OnlyListedEditorsAreRead(string? appPath, bool expected) =>
        Assert.Equal(expected, EditorDocumentMatch.IsEditor(appPath));

    [Fact]
    public void TheEditorOriginIsKeptAndInsidersTitlesGiveTheirFileName()
    {
        Assert.Equal(CaptureDocumentOrigin.Editor, CaptureDocumentOrigin.Normalize("editor"));
        Assert.Equal(CaptureDocumentOrigin.Editor,
            CaptureSourceDetails.Create("Code", @"C:\Apps\Code.exe", Alpha, documentOrigin: "editor").DocumentOrigin);
        Assert.Equal(["alpha.cs"], SourceDocument.TitleFileNames(@"C:\Apps\Code - Insiders.exe",
            "alpha.cs - My Project - Visual Studio Code - Insiders"));
        Assert.True(SourceDocument.IsUnsaved(@"C:\Apps\Code - Insiders.exe", "Untitled-1 - Visual Studio Code - Insiders"));
    }
}
