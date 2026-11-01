# 대량 텍스트 입력·선택 성능 검증

2026-10-12, 기준 `73e0293`(제품 `2fc7fa4`), 작업 사본 `C:/GitRepositories/my-capture/.claude/worktrees/agent-a491402ad82aa2759`. 합성 한글·영문·emoji 10만/100만 UTF-16 문자로 실제 AnnotationEditorWindow와 OcrResultWindow를 실행했습니다. 사용자 데이터·클립보드는 사용하지 않았습니다. 각 측정 프로세스는 60초/2GiB guard와 단계별 JSON을 사용하며 모든 실패와 중간 산출물을 보존합니다.

## 변경과 정확성

- Canvas의 무한 높이 측정으로 live TextBox가 약 70,061DIP까지 커졌습니다. 입력 viewport만 현재 canvas 높이 안으로 제한하고 세로 scrollbar의 실제 폭을 바깥 폭에 보상합니다. 저장할 전체 TextAnnotation의 FitText·Rect·줄바꿈·내용은 유지합니다.
- 읽기 전용 OCR TextBox 한 개에서만 `InputMethod.IsInputMethodEnabled=false`를 설정합니다. WPF10.0.11 TextStore.GetTextExt가 선택 범위 전체의 줄을 포맷하는 비용을 피합니다. 편집 TextBox와 전역 IME 설정은 변경하지 않습니다. 기존 자동 전체 선택·복사·재열기 동작을 유지합니다.
- 초기 OCR 자동 선택 제거 후보는 동일 결과 재열기를 6ms에서 약 0.8초로 지연시켜 채택하지 않았습니다. 해당 비교 원본은 보존했습니다.

## 같은 런타임의 실제 결과

실제 Core/Desktop 10.0.11, rollForward Disable. 시간은 한 머신에서 직렬 실행한 해당 case 관측값이며 지속 사용의 보장값이 아닙니다. 누적 할당량은 상주 메모리가 아닙니다.

| 조건·단계 | 기준 | 수정 |
|---|---:|---:|
| 편집 10만 자 다수 줄 initial layout | 4,402.6ms / 3.432GB 할당 | 486.7ms / 42.75MB |
| 같은 문서 append→undo→redo→전체 선택 후 settle | 5,993.8ms / 4.998GB | 1,935.2ms / 437.25MB |
| 편집 10만 자 긴 줄 initial layout | 1,241.2ms | 870.4ms |
| OCR 10만 자 자동 선택 포함 initial layout | 5,835.7ms / 1.230GB | 36.0ms / 1.414MB |
| OCR 10만 자 ShowResult부터 종료까지 전체 시나리오 | 7,527.4ms | 1,493.1ms |
| OCR 100만 자 전체 시나리오 | 60초 guard | 7,788.4ms, 정상 종료 |

OCR 최종 100만 자는 ShowResult 7,367.8ms, 자동 선택 포함 layout 33.1ms, 추가 SelectAll settle 8.2ms, 선택 없는 스크롤 7.4/4.5ms, 재열기 6.7ms입니다. 초기 대량 문서 배치 비용은 남아 있습니다. 전체 내용·SelectedText SHA가 같고 UI Automation의 전체 문서와 부분 선택도 회귀 검사로 확인했습니다.

편집 100만 자 긴 줄 기준 프로세스 PID58812는 WPF TextBoxView/IME TextStore의 FailFast `0x80131623`으로 종료됐고, 수정본은 전체 내용·snapshot·문서 Rect·native/document undo/redo·닫기까지 27.53초에 완료했습니다. 한 번의 paired 재현 결과입니다. 100만 자 다수 줄은 기준 initial layout에서 60초 guard, 수정본 initial layout 4.76초 이후 편집 burst/전체 선택 settle에서 60초 guard가 남습니다. 편집기의 한글 IME를 끄는 방식은 적용하지 않았습니다.

## 실행 근거

위 작업 사본 cwd, `artifacts/stress-text/`의 raw JSON, before/after DLL, PNG, TRX를 보존합니다. 기준 DLL은 최종 probe를 사용하는 `final-before`/`paired-before`에 복사했으며 원본 baseline DLL hash를 확인했습니다.

```powershell
dotnet build artifacts/stress-text/probe/StressTextProbe.csproj -c Release -o artifacts/stress-text/readonly-final-build -nologo -v quiet
dotnet exec --runtimeconfig artifacts/stress-text/final-build/pinned.runtimeconfig.json artifacts/stress-text/readonly-final-build/MyCapture.App.Tests.dll ocr 100000 long C:/GitRepositories/my-capture/.claude/worktrees/agent-a491402ad82aa2759/artifacts/stress-text/product-readonly-ocr-100k.json
dotnet exec --runtimeconfig artifacts/stress-text/final-build/pinned.runtimeconfig.json artifacts/stress-text/readonly-final-build/MyCapture.App.Tests.dll ocr 1000000 long C:/GitRepositories/my-capture/.claude/worktrees/agent-a491402ad82aa2759/artifacts/stress-text/product-readonly-ocr-1m.json
dotnet test tests/MyCapture.App.Tests/MyCapture.App.Tests.csproj -c Release --filter "FullyQualifiedName~OcrLargeTextWindowTests|FullyQualifiedName~Ocr410WindowTests|FullyQualifiedName~EditorLargeTextViewportTests" --logger "trx;LogFileName=text-final-root.trx" --results-directory artifacts/stress-text/root-focused -nologo -v quiet
```

최종 build `3ca9be` exit0, 0경고/0오류. OCR 10만 `25950b` exit0/PID47620, 100만 `35b2e3` exit0/PID65552. JSON `product-readonly-ocr-*` PASS와 전체 내용 확인 `0453c8` exit0. 집중 검사 session98758/final5410e5 exit0, 21/21 PASS. 원래 viewport 집중 검사 28/28 PASS(63456/002729), 최종 viewport source는 이후 동일합니다. 새 회귀 검사는 실제 monitor DPI 변경 대신 논리 viewport 크기, scrollbar 줄바꿈, resize, 전체 내용/Undo/commit을 확인합니다.

원인 분리만 한 readonly-no-ime 진단은 build `b9c615` exit0, 100k `f47a38` exit0, 1m session42238/final6d8c22 exit0. 해당 후보의 1m manual selection settle 47,477→20.9ms였으며 최종 제품은 원래 자동 선택 계약을 복원했습니다. 전역 selection renderer compatibility switch는 채택하지 않았습니다.

편집 before/after 100k PNG의 top/bottom을 root가 직접 확인했습니다. 수정본은 viewport 안에서 끝까지 스크롤되며 짧은 문서 top PNG는 전후 SHA가 같습니다. 실제 OS 키보드·최종 통합 빌드 검증은 root 통합 단계에서 기록합니다. WPF layout/render dispatch 측정은 물리 화면 표시/DWM latency와 구분합니다.

작성 worker의 capacity 중단 후 root가 기존 변경을 인수하고 최종 readonly OCR 수정·실측·UIA 회귀 검사를 완료했습니다. 기존 untracked CaptureLatencyInvestigationTests.cs, 모든 작업 사본과 실패 자료를 보존했습니다.

## 2026-10-31 OCR 결과 편집과 입력기 상한

OCR 결과 글자 상자를 편집할 수 있게 바꿨습니다(읽기 전용 해제, Undo 사용). 입력기는 선택이 1,024자 이하일 때만 켭니다. 결과를 열 때의 전체 선택처럼 그보다 큰 선택에서는 끄고, 선택이 줄면 다시 켭니다. 위 TextStore.GetTextExt 비용은 입력기가 켜진 채 큰 범위를 선택할 때 생기므로 이 계약으로 피합니다. 단어·줄·문단을 골라 한글로 다시 입력하는 흐름은 그대로입니다.

측정은 `build/tools/TextStressProbe`의 `ocr` 사례로 했습니다. Core/Desktop 10.0.12에 고정(rollForward Disable)했고 형태는 `lines`, 조건마다 한 번입니다. 기준은 707d6912의 읽기 전용 결과, 상한은 편집 가능 결과입니다. 상한 없음은 같은 편집 가능 결과에 `ime-always-on` 진단을 건 대조입니다. 여섯 실행 모두 exit 0, PASS였습니다. 단위는 ms입니다.

| 단계 | 기준 10만 | 상한 10만 | 상한 없음 10만 | 기준 1만 | 상한 1만 | 상한 없음 1만 |
|---|---:|---:|---:|---:|---:|---:|
| 결과 표시 뒤 첫 배치 | 39.4 | 34.2 | 48.3 | 32.7 | 39.8 | 127.9 |
| 전체 선택 뒤 정착 | 0.6 | 0.6 | 3,903.7 | 0.5 | 0.7 | 1,574.0 |
| 커서에서 전체 선택·정착 | 1.1 | 14.7 | 2,217.8 | 1.1 | 5.8 | 299.9 |
| 1,024자 선택·정착 | 4.9 | 18.1 | 34.8 | 4.6 | 18.6 | 48.9 |
| 4,096자 선택·정착 | 2.7 | 12.0 | 152.3 | 2.7 | 7.5 | 182.5 |
| 50자 입력·정착 | 396.3 | 548.7 | 546.8 | 35.4 | 80.7 | 57.3 |
| 다시 연 결과 정착 | 286.4 | 289.9 | 3,802.8 | 27.1 | 41.4 | 476.5 |

상한 없음은 1만 자에서도 전체 선택 뒤 1.6초가 걸렸습니다. 입력기가 켜진 4,096자 선택은 대조에서 150–180 ms였고 1,024자 선택은 상한 조건에서 18 ms였으므로 상한을 1,024자로 정했습니다. 50자 입력의 차이는 편집 표시와 원본 강조 갱신 비용이며, 1만 자에서 한 글자당 약 1.6 ms입니다. `OcrLargeTextWindowTests`는 편집 가능 여부, 큰 선택에서 입력기가 꺼지는지, 상한 경계에서 켜지는지를 검사합니다.

```powershell
dotnet build build/tools/TextStressProbe/TextStress.csproj -c Release -p:AppPayload=C:/absolute/payload -o C:/absolute/new-host-output
dotnet exec --runtimeconfig C:/absolute/pinned-10.0.12.runtimeconfig.json C:/absolute/new-host-output/MyCapture.App.Tests.dll C:/absolute/payload ocr 100000 lines C:/absolute/new-result.json ime-always-on
```
