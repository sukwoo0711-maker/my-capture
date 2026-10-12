# Smooth tutorial pilot LOG

작성자 `/root/tutorial_asset`, 2026-10-12. 부모 지시: 기존 11frame AS-IS를 보존하고 text-copy 먼저 25fps motion pilot로 제작, 기능별 확장은 pilot 실제 재생 확인 뒤 진행합니다. 소유권은 `build/tools/TutorialGifGenerator/**`, `src/MyCapture.App/Assets/Tutorials/*.gif`뿐입니다. 추가 child agent 없음.

`C:/GitRepositories/my-capture-edit-recovery`의 clean 상태와 ignored attempt1–4/bin/obj 보존, root/common-dir `C:/GitRepositories/my-capture/.git`를 확인했습니다(`9bf833` exit0). `2bb6cd657a7bbccfbc4178ad03c9f733ccb4a3d1`은 root `docs(qa): 텍스트 복사 튜토리얼 QA13 검증과 전달 기록` 커밋으로 worker 자체 커밋이 아닙니다. `git switch -c feat/smooth-tutorial-assets 2bb6cd6` PASS(`e24d1f`, exit0). 이전 worker 브랜치와 모든 파일을 보존했습니다.

Windows 정본 `C:/Users/sukwo/.codex/instructions/windows-execution.md` 2026-09-28-developer-log-r1을 UTF8로 직접 다시 확인(`6173f0`, exit0). 같은 작업에서 읽은 development/creative/assembly/surface/collaboration/webgpt 본문을 재사용했습니다. ExecutionPolicy/Bypass/Unblock 변경, 실제 창 표시/입력/화면 획득/클립보드/사용자 프로필/원격/삭제 없음. 부모가 CPU 슬롯을 배정하고 이후 native probe에 슬롯을 이관했습니다.

최초 smooth pilot는 150 motion frames + 6 reading holds = 156 전체 프레임, 640×360, 11.2초입니다. motion은 4cs(25fps)이며 smoothstep 포인터 가감속, 클릭 press/release 링, 자동복사/메모 scene 전환을 그립니다. 도구·입력 상태는 즉시 변경합니다. 한·영 샘플과 제품 CountCharacters25, 제품 CaptureOverlayView/ModeBar/테마/encoder/decoder/player를 유지했습니다.

| 상태 | cwd | 실제 명령 | exit code | 근거 |
|---|---|---|---:|---|
| PASS | 위 worktree | `dotnet build build/tools/TutorialGifGenerator/TutorialGifGenerator.csproj -c Release -v q -nologo -p:AppPayload=C:/GitRepositories/my-capture-codex/artifacts/qa13-text-copy-tutorial` | 0 | `830def`, 경고0/오류0, 최초 smooth streaming source |
| PASS | 위 worktree | `dotnet exec --runtimeconfig build/tools/TutorialGifGenerator/fixture.runtimeconfig.json build/tools/TutorialGifGenerator/bin/Release/net10.0-windows10.0.22000.0/MyCapture.App.Tests.dll C:/GitRepositories/my-capture-codex/artifacts/qa13-text-copy-tutorial C:/GitRepositories/my-capture-edit-recovery/build/tools/TutorialGifGenerator/review-attempt-smooth-1` | 0 | `d4aab1`, 5.1sec, 두 언어 156frames/11200ms 및 실제 decode validation PASS |

최초 pilot raw metric:

- ko: 1,908,377B, SHA256 `40D3BE52FC2976556D9043B554908684210E29E2811DC6FC7D5B1BDD535625F2`.
- en: 1,986,202B, SHA256 `41898DD92073F491C9DD5BED2F6D10E0524D6E67154996EF26EB3199097B3633`.
- 각 decoded Indexed8 payload 35,942,400B. 모든 frame full opaque/disposal1/infinite loop/exact delay/timeline boundaries/player seek·play·pause PASS.
- approach35frames/1400ms/max4.0928px, drag50/2000ms/max9.4575px, note-approach25/1000ms/max18.2117px; 각 구간 모든 프레임 distinct. press/release/copy-transition/note-transition/note-click 각각8frames/320ms/distinct8. `receipt.json`에 모든 descriptor와 frameSHA를 보존했습니다.
- 영어 decoded contact sheet를 직접 확인, 부모도 한국어 decoded contact sheet 직접 확인. 실제 animated native 검토는 부모가 외부 자산 경로로 격리 probe를 준비합니다. QA13 제품 payload를 덮어쓰지 않습니다.

공통 API를 독립 파일로 추출했습니다: `TutorialStoryboard`, `ITutorialSceneRenderer`, `TutorialDrawing`, `TutorialGifPipeline`; text-copy는 `TextCopySceneRenderer`가 소유합니다. renderer가 정적 scene·현재 UI만 보관하고 Generate가 하나씩 렌더/quantize/encode합니다. 계획 객체만 보관하며 원본 프레임 목록을 누적하지 않습니다. 자세한 signature/worker 경계는 `RENDERER-API.md`에 있습니다. 이 추출 뒤의 빌드/재생성은 부모 native CPU 슬롯 반환 후 실행하며 아직 통과 판정이 아닙니다. 최초 pilot 출력과 기존11frame review/attempt는 보존했습니다.

## API 分리 및 기능별 확장

기능 목록 38개가 부모에 의해 확정되었습니다. text/capture8 = 9개와 common CLI/pipeline은 이 worker, image13/video4 = 17개는 icons worker, Workspace12는 이후 UI worker에 소유권을 이관했습니다. Workspace 초안은 first3 snapshot baseline이며 final asset 채택 대상이 아닙니다. 부모 native pilot 실제 재생 PASS는 851d95 exit0, QA13 decoder/player156frames/35,942,400B/11.2sec, decode126ms, moving callback559 P50 41.06ms/P95 54.29ms/max60.22ms; root가 실제 pointer/selection/caption을 관찰했습니다.

모든 다음 명령 cwd는 `C:/GitRepositories/my-capture-edit-recovery`입니다. source 정책 정본 Windows를 다시 직접 읽은 tool73d417(exit0)을 기록합니다.

| 상태 | 실제 명령 | exit | 근거 |
|---|---|---:|---|
| PASS | `dotnet build build/tools/TutorialGifGenerator/TutorialGifGenerator.csproj -c Release -v q -nologo -p:AppPayload=C:/GitRepositories/my-capture-codex/artifacts/qa13-text-copy-tutorial` | 0 | 2e85e9,0warn/err, common extraction |
| PASS | `dotnet exec --runtimeconfig build/tools/TutorialGifGenerator/fixture.runtimeconfig.json build/tools/TutorialGifGenerator/bin/Release/net10.0-windows10.0.22000.0/MyCapture.App.Tests.dll C:/GitRepositories/my-capture-codex/artifacts/qa13-text-copy-tutorial C:/GitRepositories/my-capture-edit-recovery/build/tools/TutorialGifGenerator/review-attempt-smooth-2` | 0 | b0c818, text two locales156/11200ms |
| FAIL | 위와 동일 build command | 1 | a0571b, newsource20errors: UiText namespace missing + Typeface(FontFamily) overload. Compile only; render was NOT RUN at this point. GlobalUsings and full Typeface constructor fixed. |
| PASS | 위와 동일 build command | 0 | 47bc84,0warn/err, source integration including icons through447ad63 |
| PASS | `dotnet exec --runtimeconfig build/tools/TutorialGifGenerator/fixture.runtimeconfig.json build/tools/TutorialGifGenerator/bin/Release/net10.0-windows10.0.22000.0/MyCapture.App.Tests.dll C:/GitRepositories/my-capture-codex/artifacts/qa13-text-copy-tutorial C:/GitRepositories/my-capture-edit-recovery/build/tools/TutorialGifGenerator/review-attempt-batch-1 text-copy,capture-region,folder-create` | 0 | 967d59 session61108→3db19a;6 actual decoded validated assets |

first3 batch bytes: text ko1,920,144/en1,998,598(frames156),capture-region ko1,985,220/en2,137,158(frames140),folder-create ko2,634,191/en3,043,502(frames140). All11200ms; budgets/fullopaque/exactdelays/disposal1/loop0/motion/player validated per asset; hashes/frames/motion in each locale receipt.json. Direct decoded ko text/capture/folder contact sheets inspected. Root also viewed ko/en capture +ko text. Folder pointer ends near Cancel rather than Create and uses generic sequencing; ownership moved to UI worker for correction. Originals/attempts retained without deletion.

Following source fixes are not yet built/rendered at this entry: capture output dimension removed and headers changed to Captured image/Recorded video; C uses actual CopyColorAtPointerAsync with inert delegate and exact solid swatch552,249 assertion; region physical pixel crop asserts matches actual overlay; recording independent child fixture matches product order/style/minwidth/grid and measures actualbuttoncenters; region retains same visual350x89 from selection throughready/start/record/stop/videoresult; pause/resume use each measuredbuttoncenter and freeze elapsed whilepaused. RecordingControlWindow constructor is excluded because ApplyPhysicalLayout enumerates native monitors. An uninitialized Window approach was considered and rejected before implementation. No Window internals skipped. Text secondline y177 font22 FormattedText.Height+4px fits selection bottom212.

CLI directly constructs internal editor/video renderers rather than public-only reflection ctor lookup. Final icons source through4febb80 integrated (local08f4637/808f8ab); rootpost-UI native inspection CPUslot currently held, no conflicting build/render started. Each completed clip writes its own receipt before batch completion; decoded frames and renderer leave scope before next asset, forcedGC only at clip boundaries. Input/scene descriptors are not a bitmap preload list.
좁은 image-text followup: 부모가 icons capacity 종료에 따라 `EditorSceneRenderer.cs`의 type motion8→6 한 줄만 이 호스트에 이관했습니다. batch3 image-text.ko 실제decode FAIL398f02(exit1), typing8frames Korean6chars가 smoothstep/ceil로5distinct(최소6)였으며 failedGIF/storyboard/failure metadata를 보존했습니다. type6이면5distinct(최소4)이고 입력 문자의 이산 변화 계약을 유지합니다. 다른 assertion은 약화하지 않았습니다. cwd 동일, build23b4d2 exit0, batch4 시작94e4d2(session65042)의 image-text ko/en104frames/11000ms 실제decode PASS 관측(2,130,706B/2,375,998B). 나머지 batch4는 이 기록 시점 진행 중입니다. 실행 명령은 `dotnet exec --runtimeconfig build/tools/TutorialGifGenerator/fixture.runtimeconfig.json build/tools/TutorialGifGenerator/bin/Release/net10.0-windows10.0.22000.0/MyCapture.App.Tests.dll C:/GitRepositories/my-capture-codex/artifacts/qa13-text-copy-tutorial C:/GitRepositories/my-capture-edit-recovery/build/tools/TutorialGifGenerator/review-attempt-batch-4 image-text,image-step-number,image-mosaic,image-blur,image-crop,image-rotate,image-save,image-copy,video-play,video-frame-step,video-trim,video-gif-export,record-region,capture-window,capture-fullscreen`입니다. 부모가 요청한 selected-source manifest는 모든 최종76개를 경로·receipt·hash·bytes·frames·duration·decodedbytes로 연결할 예정입니다.
## 최종 기능별 렌더 및 교정 기록

이 절의 모든 cwd는 `C:/GitRepositories/my-capture-edit-recovery`입니다. build command는 위 표의 실제 `dotnet build ... -p:AppPayload=...`와 동일합니다. 다음 실제 render command는 모두 아래 prefix에 표의 output/selector 인수를 붙여 실행했습니다. 각 storyboard/receipt의 `Command`에는 프로세스가 받은 실제 인수도 남아 있습니다.

```powershell
dotnet exec --runtimeconfig build/tools/TutorialGifGenerator/fixture.runtimeconfig.json build/tools/TutorialGifGenerator/bin/Release/net10.0-windows10.0.22000.0/MyCapture.App.Tests.dll C:/GitRepositories/my-capture-codex/artifacts/qa13-text-copy-tutorial
```

각 output 절대 경로는 `C:/GitRepositories/my-capture-edit-recovery/build/tools/TutorialGifGenerator/` 아래입니다.

| 상태 | output / selector | exit | 실행 근거와 결과 |
| --- | --- | ---: | --- |
| FAIL | `review-attempt-batch-2` / `text-copy,capture-region,record-region,record-pause,capture-window,capture-fullscreen,capture-scroll,capture-delay,color-copy,image-rectangle,image-ellipse,image-arrow,image-pen,image-highlighter,image-text,image-step-number,image-mosaic,image-blur,image-crop,image-rotate,image-save,image-copy,video-play,video-frame-step,video-trim,video-gif-export` | 1 | 7049ed/session13934→fa9771. text-copy/capture-region 4개 PASS 후 record-region 첫 접근 최대간격 초과. 4 PASS는 최종 재사용하며 실패 원본 보존. |
| FAIL | `review-attempt-preflight-1` / `preflight:all` (당시 26기능 registry) | 1 | bb48f6. capture9/editor첫5 PASS 뒤 image-text text-away 최대27.83px 실패. |
| FAIL | `review-attempt-preflight-2` / `preflight:all` (당시 26기능 registry) | 1 | 3cb789. 26개 한영 결과 수집 후 image-copy 첫 접근27.4101px 한 항목 실패. |
| PASS | `review-attempt-preflight-3` / `preflight:all` | 0 | 31a836. 38기능/76개 프레임·시간·최대간격 PASS. |
| PASS | `review-attempt-preflight-4` / `preflight:library-open,quick-access,folder-create,folder-rename,library-move,library-delete,pin-clipboard,pin-hide-all,pin-click-through,pin-restore,pin-copy,pin-save` | 0 | 920ccd. Workspace 최종912f5ef 후12기능/24개 PASS. |
| FAIL | `review-attempt-batch-3` / `record-region,record-pause,capture-window,capture-fullscreen,capture-scroll,capture-delay,color-copy,image-rectangle,image-ellipse,image-arrow,image-pen,image-highlighter,image-text,image-step-number,image-mosaic,image-blur,image-crop,image-rotate,image-save,image-copy,video-play,video-frame-step,video-trim,video-gif-export` | 1 | 723246/session43494→398f02. 12기능/24개 실제decode PASS 뒤 image-text ko typing8의5distinct<6 실패. 입력 문자별 이산변화에 맞춰6frames로 수정; 다른 assertion 유지. |
| FAIL | `review-attempt-batch-4` / `image-text,image-step-number,image-mosaic,image-blur,image-crop,image-rotate,image-save,image-copy,video-play,video-frame-step,video-trim,video-gif-export,record-region,capture-window,capture-fullscreen` | 1 | 94e4d2/session65042→afda43. 10기능/20개 PASS 뒤 video-trim ko3,435,402B>3MiB. failed source/GIF/receipt 보존. 이후 기능은 이 실행에서 NOT RUN. |
| PASS | `review-attempt-batch-5` / `record-region,capture-window,capture-fullscreen,library-open,quick-access,folder-create,folder-rename,library-move,library-delete,pin-clipboard,pin-hide-all,pin-click-through,pin-restore,pin-copy,pin-save` | 0 | 1e40dd/session71435→699b53. 15기능/30개 실제decode PASS. window/fullscreen aspect-fit,record selector plain instruction 적용. record click 상태와 Workspace dialog 좌표는 후속검토로 교정 대상. |
| PASS | `review-attempt-preflight-5` / `preflight:all` | 0 | 3c25cf. 최종교정38기능/76개 계획 모두 PASS,trim114frames/11000ms. |
| PASS | `review-attempt-batch-6` / `record-region,image-mosaic,image-blur,image-rotate,video-trim,video-gif-export` | 0 | d58568/session74483→0a41a0. 6기능/12개 실제decode PASS. frame152/105/105/82/114/102;11200/11000ms. trimko2,757,166B/en2,745,147B. |

해당 과정의 실제 build 실행: 0018da/bb304d/da04c5/01b94a/31a836/d1294f/23b4d2/94e4d2 모두 exit0/0warnings/0errors. 최신 narrow geometry 수정 첫 build ac4fdc exit1은 존재하지 않는 TutorialDrawing.Require helper 참조 1개 오류였습니다. 실제 블록 검사를 같은 조건의 local InvalidOperationException으로 수정한 뒤6e17b3 exit0/0warnings/0errors. 무변경 재시도 없음. 최신 Windows 정본 UTF8 직접 확인 ca06a3 exit0; 앞선 e80762의 잘못된 ANSI 디코딩은 로딩완료로 사용하지 않았습니다.

부모는 icons capacity 종료 후 다음 한정 수정 소유권을 이 호스트에 이관했습니다. Mosaic/Blur 공통 RedactionBounds=(110,132,280,38), screen start=(182,197),finish=(462,235)로 source/clip/annotation/샘플crop/포인터를 단일화했습니다. 제품 AnnotationEditorControl.ApplyMosaic(661행)을 직접 읽고 같은14px box-average +블록 전체 픽셀복제 계산을 적용했습니다. 각각의 블록을 fullopaque 상수색으로 검사합니다. transformed-bitmap 축소/확대 재샘플링 제거. 회전 결과는 |cos|/|sin| 외곽 크기로 uniformFit하여 중앙 유지/90도 전체표시합니다. trim은 동작 frame137→109(총114),25fps와이동간격 assertion/10–12s를 유지하며 읽기hold1240ms로 자동보상합니다.

Capture 결과 window/fullscreen은 실제 crop 비율을 유지하며 height상한200일 때 width도 함께 줄여중앙배치합니다. recording selection의 ShowModeBar/OffersOtherActions=false는 실제 RegionRecordingCoordinator 계약입니다. region Start click94–99까지Ready,recording100부터Recording,Stop145–150까지Recording,done151부터VideoResult로 전환합니다. 실제 decode motion-sheet/마지막PNG에서 이 전환/모자이크 블록 vsblur/회전 전체표시를 직접 확인했습니다(이미지도구 batch6). 부모가 ko/en 독립검토하며 영어 시각검토 소유권을 인수했습니다. 모든 원본·실패·옛 PNG 보존.

최종 source와 자산을 검증 전에 전부 다시 읽어 쌓지 않습니다. 선택 manifest는 메타데이터와 파일SHA만 읽고 각 생성 시 수행한 실제decode receipt를 연결합니다. 기존 성공 중 영향을 받지 않는 기능은 재생성하지 않습니다. 제품copy/publish는 이후 별도 단계입니다.

## Workspace 최종 교정 및 76개 전달

부모가 승인한 UI 소스5d7e436/3bce8e6을 로컬3cdaf51/290619e로 통합했습니다. Snapshot root Content Margin=24 offset을 pointer/selection 좌표에 포함하고, 원본240×88 핀의 height cap 및 saved thumbnail에 uniformFit+중앙배치를 적용합니다. 부모 ko38개와 UI worker en38개는 최종 selected-source contact/motion sheet를 직접 검토해 승인했습니다. UI 최종 영어 Workspace 검토는 batch5 library-open/quick-access + batch7 folder4 + batch8 pin6입니다.

| 상태 | cwd | 실제 명령 | exit | 근거 |
| --- | --- | --- | ---: | --- |
| PASS | `C:/GitRepositories/my-capture-edit-recovery` | `git cherry-pick 5d7e436` 후 위 표 동일 `dotnet build ...` | 0 | 678f47, local3cdaf51/build0warnings0errors |
| PASS | 동일 | `dotnet exec --runtimeconfig build/tools/TutorialGifGenerator/fixture.runtimeconfig.json build/tools/TutorialGifGenerator/bin/Release/net10.0-windows10.0.22000.0/MyCapture.App.Tests.dll C:/GitRepositories/my-capture-codex/artifacts/qa13-text-copy-tutorial C:/GitRepositories/my-capture-edit-recovery/build/tools/TutorialGifGenerator/review-attempt-batch-7 folder-create,folder-rename,library-move,library-delete` | 0 | 4e81ad/session58224→6542a9,4features8assets actualdecode PASS |
| PASS | 동일 | `git cherry-pick 3bce8e6` 후 위 표 동일 `dotnet build ...` | 0 | 0ff487, local290619e/build0warnings0errors |
| PASS | 동일 | `dotnet exec --runtimeconfig build/tools/TutorialGifGenerator/fixture.runtimeconfig.json build/tools/TutorialGifGenerator/bin/Release/net10.0-windows10.0.22000.0/MyCapture.App.Tests.dll C:/GitRepositories/my-capture-codex/artifacts/qa13-text-copy-tutorial C:/GitRepositories/my-capture-edit-recovery/build/tools/TutorialGifGenerator/review-attempt-batch-8 pin-clipboard,pin-hide-all,pin-click-through,pin-restore,pin-copy,pin-save` | 0 | b5cef5/session66611→d9a74d,6features12assets actualdecode PASS |
| PASS | 동일 | `& ./build/tools/TutorialGifGenerator/AssembleManifest.ps1 -CopyAssets` | 0 | ef4258,76selectedsources+receipt provenance+budget+fileSHA,productcopySHA equality |
| PASS | 동일 | 最終manifest/76product files metadata read | 0 | f98281, count76,bytes129867958,totalframes7080,totaldecodedbytes1631232000,maxfile3014188,maxframes156,maxdecoded35942400 |

`review-smooth-final/selected-source-manifest.json` と `.csv` は source GIF/receipt の絶対path、選択batch、SHA256、bytes、frames、duration、decodedbytes、delay、generator/validation timing、product destination/pathSHAを記録します。最終76receiptも`review-smooth-final/receipts/`にbyte-identicalcopyしました。原本11frame text GIF2個はproductの内容置換前に`review-smooth-final/as-is/`へphysicalcopyし保管しています。古いPNG/試行/failedGIFとworktreeもすべて保持、削除なし。詳細payloadSHAは全76receipt/manifestで一致 `73916DA5553DBB7D3A857BEF99D7675C9519CFD6400DDF3929D655C645D69D72` (.NET10.0.11)。合計decodedbytesは各clipの和で同時residentmemoryではなく、最大1clip35,942,400Bです。

親による独立manifest確認dde6e5 exit0は76sourceGIF/receipt/committedreceipt/destinationSHAおよびframe/duration/合計一致PASS。このworkerは全GIFを再度同時loadしていません。個別生成時のproductdecoder/player検証receiptと、assembly hash一致を使用しました。CPUslotはbatch8とassembly完了後に親へ返却しました。publish/製品全test/統合native検証は親が実行します。

親は67f956e以後3cdaf51まで16commitをroot382cc00へ統合済みです。その後のordered local sourceは290619e(親が統合着手)、最後のasset/manifest commitの順です。今回commitはnewgenerator assembly script、final LOG/README、76製品GIFと76receipt/selectedmanifest/原本text2GIFのみで、製品csproj/resx/UIsource変更はありません。
