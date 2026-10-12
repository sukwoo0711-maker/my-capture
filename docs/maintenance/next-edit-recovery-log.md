# 편집 초안 복구·공통 종료 보호 개발 LOG

2026-10-12. 작업 사본 `C:/GitRepositories/my-capture-edit-recovery`, branch `feat/next-edit-recovery`, base `d3223efbb7a921fc5785fd57ae029bd2643488fd`. 앱 실행·사용자 앱 종료·원격 변경·파일 삭제 없이 구현하고 아래 집중 검사만 실행했습니다. 테스트 데이터와 이전 산출물은 보존합니다.

## 구현·통합 계약

- `App.InitializeEditRecoveryAsync()`는 queue/persistence 초기화 뒤 호출합니다. `edit-drafts/<session>`의 최신 완성 revision을 읽고 복구/초안 버리기/취소를 제공합니다. 이미지의 원본·현재 base·삽입 이미지·편집 문서·회전·crop origin, 영상의 불변 원본 사본·편집 문서를 보존합니다.
- 변경 이벤트는 revision 증가와 750ms dispatcher timer만 수행합니다. PNG 인코딩은 별도 STA, 파일 쓰기/영상 복사/hash는 background task입니다. `ready.pending`의 durable flush 뒤 현재 revision과 일치할 때만 같은 디렉터리의 `ready.json`으로 이동합니다. 부분·손상 revision은 복구 대상에서 제외합니다.
- `RequestSafeShutdownAsync()`는 녹화 종료/저장·내보내기 완료·미저장 편집 선택을 처리한 뒤 앱을 종료합니다. 취소 또는 저장 실패는 앱과 편집을 유지합니다. `PrepareForStoreUpdateAsync()`는 같은 gate만 수행하며 앱을 계속 유지합니다. root가 tray/shell 종료 및 Store callback을 연결합니다.
- `PrepareCapturePersistenceForExitAsync` optional callback은 root가 진행 중 capture-persistence task 추적과 연결합니다. 종료 준비 동안 Gallery와 편집 입력의 이전 enabled 값을 보존하고 잠시 잠급니다. root는 `_preparingExit` 동안 새 capture/paste/gallery/record 진입을 차단하고 Store 설치 중 명시 종료도 차단합니다.
- OS session ending은 `FlushEditingDraftsAsync()`로 사용자 질문 없이 최근 초안을 보존하는 별도 경로입니다. SessionEnding hook과 대기 시간은 root App.xaml.cs 통합 범위입니다.
- 정상 commit/discard는 durable `retired` marker를 먼저 기록하고 실제 휴지통으로 이동합니다. 휴지통 실패는 파일을 유지하고 marker로 재복구를 막습니다. marker 기록 실패는 편집창을 유지합니다. 원본 사용자 파일을 제거하지 않습니다.
- `WindowsRecycleBin`은 library worker 소유 공용 서비스입니다. 이 branch에 복제하지 않습니다. 집중 빌드용 ignored targets가 Platform compile에 해당 worker 원본 파일을 포함했습니다. root는 공용 서비스 commit을 먼저 통합해야 합니다.
- 첫 cleanup commit `6b4950c`는 CapturePersistenceService의 rollback/파일/폴더 정리 3곳만 공용 휴지통 호출로 바꿨습니다. 이번 commit은 root 추가 승인에 따라 VideoLibraryService 2곳, VideoEditorWindow private render stage, VideoExportDialog temp cleanup도 같은 API로 바꿉니다. 기존 경로 검사와 실패 시 원본 보존 계약을 유지합니다.

## 실제 검증

모든 명령 cwd는 `C:/GitRepositories/my-capture-edit-recovery`입니다. 실행 정책 변경 또는 Bypass는 사용하지 않았습니다.

### Core 초안 저장 계약 — PASS

실제 명령:

```text
dotnet test tests/MyCapture.Core.Tests/MyCapture.Core.Tests.csproj -c Debug --filter FullyQualifiedName~EditDraftStoreTests --logger 'trx;LogFileName=edit-drafts-core-final.trx' --results-directory C:/GitRepositories/my-capture-edit-recovery/artifacts/edit-recovery/core-final -v quiet -nologo
```

exit 0, chunk `1d256a`, 6 PASS / 0 FAIL / 0 skip. 완성 revision/원본 유지/오래된 writer 제외/부분 및 hash 손상 제외/휴지통 실패 tombstone/경로 탈출 거부/영상 원본 한 번 복사를 검사합니다. TRX `artifacts/edit-recovery/core-final/edit-drafts-core-final.trx`.

### App 비표시 창 집중 검사 — PASS

실제 명령:

```text
dotnet test tests/MyCapture.App.Tests/MyCapture.App.Tests.csproj -c Debug --filter FullyQualifiedName~EditRecoveryTests --logger 'trx;LogFileName=edit-recovery-unit-final-14.trx' --results-directory C:/GitRepositories/my-capture-edit-recovery/artifacts/edit-recovery/unit-final-14 -p:CustomAfterMicrosoftCommonTargets=C:/GitRepositories/my-capture-edit-recovery/artifacts/edit-recovery/compile-shared-trash.targets -v quiet -nologo
```

exit 0, chunk `39e40a`, 14 PASS / 0 FAIL / 0 skip. save/discard/cancel·저장 실패 재시도·복구 문서에 undo history가 없어도 질문·원본 pixels·marker 실패 시 창 유지·휴지통 실패 시 파일 보존·회전/crop 원래 좌표·영상 MediaOpened 전 및 실패 상태의 세 선택·privacy detection 완료 대기·영상 동일 문서 저장 중 입력 차단·비동기 flush 중 입력 차단을 검사합니다. 창 `Show()` 및 입력 주입 없이 실행했습니다. TRX `artifacts/edit-recovery/unit-final-14/edit-recovery-unit-final-14.trx`.

### 최종 App 빌드 — PASS

```text
dotnet build src/MyCapture.App/MyCapture.App.csproj -c Debug -nologo -v quiet -p:CustomAfterMicrosoftCommonTargets=C:/GitRepositories/my-capture-edit-recovery/artifacts/edit-recovery/compile-shared-trash.targets -o C:/GitRepositories/my-capture-edit-recovery/artifacts/edit-recovery/build-final-2
```

exit 0, chunk `2223a0`, 경고 0 / 오류 0. 공용 휴지통 서비스의 외부 source include를 이용한 집중 빌드입니다.

### 정적 경계 검사 — PASS

`git diff --check` exit 0. 한/영 `EditRecovery_*` 키 집합 8개 동일 확인(exit 0, chunk `175bb9`). 같은 명령의 첫 raw-delete 경로가 틀려 Select-String 오류가 있었으므로 그 출력의 raw-delete 수치는 검증으로 쓰지 않습니다. 후속 잘못된 경로 검사는 exit 1(chunk `611a5e`)로 보존합니다. 정확한 path 확인 후 아래 명령을 실제 실행했습니다.

```powershell
$ErrorActionPreference = 'Stop'; $paths = @('src/MyCapture.App/Editing/CapturePersistenceService.cs','src/MyCapture.App/Recording/VideoLibraryService.cs','src/MyCapture.App/Recording/VideoEditorWindow.cs','src/MyCapture.App/Recording/VideoExportDialog.cs','src/MyCapture.Core/Storage/EditDraftStore.cs','src/MyCapture.App/Editing/EditDraftSession.cs','tests/MyCapture.Core.Tests/EditDraftStoreTests.cs','tests/MyCapture.App.Tests/EditRecoveryTests.cs'); foreach ($p in $paths) { if (!(Test-Path -LiteralPath $p)) { throw "Missing path $p" } }; $bad = Select-String -Path $paths -Pattern '\b(?:File|Directory)\.Delete\s*\('; if ($bad) { $bad; exit 1 }; Write-Output 'RawDeleteCalls=0; CheckedPaths=8'
```

exit 0, chunk `1965e4`, `RawDeleteCalls=0; CheckedPaths=8`.

## 중간 실패와 수정

중간 TRX/작업 사본을 보존합니다. 테스트 추가 과정에서 ctor 인수 형식 오류(exit 1, `c799a1`), internal enum의 public theory 인수 접근성 오류(exit 1, `5021a0`)를 수정했습니다. 비표시 STA fixture의 SynchronizationContext 누락으로 실패한 2건(`35556`, final `b412b6`, 6 PASS / 2 FAIL)은 fixture에 dispatcher context를 설정하고 production session의 snapshot/failure callback도 명시적으로 dispatcher에서 실행하도록 보강했습니다. 이후 확대 집중 검사 `b649f9` 8/8, `0228ac` 11/11, `fa6675` 13/13, `607fed` 13/13과 위 최종 14/14 모두 exit 0입니다.

독립 source reviewer가 복구 영상의 media-ready 의존 폐기, 동일 문서 저장 중 draft retire 대기 입력 경쟁, 개인정보 탐지 중 종료, flush 중 새 작업 시작 경쟁을 지적했습니다. 해당 경계를 수정하고 위 회귀 검사를 추가했습니다. 영상 복구의 큰 원본 hash 계산도 UI thread 밖으로 이동했습니다.

## 다음 실기 검증

실행 상태 `NOT RUN`, exit `N/A`: root 직렬 슬롯 대기 중인 실제 복구 대화상자/한영 UI 전후 이미지, 자신이 띄운 격리 fixture만 강제 종료 후 재시작 복구, 실제 영상 media-load/재인코딩/commit, tray 종료 및 Store gate, OS session-ending 통합. 사용자 앱은 종료하지 않습니다. 공용 휴지통 canary와 root App hooks 통합 후 수행합니다. 기존 전체 App/native 필수 검사는 root 소유입니다.

최종 전달 전 recovered-session 이벤트 연결을 보강하고 commit callback을 동일 동작의 명시적 분기로 정리한 뒤 같은 14건을 재실행합니다. 실제 명령은 다음과 같습니다.

```text
dotnet test tests/MyCapture.App.Tests/MyCapture.App.Tests.csproj -c Debug --filter FullyQualifiedName~EditRecoveryTests --logger 'trx;LogFileName=edit-recovery-unit-receipt.trx' --results-directory C:/GitRepositories/my-capture-edit-recovery/artifacts/edit-recovery/unit-receipt -p:CustomAfterMicrosoftCommonTargets=C:/GitRepositories/my-capture-edit-recovery/artifacts/edit-recovery/compile-shared-trash.targets -v quiet -nologo
```

PASS, exit 0, chunk `9e31a0`, 14 PASS / 0 FAIL / 0 skip. 최종 source의 집중 검사 결과입니다.

## 반복 주석 편집의 초안 IO 보강

root 검토에서 매 revision의 PNG 재쓰기와 이전 revision 휴지통 이동이 확인되어 후속으로 보강했습니다. 이제 session 내부 `blobs/<SHA256>.blob`에 같은 내용은 한 번만 durable 저장하고 revision에는 ready 문서만 씁니다. 작은 revision marker는 session 폐기 때 함께 휴지통으로 이동하므로 debounce마다 shell 삭제 작업을 하지 않습니다. 복구 시 최신 revision부터 검사하고 첫 유효 revision을 선택합니다. 안전한 `ResolveFile`이 문서에 포함된 파일명과 64자리 hash·session 내부 경로·링크 제외를 확인합니다. 같은 hash를 가리키는 original/base는 복구 integrity 검사를 한 번만 하고 같은 bitmap으로 읽습니다.

이미지 초안 PNG는 frozen BitmapSource별 weak cache를 이용해 같은 bitmap을 다시 인코딩하지 않습니다. 회전/crop으로 새 bitmap이 생기면 별도 내용으로 저장합니다. 활성 session의 기존 blob은 제거하지 않으며 최종 retired session만 실제 휴지통으로 이동합니다.

아래 실제 명령 cwd는 동일합니다.

```text
dotnet test tests/MyCapture.Core.Tests/MyCapture.Core.Tests.csproj -c Debug --filter FullyQualifiedName~EditDraftStoreTests --logger 'trx;LogFileName=edit-drafts-core-blobs-receipt.trx' --results-directory C:/GitRepositories/my-capture-edit-recovery/artifacts/edit-recovery/core-blobs-receipt -v quiet -nologo
```

PASS exit 0, chunk `f25807`, Core 8/8. 기존 6건과 반복 내용 blob 재사용/변경된 pixels의 별도 blob 및 원본 유지 2건입니다. 실제 TRX를 읽은 명령 `[xml]$result = Get-Content -Encoding UTF8 artifacts/edit-recovery/core-blobs-receipt/edit-drafts-core-blobs-receipt.trx; $result.TestRun.Results.UnitTestResult | Where-Object testName -like '*AnnotationOnlyRevisions*' | ForEach-Object { $_.outcome; $_.Output.StdOut }` exit 0(chunk `165a4d`)에서 `Revisions=20; BlobFiles=2; BlobBytes=1025; RecycleCallsBeforeRetire=0; OriginalLastWriteUtcUnchanged=True`를 확인했습니다. 작은 fixture의 실제 파일 수/크기 근거이며 제품 실기 시간 측정은 아닙니다.

```text
dotnet test tests/MyCapture.App.Tests/MyCapture.App.Tests.csproj -c Debug --filter FullyQualifiedName~EditRecoveryTests --logger 'trx;LogFileName=edit-recovery-unit-blobs.trx' --results-directory C:/GitRepositories/my-capture-edit-recovery/artifacts/edit-recovery/unit-blobs -p:CustomAfterMicrosoftCommonTargets=C:/GitRepositories/my-capture-edit-recovery/artifacts/edit-recovery/compile-shared-trash.targets -v quiet -nologo
```

PASS exit 0, chunk `b8ac69`, App 15/15. 새 검사에서 주석 문서가 변경돼도 original/base PNG byte[]가 같은 캐시 객체인 것을 확인했습니다. 이전 14건도 유지합니다.

최종 후속 App 빌드 실제 명령 `dotnet build src/MyCapture.App/MyCapture.App.csproj -c Debug -nologo -v quiet -p:CustomAfterMicrosoftCommonTargets=C:/GitRepositories/my-capture-edit-recovery/artifacts/edit-recovery/compile-shared-trash.targets -o C:/GitRepositories/my-capture-edit-recovery/artifacts/edit-recovery/build-blobs`: PASS exit 0, chunk `dde2af`, 경고 0 / 오류 0.
