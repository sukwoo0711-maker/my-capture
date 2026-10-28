using System.IO;
using Xunit;
using System.Text.RegularExpressions;
using MyCapture.App.Pinning;

namespace MyCapture.App.Tests;

public sealed class PinTranslationPlannerMermaidTests
{
    private const PinTranslationDirection ToKorean = PinTranslationDirection.EnglishToKorean;
    private const PinTranslationDirection ToEnglish = PinTranslationDirection.KoreanToEnglish;

    private const string Flowchart = """
        flowchart LR
            %% a comment with english words
            n1[Rectangle] --> n2(Rounded)
            n2 --> n3((Circle)) --> n4{Decision}
            n4 -->|Yes| n5{{Hexagon}}
            n4 -- Not yet --> n6[[Subroutine]]
            n5 -.-> n7[(Database)]
            n6 ==> n8([Stadium])
            n7 -. maybe .-> n9[/Parallelogram/]
            n8 == strong ==> n10[\Alt parallelogram\]
            n9 --- n11>Asymmetric]
            n10 --> n12["Quoted label"]
            n12 --> n13("Quoted round")
            subgraph g1 [First group]
                n14[Inner node]
            end
            subgraph Second Group
                n15[Other node]
            end
            classDef green fill:#9f6,stroke:#333
            class n1,n2 green
            style n3 fill:#f9f
            click n1 callback "Tooltip text"
        """;

    private const string Sequence = """
        sequenceDiagram
            title Login flow
            participant U as User
            actor A as Admin Person
            participant S
            autonumber
            box Aqua Backend
            participant DB as Database
            end
            U->>+S: Send the credentials
            S-->>-U: Return a token
            U-)S: Fire and forget
            S--)U: Async reply
            U-xS: Lost message
            Note left of U: A note here
            Note over U,S: Shared note text
            loop Every minute
                S->>DB: Check status
            end
            alt Success case
                S->>U: Welcome back
            else Failure case
                S->>U: Access denied
            end
            opt Optional step
                U->>S: Maybe later
            end
            par Branch one
                U->>S: First call
            and Branch two
                U->>S: Second call
            end
            critical Establish connection
                U->>S: Connect now
            option Network timeout
                U->>S: Retry later
            end
            break When failed
                U->>S: Stop here
            end
            rect rgb(200, 150, 255)
                U->>S: Colored area
            end
        """;

    private const string StateDiagram = """
        stateDiagram-v2
            [*] --> Still
            Still --> Moving : Start moving
            Moving --> Crash: Hit the wall
            state "Long description here" as long1
            state fork_state <<fork>>
            long1 : Another description line
            note right of Still : Single line note
            note left of Moving
                First note line
                Second note line
            end note
            Crash --> [*]
        """;

    private const string ClassDiagram = """
        classDiagram
            class Animal["Animal with a label"]
            class Duck {
                +String beakColor
                +swim() void
            }
            Animal <|-- Duck : Inheritance link
            Animal "1" --> "*" Zoo : lives in
            Animal : +isMammal() bool
            note "General note text"
            note for Duck "Can fly high"
        """;

    private const string ErDiagram = """
        erDiagram
            CUSTOMER ||--o{ ORDER : places
            CUSTOMER ||--o{ INVOICE : "is billed"
            ORDER ||--|{ LINE-ITEM : contains
            CUSTOMER {
                string name PK "Customer name"
            }
        """;

    private const string KoreanFlowchart = """
        flowchart LR
            copy["텍스트 복사"] --> pin["F3으로 핀"]
            pin --> review{"확인"}
            review -->|완료| source["원문 복사"]
            review -->|수정| copy
        """;

    private const string KoreanSequence = """
        sequenceDiagram
            participant U as 사용자
            participant C as 클립보드
            participant P as 핀
            U->>C: Markdown 복사
            U->>P: F3
            P->>C: 원문 읽기
            P-->>U: 문서 표시
            U->>P: Ctrl+더블클릭
            P->>C: 원문 복사
        """;

    private const string Gantt = "gantt\n    title A Gantt chart\n    dateFormat YYYY-MM-DD\n    section Planning phase\n    Collect requirements :a1, 2024-01-01, 30d\n    Write the design : after a1, 20d\n    section Build phase\n    Implement features : 2024-03-01, 12w\n";
    private const string Pie = "pie showData title Pets adopted by volunteers\n    \"Dogs\" : 386\n    \"Rats Cats\" : 85\n";
    private const string Timeline = "timeline\n    title History of social media\n    section Early years\n        2002 : LinkedIn launched\n        2004 : Facebook opens : Google adds mail\n             : Extra event text\n";
    private const string Journey = "journey\n    title My working day\n    section Go to work\n      Make tea: 5: Me\n      Go upstairs: 3: Me, Cat\n";
    private const string Mindmap = "mindmap\n  root((Central idea))\n    Origins\n      Long history\n    Research\n    id1[Square text]\n    id2(Rounded text)\n    ::icon(fa fa-book)\n    id3{{Hexagon text}}\n    id4))Bang text((\n";

    private static IEnumerable<string> AllSamples()
    {
        yield return Flowchart;
        yield return Sequence;
        yield return StateDiagram;
        yield return ClassDiagram;
        yield return ErDiagram;
        yield return KoreanFlowchart;
        yield return KoreanSequence;
        yield return Gantt;
        yield return Pie;
        yield return Timeline;
        yield return Journey;
        yield return Mindmap;
        yield return "";
        yield return "\n\n";
        yield return "flowchart TD";
        yield return "graph LR;a[First label]-->b[Second label];b-->c;\n";
        yield return "gitGraph\n    commit id: \"Initial commit message\"\n";
        yield return "---\ntitle: Front matter title\n---\nflowchart TD\n    a[First label] --> b[Second label]\n";
        yield return "%%{init: {'theme': 'dark'}}%%\nflowchart TD\r\n    a[First label] --> b[Second label]\r\n";
        yield return "not a diagram at all, just text";
        yield return "quadrantChart\n    title Reach and engagement\n    x-axis Low Reach --> High Reach\n";
        yield return "﻿flowchart TD\n    a[First label] --> b\n";
        yield return "flowchart TD\n    a[\"Unclosed quote label\n    b[Open bracket label\n";
    }

    private static PinTranslationPlan Plan(string source, PinTranslationDirection direction = ToKorean) =>
        PinTranslationPlanner.CreateMermaid(source, direction);

    private static string[] Sources(string source, PinTranslationDirection direction = ToKorean) =>
        [.. Plan(source, direction).Sources];

    private static string Translate(string source, Func<string, string> transform, PinTranslationDirection direction = ToKorean)
    {
        PinTranslationPlan plan = Plan(source, direction);
        return plan.Assemble([.. plan.Sources.Select(transform)]);
    }

    private static void AssertInvariants(string source, PinTranslationDirection direction)
    {
        PinTranslationPlan plan = Plan(source, direction);
        Assert.Equal(PinTextFormat.Mermaid, plan.Format);
        PinTranslationPlannerMarkdownTests.AssertInvariants(plan, source);
        string crlf = source.Replace("\r\n", "\n").Replace("\n", "\r\n");
        PinTranslationPlan windows = Plan(crlf, direction);
        PinTranslationPlannerMarkdownTests.AssertInvariants(windows, crlf);
        Assert.Equal(plan.Sources, windows.Sources);
    }

    // ---- invariants -----------------------------------------------------------------------------------

    [Fact]
    public void Every_sample_round_trips_in_both_directions_and_line_endings()
    {
        foreach (string sample in AllSamples())
        {
            AssertInvariants(sample, ToKorean);
            AssertInvariants(sample, ToEnglish);
        }
    }

    [Fact]
    public void Sample_files_round_trip_when_present()
    {
        foreach (string file in PinTranslationPlannerMarkdownTests.SampleFiles("*.mmd"))
        {
            string source = File.ReadAllText(file);
            AssertInvariants(source, ToKorean);
            AssertInvariants(source, ToEnglish);
        }
    }

    [Fact]
    public void Translating_labels_keeps_ids_arrows_and_line_count()
    {
        foreach (string sample in new[] { Flowchart, Sequence, StateDiagram, ClassDiagram, Gantt, Pie, Timeline, Journey, Mindmap })
        {
            PinTranslationPlan plan = Plan(sample);
            string translated = plan.Assemble([.. plan.Sources.Select(static text => "Translated " + text)]);
            Assert.Equal(sample.Split('\n').Length, translated.Split('\n').Length);
            // Planning the translated text again finds the same labels around the same untouched syntax.
            PinTranslationPlan again = Plan(translated);
            Assert.Equal(Syntax(plan), Syntax(again));
            Assert.Equal(plan.Sources.Select(static text => "Translated " + text), again.Sources);
        }
    }

    /// <summary>The text that is not a label: ids, arrows, keywords, styling and punctuation.</summary>
    private static string Syntax(PinTranslationPlan plan) =>
        string.Concat(plan.Pieces.Where(static piece => !piece.Translate).Select(static piece => piece.Raw));

    // ---- flowchart ------------------------------------------------------------------------------------

    [Fact]
    public void Flowchart_translates_every_label_shape_and_edge_label_form()
    {
        Assert.Equal(
            ["Rectangle", "Rounded", "Circle", "Decision", "Yes", "Hexagon", "Not yet", "Subroutine", "Database", "Stadium", "maybe",
                "Parallelogram", "strong", "Alt parallelogram", "Asymmetric", "Quoted label", "Quoted round", "First group",
                "Inner node", "Second Group", "Other node"],
            Sources(Flowchart));
    }

    [Fact]
    public void Flowchart_labels_are_diagram_labels_and_ids_stay_literal()
    {
        PinTranslationPlan plan = Plan(Flowchart);
        Assert.All(plan.Pieces.Where(static piece => piece.Translate), static piece => Assert.Equal(PlanKind.MermaidLabel, piece.Kind));
        string literal = string.Concat(plan.Pieces.Where(static piece => !piece.Translate).Select(static piece => piece.Raw));
        foreach (string id in new[] { "n1", "n5", "n10", "n15", "classDef green", "style n3 fill:#f9f", "click n1 callback \"Tooltip text\"", "%% a comment with english words" })
            Assert.Contains(id, literal);
    }

    [Fact]
    public void Same_node_may_appear_several_times_and_each_occurrence_is_a_piece()
    {
        string source = "flowchart TD\n    a[Start here] --> b[Next step]\n    c --> a[Start here]\n";
        Assert.Equal(["Start here", "Next step", "Start here"], Sources(source));
    }

    [Fact]
    public void Chains_and_several_statements_on_one_line_work()
    {
        Assert.Equal(["One", "Two", "Three", "Four"], Sources("graph LR; a[One] --> b[Two] --> c[Three]; c --> d[Four]\n").Select(static text => text));
        Assert.Equal(["Left", "Right"], Sources("graph TD;a[Left]-->b[Right];\n"));
        Assert.Equal(["First", "Second"], Sources("flowchart TD\n    a[First] & b[Second] --> c\n"));
    }

    [Fact]
    public void Header_line_with_statements_after_the_direction_is_scanned()
    {
        Assert.Equal(["Start here", "End here"], Sources("flowchart TD; a[Start here] --> b[End here]\n"));
    }

    [Fact]
    public void Edge_label_forms_are_covered()
    {
        string source = "flowchart LR\n    a -->|pipe label| b\n    a --> |spaced pipe| b\n    a-- inline text -->b\n    a -. dotted text .-> b\n    a == thick text ==> b\n    a -->|\"quoted pipe\"| b\n    a -- \"quoted inline\" --> b\n";
        Assert.Equal(["pipe label", "spaced pipe", "inline text", "dotted text", "thick text", "quoted pipe", "quoted inline"], Sources(source));
    }

    [Fact]
    public void Plain_arrows_without_labels_have_nothing_to_translate()
    {
        Assert.False(Plan("flowchart TD\n    a --> b\n    b --- c\n    c -.-> d\n    d ==> e\n    e --x f\n    f <--> g\n").HasTranslatable);
    }

    [Fact]
    public void Html_entities_and_line_breaks_stay_inside_the_label()
    {
        PinTranslationPlan plan = Plan("flowchart TD\n    a[First line<br/>Second line &amp; more] --> b\n");
        PlanPiece piece = Assert.Single(plan.Pieces, static piece => piece.Translate);
        Assert.Equal("First line<br/>Second line &amp; more", piece.Raw);
    }

    [Fact]
    public void Markdown_string_labels_keep_their_backticks()
    {
        PinTranslationPlan plan = Plan("flowchart TD\n    a[\"`The **bold** label`\"] --> b\n");
        PlanPiece piece = Assert.Single(plan.Pieces, static piece => piece.Translate);
        Assert.Equal("\"`The **bold** label`\"", piece.Raw);
        Assert.Equal("`The **bold** label`", piece.Content);
        Assert.Contains("`", plan.Assemble(["`Die **fette** Beschriftung`"]));
    }

    [Fact]
    public void Subgraph_id_that_is_referenced_elsewhere_is_not_translated()
    {
        string source = "flowchart TD\n    subgraph Backend\n        a[Server node]\n    end\n    Backend --> b[Client node]\n";
        Assert.Equal(["Server node", "Client node"], Sources(source));
    }

    [Fact]
    public void New_node_syntax_label_is_translated()
    {
        Assert.Equal(["Process data"], Sources("flowchart TD\n    a@{ shape: rect, label: \"Process data\" } --> b\n"));
    }

    // ---- quoting rule ---------------------------------------------------------------------------------

    [Fact]
    public void Unquoted_label_with_syntax_characters_gets_quoted_by_assembly()
    {
        const string source = "flowchart TD\n    n1[Start here] --> n2{Is it ready}\n    n2 -->|Yes please| n3(Done)\n";
        string text = Translate(source, static label => label + " (maybe)");
        Assert.Equal("flowchart TD\n    n1[\"Start here (maybe)\"] --> n2{\"Is it ready (maybe)\"}\n    n2 -->|\"Yes please (maybe)\"| n3(\"Done (maybe)\")\n", text);
    }

    [Fact]
    public void Quote_characters_in_a_translation_become_entities()
    {
        string text = Translate("flowchart TD\n    n1[Start here] --> n2\n", static _ => "Say \"hi\"");
        Assert.Equal("flowchart TD\n    n1[\"Say #quot;hi#quot;\"] --> n2\n", text);
    }

    [Fact]
    public void Safe_translation_stays_unquoted_and_quoted_source_is_never_double_quoted()
    {
        const string source = "flowchart TD\n    n1[Start here] --> n2[\"Quoted label\"]\n";
        Assert.Equal("flowchart TD\n    n1[Start there] --> n2[Quoted there]\n", Translate(source, static label => label.Replace("here", "there").Replace("label", "there")));
        string risky = Translate(source, static label => label + " (x)");
        Assert.Equal("flowchart TD\n    n1[\"Start here (x)\"] --> n2[\"Quoted label (x)\"]\n", risky);
        Assert.DoesNotContain("\"\"", risky);
    }

    [Fact]
    public void Translation_with_line_breaks_is_flattened_to_one_line()
    {
        string text = Translate("flowchart TD\n    n1[Start here] --> n2\n", static _ => "first\nsecond");
        Assert.Equal("flowchart TD\n    n1[first second] --> n2\n", text);
    }

    [Fact]
    public void Korean_translation_of_a_flowchart_keeps_the_structure()
    {
        string text = Translate("flowchart LR\n    n1[Start] --> n2[Finish line]\n", static label => label == "Start" ? "시작" : "결승선");
        Assert.Equal("flowchart LR\n    n1[시작] --> n2[결승선]\n", text);
    }

    // ---- sequence diagram -----------------------------------------------------------------------------

    [Fact]
    public void Sequence_diagram_translates_aliases_messages_notes_and_block_texts()
    {
        Assert.Equal(
            ["Login flow", "User", "Admin Person", "Backend", "Database", "Send the credentials", "Return a token", "Fire and forget",
                "Async reply", "Lost message", "A note here", "Shared note text", "Every minute", "Check status", "Success case",
                "Welcome back", "Failure case", "Access denied", "Optional step", "Maybe later", "Branch one", "First call",
                "Branch two", "Second call", "Establish connection", "Connect now", "Network timeout", "Retry later", "When failed",
                "Stop here", "Colored area"],
            Sources(Sequence));
    }

    [Fact]
    public void Sequence_messages_keep_participants_arrows_and_activation_markers()
    {
        // The raw-string source follows the checkout's line endings; the plan keeps them as given.
        string text = Translate(Sequence, static label => label.ToUpperInvariant()).ReplaceLineEndings("\n");
        Assert.Contains("    U->>+S: SEND THE CREDENTIALS\n", text);
        Assert.Contains("    S-->>-U: RETURN A TOKEN\n", text);
        Assert.Contains("    U-)S: FIRE AND FORGET\n", text);
        Assert.Contains("    participant U as USER\n", text);
        Assert.Contains("    participant S\n", text);
        Assert.Contains("    rect rgb(200, 150, 255)\n", text);
        Assert.Contains("    box Aqua BACKEND\n", text);
    }

    [Fact]
    public void Free_text_labels_are_never_wrapped_in_quotes()
    {
        string text = Translate("sequenceDiagram\n    U->>S: Send it\n    Note over U: Hold on\n", static label => label + " (now)");
        Assert.Equal("sequenceDiagram\n    U->>S: Send it (now)\n    Note over U: Hold on (now)\n", text);
    }

    [Fact]
    public void Box_with_only_a_colour_has_no_label()
    {
        Assert.False(Plan("sequenceDiagram\n    box Aqua\n    participant A\n    end\n").HasTranslatable);
        Assert.Equal(["Frontend"], Sources("sequenceDiagram\n    box rgb(33,66,99) Frontend\n    participant A\n    end\n"));
    }

    // ---- other diagram types --------------------------------------------------------------------------

    [Fact]
    public void State_diagram_labels_descriptions_and_notes_are_translated()
    {
        Assert.Equal(
            ["Start moving", "Hit the wall", "Long description here", "Another description line", "Single line note", "First note line",
                "Second note line"],
            Sources(StateDiagram));
    }

    [Fact]
    public void Class_diagram_translates_relation_labels_notes_and_class_labels_only()
    {
        Assert.Equal(["Animal with a label", "Inheritance link", "lives in", "General note text", "Can fly high"], Sources(ClassDiagram));
    }

    [Fact]
    public void Er_diagram_translates_quoted_relationship_labels_only()
    {
        PinTranslationPlan plan = Plan(ErDiagram);
        Assert.Equal(["is billed"], plan.Sources);
        Assert.Contains("INVOICE : \"IS BILLED\"\n", plan.Assemble(["IS BILLED"]).ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Pie_title_and_slice_labels_are_translated_with_their_quotes_kept()
    {
        PinTranslationPlan plan = Plan(Pie);
        Assert.Equal(["Pets adopted by volunteers", "Dogs", "Rats Cats"], plan.Sources);
        string text = plan.Assemble(["Haustiere", "Hunde", "Ratten und Katzen"]);
        Assert.Equal("pie showData title Haustiere\n    \"Hunde\" : 386\n    \"Ratten und Katzen\" : 85\n", text);
    }

    [Fact]
    public void Pie_title_on_its_own_line_is_translated()
    {
        Assert.Equal(["Key elements"], Sources("pie\n    title Key elements\n    \"Calcium\" : 42\n").Take(1).ToArray());
    }

    [Fact]
    public void Gantt_translates_titles_sections_and_task_names_only()
    {
        Assert.Equal(["A Gantt chart", "Planning phase", "Collect requirements", "Write the design", "Build phase", "Implement features"], Sources(Gantt));
    }

    [Fact]
    public void Timeline_translates_titles_sections_and_events_only()
    {
        Assert.Equal(
            ["History of social media", "Early years", "LinkedIn launched", "Facebook opens", "Google adds mail", "Extra event text"],
            Sources(Timeline));
    }

    [Fact]
    public void Journey_translates_titles_sections_and_task_names_only()
    {
        PinTranslationPlan plan = Plan(Journey);
        Assert.Equal(["My working day", "Go to work", "Make tea", "Go upstairs"], plan.Sources);
        Assert.Contains("Go upstairs: 3: Me, Cat", plan.Assemble(["a", "b", "c", "Go upstairs"]));
    }

    [Fact]
    public void Mindmap_translates_node_texts_inside_shapes_and_plain_nodes()
    {
        PinTranslationPlan plan = Plan(Mindmap);
        Assert.Equal(
            ["Central idea", "Origins", "Long history", "Research", "Square text", "Rounded text", "Hexagon text", "Bang text"],
            plan.Sources);
        // Shaped labels follow the quoting rule; plain nodes are free text (the parser rejects a quoted plain node).
        Assert.Equal(
            [PlanKind.MermaidLabel, PlanKind.Prose, PlanKind.Prose, PlanKind.Prose, PlanKind.MermaidLabel, PlanKind.MermaidLabel,
                PlanKind.MermaidLabel, PlanKind.MermaidLabel],
            plan.Pieces.Where(static piece => piece.Translate).Select(static piece => piece.Kind));
        Assert.Contains("id1[\"Square text (x)\"]", plan.Assemble([.. plan.Sources.Select(static text => text + " (x)")]));
    }

    [Theory]
    [InlineData("gitGraph\n    commit id: \"Initial commit message\"\n")]
    [InlineData("quadrantChart\n    title Reach and engagement\n    x-axis Low Reach --> High Reach\n")]
    [InlineData("C4Context\n    title System context diagram\n    Person(user, \"Customer\", \"A customer of the bank\")\n")]
    [InlineData("requirementDiagram\n    requirement test_req {\n    text: the test text.\n    }\n")]
    [InlineData("this is not a diagram, just some english text\n")]
    public void Unsupported_diagram_types_have_nothing_to_translate(string source)
    {
        Assert.False(Plan(source).HasTranslatable);
        AssertInvariants(source, ToKorean);
    }

    [Fact]
    public void Front_matter_and_directives_before_the_declaration_are_skipped()
    {
        Assert.Equal(["First label", "Second label"], Sources("---\ntitle: Front matter title\n---\nflowchart TD\n    a[First label] --> b[Second label]\n"));
        Assert.Equal(["First label"], Sources("%%{init: {'theme': 'dark'}}%%\n%% comment words\nflowchart TD\n    a[First label] --> b\n"));
    }

    [Fact]
    public void Accessibility_texts_are_translated()
    {
        string source = "flowchart TD\n    accTitle: The diagram title\n    accDescr: A short description\n    a --> b\n";
        Assert.Equal(["The diagram title", "A short description"], Sources(source));
        string block = "flowchart TD\n    accDescr {\n    First description line\n    Second description line\n    }\n    a --> b\n";
        Assert.Equal(["First description line", "Second description line"], Sources(block));
    }

    // ---- direction and sample files -------------------------------------------------------------------

    [Fact]
    public void Korean_flowchart_translates_korean_labels_only()
    {
        Assert.Equal(["텍스트 복사", "F3으로 핀", "확인", "완료", "원문 복사", "수정"], Sources(KoreanFlowchart, ToEnglish));
        Assert.False(Plan(KoreanFlowchart, ToKorean).HasTranslatable);
    }

    [Fact]
    public void Korean_sequence_translates_aliases_and_messages_that_are_mostly_korean()
    {
        Assert.Equal(["사용자", "클립보드", "원문 읽기", "문서 표시", "원문 복사"], Sources(KoreanSequence, ToEnglish));
    }

    [Fact]
    public void English_flowchart_is_not_translated_in_the_korean_to_english_direction()
    {
        Assert.False(Plan("flowchart LR\n    a[Copy text] --> b[Check it]\n", ToEnglish).HasTranslatable);
    }
}
