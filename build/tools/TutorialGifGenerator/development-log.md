# Tutorial GIF authoring LOG

작성자 `/root/tutorial_asset`, 2026-10-12. 작업 경계: 새 `build/tools/TutorialGifGenerator/**`와 `src/MyCapture.App/Assets/Tutorials/text-copy.ko.gif`, `text-copy.en.gif`만 소유. 제품/UI/resx 변경 없음. 부모가 별도 UI/player 작성자를 배정했습니다. 부모의 기존 WebGPT `tunnel_unavailable`, Antigravity `validation_failed` 근거를 재사용하고 재시도·자식 위임을 하지 않았습니다.

`C:/GitRepositories/my-capture-edit-recovery`의 clean/untracked 없는 상태, root/common-dir `C:/GitRepositories/my-capture/.git`, origin 저장소를 확인하고 `git switch -c feat/text-copy-tutorial-assets a8ef489` 실행 PASS(exit 0, `22d5cc`). 기존 `perf/stress-editor` 브랜치와 모든 작업 사본/산출물 보존. `rg` 실행은 설치 부재로 실패했으므로 `git grep`/`git ls-files`와 범위 제한 `Get-Content -Encoding UTF8`로 조회했습니다.

읽은 정본: `C:/Users/sukwo/.codex/instructions/windows-execution.md` 2026-09-28-developer-log-r1, development.md 같은 버전, creative-autonomy.md 2026-09-30-r2, assembly.md 2026-09-28-developer-log-r1, surface-grammar.md 2026-09-28-copy-boundary-r1, webgpt.md 2026-09-24-r5, collaboration.md 2026-09-24-webgpt-pro-first-r2, root `docs/handoff/claude-codex-orchestration.md` §5. 최초 Windows 문서 조회는 깨진 인코딩이어서 UTF8로 본문을 다시 확인했습니다.

실제 소스 확인: `CaptureOverlayView.OnKeyDown`의 `Key.T` 전환, `CaptureModeBar`의 `SelectMode(Text)`, `App.TextCapture.OnTextCaptureSelected` → `RunTextCaptureAsync` → `TextCaptureService.RecognizeAndCopyAsync` → `ShowTextCaptureOutcome`, 선택적 고급 창 열기. CaptureText 직접 전역 키는 자산에 표시하지 않습니다. 부모가 스토리보드 승인 후 실행 슬롯을 배정했습니다.

제품 `TextCaptureService.CountCharacters`는 `TextElementEnumerator`로 공백/줄바꿈을 제외합니다. 샘플 UTF16 길이 29, 제품 알림 문자 수 25. 텍스트 두 줄, 붙여넣기, 알림을 단일 `Sample`에서 파생합니다. 25는 하드코딩이 아닙니다. 부모의 최초 29자 수정 의견에 실제 소스 계약을 전달했습니다.

주요 명령과 결과의 cwd는 모두 `C:/GitRepositories/my-capture-edit-recovery`입니다. 실행 슬롯을 받은 동안에만 빌드/렌더를 실행했습니다. 모든 실행은 오프스크린이고 앱/창 표시, 실제 캡처/입력/클립보드/사용자 프로필/원격 변경을 수행하지 않았습니다. ExecutionPolicy 변경·Bypass·Unblock 없음. 파일 삭제·정리 없음.

| 결과 | 실제 명령 | exit code | 증거·결과 |
|---|---|---:|---|
| FAIL | `dotnet build build/tools/TutorialGifGenerator/TutorialGifGenerator.csproj -c Release -v q -nologo -p:AppPayload=C:/GitRepositories/my-capture-codex/artifacts/qa12-extreme-load` | 1 | `4a68e2` CS0103 AppTheme namespace 누락. `MyCapture.Core.Themes` import 수정 |
| PASS | 같은 build 명령 | 0 | `ada9a0` 경고0/오류0 |
| FAIL | `dotnet build/tools/TutorialGifGenerator/bin/Release/net10.0-windows10.0.22000.0/MyCapture.App.Tests.dll C:/GitRepositories/my-capture-codex/artifacts/qa12-extreme-load C:/GitRepositories/my-capture-edit-recovery/build/tools/TutorialGifGenerator/review-attempt-1` | 1 | `2e5ee0` Core 10.0.0/Disable와 설치10.0.11·12 불일치. generator 진입 전, 출력 없음 |
| FAIL | 위 build 명령, 임시 `RuntimeFrameworkVersion=10.0.11` 속성 | 1 | `e84485` NU1505 Windows.SDK.NET.Ref 중복 버전. 해당 속성을 소스에서 제거 |
| PASS | 같은 build 명령 | 0 | `ace353` 경고0/오류0 |
| FAIL | `dotnet exec --fx-version 10.0.11 build/tools/TutorialGifGenerator/bin/Release/net10.0-windows10.0.22000.0/MyCapture.App.Tests.dll C:/GitRepositories/my-capture-codex/artifacts/qa12-extreme-load C:/GitRepositories/my-capture-edit-recovery/build/tools/TutorialGifGenerator/review-attempt-1` | 1 | `fa7679` WindowsDesktop 10.0.0/Disable 불일치. generator 진입 전 |
| FAIL | `dotnet exec --runtimeconfig C:/GitRepositories/my-capture-codex/artifacts/qa12-extreme-load/MyCapture.runtimeconfig.json build/tools/TutorialGifGenerator/bin/Release/net10.0-windows10.0.22000.0/MyCapture.App.Tests.dll C:/GitRepositories/my-capture-codex/artifacts/qa12-extreme-load C:/GitRepositories/my-capture-edit-recovery/build/tools/TutorialGifGenerator/review-attempt-1` | 1 | `cbbf7c` QA12 self-contained config는 FDD frameworks 설정 없음. generator 진입 전 |
| PASS | `dotnet exec --runtimeconfig build/tools/TutorialGifGenerator/fixture.runtimeconfig.json build/tools/TutorialGifGenerator/bin/Release/net10.0-windows10.0.22000.0/MyCapture.App.Tests.dll C:/GitRepositories/my-capture-codex/artifacts/qa12-extreme-load C:/GitRepositories/my-capture-edit-recovery/build/tools/TutorialGifGenerator/review-attempt-1` | 0 | `1c1256` 2언어11프레임/640×360/11200ms. Core+Desktop10.0.11. ko727975B/en724559B,각2534400B decode. PNG 직접 확인: modebar trailing controls 잘림 |
| PASS | 같은 build 명령 | 0 | `0ab9d4` selector800×390 수정, 경고0/오류0 |
| PASS | 위 fixture runtime 명령, 출력 `review-attempt-2` | 0 | `b9d513` 2언어/11프레임/11200ms. ko681356B/en702146B. ko bar전체, en 끝 X 일부잘림. PNG 직접 확인 |
| PASS | 같은 build 명령 | 0 | `35258e` selector864×421 + 시작점 배지에서 샘플 이격 + decode 기반 미리보기, 경고0/오류0 |
| PASS | 위 fixture runtime 명령, 출력 `review-attempt-3` | 0 | `2e2056` 2언어/11프레임/11200ms. ko716643B/en692643B,각2534400B decode. 전체bar/두줄선택/복사/붙여넣기 PNG 직접 확인. 디더 노이즈 개선 대상으로 확인 |

attempt1–4의 출력은 ignored `review-attempt-*/`에 그대로 보존했습니다. 최종 authoring은 팔레트 디더를 없애고 제품 writer의 preencoded 경로를 사용합니다. product SSOT와 통합 LOG는 부모가 소유합니다.

부모의 29자 수정 후보는 실제 제품 `CountCharacters` 계약을 부모가 직접 확인한 뒤 기각하고 25자 유지 승인했습니다. 최종 실행 슬롯을 다시 받아 위 build 명령을 실행했습니다: `9864e0` PASS(exit0, 경고0/오류0). 이어서 위 fixture runtime 명령의 출력만 `review-attempt-4`로 바꿔 실행: `2abbf2` PASS(exit0, 2언어/640×360/11프레임/11200ms). CPU 슬롯 반환을 부모에게 알렸습니다.

최종 지연(cs)은 `[140,120,50,25,25,25,25,80,160,140,330]`, disposal1, loop0(무한), full opaque입니다. 각 언어의 디코딩 frame payload는 2,534,400B(Indexed8)입니다. 모든 프레임 디코딩, 실제 지연, 모든 프레임 경계와 반복, 플레이어 모든 프레임 탐색/재생/정지 검사 PASS. 한·영 최종 GIF에서 디코딩한 PNG를 직접 확인해 전체 모드 도구 모음과 단계 문구, 두 줄 선택, 25자 알림, 최종 동일 텍스트 붙여넣기, 정적 배경 디더 제거를 확인했습니다.

| 자산 | bytes | SHA256 |
|---|---:|---|
| text-copy.ko.gif | 138368 | 18AD13D38289B5D46E8BFC868AB2EB7B61C51B163F070DB9D32E2B6648AABE6F |
| text-copy.en.gif | 144217 | EDA303D03070F9EB8E2294B6CD651556BDD14D5D176E52436EEB51D16E1A6E67 |

최종 `.gif`만 새 `src/MyCapture.App/Assets/Tutorials/`에 File.Copy(overwrite:false)로 적용하고 최종 receipt/keyframe PNG/contactsheet를 새 `review/`에 같은 방식으로 복사했습니다. `a1d310` exit0에서 제품 자산의 SHA256이 위 최종 receipt와 같음을 확인했습니다. attempt 폴더, bin/obj와 작업 사본은 보존합니다. 삭제 없음. 실행 자료는 개발 tool 디렉터리에만 보관하며 제품 Assets에는 GIF 두 개만 있습니다. 제품 실제 OCR/클립보드/전체 UI 실기 검증은 이 authoring fixture 검사의 범위 밖이며 부모의 통합 검증에서 수행합니다.

자산 staging `git add -- build/tools/TutorialGifGenerator src/MyCapture.App/Assets/Tutorials/text-copy.ko.gif src/MyCapture.App/Assets/Tutorials/text-copy.en.gif` 및 `git diff --cached --check`, stat 확인: `1db560` exit0, 소유 범위의 새24파일만 포함. `git commit -m "feat(tutorial): 텍스트 복사 한영 GIF와 재현 생성기 추가" -m <이유>`: `f082cb` exit0, 커밋 `55760701e3a2b74c68ed5df3dbcc3b3fc23c7df0`, 이후 `git status --short` clean. 기존 브랜치/ignored 모든 시도/작업 사본 보존. 원격 변경 없음.

## 독립 UI 읽기 검토

부모의 추가 요청으로 별도 작성자 UI commit `968039d598fdd0e2acac2474eb2c9caacfb934d0`를 읽기만 검토했습니다. 검토 cwd `C:/GitRepositories/my-capture/.claude/worktrees/agent-a491402ad82aa2759`. 해당 작업자의 소스·LOG·테스트를 수정하거나 실행하지 않았습니다. `git log -1 --oneline; git status --short; git show --stat --oneline HEAD; git show HEAD -- <설정/도움말/csproj/resx>; Get-Content -Encoding UTF8`로 신규 창과 테스트 확인: `16ab77` exit0. 기존 untracked `CaptureLatencyInvestigationTests.cs`가 남아 있는 상태를 확인하고 그대로 보존했습니다. capture dismiss chain와 작성자 실행 LOG 추가 확인: `b20c0e` exit0.

검토 범위: background STA/frozen frames, 생성자 IO 없음, 닫기/숨김/owner 숨김과 decode 완료의 race, CTS/세대 번호/closed·visible guard, 플레이어 timer 정지 및 timeline/image/frame 참조 해제, motion·owner 이벤트 구독 해제, 기존 `ShortcutHelpWindow.DismissForCapture`에서의 tutorial 종료와 shell capture 연계, 현재 저장된 Capture/CaptureText 단축키 안내, 미지정 대안, assembly 위치 기준 ko/en asset 경로, output+publish copy 설정, 감소 모션 시작 정지와 사용자 명시 재생 및 정적 안내. 테스트가 close-during-decode, missing/malformed, hide/minimize/deactivation, current custom key/locales, search entry를 다루는지 확인했습니다. 작업자 26/26·현지화13/13 결과는 작성자 LOG에서 확인한 근거이며 이 검토자는 재실행하지 않았습니다.

중대한 발견 사항 없음. 외부 코드가 직접 Hide 후 같은 instance.Show를 호출하는 가상 경로는 Loaded 재호출 여부에 따라 재로드가 없을 수 있으나 현재 제품은 owner 숨김/캡처에서 Close하고 ShowTutorial에서 숨은 instance를 Close 후 새로 생성하므로 제품 경로에 영향이 없습니다. 실제 자산을 포함한 native 최종 검증은 부모가 수행합니다. 검토 결과를 부모에게 전달했습니다.
