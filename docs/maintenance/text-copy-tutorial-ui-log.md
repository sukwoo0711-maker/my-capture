# 텍스트 복사 튜토리얼 UI 작업 기록

- 작업자: `/root/tutorial_ui`, 2026-10-12.
- 작업 공간: `C:/GitRepositories/my-capture/.claude/worktrees/agent-a491402ad82aa2759`.
- Git common-dir: `C:/GitRepositories/my-capture/.git`.
- 기준: `a8ef489e941c3b0d5d13cc84e93e293cc5791d87`, 브랜치 `feat/text-copy-tutorial-ui`.
- 기존 미추적 `tests/MyCapture.App.Tests/CaptureLatencyInvestigationTests.cs`는 수정·staging하지 않고 보존했습니다.
- 소유 범위: 튜토리얼 창, Settings/F1 진입점, csproj/한영 resx, 관련 테스트. GIF와 generator는 다른 작성자 소유이며 수정하지 않았습니다. 상태 SSOT는 총괄 소유입니다.
- Windows 실행 정책 2026-09-28-developer-log-r1, development 같은 버전, assembly 같은 버전, surface-grammar 2026-09-28-copy-boundary-r1, creative-autonomy 2026-09-30-r2, collaboration 2026-09-24-webgpt-pro-first-r2, webgpt 2026-09-24-r5, 프로젝트 SSOT §5를 직접 읽었습니다. 최초 PowerShell 기본 인코딩이 깨져 UTF-8로 다시 읽었습니다.
- 총괄이 확인한 WebGPT `tunnel_unavailable`/Antigravity `validation_failed`와 공식 native 경로 기존 근거를 재사용했으며 재호출하지 않았습니다. 자식 agent를 생성하지 않았습니다.

## 실제 동작 및 구현 경계

`HotkeySettings.CaptureText`는 기본 미지정입니다. 현재 영역 캡처 단축키로 선택 화면을 열고 T를 누른 다음 글자 영역을 드래그하면 자동 OCR과 텍스트 클립보드 복사를 수행합니다. 별도 인식 결과창을 열지 않아도 Ctrl+V로 붙여넣습니다. 전용 CaptureText 단축키가 지정되면 처음부터 텍스트 모드이므로 T를 누르지 않는 대안을 안내합니다.

설정의 텍스트 복사 제목과 같은 행에 보조 버튼을 추가해 좁은 창에서도 설명보다 먼저 발견할 수 있게 했습니다. F1 도움말의 TextCapture_Title/HelpToggle 탭 행과 두 검색 결과에도 버튼을 추가했습니다. 기존 탭, UniformGrid 행, 두 열, Keycaps, 검색 개수/빈 상태/검색 Esc 동작을 유지했습니다. Settings에서는 저장되어 실제 동작 중인 설정을 읽고, 도움말에서는 버튼을 누를 때 ReadHotkeys를 다시 읽습니다.

창은 modeless입니다. GIF 경로는 MyCapture assembly 위치를 기준으로 정하고 언어별 `Assets/Tutorials/text-copy.ko.gif`/`text-copy.en.gif`를 선택합니다. 파일 IO와 GifSampleDecoder 호출은 창을 열 때 별도 background STA에서 수행합니다. Frozen frames만 dispatcher에 연결하며 취소 token, 세대 번호, closed/visible 검사로 늦은 완료를 걸러냅니다. 재생은 기존 GifSamplePlayer를 사용합니다. 닫기/숨김/캡처 시작에서 timer를 중지하고 timeline/image/frames 참조를 해제합니다. owner 숨김에서는 창을 닫습니다. 비활성화/최소화에서 일시정지합니다. 감소 모션 기본 정지, 설정 변경 시 정지, 사용자가 직접 선택하는 재생/다시 보기 및 정적 세 단계 안내를 제공합니다. GIF 누락·손상은 재생 제어를 비활성화하고 단계 안내를 유지합니다.

csproj는 두 GIF를 출력 및 publish에 PreserveNewest로 복사하도록 준비했습니다. 이 작성자 검증 시 실제 GIF는 아직 작업 공간에 없었습니다. 합성 fixture는 동작 검증이며 실제 자산/최종 제품 화면 증거는 총괄의 통합 검증 대상입니다.

## 실행 근거

모든 아래 명령의 working directory는 위 절대 작업 공간입니다. 테스트 런타임 출력은 .NET 10.0.12입니다. 원본 실패 TRX와 최종 TRX를 보존했습니다.

| 상태 | 실제 명령 | exit code | 결과·근거 | 범위/사유 |
| --- | --- | --- | --- | --- |
| PASS | `git status --short; git rev-parse --show-toplevel --git-common-dir HEAD; git switch -c feat/text-copy-tutorial-ui a8ef489` | 0 | chunk `3fdc3f`, 기존 미추적 파일 보존, 기준 branch 전환 | 기존 worker 경로 재사용 |
| PASS | `Add-Type -AssemblyName WindowsBase; [System.IO.FileFormatException].BaseType.FullName` | 0 | chunk `d2c384`, `System.FormatException` | malformed GIF 예외 필터 근거 |
| FAIL | `dotnet build MyCapture.slnx -c Debug -v q -nologo` | 1 | chunk `aebc62`, fixture throw 삼항 괄호 누락 CS8115/CS0029 | 테스트 작성 문법을 수정 |
| PASS | `dotnet build MyCapture.slnx -c Debug -v q -nologo` | 0 | chunk `63c02d`, 경고 0/오류 0 | 첫 빌드 수정 확인 |
| FAIL | `dotnet test tests/MyCapture.App.Tests -c Debug --no-build --filter 'FullyQualifiedName~TextCopyTutorialTests\|FullyQualifiedName~ShortcutHelpSearch\|FullyQualifiedName~TextCaptureFlowTests\|FullyQualifiedName~TextCaptureSettings_WindowDetectColorFormatAndHotkeyApplyThroughTheWindow\|FullyQualifiedName~GifSampleAnimationTests' --logger 'trx;LogFileName=text-copy-tutorial-focused.trx' --results-directory artifacts/text-copy-tutorial-ui -v minimal` | 1 | chunk `cd89ff`, 25 PASS/1 FAIL, 원본 `text-copy-tutorial-focused.trx` | 설정 fixture는 Capture 탭(1)에 있는 상태에서 Shortcuts 탭 버튼의 IsVisible을 검사했습니다. 실제 탭(4) 전환 및 적용 후 재열기를 추가했습니다. |
| PASS | `dotnet build MyCapture.slnx -c Debug -v q -nologo` | 0 | chunk `53d613`, 경고 0/오류 0 | 최종 소스 빌드 |
| PASS | `dotnet test tests/MyCapture.App.Tests -c Debug --no-build --filter 'FullyQualifiedName~TextCopyTutorialTests\|FullyQualifiedName~ShortcutHelpSearch\|FullyQualifiedName~TextCaptureFlowTests\|FullyQualifiedName~TextCaptureSettings_WindowDetectColorFormatAndHotkeyApplyThroughTheWindow\|FullyQualifiedName~GifSampleAnimationTests' --logger 'trx;LogFileName=text-copy-tutorial-focused-final.trx' --results-directory artifacts/text-copy-tutorial-ui -v minimal` | 0 | chunk `8577dd`, 26 PASS/0 FAIL/0 skip, 최종 TRX | 새 lifecycle/손상/감소 모션 회귀, 기존 도움말/실제 테마/텍스트 캡처 경로/설정 적용/GIF player |
| PASS | `dotnet test tests/MyCapture.Core.Tests -c Debug --no-build --filter 'FullyQualifiedName~LocalizationTests' --logger 'trx;LogFileName=text-copy-tutorial-localization.trx' --results-directory artifacts/text-copy-tutorial-ui -v minimal` | 0 | chunk `a3f21e`, 13 PASS/0 FAIL/0 skip | 한영 키와 문구 계약 |
| PASS | `git diff --check` | 0 | chunk `e5d502`, whitespace 오류 없음 | 줄바꿈 변환 안내만 출력 |

표의 `\|`는 Markdown 표 이스케이프이며 실제 필터 인수에서는 `|`였습니다. 각 TRX는 `artifacts/text-copy-tutorial-ui/`에 있습니다. 최신 실제 source diff가 최종 26/26 + 13/13 검사 대상입니다. 새 자산이 없어 worker의 실제 GIF 포함 검증은 수행하지 않았고, 최종 asset/native/AS-IS·TO-BE 및 통합 전체 검사 슬롯은 총괄 담당입니다.

## 보존 및 인계

파일 삭제·휴지통 비우기·새 worktree 생성·사용자 MyCapture 종료·클립보드 변경·프로필 변경·원격 변경을 수행하지 않았습니다. 기존 worker 작업 사본은 미추적 성능 테스트와 실패/최종 TRX가 있어 보존했습니다. 총괄이 제품 diff와 실제 화면을 확인하고 커밋을 cherry-pick한 뒤 통합 검증합니다.

## 통합 회귀 수정

총괄의 전체 App 테스트 session `75172`에서 `ShortcutHelpFrameKeysTests` 3건이 실패했습니다. 초기 작성은 모든 탭 행의 action TextBlock을 StackPanel로 감싸 기존 비디오/핀 행의 직접 자식 구조 계약을 변경했습니다. 초기 집중 필터에 `ShortcutHelpFrameKeysTests`가 빠져 이 회귀를 확인하지 못했습니다. 기존 일반 행에는 직접 TextBlock을 유지하고 두 튜토리얼 해당 행만 버튼을 위한 wrapper를 사용하도록 최소 수정했습니다. 기존 프레임/핀 구조 assertions는 유지했습니다. 총괄 전체 검사가 진행 중일 때 소스만 수정했습니다. 총괄 지시에 따라 집중 재실행 전에 이 최소 수정을 커밋하고, 통합 빌드·확장 집중 필터·전체 회귀는 총괄이 직렬 실행합니다. 이 수정 이후 worker 빌드/테스트는 미실행(exit code N/A)이며 앞선 26/26 결과는 이전 소스의 결과입니다. worker의 변경 후 `git diff --check`는 chunk `f702ad`, exit 0입니다.

## ??? ???? ?? ? ?? ??

2026-10-12 `/root/tutorial_ui`? ?? ??? ?? ?? ?? ??? ???????. `git status --short; git rev-parse --show-toplevel --git-common-dir HEAD`? chunk `1e274a`, exit 0?? ?? ??? CaptureLatencyInvestigationTests.cs? ?????. `git switch -c feat/feature-tutorial-ui 2bb6cd6`? chunk `271f3a`, exit 0???. ?? ??? ????? staging?? ?????. ??? ?? ?? Windows/development/assembly/surface-grammar/creative-autonomy/collaboration/webgpt ?? ? ?? fallback ??? ???????. ?? ??? UTF-8? ???????. ?????? ??????????? ??? ????.

??? ??? 38? ?? ?? ??? ?? ?? ???? TutorialCatalog? ?????. 42? F1 ?? ????? action resource key? ?????. ??? ?? ? ?, ??? ?? ??, ??? ?? I/O? ?? ?? ???? ???? ? ??? pin-clipboard? ?? ??? ?????. ??????????? ?? ?? ??? ?? ?????. ComboBox?? ????? ??? ???? ??? GIF? ?? ?????. Settings? ?? ?? ??? 13? ?? ??? ?? ??? ?????. repeat/upload ? ?? 38? ?? ?? ??? ? ??? ???? ?????.

FeatureTutorialWindow? ?? STA ??? decoder/player/?? ??/owner ? capture dismissal lifecycle? ?????. TextCopyTutorialWindow? ?? constructor/ShowTutorial ? private motion reflection ?? wrapper???. HasFrames/IsLoading/IsPlaying/FrameIndex/AssetPath ?? ??? ???? SelectedFeatureId/SelectFeature(id)? ??????. ?? _frames/_player ??? base ???? ????. ?? ???? ?? token? ???? ??? ????? player timeline, image source, frames ??? ?????. ?? decode? closed/visible/token/?? guard? ???? ????. ?? GIF ???? ??? ????. csproj ??? Assets\Tutorials\*.gif? ??/publish PreserveNewest ?????. GIF/generator/?? SSOT? ???? ?????.

F1 ?? ?? ?? ?? TextBlock? WrapPanel ??? ?????. ??? ??? ?? keycap WrapPanel ?? ???? ?cap automation name? ?? ??? ?????. ?? ??? HotkeySettings? ?? ??? CaptureText? ?? Capture?T ??? ?????. ??? PasteToScreen/HideAllPins? ?? ??? ??? ???? ??? ?? ??? ???? ?????. ??? Ctrl+C? ????????? ?????. ?? local key? ShortcutCatalog? ??? ?? hint? ?????. ?? ? ??? ? ?? ? ?? GIF ?? fallback? ?????.

?? FeatureTutorialTests? 38?/42? ? ?? Copy/Save ???? ??, ?? ? ??, ? ? ?? ?? ??, ??/??? ???, ???? constructor, ? GIF? ??, ?? decoder cancel? ??? ??? ??, ?? ? missing GIF fallback? ?????. ?? TextCopyTutorialTests/ShortcutHelpFrameKeysTests ? assertions? ???? ?????.

?? ??? PowerShell ?? ?? heredoc? Python pathlib ???? ??????(chunks bf1e44/0a0dfb/c201cb/1febbc/502e58, ? exit 0). ?? ?? ???? ?? CPU ??? ?? ????? ???? ?? ??/?? ???/Localization? NOT RUN, exit N/A???. ??? ??? ???? ?? ?? ??? ?????. ?? ? GIF ? AS-IS/TO-BE/native UI ??? ?? ?? ?? ?????.
