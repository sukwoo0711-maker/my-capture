using System.Globalization;
using System.Windows;
using System.Windows.Media.Imaging;
using MyCapture.App.Pinning;
using MyCapture.Ocr;
using Xunit;
using Xunit.Abstractions;

namespace MyCapture.App.Tests;

/// <summary>Round-two composition decisions, captured before the fitting optimization.</summary>
public sealed class PinTranslationOverlayDecisionTests(ITestOutputHelper output)
{
    [Fact]
    public void DeterministicFixturesKeepRoundTwoDecisions() => StaTestHost.Run(() =>
    {
        var decisions = new List<string>();
        foreach (bool korean in new[] { false, true })
        {
            foreach (string scene in new[] { "light-ui", "dark-ui", "table", "columns", "noisy-photo", "dense-settings" })
            {
                var lines = new List<OcrLine>();
                BitmapSource image = PinTranslationOverlayEvidenceTests.Draw(scene, korean, lines);
                IReadOnlyList<PinOverlayBlock> blocks = PinTranslationOverlay.BuildBlocks(PinTranslationOverlayTests.Recognition(lines.ToArray()));
                var translator = new PinTranslationOverlayTests.FakeTranslator(text => Translate(text, korean));
                PinOverlayTranslation[] translated = blocks.Select(block => new PinOverlayTranslation(block,
                    string.Join(" ", translator.TranslateAsync(block.Lines.Select(line => line.Text).ToArray(), null, CancellationToken.None)
                        .GetAwaiter().GetResult().Select(item => item.Text)))).ToArray();
                Capture((korean ? "ko-en/" : "en-ko/") + scene, image, translated, decisions);
            }
        }

        // The two image-composition fixtures already exercised with FakeTranslator in PinTranslationOverlayTests.
        var safeTranslator = new PinTranslationOverlayTests.FakeTranslator(_ => "문서를 저장합니다.");
        string safe = string.Join(" ", safeTranslator.TranslateAsync(["Save the document.", "Close the window."], null,
            CancellationToken.None).GetAwaiter().GetResult().Select(item => item.Text));
        Capture("existing/batched-unsafe-block", PinTranslationOverlayTests.Bitmap(500, 240),
            [new(Block(PinTranslationOverlayTests.Line("Save the document. Close the window.", 20, 20, 280, 24)), safe),
             new(Block(PinTranslationOverlayTests.Line("Keep 42 documents. Open the folder.", 20, 100, 240, 24)), null)], decisions);
        string longTranslation = string.Join(" ", Enumerable.Repeat("문서", 500));
        var longTranslator = new PinTranslationOverlayTests.FakeTranslator(_ => longTranslation);
        string unfittable = longTranslator.TranslateAsync(["Save document"], null, CancellationToken.None)
            .GetAwaiter().GetResult()[0].Text;
        Capture("existing/unfittable", PinTranslationOverlayTests.Bitmap(100, 40),
            [new(Block(PinTranslationOverlayTests.Line("Save document", 10, 10, 80, 15)), unfittable)], decisions);
        Capture("existing/native-pixels", PinTranslationOverlayTests.Bitmap(320, 160, 144, 192),
            [new(Block(PinTranslationOverlayTests.Line("Save the document", 20, 20, 170, 22)), "문서를 저장합니다")], decisions);
        Capture("existing/fractional-neighbor", PinTranslationOverlayTests.Bitmap(250, 140),
            [new(Block(PinTranslationOverlayTests.Line("Save", 20, 20.2, 80, 20.2)), "저장"),
             new(Block(PinTranslationOverlayTests.Line("Keep", 20, 42.6, 80, 20)), null)], decisions);
        Capture("existing/failed-neighbor", PinTranslationOverlayTests.Bitmap(250, 140),
            [new(Block(PinTranslationOverlayTests.Line("Save the document", 20, 20, 150, 20)), "문서를 저장합니다"),
             new(Block(PinTranslationOverlayTests.Line("Keep 42", 20, 41, 150, 20)), null)], decisions);
        Capture("existing/identity", PinTranslationOverlayTests.Bitmap(250, 140),
            [new(Block(PinTranslationOverlayTests.Line("숨김", 20, 20, 40)), " \t숨김\n"),
             new(Block(PinTranslationOverlayTests.Line("Keep   original", 20, 70, 120)), "Keep original")], decisions);

        string actual = string.Join("\n", decisions);
        Assert.Equal(RoundTwoDecisions.Replace("\r\n", "\n"), actual);
        output.WriteLine("All 18 fixtures match the round-two bounds, font sizes, line breaks, plate and review decisions.");
    });

    private static void Capture(string scene, BitmapSource image, IReadOnlyList<PinOverlayTranslation> translated, List<string> decisions)
    {
        var fitted = new List<string>();
        PinOverlayComposition composition = PinTranslationOverlay.Compose(image, translated, observeFit: (block, fit, plate) =>
            fitted.Add($"{block.Text.Replace("\n", "\\n", StringComparison.Ordinal)}|{Rectangle(block.Bounds)}|{Rectangle(fit.Bounds)}|"
                + $"{Number(fit.FontSize)}|{fit.RenderText.Replace("\n", "\\n", StringComparison.Ordinal)}|{plate}|{fit.Grew}"));
        decisions.Add($"{scene}|{translated.Count}|{composition.RenderedBlocks}|{composition.NeedsReview}");
        decisions.AddRange(fitted);
    }

    private static string Number(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
    private static string Rectangle(OcrRect bounds) => $"{Number(bounds.X)},{Number(bounds.Y)},{Number(bounds.Width)},{Number(bounds.Height)}";
    private static PinOverlayBlock Block(OcrLine line) => new(line.Text, line.Bounds, [line]);

    private static string Translate(string text, bool korean)
    {
        string normalized = PinTranslationOverlay.NormalizeWhitespace(text);
        foreach ((string english, string hangul) in Translations)
            if (string.Equals(normalized, korean ? hangul : english, StringComparison.Ordinal)) return korean ? english : hangul;
        throw new InvalidOperationException($"No deterministic translation for '{text}'.");
    }

    private static readonly (string English, string Korean)[] Translations =
    [
        ("Document library", "문서 라이브러리"), ("File Edit View", "파일 편집 보기"),
        ("Save document", "문서 저장"), ("Open folder", "폴더 열기"), ("Copy picture", "그림 복사"),
        ("Translate selection", "선택 영역 번역"), ("Show original", "원본 보기"), ("Close window", "창 닫기"),
        ("Work list", "작업 목록"), ("Name", "이름"), ("Status", "상태"), ("Count", "수량"),
        ("Document", "문서"), ("Ready", "준비됨"), ("12", "12"), ("Picture", "그림"), ("Saved", "저장됨"), ("24", "24"),
        ("Working with documents and pictures", "문서와 그림 관리"),
        ("Create a new document.", "새 문서를 만듭니다."), ("Save your work regularly.", "작성한 내용을 저장합니다."),
        ("Review the work with your team.", "동료와 작업을 검토합니다."), ("Check the latest changes.", "변경 사항을 확인합니다."),
        ("Add pictures to your library.", "그림을 라이브러리에 추가합니다."), ("Select the area you need.", "필요한 부분을 선택합니다."),
        ("Keep the original picture safe.", "원본을 안전하게 보관합니다."), ("Open it again later.", "나중에 다시 열 수 있습니다."),
        ("Keep your travel memories in photographs", "여행의 추억을 사진으로 남깁니다"),
        ("Explore the mountains and the coast", "산과 바다의 풍경을 둘러봅니다"),
        ("Save your favourite places", "좋아하는 장소를 저장합니다"),
        ("Capture settings", "캡처 설정"), ("File name", "파일 이름"), ("Save location", "저장 위치"),
        ("Image format", "그림 형식"), ("Copy to clipboard", "클립보드 복사"), ("Mouse pointer", "마우스 포인터"),
        ("Show notifications", "알림 표시"), ("Window border", "창 테두리"), ("Keyboard shortcut", "단축키"),
        ("Display scale", "화면 배율"), ("Automatic save", "자동 저장"), ("Keep original", "원본 보관"), ("Colour theme", "색상 테마"),
        ("Automatic", "자동"), ("Documents", "문서"), ("Original", "원본"), ("Enabled", "사용"),
        ("Hidden", "숨김"), ("Visible", "표시"), ("Default", "기본값"), ("Light", "밝게")
    ];

    // Captured from commit 16fc3590 with the observation hook only; values are rounded to one millionth of a pixel.
    private const string RoundTwoDecisions = """
        en-ko/light-ui|8|8|False
        Document library|43.121094,41.522034,256.435028,33.169922|43.121094,41.522034,256.435028,33.169922|34|문서 라이브러리|False|False
        File     Edit     View|42.386719,108.810577,205.819656,19.550781|42.386719,108.810577,205.819656,24.489258|26|파일 편집 보기|False|True
        Save document|65.595215,206.150345,181.518463,20.302734|65.595215,206.150345,181.518463,24.489258|26|문서 저장|False|True
        Open folder|445.239258,205.860306,141.855286,26.48584|445.239258,205.860306,141.855286,26.48584|26|폴더 열기|False|False
        Copy picture|65.193359,339.051788,143.858307,25.124023|65.193359,339.051788,143.858307,25.124023|26|그림 복사|False|False
        Translate selection|64.520508,396.810577,207.689941,19.550781|64.520508,396.810577,207.689941,24.489258|26|선택 영역 번역|False|True
        Show original|65.536133,454.810577,152.465317,25.365234|65.536133,454.810577,152.465317,25.365234|26|원본 보기|False|False
        Close window|65.193359,512.810547,157.015015,19.550781|65.193359,512.810547,157.015015,24.717773|26|창 닫기|False|True
        en-ko/dark-ui|8|8|False
        Document library|43.121094,41.522034,256.435028,33.169922|43.121094,41.522034,256.435028,33.169922|34|문서 라이브러리|False|False
        File     Edit     View|42.386719,108.810577,205.819656,19.550781|42.386719,108.810577,205.819656,24.489258|26|파일 편집 보기|False|True
        Save document|65.595215,206.150345,181.518463,20.302734|65.595215,206.150345,181.518463,24.489258|26|문서 저장|False|True
        Open folder|445.239258,205.860306,141.855286,26.48584|445.239258,205.860306,141.855286,26.48584|26|폴더 열기|False|False
        Copy picture|65.193359,339.051788,143.858307,25.124023|65.193359,339.051788,143.858307,25.124023|26|그림 복사|False|False
        Translate selection|64.520508,396.810577,207.689941,19.550781|64.520508,396.810577,207.689941,24.489258|26|선택 영역 번역|False|True
        Show original|65.536133,454.810577,152.465317,25.365234|65.536133,454.810577,152.465317,25.365234|26|원본 보기|False|False
        Close window|65.193359,512.810547,157.015015,19.550781|65.193359,512.810547,157.015015,24.717773|26|창 닫기|False|True
        en-ko/table|10|8|False
        Work list|40.380859,45.166306,114.248795,22.558594|40.380859,45.166306,128.216667,28.256836|30|작업 목록|False|True
        Name|64.753906,161.367477,75.273407,21.357422|64.753906,161.367477,75.273407,28.256836|30|이름|False|True
        Status|393.772461,161.015915,77.257263,21.708984|393.772461,161.015915,77.257263,28.256836|30|상태|False|True
        Count|723.376953,161.015915,78.122681,21.708984|723.376953,161.015915,78.122681,28.256836|30|수량|False|True
        Document|64.753906,271.367462,134.585739,21.357422|64.753906,271.367462,134.585739,28.256836|30|문서|False|True
        Ready|394.753906,270.16629,77.316223,29.267578|394.753906,270.16629,90,29.267578|30|준비됨|False|True
        Picture|64.753906,380.444611,87.243393,22.280273|64.753906,380.444611,87.243393,27.597656|30|그림|False|True
        Saved|393.772461,380.16629,74.721924,22.558594|393.772461,380.16629,90,28.256836|30|저장됨|False|True
        en-ko/columns|3|3|False
        Working with documents and pictures|40.40625,45.842499,534.612122,31.21875|40.40625,45.842499,534.612122,31.21875|32|문서와 그림 관리|False|False
        Create a new document. Save your work regularly. Review the work with your team. Check the latest changes.|41.193359,138.810577,369.594269,139.365234|41.193359,138.810577,369.594269,139.365234|26|새 문서를 만듭니다. 작성한\n내용을 저장합니다. 동료와\n작업을 검토합니다. 변경 사항을\n확인합니다.|False|False
        Add pictures to your library. Select the area you need. Keep the original picture safe. Open it again later.|590.279297,138.810577,339.022339,139.365234|590.279297,138.810577,469.006667,139.365234|26|그림을 라이브러리에 추가합니다. 필요한\n부분을 선택합니다. 원본을 안전하게\n보관합니다. 나중에 다시 열 수 있습니다.|False|True
        en-ko/noisy-photo|3|3|False
        Keep your travel memories in photographs|45.121094,121.522034,636.701904,33.169922|45.121094,121.522034,636.701904,33.169922|34|여행의 추억을 사진으로 남깁니다|True|False
        Explore the mountains and the coast|44.753906,210.166306,480.095764,29.106445|44.753906,210.166306,480.095764,29.106445|30|산과 바다의 풍경을 둘러봅니다|True|False
        Save your favourite places|43.772461,299.844025,342.007263,29.589844|43.772461,299.844025,376.433333,29.589844|30|좋아하는 장소를 저장합니다|True|True
        en-ko/dense-settings|25|25|False
        Capture settings|26.009766,27.658947,156.737366,21.258789|26.009766,27.658947,156.737366,21.258789|22|캡처 설정|False|False
        File name|26.46875,77.42292,65.537811,12.03125|26.46875,77.42292,68.383333,15.070313|16|파일 이름|False|True
        Save location|25.945313,109.42292,91.504189,12.03125|25.945313,109.42292,91.504189,15.070313|16|저장 위치|False|True
        Image format|26.46875,141.251038,93.763062,15.78125|26.46875,141.251038,93.763062,15.78125|16|그림 형식|False|False
        Copy to clipboard|25.734375,173.422913,124.430618,15.609375|25.734375,173.422913,124.430618,15.609375|16|클립보드 복사|False|False
        Mouse pointer|26.46875,205.57135,101.839996,15.375|26.46875,205.57135,101.839996,15.375|16|마우스 포인터|False|False
        Show notifications|25.945313,237.251038,127.923874,12.203125|25.945313,237.251038,127.923874,15.070313|16|알림 표시|False|True
        Window border|25.203125,269.422913,110.03363,12.03125|25.203125,269.422913,110.03363,15.210938|16|창 테두리|False|True
        Keyboard shortcut|26.46875,301.422913,127.68306,15.609375|26.46875,301.422913,127.68306,15.609375|16|단축키|False|False
        Display scale|26.46875,333.422913,87.87381,15.609375|26.46875,333.422913,87.87381,15.609375|16|화면 배율|False|False
        Automatic save|25.171875,365.57135,107.314697,11.882813|25.171875,365.57135,107.314697,15.070313|16|자동 저장|False|True
        Keep original|26.46875,397.422913,90.021378,15.609375|26.46875,397.422913,90.021378,15.609375|16|원본 보관|False|False
        Colour theme|25.734375,429.422913,95.152191,12.03125|25.734375,429.422913,95.152191,15.070313|16|색상 테마|False|True
        Automatic|380.171875,77.571358,71.985748,11.882813|380.171875,77.571358,71.985748,15.070313|16|자동|False|True
        Documents|381.46875,110.063545,78.312439,11.390625|381.46875,110.063545,78.312439,15.070313|16|문서|False|True
        Original|380.734375,141.422913,53.84375,15.609375|380.734375,141.422913,53.84375,15.609375|16|원본|False|False
        Enabled|381.46875,173.422913,53.60025,12.03125|381.46875,173.422913,53.60025,15.070313|16|사용|False|True
        Hidden|381.46875,205.422913,48.916748,12.03125|381.46875,205.422913,48.916748,14.65625|16|숨김|False|True
        Enabled|381.46875,237.422913,53.60025,12.03125|381.46875,237.422913,53.60025,15.070313|16|사용|False|True
        Visible|380.140625,269.422913,45.329926,12.03125|380.140625,269.422913,45.329926,15.070313|16|표시|False|True
        Default|381.46875,301.251038,49.203064,12.203125|381.46875,301.251038,49.203064,15.070313|16|기본값|False|True
        Automatic|380.171875,333.57135,71.985748,11.882813|380.171875,333.57135,71.985748,15.070313|16|자동|False|True
        Enabled|381.46875,365.422913,53.60025,12.03125|381.46875,365.422913,53.60025,15.070313|16|사용|False|True
        Enabled|381.46875,397.422913,53.60025,12.03125|381.46875,397.422913,53.60025,15.070313|16|사용|False|True
        Light|381.46875,429.422913,33.427063,15.609375|381.46875,429.422913,33.427063,15.609375|16|밝게|False|False
        ko-en/light-ui|8|8|False
        문서 라이브러리|42.141602,37.65387,239.593628,32.024414|42.141602,37.65387,259.696667,33.169922|34|Document library|False|True
        파일     편집     보기|41.294922,105.85257,221.672791,24.489258|41.294922,105.85257,221.672791,24.489258|26|File Edit View|False|False
        문서 저장|65.700684,203.078568,112.153503,25.431152|65.700684,203.078568,176.996667,25.431152|26|Save document|False|True
        폴더 열기|445.700684,203.078568,109.266296,25.431152|445.700684,203.078568,137.933333,25.504883|26|Open folder|False|True
        그림 복사|65.637695,335.85257,108.419235,24.489258|65.637695,335.85257,146.073333,25.124023|26|Copy picture|False|True
        선택 영역 번역|65.955078,393.85257,164.292572,24.489258|65.955078,393.85257,210.023333,24.489258|26|Translate selection|False|True
        원본 보기|66.335938,451.85257,104.521774,24.489258|66.335938,451.85257,156.09,25.365234|26|Show original|False|True
        창 닫기|65.764648,509.624023,79.093063,24.717773|65.764648,509.624023,158.513333,24.717773|26|Close window|False|True
        ko-en/dark-ui|8|8|False
        문서 라이브러리|42.141602,37.65387,239.593628,32.024414|42.141602,37.65387,259.696667,33.169922|34|Document library|False|True
        파일     편집     보기|41.294922,105.85257,221.672791,24.489258|41.294922,105.85257,221.672791,24.489258|26|File Edit View|False|False
        문서 저장|65.700684,203.078568,112.153503,25.431152|65.700684,203.078568,176.996667,25.431152|26|Save document|False|True
        폴더 열기|445.700684,203.078568,109.266296,25.431152|445.700684,203.078568,137.933333,25.504883|26|Open folder|False|True
        그림 복사|65.637695,335.85257,108.419235,24.489258|65.637695,335.85257,146.073333,25.124023|26|Copy picture|False|True
        선택 영역 번역|65.955078,393.85257,164.292572,24.489258|65.955078,393.85257,210.023333,24.489258|26|Translate selection|False|True
        원본 보기|66.335938,451.85257,104.521774,24.489258|66.335938,451.85257,156.09,25.365234|26|Show original|False|True
        창 닫기|65.764648,509.624023,79.093063,24.717773|65.764648,509.624023,158.513333,24.717773|26|Close window|False|True
        ko-en/table|10|8|False
        작업 목록|41.948242,41.75322,124.378769,28.256836|41.948242,41.75322,124.378769,28.256836|30|Work list|False|False
        이름|65.515625,156.75322,54.594727,28.256836|65.515625,156.75322,79.233333,28.256836|30|Name|False|True
        상태|393.918945,156.75322,54.213867,28.256836|393.918945,156.75322,80.273333,28.256836|30|Status|False|True
        수량|723.889648,156.75322,56.396484,28.256836|723.889648,156.75322,80.27,28.256836|30|Count|False|True
        문서|63.889648,266.753204,53.496094,28.256836|63.889648,266.753204,138.12,28.256836|30|Document|False|True
        준비됨|393.889648,266.753204,83.24707,28.256836|393.889648,266.753204,83.24707,29.267578|30|Ready|False|True
        그림|63.889648,376.753204,53.496094,27.597656|63.889648,376.753204,91.19,27.597656|30|Picture|False|True
        저장됨|394.373047,376.753204,82.763672,28.256836|394.373047,376.753204,82.763672,28.256836|30|Saved|False|False
        ko-en/columns|3|3|False
        문서와 그림 관리|42.015625,42.201874,234.2677,30.140625|42.015625,42.201874,536.3,31.21875|32|Working with documents and pictures|False|True
        새 문서를 만듭니다. 작성한 내용을 저장합니다. 동료와 작업을 검토합니다. 변경 사항을 확인합니다.|41.637695,135.85257,302.874603,138.489258|41.637695,135.85257,484.953333,138.489258|26|Create a new document. Save your work\nregularly. Review the work with your team.\nCheck the latest changes.|False|True
        그림을 라이브러리에 추가합니다. 필요한 부분을 선택합니다. 원본을 안전하게 보관합니다. 나중에 다시 열 수 있습니다.|591.637695,135.459015,380.874573,138.882813|591.637695,135.459015,380.874573,138.882813|26|Add pictures to your library.\nSelect the area you need. Keep\nthe original picture safe. Open it\nagain later.|False|False
        ko-en/noisy-photo|3|3|False
        여행의 추억을 사진으로 남깁니다|46.216797,117.139221,498.428284,32.539063|46.216797,117.139221,641.16,33.169922|34|Keep your travel memories in photographs|True|True
        산과 바다의 풍경을 둘러봅니다|43.918945,206.75322,411.58847,28.256836|43.918945,206.75322,483.57,29.106445|30|Explore the mountains and the coast|True|True
        좋아하는 장소를 저장합니다|43.889648,296.357697,373.401093,28.652344|43.889648,296.357697,373.401093,29.589844|30|Save your favourite places|True|True
        ko-en/dense-settings|25|25|False
        캡처 설정|26.890625,24.951916,89.171196,20.72168|26.890625,24.951916,158.65,21.258789|22|Capture settings|False|True
        파일 이름|25.796875,75.602608,66.578644,15.070313|25.796875,75.602608,67.65,15.070313|16|File name|False|True
        저장 위치|26.265625,107.602608,64.641144,15.070313|26.265625,107.602608,93.553333,15.070313|16|Save location|False|True
        그림 형식|26.007813,139.524475,64.750519,15.148438|26.007813,139.524475,95.636667,15.78125|16|Image format|False|True
        클립보드 복사|26.007813,171.6026,98.719269,15.070313|26.007813,171.6026,126.443333,15.609375|16|Copy to clipboard|False|True
        마우스 포인터|27,203.6026,95.773956,15.070313|27,203.6026,103.393333,15.375|16|Mouse pointer|False|True
        알림 표시|26.570313,235.6026,64.352081,15.070313|26.570313,235.6026,129.52,15.070313|16|Show notifications|False|True
        창 테두리|26.085938,267.461975,64.672394,15.210938|26.085938,267.461975,110.313333,15.210938|16|Window border|False|True
        단축키|27.125,299.4776,43.25,15.195313|27.125,299.4776,129.54,15.609375|16|Keyboard shortcut|False|True
        화면 배율|25.78125,331.6026,66.594269,15.070313|25.78125,331.6026,89.99,15.609375|16|Display scale|False|True
        자동 저장|26.101563,363.6026,66.367706,15.070313|26.101563,363.6026,108.12,15.070313|16|Automatic save|False|True
        원본 보관|26.4375,395.6026,66.180206,14.671875|26.4375,395.6026,92.766667,15.609375|16|Keep original|False|True
        색상 테마|26.117188,427.6026,66.609894,15.070313|26.117188,427.6026,96.516667,15.070313|16|Colour theme|False|True
        자동|381.101563,75.602608,29.890625,15.070313|381.101563,75.602608,72.776667,15.070313|16|Automatic|False|True
        문서|381.007813,107.602608,28.53125,15.070313|381.007813,107.602608,73.66,15.070313|16|Document|False|True
        원본|381.4375,139.6026,29.554688,14.671875|381.4375,139.6026,55.863333,15.609375|16|Original|False|True
        사용|380.945313,171.6026,30.046875,15.070313|380.945313,171.6026,56.356667,15.070313|16|Enabled|False|True
        숨김|381.007813,203.6026,28.53125,14.65625|381.007813,203.6026,51.5,14.65625|16|Hidden|False|True
        사용|380.945313,235.6026,30.046875,15.070313|380.945313,235.6026,56.356667,15.070313|16|Enabled|False|True
        표시|381.007813,267.6026,28.53125,15.070313|381.007813,267.6026,46.12,15.070313|16|Visible|False|True
        기본값|381.507813,299.6026,45.5625,15.070313|381.507813,299.6026,51.083333,15.070313|16|Default|False|True
        자동|381.101563,331.6026,29.890625,15.070313|381.101563,331.6026,72.776667,15.070313|16|Automatic|False|True
        사용|380.945313,363.6026,30.046875,15.070313|380.945313,363.6026,56.356667,15.070313|16|Enabled|False|True
        사용|380.945313,395.6026,30.046875,15.070313|380.945313,395.6026,56.356667,15.070313|16|Enabled|False|True
        밝게|381.890625,427.6026,27.882813,15.070313|381.890625,427.6026,35.303333,15.609375|16|Light|False|True
        existing/batched-unsafe-block|2|1|False
        Save the document. Close the window.|20,20,280,24|20,20,472.17,26.135464|27.364738|문서를 저장합니다. 문서를 저장합니다.|False|True
        existing/unfittable|1|0|True
        existing/native-pixels|1|1|False
        Save the document|20,20,170,22|20,20,242.066667,27.94286|29.257143|문서를 저장합니다|False|True
        existing/fractional-neighbor|2|0|True
        existing/failed-neighbor|2|0|True
        existing/identity|2|0|False
        """;
}

