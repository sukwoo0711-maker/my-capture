using System.IO;
using Xunit;
using MyCapture.App.Pinning;

namespace MyCapture.App.Tests;

public sealed class PinTranslationPlannerMarkdownTests
{
    private const PinTranslationDirection ToKorean = PinTranslationDirection.EnglishToKorean;
    private const PinTranslationDirection ToEnglish = PinTranslationDirection.KoreanToEnglish;

    // ---- shared helpers (also used by the Mermaid tests) -----------------------------------------------

    internal static string SampleDirectory => @"C:\GitRepositories\my-capture\work\pin-content-20261027\test-samples";

    internal static IEnumerable<string> SampleFiles(string pattern) =>
        Directory.Exists(SampleDirectory) ? Directory.GetFiles(SampleDirectory, pattern) : [];

    private static PinTranslationPlan Plan(string source, PinTranslationDirection direction = ToKorean) =>
        PinTranslationPlanner.CreateMarkdown(source, direction);

    private static string[] Sources(string source, PinTranslationDirection direction = ToKorean) =>
        [.. Plan(source, direction).Sources];

    /// <summary>Checks the invariants every plan must keep: exact round trip, literal pieces untouched, no overlap.</summary>
    internal static void AssertInvariants(PinTranslationPlan plan, string source)
    {
        Assert.Equal(source, plan.Original);
        Assert.Equal(source, plan.Assemble(new string?[plan.Sources.Count]));
        Assert.All(plan.Pieces, static piece => Assert.NotEqual(string.Empty, piece.Raw));

        string[] upper = [.. plan.Sources.Select(static text => text.ToUpperInvariant())];
        string assembled = plan.Assemble(upper);
        int position = 0, index = 0;
        foreach (PlanPiece piece in plan.Pieces)
        {
            string emitted = piece.Translate ? piece.Emit(upper[index++]) : piece.Raw;
            Assert.True(string.CompareOrdinal(assembled, position, emitted, 0, emitted.Length) == 0,
                $"piece changed or moved: {piece}");
            position += emitted.Length;
        }
        Assert.Equal(assembled.Length, position);
        Assert.All(plan.Pieces.Where(static piece => !piece.Translate), static piece => Assert.Equal(PlanKind.Literal, piece.Kind));
    }

    internal static void AssertMarkdownInvariants(string source, PinTranslationDirection direction)
    {
        PinTranslationPlan plan = Plan(source, direction);
        Assert.Equal(PinTextFormat.Markdown, plan.Format);
        AssertInvariants(plan, source);

        string crlf = source.Replace("\r\n", "\n").Replace("\n", "\r\n");
        PinTranslationPlan windows = Plan(crlf, direction);
        AssertInvariants(windows, crlf);
        Assert.Equal(plan.Sources.Count, windows.Sources.Count);
        Assert.Equal(plan.Sources, windows.Sources);
    }

    internal const string Mixed = """
        # Release checklist

        Review the changes before you merge them into the main branch.
        Use `git status` to confirm the working tree is clean.

        - [x] Build the solution
        - [ ] Run the **unit tests** again
          1. Open the test explorer
          2. Select the failing tests

        > Remember to update the [changelog](https://example.com/changelog).
        > See https://example.com/docs/guide for details.

        | Name | Description |
        | --- | :---: |
        | Alpha | The first option |
        | `beta` | Escaped \| pipe stays in the cell |

        ```csharp
        // This comment stays untouched
        var message = "Hello world";
        ```

        ---
        Final remarks follow below.
        """;

    internal const string KoreanMixed = """
        # 배포 전 확인

        변경 내용을 **한눈에 확인**합니다.

        - [x] 빌드 확인
        - [ ] 최종 화면 확인

        > 원문은 그대로 보존합니다.

        | 항목 | 상태 |
        | --- | --- |
        | Markdown | 자동 렌더링 |
        | 코드 | 원문 유지 |

        ```js
        const timeout = 5000;
        ```
        """;

    internal const string MarkdownWithMermaid = """
        # 작업 흐름

        복사한 내용을 핀으로 확인하고 원문을 다시 사용할 수 있습니다.

        ```mermaid
        flowchart TD
            copy["텍스트 복사"] --> pin["핀으로 확인"]
            pin --> source["원문 다시 사용"]
        ```

        - 화면에서 문서와 그림을 확인합니다.
        - Ctrl+더블클릭으로 원문을 복사합니다.
        - 메모장에 붙여 넣습니다.
        """;

    internal const string EnglishWithMermaid = """
        ## Workflow

        Copy the text and check it in a pin.

        ```mermaid
        flowchart LR
            copy["Copy text"] --> pin["Check in pin"]
            pin -->|done| source[Use the original]
        ```

        ~~~~mermaid
        sequenceDiagram
            participant U as User
            U->>P: Press the hotkey
        ~~~~

        * Open the document.
        """;

    internal static IEnumerable<string> AllSamples()
    {
        yield return Mixed;
        yield return KoreanMixed;
        yield return MarkdownWithMermaid;
        yield return EnglishWithMermaid;
        yield return "";
        yield return "\n";
        yield return "plain text only, no markdown here at all";
        yield return "---\ntitle: Front matter\ntags: [a, b]\n---\n# Heading after front matter\n";
        yield return "Unclosed fence follows:\n```python\nprint('this is code')\nstill code";
        yield return "    indented code block\n\nParagraph text goes here.\n";
        yield return "- item one\n    - nested item two\n        - deep item three\n";
        yield return "1. First step\n\n       code inside the list\n\n2. Second step\n";
        yield return "<div align=\"center\">\n  <img src=\"logo.png\">\n  Welcome to the project\n</div>\n\nA paragraph after html.\n";
        yield return "<!-- a multi line\ncomment that spans lines -->\nVisible text appears here.\n";
        yield return "Title line\n=========\n\nSubtitle line\n---------\n\nBody text appears here.\n";
        yield return "[ref]: https://example.com/page \"Title text\"\n\nUse the [reference][ref] link here.\n";
        yield return "Line with trailing spaces for a break  \nnext line continues the paragraph here\r\nand a third line follows.\r\n";
        yield return "> [!WARNING]\n> Be careful with this operation today.\n";
        yield return "![A screenshot of the app](images/shot.png)\n\n[![Build status](https://img.shields.io/b.svg)](https://ci.example.com)\n";
        yield return "Footnote reference here.[^1]\n\n[^1]: The footnote text explains the claim.\n";
        yield return "\uFEFF# BOM heading\r\n\r\nText after the byte order mark.\r\n";
        yield return "| a | b |\n| - | - |\n| one two three | four five six |\n| single row without pipe at the end\n";
        yield return "$$\nx = y + z\n$$\n\nThe equation above is simple.\n";
    }

    // ---- invariants -----------------------------------------------------------------------------------

    [Fact]
    public void Every_sample_round_trips_in_both_directions_and_line_endings()
    {
        foreach (string sample in AllSamples())
        {
            AssertMarkdownInvariants(sample, ToKorean);
            AssertMarkdownInvariants(sample, ToEnglish);
        }
    }

    [Fact]
    public void Sample_files_round_trip_when_present()
    {
        foreach (string file in SampleFiles("*.md"))
        {
            string source = File.ReadAllText(file);
            AssertMarkdownInvariants(source, ToKorean);
            AssertMarkdownInvariants(source, ToEnglish);
        }
    }

    [Fact]
    public void Empty_source_has_no_pieces()
    {
        PinTranslationPlan plan = Plan("");
        Assert.Empty(plan.Pieces);
        Assert.False(plan.HasTranslatable);
    }

    // ---- block prefixes -------------------------------------------------------------------------------

    [Fact]
    public void Heading_keeps_its_marker_and_closing_hashes_literal()
    {
        PinTranslationPlan plan = Plan("## Getting started quickly ##\n");
        PlanPiece piece = Assert.Single(plan.Pieces, static piece => piece.Translate);
        Assert.Equal("Getting started quickly", piece.Raw);
        Assert.Equal(PlanKind.MarkdownLine, piece.Kind);
        Assert.Equal("## ", plan.Pieces[0].Raw);
        Assert.StartsWith(" ##", plan.Pieces[^1].Raw);
    }

    [Fact]
    public void Hash_without_space_is_a_paragraph_not_a_heading()
    {
        Assert.Equal(["#hashtag followed by words"], Sources("#hashtag followed by words\n"));
    }

    [Fact]
    public void List_markers_task_boxes_and_numbers_stay_literal()
    {
        string source = "- [x] Build the solution\n* [ ] Run all the tests\n3) Deploy to staging\n+ Plain bullet item\n";
        PinTranslationPlan plan = Plan(source);
        Assert.Equal(["Build the solution", "Run all the tests", "Deploy to staging", "Plain bullet item"], plan.Sources);
        Assert.All(plan.Pieces.Where(static piece => piece.Translate), static piece => Assert.Equal(PlanKind.MarkdownLine, piece.Kind));
        Assert.StartsWith("- [x] ", plan.Pieces[0].Raw);
    }

    [Fact]
    public void Nested_lists_and_quotes_keep_their_prefixes()
    {
        string source = "- first item here\n    - nested second item\n        - deeper third item\n> > nested quote text here\n> - quoted list entry\n";
        Assert.Equal(
            ["first item here", "nested second item", "deeper third item", "nested quote text here", "quoted list entry"],
            Sources(source));
    }

    [Fact]
    public void Block_quote_with_alert_marker_keeps_the_marker_literal()
    {
        Assert.Equal(["Be careful with this operation today."], Sources("> [!WARNING]\n> Be careful with this operation today.\n"));
    }

    [Fact]
    public void Footnote_definition_text_is_translatable_and_its_label_is_not()
    {
        PinTranslationPlan plan = Plan("[^1]: The footnote text explains the claim.\n");
        Assert.Equal(["The footnote text explains the claim."], plan.Sources);
        Assert.Equal("[^1]: ", plan.Pieces[0].Raw);
    }

    [Fact]
    public void Inline_syntax_stays_inside_the_piece()
    {
        const string line = "Use the **bold** option, `code`, a [link](https://example.com/x) and <kbd>Ctrl</kbd> here.";
        PlanPiece piece = Assert.Single(Plan(line + "\n").Pieces, static piece => piece.Translate);
        Assert.Equal(line, piece.Raw);
    }

    [Fact]
    public void Leading_and_trailing_whitespace_is_literal()
    {
        PinTranslationPlan plan = Plan("  Indented text with trailing space.   \n");
        Assert.Equal(["Indented text with trailing space."], plan.Sources);
        Assert.Equal("  ", plan.Pieces[0].Raw);
        Assert.Equal("   \n", plan.Pieces[^1].Raw);
    }

    // ---- literal blocks -------------------------------------------------------------------------------

    [Theory]
    [InlineData("```\nThis is a code comment that looks like prose\n```\n")]
    [InlineData("~~~text\nThis is a code comment that looks like prose\n~~~\n")]
    [InlineData("````\n```\nnested fence text inside\n```\nthe outer fence has not closed\n````\n")]
    [InlineData("```js\nunclosed code block with english prose text\n")]
    [InlineData("    indented code with english words here\n")]
    [InlineData("\tTabbed code with english words here\n")]
    [InlineData("<!-- A comment with english words -->\n")]
    [InlineData("<!--\nmulti line comment with english words\n-->\n")]
    [InlineData("[ref]: https://example.com/path \"Some title words\"\n")]
    [InlineData("***\n")]
    [InlineData("- - -\n")]
    [InlineData("___\n")]
    [InlineData("| --- | :---: | ---: |\n")]
    [InlineData("<div class=\"note\">\nThis plain text sits inside an html block\n</div>\n")]
    [InlineData("<details>\n<summary>Click to expand the section</summary>\nHidden words appear here\n</details>\n")]
    [InlineData("$$\nsome english words inside math\n$$\n")]
    [InlineData("---\ntitle: Some front matter title\ndescription: text in yaml\n---\n")]
    public void Literal_blocks_have_nothing_to_translate(string source)
    {
        PinTranslationPlan plan = Plan(source);
        Assert.False(plan.HasTranslatable, string.Join(" | ", plan.Sources));
        AssertInvariants(plan, source);
    }

    [Fact]
    public void Text_after_a_closed_fence_is_translatable_again()
    {
        Assert.Equal(["Text after the code."], Sources("```\ncode words here\n```\nText after the code.\n"));
    }

    [Fact]
    public void Longer_opening_fence_needs_a_longer_closing_fence()
    {
        string source = "````\n```\nstill code words here\n```\n````\nAfter the block ends.\n";
        Assert.Equal(["After the block ends."], Sources(source));
    }

    [Fact]
    public void Html_block_ends_at_the_blank_line()
    {
        string source = "<div>\nInside the block text\n</div>\n\nOutside the block text follows.\n";
        Assert.Equal(["Outside the block text follows."], Sources(source));
    }

    [Fact]
    public void Setext_underline_is_literal_and_heading_text_is_not()
    {
        PinTranslationPlan plan = Plan("Getting started guide\n=====================\n");
        Assert.Equal(["Getting started guide"], plan.Sources);
        Assert.Equal("\n=====================\n", plan.Pieces[^1].Raw);
    }

    [Fact]
    public void Front_matter_is_literal_only_when_closed_and_yaml_like()
    {
        Assert.Equal(["Heading after front matter"], Sources("---\ntitle: Front matter\n---\n# Heading after front matter\n"));
        // An unrelated pair of rules around prose is not front matter.
        Assert.Equal(["Prose between two rules stays translatable."], Sources("---\nProse between two rules stays translatable.\n---\n"));
    }

    [Fact]
    public void Indented_line_after_a_paragraph_is_a_continuation_not_code()
    {
        Assert.NotEmpty(Sources("A paragraph line without a final stop\n    indented continuation text here\n"));
    }

    [Fact]
    public void Code_block_inside_a_list_item_stays_literal()
    {
        string source = "1. Run the installer:\n\n       npm install everything now\n\n2. Restart the application.\n";
        Assert.Equal(["Run the installer:", "Restart the application."], Sources(source));
    }

    [Fact]
    public void Fence_inside_a_list_item_is_literal()
    {
        string source = "- Install it:\n  ```bash\n  npm install some words\n  ```\n- Done with the setup.\n";
        Assert.Equal(["Install it:", "Done with the setup."], Sources(source));
    }

    // ---- inline protections ---------------------------------------------------------------------------

    [Theory]
    [InlineData("https://example.com/docs/getting-started")]
    [InlineData("<https://example.com/docs/getting-started>")]
    [InlineData("www.example.com/some/page")]
    [InlineData("person.name@example.com")]
    [InlineData("C:\\Users\\someone\\Documents\\notes\\readme")]
    [InlineData("/usr/local/share/documentation")]
    [InlineData("./relative/path/to/file")]
    [InlineData("![Alt text words](images/screenshot.png)")]
    [InlineData("[![Build status](https://img.shields.io/badge.svg)](https://ci.example.com/build) [![License](https://img.shields.io/l.svg)](LICENSE)")]
    [InlineData("12345.67")]
    [InlineData("`just inline code words`")]
    [InlineData("- https://example.com/docs/getting-started")]
    public void Protected_lines_are_literal(string line)
    {
        Assert.False(Plan(line + "\n").HasTranslatable);
    }

    [Fact]
    public void Url_line_inside_a_paragraph_does_not_join_with_its_neighbours()
    {
        string source = "See the documentation at the address below\nhttps://example.com/documentation\nand then continue reading the text\n";
        Assert.Equal(["See the documentation at the address below", "and then continue reading the text"], Sources(source));
    }

    // ---- hard wrapped paragraphs ----------------------------------------------------------------------

    [Fact]
    public void Hard_wrapped_paragraph_is_one_piece_with_joined_content()
    {
        const string source = "This tool pins clipboard text and lets you translate it\n  without leaving the window, so the original text stays\navailable for copying.\n";
        PinTranslationPlan plan = Plan(source);
        PlanPiece piece = Assert.Single(plan.Pieces, static piece => piece.Translate);
        Assert.Equal("This tool pins clipboard text and lets you translate it\n  without leaving the window, so the original text stays\navailable for copying.", piece.Raw);
        Assert.Equal("This tool pins clipboard text and lets you translate it without leaving the window, so the original text stays available for copying.", piece.Content);
        Assert.Equal(piece.Content, Assert.Single(plan.Sources));
        Assert.Equal("\n", plan.Pieces[^1].Raw);
        // The translation replaces the whole wrapped span with one line.
        Assert.Equal("번역\n", plan.Assemble(["번역"]));
    }

    [Fact]
    public void Crlf_wrapped_paragraph_joins_without_carriage_returns_in_content()
    {
        const string source = "The first half of a sentence that is wrapped\r\nat the right margin of the file.\r\n";
        PlanPiece piece = Assert.Single(Plan(source).Pieces, static piece => piece.Translate);
        Assert.DoesNotContain('\r', piece.Source);
        Assert.Contains("\r\n", piece.Raw);
    }

    [Fact]
    public void Complete_sentences_on_separate_lines_are_not_joined()
    {
        Assert.Equal(["First sentence ends here.", "Second sentence ends here."], Sources("First sentence ends here.\nSecond sentence ends here.\n"));
    }

    [Fact]
    public void Short_label_lines_are_not_joined_with_the_following_text()
    {
        Assert.Equal(["Overview", "This section describes everything."], Sources("Overview\nThis section describes everything.\n"));
    }

    [Fact]
    public void Hard_line_breaks_end_a_join()
    {
        string trailingSpaces = "A line that carries a hard break at its end  \nis followed by another wrapped line here\nand it keeps going on without stop\n";
        Assert.Equal(
            ["A line that carries a hard break at its end", "is followed by another wrapped line here and it keeps going on without stop"],
            Sources(trailingSpaces));

        PinTranslationPlan backslash = Plan("A line that carries a hard break at its end\\\nis the next line of the same text.\n");
        Assert.Equal(["A line that carries a hard break at its end", "is the next line of the same text."], backslash.Sources);
        Assert.Equal("\\\n", backslash.Pieces[1].Raw);
    }

    [Fact]
    public void Wrapped_group_ends_at_a_list_or_heading_start()
    {
        string source = "A wrapped paragraph that runs over two lines in the file\nbecause the author used a narrow editor window\n- a list item\n";
        Assert.Equal(
            ["A wrapped paragraph that runs over two lines in the file because the author used a narrow editor window", "a list item"],
            Sources(source));
    }

    // ---- tables ---------------------------------------------------------------------------------------

    [Fact]
    public void Table_cells_are_separate_pieces_and_pipes_stay_literal()
    {
        string source = "| Name | Description |\n| --- | :---: |\n| Alpha | The first option |\n| `beta` | Escaped \\| pipe stays here |\n";
        PinTranslationPlan plan = Plan(source);
        Assert.Equal(["Name", "Description", "Alpha", "The first option", "Escaped \\| pipe stays here"], plan.Sources);
        Assert.All(plan.Pieces.Where(static piece => piece.Translate), static piece => Assert.Equal(PlanKind.TableCell, piece.Kind));
        Assert.StartsWith("| ", plan.Pieces[0].Raw);
        Assert.Contains("| --- | :---: |", string.Concat(plan.Pieces.Select(static piece => piece.Raw)));
    }

    [Fact]
    public void Table_without_outer_pipes_and_with_inline_code_is_planned()
    {
        string source = "Option | Meaning\n--- | ---\n`--force` | Overwrite existing files\nverbose | Print more details\n";
        Assert.Equal(["Option", "Meaning", "Overwrite existing files", "verbose", "Print more details"], Sources(source));
    }

    [Fact]
    public void Pipe_inside_a_code_span_does_not_split_a_cell()
    {
        string source = "| Command | Notes |\n| --- | --- |\n| `a|b` | Runs the pipeline now |\n";
        Assert.Equal(["Command", "Notes", "Runs the pipeline now"], Sources(source));
    }

    [Fact]
    public void Rows_without_a_valid_separator_are_normal_lines()
    {
        PinTranslationPlan plan = Plan("| Name | Description |\n| not a separator | because of text |\n");
        Assert.All(plan.Pieces.Where(static piece => piece.Translate), static piece => Assert.Equal(PlanKind.MarkdownLine, piece.Kind));
    }

    [Fact]
    public void Table_with_a_different_column_count_in_the_separator_is_not_a_table()
    {
        PinTranslationPlan plan = Plan("| Name | Description |\n| --- |\n| Alpha | first |\n");
        Assert.DoesNotContain(plan.Pieces, static piece => piece.Kind == PlanKind.TableCell);
    }

    [Fact]
    public void Table_inside_a_block_quote_is_planned_by_cell()
    {
        string source = "> | Name | Meaning |\n> | --- | --- |\n> | Alpha | The first one |\n";
        PinTranslationPlan plan = Plan(source);
        Assert.Equal(["Name", "Meaning", "Alpha", "The first one"], plan.Sources);
    }

    [Fact]
    public void Table_cells_translate_by_language_per_cell()
    {
        string source = "| 항목 | Status |\n| --- | --- |\n| 코드 | Done |\n";
        Assert.Equal(["항목", "코드"], Sources(source, ToEnglish));
        Assert.Equal(["Status", "Done"], Sources(source, ToKorean));
    }

    // ---- mermaid fences -------------------------------------------------------------------------------

    [Fact]
    public void Mermaid_fence_body_is_planned_as_a_diagram_and_fence_lines_stay_literal()
    {
        PinTranslationPlan plan = Plan(EnglishWithMermaid);
        Assert.Equal(
            ["Workflow", "Copy the text and check it in a pin.", "Copy text", "Check in pin", "done", "Use the original", "User",
                "Press the hotkey", "Open the document."],
            plan.Sources);
        Assert.Contains(plan.Pieces, static piece => piece.Kind == PlanKind.MermaidLabel);
        string text = plan.Assemble([.. plan.Sources.Select(static source => source.ToUpperInvariant())]);
        Assert.Contains("```mermaid\nflowchart LR\n", text);
        Assert.Contains("copy[COPY TEXT]", text);
        Assert.Contains("~~~~mermaid\nsequenceDiagram\n", text);
        Assert.Equal(EnglishWithMermaid.Split('\n').Length, text.Split('\n').Length);
    }

    [Fact]
    public void Korean_markdown_with_a_mermaid_fence_translates_text_and_labels()
    {
        PinTranslationPlan plan = Plan(MarkdownWithMermaid, ToEnglish);
        Assert.Equal(
            ["작업 흐름", "복사한 내용을 핀으로 확인하고 원문을 다시 사용할 수 있습니다.", "텍스트 복사", "핀으로 확인", "원문 다시 사용",
                "화면에서 문서와 그림을 확인합니다.", "Ctrl+더블클릭으로 원문을 복사합니다.", "메모장에 붙여 넣습니다."],
            plan.Sources);
    }

    [Fact]
    public void Mermaid_fence_must_use_the_exact_info_string()
    {
        // The renderer only draws an exact "mermaid" fence; any other info string is plain code.
        string source = "```mermaid-ish\nflowchart LR\n    a[\"English label\"] --> b\n```\n";
        Assert.False(Plan(source).HasTranslatable);
    }

    [Fact]
    public void Unclosed_mermaid_fence_runs_to_the_end_of_the_text()
    {
        string source = "```mermaid\nflowchart LR\n    a[Start here] --> b[Stop here]\n";
        Assert.Equal(["Start here", "Stop here"], Sources(source));
    }

    [Fact]
    public void Mermaid_fence_inside_a_list_item_is_planned_like_the_renderer_draws_it()
    {
        // The renderer extracts a mermaid fence with up to three spaces of indentation wherever it appears.
        string source = "- Diagram:\n\n  ```mermaid\n  flowchart LR\n      a[Start here] --> b\n  ```\n";
        Assert.Equal(["Diagram:", "Start here"], Sources(source));
    }

    [Fact]
    public void Pure_diagram_text_is_planned_by_diagram_rules_even_through_the_markdown_entry()
    {
        const string source = "flowchart TD\n    a[Start here] --> b[Stop here]\n";
        PinTranslationPlan plan = Plan(source);
        Assert.Equal(PinTextFormat.Markdown, plan.Format);
        Assert.Equal(["Start here", "Stop here"], plan.Sources);
    }

    // ---- direction ------------------------------------------------------------------------------------

    [Fact]
    public void Korean_direction_translates_korean_lines_only()
    {
        string source = "# 설치 안내\n\nThis English line stays as it is.\n\n- 먼저 프로그램을 내려받습니다.\n- Install the package.\n";
        Assert.Equal(["설치 안내", "먼저 프로그램을 내려받습니다."], Sources(source, ToEnglish));
        Assert.Equal(["This English line stays as it is.", "Install the package."], Sources(source, ToKorean));
    }

    [Fact]
    public void Korean_sample_plans_headings_lists_quotes_and_cells()
    {
        PinTranslationPlan plan = Plan(KoreanMixed, ToEnglish);
        Assert.Equal(
            ["배포 전 확인", "변경 내용을 **한눈에 확인**합니다.", "빌드 확인", "최종 화면 확인", "원문은 그대로 보존합니다.", "항목", "상태",
                "자동 렌더링", "코드", "원문 유지"],
            plan.Sources);
    }

    [Fact]
    public void Mixed_sample_matches_the_expected_pieces()
    {
        PinTranslationPlan plan = Plan(Mixed);
        Assert.Equal(
            [
                "Release checklist",
                "Review the changes before you merge them into the main branch.",
                "Use `git status` to confirm the working tree is clean.",
                "Build the solution",
                "Run the **unit tests** again",
                "Open the test explorer",
                "Select the failing tests",
                "Remember to update the [changelog](https://example.com/changelog).",
                "See https://example.com/docs/guide for details.",
                "Name", "Description", "Alpha", "The first option", "Escaped \\| pipe stays in the cell",
                "Final remarks follow below.",
            ],
            plan.Sources);
    }
}
