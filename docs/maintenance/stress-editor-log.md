# 최악 조건 이미지 편집 성능 작업

2026-10-12. worktree C:/GitRepositories/my-capture-edit-recovery, branch perf/stress-editor, base73e02936b55db1e42d7a547d2107a80cf898aa1c. 이전 b07902c clean/common-dir 확인 후 기존 branch/artifacts를 보존하며 재사용. root에서 지정한 source/API 소유권은 vector/ink/image/draft이며 text 입력/fit/renderer 및 library/queue는 다른 worker입니다. 공유 AnnotationCanvas 변경은 사전 조율합니다.

Windows 정본 2026-09-28-developer-log-r1 및 handoff§5 직접 읽기 chunk765df2 exit0. creative-autonomy2026-09-30-r2 직접 읽기 e3631c exit0. assembly2026-09-28-developer-log-r1/surface2026-09-28-copy-boundary-r1 본문 재확인 a8174d exit0(앞선 code와 합친 잘린 출력은 로딩 완료 증거로 사용하지 않음).

준비 source 가설: AnnotationRenderer는 모든 pass에서 모든 item을 순회하고 stroke마다 frozen Pen/Brush를 새로 생성합니다. Dense Pen은 point마다 LineSegment를 가진 PathGeometry를 매 render 새로 구성합니다. AnnotationDocument.Add의 ZIndex는 Items.Max 스캔입니다. 현재는 읽기 근거일 뿐 실제 병목으로 확정하지 않았으며 product source 수정도 없습니다.

artifact harness: actual product QA11 payload를 argument로 load, 실제 AnnotationEditorControl/Surface/controller/render/undo/session/snapshot/store를 사용. 4K/8K × 1k/10k 주석, 각각 ink20개 × 기본5000points(총100k points). 초기 item 위치/geometry는 합성, 텍스트 없음. 실제 surface offscreen viewport1280×720 raster화 및 fit-scale150%, 추가 rectangle10회와 undo/redo, selection move, 동시에 실제 draft PNG encode/hash/durable write. 결과의 초기 viewport PNG+pixelSHA, 원본/최신 JSON, IDs/count/inkpoint보존, recoverable draft completeness 확인. repeat actual Window constructor/Close(no Show)3회 weakref/forcedGC는 편집 측정 밖에서 유지 상태 관측. HWND presentation/실제 zoom UI는 측정하지 않음.

60초/owned process private memory2GB guard가 자체 process만 exit124로 중단하며 guard JSON/phase progress를 보존. 다른 process 종료 없음. 원본/출력/임시 파일 모두 보존. renderer/case threshold를 발명하지 않음.

실제 준비 build(cwd 위 worktree):

```powershell
dotnet build artifacts/stress-editor/harness/EditorStress.csproj -c Release -o artifacts/stress-editor/harness-build -nologo -v quiet
```

chunk303303 exit1: C# var 다중 선언 CS0819 1개. explicit PointD로 source 수정. 이 시점 root library/selection baseline 시작으로 build/test/GUI/perf 모두 보류 지시를 받아 최종 build 재실행도 아직 NOT RUN(exit N/A). source/harness만 계속 준비했습니다.

다음 슬롯의 실제 명령 예정:

```powershell
dotnet build artifacts/stress-editor/harness/EditorStress.csproj -c Release -o artifacts/stress-editor/harness-build -nologo -v quiet
& ./artifacts/stress-editor/run-editor-stress.ps1 -Payload C:/GitRepositories/my-capture-codex/artifacts/qa11-product-readiness -Width 3840 -Items 1000 -StrokePoints 5000 -Label baseline
```

baseline/case performance는 아직 NOT RUN, exitN/A. 처음 case 완료 후 같은 명령의 Width/Items만 변경해4k10k/8k1k/8k10k를 root 직렬 슬롯에서 실행합니다. 필요한 UI native presentation은 root 슬롯 별도이며 현재는 새 창을 띄우지 않는 actual detached rendering/Window.Close 경로입니다. 실제 결과를 확보하기 전 성능 개선 확정/commit 하지 않습니다.

runtime 비교 계약 추가: runner는 baseline/after 모두 Microsoft.NETCore.App 및 Microsoft.WindowsDesktop.App10.0.11, rollForward Disable인 고유 artifact runtimeconfig를 생성해 dotnet exec에 명시합니다. 제품 원본 configProperties는 유지하고 source payload runtimeconfig는 수정하지 않습니다. runtimeconfig raw/SHA를 receipt에 기록하고 실제 FrameworkDescription/Environment.Version 및 PresentationFramework loaded DLL 경로/FileVersion을 측정 JSON에 기록합니다. runtime이 없으면 실행 실패하며 자동 fallback하지 않습니다. global RuntimeFrameworkVersion 속성은 사용하지 않습니다.

## 첫 baseline 실제 실행

root 단독 슬롯 승인 후 위 build를 동일 cwd/명령으로 재실행했습니다. chunk67d0eb exit0, 0경고/0오류. 이어 위 3840/1000 baseline runner를 동일 cwd/명령으로 실행하여 chunk1fae72 exit0, owned PID40528 종료를 확인했습니다. receipt와 raw stdout/stderr/runtimeconfig 및 result는 `artifacts/stress-editor/baseline-3840-1000-20261012T081249Z-af992f1b*`에 보존합니다. result 읽기 b75da7 exit0.

제품 SHA256 A5A7BB42638741063D9E368050723C8A334D32A31EFA1E6D39C12DF213852CE0, ProductVersion4.1.1+2fc7fa4b0de8fd4ac6c13c916c2b83d5b9f15402, 실제 .NET10.0.11/WPF10.0.11(FileVersion10.0.1126.37416). 4K/1000주석/100000inkpoints: viewport3pass209.34/227.71/251.60ms, 각32.8MB allocation; fit150%351.83ms. 실제 add+undo+redo+render10회135.09–311.73ms. heartbeat37samples max705.59ms/p95284.05ms. latest draft flush52.58ms, blob1/37515B, revision32, 최종1010items/100000points/기존ID보존/복구JSON completeness 통과. peakprivate443084800B. repeated unopened window weakref1/3 생존은 단일 forced-GC 관측이며 누수 확정 근거가 아닙니다. initial viewport SHA A7D407C5EDB8F2369332348015058B716863F716BE71D13972624C76C19202BD.

실제 rendering의 allocation과 매pass100k LineSegment 생성이 대응합니다. 우선 point누락 없이 StreamGeometry 사용의 동일pixel/할당량 감소를 검토하며 수정·효과 확정은 후속 측정으로 확인합니다. 슬롯 종료 후 root/icons 다음 측정 동안 build/test/성능 실행을 다시 보류합니다.

소스 준비: DrawPen에서만 매point LineSegment/PathFigure를 StreamGeometry BeginFigure/LineTo(isSmoothJoin:false)로 바꿉니다. 기존 segment 기본값과 동일하며 cap/join/opacity/highlighter/onepoint 처리는 유지합니다. 캐시를 도입하지 않아 같은 point count의 live replacement/append/undo가 stale geometry를 읽지 않습니다. 텍스트/Canvas/Controls 소유 파일은 변경하지 않습니다. legacy PathGeometry pixel oracle 및 mutable points 테스트8개 준비. 현재 테스트/after publish/비교는 슬롯 대기 중 NOT RUN입니다.

재현 소스는 build/tools/EditorStressProbe에 작은 standalone project+runner+README로 보존합니다(원 ignored artifact도 그대로 보존, source 복사10f00c exit0). runner의 저장소/evidence 경로는 PSScriptRoot에서 absolute로 계산하며 label+UTC+GUID의 고유 출력만 사용합니다. 원본 baseline ProductVersion2fc7fa와 작업base73e029의 git diff는 handoff 문서만입니다(a25e07 exit0). 기존profile PublishReadyToRun=true/selfcontained 및 pin targets의 selective Core/Desktop10.0.11(Windows SDK ref 불변) 실제 본문 확인413670 exit0.

반복 Window lifetime fixture는 root 지적을 반영해 NoInlining 생성/Close helper에서 WeakReference만 반환하고 dispatcher ApplicationIdle drain 후 forced-GC2회 각각 관측하도록 보강했습니다. 첫 baseline weakref1/3과 이 보강값을 성능 개선으로 비교하지 않습니다. 편집/render phase는 동일하며 Window GC는 모두 heartbeat 종료 뒤 실행합니다.

## 최소 수정 검증과 4조건 비교

모든 명령 cwd는 C:/GitRepositories/my-capture-edit-recovery입니다. root 단독 슬롯에서 아래 순서로 실행했습니다.

```powershell
dotnet test tests/MyCapture.App.Tests/MyCapture.App.Tests.csproj -c Release --filter FullyQualifiedName~EditorInkRenderingTests --logger 'trx;LogFileName=ink-focused.trx' --results-directory artifacts/stress-editor/focused-tests -nologo -v quiet
dotnet publish src/MyCapture.App/MyCapture.App.csproj -c Release -r win-x64 --self-contained true -p:PublishProfile=win-x64-self-contained -p:CustomAfterMicrosoftCommonTargets=C:/GitRepositories/my-capture-codex/artifacts/latency-regression/pin-runtime-10.0.11.targets -o C:/GitRepositories/my-capture-edit-recovery/artifacts/stress-editor/after-publish -nologo -v quiet
dotnet build build/tools/EditorStressProbe/EditorStress.csproj -c Release -p:AppPayload=C:/GitRepositories/my-capture-codex/artifacts/qa11-product-readiness -o artifacts/stress-editor/harness-build -nologo -v quiet
& ./build/tools/EditorStressProbe/run-editor-stress.ps1 -Payload C:/GitRepositories/my-capture-edit-recovery/artifacts/stress-editor/after-publish -Width 3840 -Items 1000 -StrokePoints 5000 -Label after
```

focused test session69981 finalc92f1d exit0: 8통과/0실패/0skip, TRX artifacts/stress-editor/focused-tests/ink-focused.trx. Publish session57373 finala00ca6 exit0. Probe build1da332 exit0, 경고0/오류0. Firstafter b5faca exit0 PID55848 종료. 비교 대상 after payload는 base73e029에 이 미커밋 제품 diff를 적용한 측정 산출물이므로 ProductVersion의 baseSHA와 최종커밋을 구분합니다. 실제 DLL SHA3343646CFB965B19820D40EF40AA462FD1B98ADB16B170CEC1B0194746D32694. PE/CLR ManagedNativeHeader196bytes+R2Rsignature0x00525452 두 publish 모두 실제 확인(fb6607 exit0), runtimeconfig Core/Desktop10.0.11 일치. publish-metadata.json 보존.

나머지 3조건은 위 runner의 Width/Items를 아래 표 값으로 바꾸어 baseline(기존QA11 payload)→after(위 after-publish) 순서로 직렬 실행했습니다. 각 정확한 dotnetexec arguments/cwd/host/product/runtimeconfig hash/PID/exit와 rawstdout/stderr는 각 *-process.json 및 log에 있습니다.

| 조건 | baseline chunk/PID/exit | after chunk/PID/exit | viewport median ms | 해당 pass allocation MB | heartbeat max ms | heartbeat p95 ms |
|---|---|---|---|---|---|---|
|4K/1k|1fae72/40528/0|b5faca/55848/0|227.71→125.26|32.81→5.22|705.59→330.21|284.05→179.16|
|4K/10k|00e54b/18200/0|ec900f/45496/0|341.17→219.24|42.25→14.62|674.07→581.29|391.84→296.05|
|8K/1k|e2202c/64504/0|3a5cc6/57704/0|204.58→103.35|32.81→5.22|369.67→301.65|297.82→294.33|
|8K/10k|1936cc/26468/0|f0f19f/47960/0|276.71→189.87|42.25→14.12|728.74→512.87|277.22→348.73|

8개 결과 모두 .NET/WPF10.0.11, 같은 초기 viewportpixelSHA, 최종1010/10010주석 및100000inkpoints/기존ID보존/복구JSON completeness PASS, blob1 유지. after weakref2회관측 모두0/0. paired-summary.json 생성/비교43ce8c exit0. 원본8개 result/PNG/JSON/receipt 모두 보존. 슬롯 완료 후 root에 반환했습니다.

확정 효과는 매render의 point별 mutableFreezable 제거와 rendering시간/할당량 감소입니다. draft latestflush는 4K1k52.58→87.56ms,4K10k90.99→91.01,8K1k97.04→86.62,8K10k92.58→94.03로 일관된 개선이 아닙니다. peakprivate도443→449/803→778/567→606/672→804MB로 일관된 개선이 없으며8K10kheartbeatp95는 악화했습니다. 단회/3render median이고 heartbeat는 렌더와 autosave가 겹친 process 관측입니다. 전체메모리/초안성능 개선으로 보고하지 않습니다.

추가 진단 준비: root 요청으로 PixelPattern entropy 옵션(고정xorshift32 seed/불투명alpha)을 준비했습니다. 기존 solid 산출물은 그대로 보존합니다. 제품 추가 변경 없이 원본/복구 PNG pixelSHA 및 크기 검증을 heartbeat 밖에 추가했습니다. 준비 후 build/8K1kentropy baseline/after는 다음root단독슬롯 대기이며 이 시점 NOT RUN입니다.
