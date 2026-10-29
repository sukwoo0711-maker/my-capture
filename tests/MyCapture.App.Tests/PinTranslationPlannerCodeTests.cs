using System.IO;
using MyCapture.App.Pinning;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class PinTranslationPlannerCodeTests
{
    private static string[] English(string source) => PlannerSamples.Sources(source, PinTranslationDirection.EnglishToKorean, PinTextFormat.Code);

    [Theory]
    [InlineData("csharp")]
    [InlineData("python")]
    [InlineData("javascript")]
    [InlineData("sql")]
    [InlineData("yaml")]
    [InlineData("shell")]
    public void CodeSamplesAreClassifiedAsCode(string name) =>
        Assert.Equal(PinTextFormat.Code, PinTranslationPlanner.Classify(PlannerSamples.Inline[name], false, false));

    [Theory]
    [InlineData("stray-marks")]
    [InlineData("urls-paths")]
    [InlineData("poem")]
    [InlineData("list")]
    [InlineData("korean")]
    public void ProseWithStrayMarksIsNotCode(string name) =>
        Assert.NotEqual(PinTextFormat.Code, PinTranslationPlanner.Classify(PlannerSamples.Inline[name], false, false));

    [Fact]
    public void CSharpCommentsAreTranslatedAndStringsAreNot()
    {
        string source = PlannerSamples.Inline["csharp"];
        string[] sources = English(source);
        Assert.Equal(
        [
            "Calculates the total price of all items in the cart.",
            "Represents a shopping cart with several items.",
            "The list of items to add.",
            "the endpoint address",
            "The first line of a block comment",
            "continues on the second line",
            "returns the running total",
            "refactor this method later",
        ], sources);
        Assert.DoesNotContain(sources, static text => text.Contains("not a comment", StringComparison.Ordinal) || text.Contains("raw string", StringComparison.Ordinal) || text.Contains("still", StringComparison.Ordinal));
    }

    [Fact]
    public void MarkersTagsAndTodoPrefixStayLiteralInCSharp()
    {
        string source = PlannerSamples.Inline["csharp"];
        PinTranslationPlan plan = PlannerSamples.Plan(source, PinTranslationDirection.EnglishToKorean, PinTextFormat.Code);
        string output = plan.Assemble([.. plan.Sources.Select(static text => (string?)text.ToUpperInvariant())]);
        Assert.Contains("/// <summary>\r\n", output, StringComparison.Ordinal);
        Assert.Contains("/// REPRESENTS A SHOPPING CART WITH SEVERAL ITEMS.\r\n", output, StringComparison.Ordinal);
        Assert.Contains("/// <param name=\"items\">THE LIST OF ITEMS TO ADD.</param>", output, StringComparison.Ordinal);
        Assert.Contains("// TODO(sw): REFACTOR THIS METHOD LATER\r\n", output, StringComparison.Ordinal);
        Assert.Contains("// int x = 5;\r\n", output, StringComparison.Ordinal);
        Assert.Contains("\"http://example.com/a//b\"; // THE ENDPOINT ADDRESS", output, StringComparison.Ordinal);
        Assert.Contains("\"// not a comment\"", output, StringComparison.Ordinal);
        Assert.Contains("/* THE FIRST LINE OF A BLOCK COMMENT\r\n         * CONTINUES ON THE SECOND LINE\r\n         */", output, StringComparison.Ordinal);
        Assert.Contains("@\"C:\\temp\\ // still a string \"\" with quote // still\"", output, StringComparison.Ordinal);
        Assert.Contains("            // inside a raw string\r\n", output, StringComparison.Ordinal);
    }

    [Fact]
    public void LineEndingsDoNotChangeWhatIsTranslated()
    {
        string crlf = PlannerSamples.Inline["csharp"];
        string lf = crlf.Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Equal(English(crlf), English(lf));
        PlannerSamples.AssertInvariants(lf, PlannerSamples.Plan(lf, PinTranslationDirection.EnglishToKorean, PinTextFormat.Code));
    }

    [Fact]
    public void PythonDocstringsAndDirectivesAreLeftAlone()
    {
        string[] sources = English(PlannerSamples.Inline["python"]);
        Assert.Equal(["Load the configuration file from the disk.", "return the loaded text", "it's the number one"], sources);
    }

    [Fact]
    public void PythonCommentMarkerInsideStringsIsNotAComment()
    {
        const string source = "x = \"# not a comment in a string\"\ny = 'also # not a comment here'  # this one is a comment\nz = '''\n# inside triple quotes\n'''\n";
        Assert.Equal(["this one is a comment"], English(source));
    }

    [Fact]
    public void JavaScriptTemplateLiteralsDocTagsAndDirectives()
    {
        string[] sources = English(PlannerSamples.Inline["javascript"]);
        Assert.Equal(
        [
            "Fetch the user profile from the server.",
            "template literal with slashes",
            "Adds two numbers together.",
            "The first number.",
            "The sum of both numbers.",
            "matches one or more letters",
        ], sources);
    }

    [Fact]
    public void JavaScriptDocTagPrefixesStayLiteral()
    {
        PinTranslationPlan plan = PlannerSamples.Plan(PlannerSamples.Inline["javascript"], PinTranslationDirection.EnglishToKorean, PinTextFormat.Code);
        string output = plan.Assemble([.. plan.Sources.Select(static _ => (string?)"X")]);
        Assert.Contains(" * @param {number} a X\n", output, StringComparison.Ordinal);
        Assert.Contains(" * @returns {number} X\n", output, StringComparison.Ordinal);
        Assert.Contains("// eslint-disable-line", output, StringComparison.Ordinal);
        Assert.Contains("// @ts-ignore", output, StringComparison.Ordinal);
        Assert.Contains("`http://example.com/${id}//x`", output, StringComparison.Ordinal);
    }

    [Fact]
    public void UrlsInsideCommentsAndCodeDoNotStartNewComments()
    {
        const string source = "var a = 1; // see http://example.com/a//b for details about this call\nvar b = \"http://x.org//y\";\n";
        Assert.Equal(["see http://example.com/a//b for details about this call"], English(source));
    }

    [Fact]
    public void ApostrophesInCommentsAndNestedBlockMarkersAreHandled()
    {
        const string source = "int f() { // don't forget that it's here\n  /* outer /* not nested in C */ return 0; }\n// and that's all, folks\n";
        Assert.Equal(["don't forget that it's here", "outer /* not nested in C", "and that's all, folks"], English(source));
    }

    [Fact]
    public void BlockCommentsSpanLinesAndKeepLeadingStars()
    {
        const string source = "/*\n * Licensed under the MIT License.\n * See the LICENSE file in the project root.\n */\nint main() { return 0; }\n";
        PinTranslationPlan plan = PlannerSamples.Plan(source, PinTranslationDirection.EnglishToKorean, PinTextFormat.Code);
        Assert.Equal(["Licensed under the MIT License.", "See the LICENSE file in the project root."], plan.Sources);
        Assert.Equal("/*\n * A\n * B\n */\nint main() { return 0; }\n", plan.Assemble(["A", "B"]));
    }

    [Fact]
    public void XmlDocTagsAreSplitOutAndCodeBlocksAreSkipped()
    {
        const string source = "/// <summary>Returns the <see cref=\"Foo\"/> value when it is available.</summary>\n/// <code>\n/// var result = Compute the total of all items;\n/// </code>\nint F() { return 1; }\n";
        PinTranslationPlan plan = PlannerSamples.Plan(source, PinTranslationDirection.EnglishToKorean, PinTextFormat.Code);
        Assert.Equal(["Returns the", "value when it is available."], plan.Sources);
        Assert.Equal("/// <summary>A <see cref=\"Foo\"/> B</summary>\n/// <code>\n/// var result = Compute the total of all items;\n/// </code>\nint F() { return 1; }\n", plan.Assemble(["A", "B"]));
    }

    [Fact]
    public void KoreanCommentsAreTranslatedForKoreanToEnglish()
    {
        const string source = "// 사용자 정보를 불러옵니다\nint Load() { return 1; } // returns one for now\n";
        Assert.Equal(["사용자 정보를 불러옵니다"], PlannerSamples.Sources(source, PinTranslationDirection.KoreanToEnglish, PinTextFormat.Code));
        Assert.Equal(["returns one for now"], English(source));
    }

    [Fact]
    public void SqlDashCommentsAreCommentsOnlyWithStrongSqlKeywords()
    {
        Assert.Equal(["Select all active users from the table", "only active ones here"], English(PlannerSamples.Inline["sql"]));
        // Without strong SQL keywords '--' is not a comment marker.
        Assert.Empty(English("x = a -- b and some other words here\n"));
    }

    [Fact]
    public void HashFamilyYamlAndShellCommentsAreTranslated()
    {
        Assert.Equal(["Server settings for the production environment", "the public host name of the server"], English(PlannerSamples.Inline["yaml"]));
        Assert.Equal(["Install the dependencies first before building", "quiet install of curl"], English(PlannerSamples.Inline["shell"]));
    }

    [Fact]
    public void ProseWithStrayHashAndUrlGetsNoCommentPieces()
    {
        string source = PlannerSamples.Inline["stray-marks"];
        PinTranslationPlan plan = PlannerSamples.Plan(source, PinTranslationDirection.EnglishToKorean);
        Assert.Equal(PinTextFormat.Prose, plan.Format);
        Assert.DoesNotContain(plan.Pieces, static piece => piece.Kind == PlanKind.Comment);
    }

    [Fact]
    public void CommentedOutCodeDirectivesAndShebangStayLiteral()
    {
        const string source = "#!/usr/bin/env python\n# -*- coding: utf-8 -*-\n# x = compute(1, 2)\n# print(value)\n# noqa: E501\n# pylint: disable=unused-import\n#region Helpers\n# fmt: off\n"
            + "def f(a):\n    # n = number of items to process here\n    return a\n";
        Assert.Equal(["n = number of items to process here"], English(source));
    }

    [Fact]
    public void UnterminatedStringsAndBlocksNeverThrow()
    {
        foreach (string source in new[] { "x = \"unclosed // comment words follow here\n", "/* never closed with some words", "`template never closed // x", "s = '''never closed # comment words" })
        {
            PinTranslationPlan plan = PlannerSamples.Plan(source, PinTranslationDirection.EnglishToKorean, PinTextFormat.Code);
            PlannerSamples.AssertInvariants(source, plan);
        }
    }

    [Fact]
    public void SampleFilesKeepTheirCode()
    {
        foreach (string name in new[] { "T5-comments.cs", "T5-comments.py", "T5-comments.js" })
        {
            if (PlannerSamples.Load(name) is not { } source) continue;
            Assert.Equal(PinTextFormat.Code, PinTranslationPlanner.Classify(source, false, false));
            PinTranslationPlan plan = PlannerSamples.Plan(source, PinTranslationDirection.EnglishToKorean, PinTextFormat.Code);
            PlannerSamples.AssertInvariants(source, plan);
            Assert.True(plan.HasTranslatable, name);
        }
    }
}
