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