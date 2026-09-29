# MyCapture library workspace — execution and validation record

최신 판정 (2026-09-25 r3): **태그 검색 수정 검증 완료 / 전체 과제 PARTIAL / 패키징 BLOCKED**.

최신 세부 결과는 문서 아래 `2026-09-25 r3` 절을 따른다. 아래 2026-09-24 내용의 태그 검색 실패는 과거 이력이다. 이번 전체 Release 실행에서는 해당 테스트가 통과했지만, 이후 추가한 저장 경합 테스트의 실행 요청이 차단되어 추가 검증과 최종 전체 재실행은 미완료다.

## 2026-09-24 이전 시도 (이력 보존)

Date: 2026-09-24. Outcome: **PARTIAL / RELEASE BLOCKED**.

## Ownership and source

- Repository / working directory for commands below: `C:\GitRepositories\my-capture`.
- Source baseline: `117b0ef3a9537f93ed6a2c0935f62d93cdd4664a` (3.0.0).
- Task branch: `feat/library-workspace-aac1a031`; local development checkpoint, not an approved release.
- WebGPT task ID: `aac1a031-0805-4643-81a0-596d8181b03e`. The task token is intentionally not recorded.
- Pre-existing README.md, CONTRIBUTING.md and untracked AGENTS.md were preserved and excluded from this task's commit.
- No new worktree, installation, user-data migration, resident-process termination, public push, tag or GitHub Release was performed.

## Product direction and implemented scope

Preserve the existing Windows 11 local-first screenshot / annotation / pin / OCR / silent-video workspace. Prioritize finding and reusing past captures and controlled sharing rather than adding unrelated recording features. Audio remains intentionally out of scope. MIT terms, third-party notices, payment infrastructure and Authenticode signing remain unchanged.

Added bounded title/tag metadata editing, F2/detail access, restart/OCR preservation checks, a cancellable media-only ZIP exporter, visible/total counts and recoverable empty/filter states. ZIP exports use generic filenames and rendered PNG/current MP4 only; they do not include original images, OCR index, user labels or settings. This is not a promise that visible pixels or intrinsic media metadata are free of sensitive information; preview the actual media before sharing.

**Known unfinished requirement:** tags persist, but tag-based search does not work. The actual field-aware search engine does not consume CaptureRecord.SearchHaystack. Its necessary modification was denied by platform security review; no equivalent implementation was added elsewhere to bypass that denial. The failing test is retained as a release gate.

## Verification matrix

All command working directories are the repository above unless otherwise stated. Exit code N/A means the validation itself did not run; it is not a pass.

| Check | Actual command / execution | Exit | Result and evidence |
|---|---|---:|---|
| Baseline solution | `powershell.exe -NoProfile -File build\build.ps1 -Configuration Release -Test` | 0 | Build/test wrapper passed before edits. Reported totals: Core 445 + App 787. Total count is not an assertion that no tests were skipped. Session ea5b5308-9fa7-4b68-a906-904afa6bd97a. |
| First implementation | Same build/test command | 1 | Build passed; App 798 passed, 1 failed, 1 skipped (800 total). The combined persistence/search test stopped at a missing tag match. Session 7f92ff66-27d2-4140-a32e-c82b0af21c60. |
| Separated behavioral checks | Same build/test command | 1 | Persistence and OCR preservation passed; standalone tag search still failed. App 799 passed, 1 failed, 1 skipped (801 total); Core 445 passed. Session 543a3209-65eb-43c0-8115-c438b7e9f53b. |
| Final source build/test | Same build/test command | 1 overall | Build passed. Core 445 passed; App 799 passed, 1 failed, 1 skipped. Total 1,246: 1,244 passed, 1 failed, 1 skipped. Session 2a48a96d-b3a2-4f62-967f-05fba43e52c8. Logs: build/logs/build.log and test.log. |
| Self-contained candidate r1 | Doctor + publish command below; output workspace-candidate-aac1a031 | 0 | Actual runtime-included Windows executable built. Session bb43ec4b-85ec-40fe-a830-13a988da0844. Not a release package. |
| Seven-test runner preflight | `powershell.exe -NoProfile -File build\run-packaged-self-tests.ps1 -PublishRoot C:\GitRepositories\my-capture\artifacts\validation\workspace-candidate-aac1a031 -OutputRoot C:\GitRepositories\my-capture\artifacts\validation\workspace-runtime-aac1a031` | 1 | Wrapper refused because an existing user MyCapture instance, PID 16476, was running. Session bc931fea-4465-47bb-9c96-91aa72f51429. |
| Capture / shell / advanced / settings / OCR / recording / video-editor packaged checks | Internal checks of the runner above | N/A | NOT_RUN: prerequisite failure occurred before launching any of these seven checks. The existing instance was not stopped and the gate was not bypassed. |
| Real WPF UX r1 | Candidate r1 MyCapture.exe `--selftest-ux-review`, launched with Start-Process -PassThru -Wait | 1 | Six compact-gallery failures. Report directory: C:\Users\sukwo\AppData\Local\Temp\MyCapture-ux-review-eu53sdqk.x3s. Session fb9dffe9-5275-44c0-b00d-9131b87fed21. |
| Self-contained candidate r2 | Doctor + publish command below | 0 | Includes layout fixes and stronger dialog-minimum checks. Session a44d5837-d9d9-4ae0-aeb4-c5420433a9fd. |
| Real WPF UX r2 | Candidate r2 MyCapture.exe `--selftest-ux-review`, launched with Start-Process -PassThru -Wait | 0 | RESULT: PASS (fixture rendering and command checks; manual visual review required). Report directory: C:\Users\sukwo\AppData\Local\Temp\MyCapture-ux-review-ui35oois.xt3. Session f4d944b5-aa18-4336-aaed-5bb17499b501. Selected generated screenshots were visually reviewed. |
| Final installer / portable package, public push, tag and GitHub Release | Not attempted | N/A | NOT_RUN: release condition was not met. No version bump or false passing release was produced. |

### Actual r2 publish command

```powershell
$ErrorActionPreference = 'Stop'
& .\build\doctor.ps1 -Quiet
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
& dotnet publish src\MyCapture.App\MyCapture.App.csproj -c Release -p:PublishProfile=win-x64-self-contained -o artifacts\validation\workspace-candidate-r2-aac1a031
exit $LASTEXITCODE
```

### Actual r2 UI command

```powershell
$p = Start-Process -FilePath 'C:\GitRepositories\my-capture\artifacts\validation\workspace-candidate-r2-aac1a031\MyCapture.exe' -ArgumentList '--selftest-ux-review' -WorkingDirectory 'C:\GitRepositories\my-capture\artifacts\validation\workspace-candidate-r2-aac1a031' -PassThru -Wait
Write-Output ('UX_SELFTEST_EXIT=' + $p.ExitCode)
exit $p.ExitCode
```

## Measured UI correction loop

The first real WPF run measured only 292 DIP of compact gallery viewport for a 304-DIP full-card requirement, failing in all six themes. A blank action-status line consumed vertical space. Generated screenshots also showed the organization dialog's save/cancel actions clipped by its scrolling content.

Changes: collapse the empty status row, make the compact filter rail scrollable, move dialog save/cancel into a fixed footer, increase its default height, and ignore late ZIP-progress callbacks after completion/cancellation. The diagnostic now also checks the dialog at its true 400x360 minimum and asserts both action buttons stay inside the window.

The second run measured **311 DIP**, meeting the unchanged 304-DIP gallery requirement in midnight, glass, workspace, daylight, glass-light and high-contrast. Automated gallery contrast checks reported zero unreadable text elements in each theme. Both organization actions were fully visible at normal and minimum sizes in all six themes. The gallery wheel moved 48 DIP, with no nested tile scroll viewer and offscreen rows virtualized.

Viewed synthetic screenshots include:
- r1 gallery-compact-midnight-144dpi.png and organization-normal-midnight-144dpi.png;
- r2 gallery-compact-midnight-144dpi.png and organization-compact-midnight-144dpi.png.

The fixture does not load the user's capture library or settings and does not capture their screen. It is not a user study, complete keyboard/screen-reader audit, installer acceptance test, or full capture/recording end-to-end validation.

Hardware observed: DISPLAY1 3440x1440 and DISPLAY2 768x1366, both 96 DPI. PNG rendering at 96/144/192 DPI is raster-density testing, not native mixed-DPI hardware or pointer-gesture validation.

## Tests, skip and security exceptions

Fourteen new behavioral cases were added: thirteen passed and the tag-search requirement failed. Coverage includes validation/deduplication, metadata persistence through reload and OCR updates without advancing pixel revisions, missing/cancelled edits, no thumbnail decode on metadata notification, ZIP contents, no overwrite (including a destination race), cancellation cleanup and missing-media cleanup.

The unchanged skipped test is `GalleryBatchDragTests.FinalSourceSymlinkIsRejected_AndExternalTargetIsPreserved`: the local Windows token cannot create file symlinks (error 1314). The test says the actual file-symlink case is mandatory on GITHUB_ACTIONS; junction tests do not substitute for it. No privilege or policy setting was changed to force a local pass.

Two platform-denied operations had no execution session/exit code:
1. Read attempt: `git grep -n -E 'PublishRecordAsync|CloneRecord|Title =|SaveRecordMeta|ReservePublication' -- src/MyCapture.Core/Queue/CaptureQueue.cs`.
2. Guarded PowerShell modification of `src/MyCapture.Core/Queue/CaptureTextSearch.cs`, intended to add Tags field attribution and term matching. That mutation did not run, and the file remains unchanged.

Exact response for both:

> 요청의 보안 상태를 결정하지 못해 이 도구 요청은 OpenAI에 의해 차단되었습니다.

Neither denial proves that a file is absent, unreadable or malicious. The blocked operations were not retried or rephrased through another connector, shell, encoding or path. Independent public-API behavioral checks continued.

## Remaining release conditions

This checkpoint must not be merged/released as a complete feature. Resolve the authorized-workflow blocker for tag search without bypassing platform review; retain the test until it genuinely passes. Then rerun the complete suite, the seven packaged feature tests when the existing application can be safely closed, the mandatory symlink case on its required runner, and the repository's installer/update/release verification gates. Packaging requires an exact committed, clean source tree; preserve unrelated user changes through an authorized isolated release workspace rather than resetting them.

Code signing, payment/entitlement, commercial support and sales terms were not implemented or validated. No claim that MyCapture is ready for paid distribution is made.

## 2026-09-25 r3 — 검색 수정 체크포인트와 독립 WPF 재검증

### 범위와 결론

작업 시작 시 실제 브랜치는 `feat/library-workspace-aac1a031`, HEAD는 `369df6b1f75d4c1a4e51c35de918aa906451f8f7`이었다. 기존 작업 디렉터리를 재사용했다. 시작 시 변경은 README.md, CONTRIBUTING.md, untracked AGENTS.md뿐이었다. 이 세 파일은 편집하거나 커밋하지 않았다.

`CaptureTextSearch`에 `Tags = 16` 출처 플래그와 실제 태그 매칭을 추가했다. 기존 Title/WindowTitle/OcrText/MediaType 비트값, OrdinalIgnoreCase 부분 문자열, 공백 구분 다중어 AND, 출처 합집합, 최신순 정렬은 유지한다. SearchHaystack 우회가 아니다. 태그 null/빈 검색/한영 혼합/중복어/부분 문자열/여러 필드의 AND/정렬/기존 OCR 출처 및 리비전 보존을 검사하는 Core 회귀 14개를 추가했다.

검색 소스와 이 14개 테스트를 추가한 뒤 전체 Release 빌드·테스트를 실제 완료했다. **Core 459 PASS, App 800 PASS / 0 FAIL / 1 SKIP; 총 1,260개 중 1,259 PASS / 1 SKIP.** 기존 `LibraryWorkspaceTests.Tags_AreSearchableTogetherWithTitle`는 PASS다. 이후 작성한 저장 경합 테스트 초안 4개는 이 집계에 포함되지 않는다.

### 추가 검토와 미완료 범위

메타데이터 경로는 저장 예약, eviction lease, 실패 시 제목/태그/UpdatedAt 복구, 사용자 라벨을 기록하지 않는 로그를 사용한다. 다만 공유 레코드를 변경한 뒤 await하고 실패 시 복구하므로 연속 편집/OCR 저장과 실패 복구의 상호작용을 더 검증해야 한다. 실제 오류로 입증된 것은 아니며 안전성이 추가 검증된 것도 아니다. 기존에 차단됐던 CaptureQueue 소스 읽기를 이번 작업에서 재시도하지 않았다.

이 검증을 위해 `tests/MyCapture.App.Tests/LibraryWorkspaceConcurrencyTests.cs` 초안을 작성했다. 저장 실패 시 메모리·디스크 복구, 다른 GalleryController의 연속 편집/OCR과 실패 경합(2개), 저장 용량 대기 중 취소의 총 4개 케이스다. 실행 요청이 플랫폼에서 거부되어 **컴파일·실행 결과 없음 / 커밋 제외 / 작업 디렉터리에 그대로 보존**이다. 이 파일을 삭제·이동하거나 검사를 스킵시켜 재실행하지 않았다. 전체 솔루션 실행으로 같은 차단 검증을 우회하지도 않았다. 따라서 초안 추가 후의 최종 전체 재실행은 NOT_RUN이며, 전체 기능 완료나 패키징 준비 완료를 선언하지 않는다.

ZIP 검토: UI는 준비된 rendered PNG/current MP4 경로를 넘기며 PrepareBatchAsync는 디스크 준비를 Task.Run에서 수행한다. exporter는 사용자 제목/태그/OCR 대신 일반 파일명, CreateNew 임시 파일, 취소 검사, no-overwrite File.Move, 소유한 partial만 정리하는 경로를 사용한다. 중복 실행 가드와 완료 후 stale progress callback 가드가 있다. 기존 매체 전용 ZIP, 목적지 선점, 취소, 없는 매체 정리 회귀는 위 전체 실행에 포함되어 통과했다. 새 ZIP 소스 변경은 하지 않았다. 대형 실제 라이브러리 지연·실제 공유 미디어의 개인정보를 이번 fixture 검증이 보증하지는 않는다.

### 실행 명령과 결과

`ROOT = C:\GitRepositories\my-capture`, `CANDIDATE = C:\GitRepositories\my-capture\artifacts\validation\workspace-candidate-r3`, `UX = C:\Users\sukwo\AppData\Local\Temp\MyCapture-ux-review-wpw4jwfl.fon`.

cmd 명령은 `chcp 65001 >nul & `로 시작했고, PowerShell 명령은 `[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false);`로 시작했다. 텍스트 읽기는 `-Encoding utf8`였다. 아래 cmd/powershell 본문에는 이 접두어를 포함하거나 이 규칙을 적용한다. 프로세스 ID가 별도로 반환되지 않은 명령은 실제 Worker 세션 ID를 기록했다. 진행 중이던 모든 실행은 같은 세션에서 최종 종료 코드를 수집했다.

| 검사 | cwd | 명령/실행 | 세션 또는 PID | 종료 코드 | 결과와 근거 |
|---|---|---|---|---:|---|
| 저장소 지침 직접 읽기 | ROOT | `Get-Content -LiteralPath 'AGENTS.md' -Encoding utf8` | 1e965d02-8fb6-4347-a547-740657c79322 | 0 | 지침 확인 |
| Windows 정본 직접 읽기 | ROOT | `Get-Content -LiteralPath 'C:/Users/sukwo/.codex/instructions/windows-execution.md' -Encoding utf8` | b1e329bd-cd24-4b7e-bd54-267e4c1bee8d | 0 | 실행 정책 확인, 변경 없음 |
| 시작 상태 | ROOT | `git status --short --branch` | 8b236c80-b578-4ab0-9674-e558fcd3408d | 0 | 지정 브랜치; 기존 3개 파일만 변경 |
| 시작 HEAD | ROOT | `git rev-parse HEAD` | 68e4e02f-7cf8-45dc-be88-fae6f04e6267 | 0 | 지정 체크포인트와 일치 |
| 전체 Release | ROOT | `chcp 65001 >nul & powershell.exe -NoProfile -File build\build.ps1 -Configuration Release -Test` | 1788ade4-c9f0-43be-ab3d-3883cc7a9d20 | 0 | 빌드 완료, Core 459 PASS; App 800 PASS / 1 SKIP |
| 새 저장 경합 회귀 | ROOT | 아래 차단 명령 그대로 | 생성 안 됨 | N/A | BLOCKED / NOT_RUN; 테스트 결과 아님 |
| 저장 경합 초안 이후 전체 재실행 | ROOT | 실행하지 않음 | 없음 | N/A | NOT_RUN; 차단된 검증을 전체 실행으로 우회하지 않음 |
| self-contained 진단 실행 파일 | ROOT | 아래 publish 명령 | f250a885-5216-4413-8807-5b654f3808a8 | 0 | CANDIDATE에 실제 Windows 실행 파일 생성; 배포 패키지 아님 |
| 실제 WPF UX | CANDIDATE | 아래 Start-Process 명령 | 2dcb7b02-b807-4289-abe6-07d159212ff2 / PID 77716 | 0 | UX 보고서의 RESULT: PASS와 산출물 확인 |
| 검색 변경 diff | ROOT | `git diff -- src/MyCapture.Core/Queue/CaptureTextSearch.cs` | f48729bb-a43d-4845-a069-da1e87216389 | 0 | 기존 필드 유지, Tags 플래그/매칭만 추가 |
| 네이티브 혼합-DPI 포인터 | 해당 없음 | 실행하지 않음 | 없음 | N/A | NOT_RUN; 두 실제 디스플레이 모두 96 DPI |
| 새 PNG의 육안 디자인 검토 | 해당 없음 | 실행하지 않음 | 없음 | N/A | 텍스트 Worker에서 레이아웃 보고서와 PNG 디코딩만 검사; 육안 승인 아님 |
| 설치 앱 종료·7개 packaged feature 검사·설치 패키지·push/tag/release | 해당 없음 | 실행하지 않음 | 없음 | N/A | 부모 담당/이번 범위 밖; 설치 앱과 릴리스 게이트를 우회하지 않음 |

제품 계획과 이전 validation 문서는 각각 read_project_files로 독립적으로 읽었다(42줄 / 94줄, read-only UTF-8, OS 실행 세션·종료 코드 해당 없음). 핵심 검토 파일도 한 호출당 한 파일/범위로 읽었다. 이전 차단은 파일의 내용이나 유해성의 증거로 해석하지 않았다.

#### 현재 차단의 정확한 기록

아래 명령의 실행 요청은 세션 생성 전에 차단됐다. 명령을 재시도하거나 다른 셸/커넥터/필터/전체 솔루션 실행으로 재표현하지 않았다.

```powershell
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false); $ErrorActionPreference = 'Stop'; & .\build\doctor.ps1 -Quiet; $env:DOTNET_CLI_UI_LANGUAGE = 'en'; $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'; & dotnet test tests\MyCapture.App.Tests\MyCapture.App.Tests.csproj -c Release --filter 'FullyQualifiedName~LibraryWorkspaceConcurrencyTests' --logger 'trx;LogFileName=organization-before.trx' --results-directory artifacts\validation\workspace-r3; exit $LASTEXITCODE
```

정확한 응답:

> 요청의 보안 상태를 결정하지 못해 이 도구 요청은 OpenAI에 의해 차단되었습니다.

이 결과는 테스트 FAIL, 소스 파일 결함, 파일 없음 또는 `.ps1` ExecutionPolicy 차단의 증거가 아니다. 원인은 확인되지 않았다. 판정은 BLOCKED / NOT_RUN이다.

별개의 준비 오류도 보존한다. 첫 소스 수정 명령은 `Get-FileHash -LiteralPath $path -Algorithm SHA256` (`$path = 'src/MyCapture.Core/Queue/CaptureTextSearch.cs'`)에서 CommandNotFoundException으로 종료했다. 세션 `1e69a1df-6b74-48cd-9dcd-b71729c2586c`, exit 1이며 파일 쓰기 전 실패했다. 이것은 플랫폼 거부가 아니었다. 같은 원본 SHA256 검사를 .NET SHA256 API로 수행한 정상 수정은 세션 `0f27377d-1bad-4202-9d60-0d9763eb2e8a`, exit 0이다. ExecutionPolicy 완화, Bypass, Unblock-File, 차단된 스크립트 본문 재실행은 없었다. Release wrapper와 doctor는 정상 경로로 실행했으므로 대체 검증을 주장하지 않는다.

#### 실제 self-contained publish 명령

```powershell
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false); $ErrorActionPreference = 'Stop'; & .\build\doctor.ps1 -Quiet; $env:DOTNET_CLI_UI_LANGUAGE = 'en'; $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'; & dotnet publish src\MyCapture.App\MyCapture.App.csproj -c Release -p:PublishProfile=win-x64-self-contained -o artifacts\validation\workspace-candidate-r3; exit $LASTEXITCODE
```

#### 실제 WPF 실행 명령

```powershell
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false); $ErrorActionPreference = 'Stop'; $p = Start-Process -FilePath 'C:\GitRepositories\my-capture\artifacts\validation\workspace-candidate-r3\MyCapture.exe' -ArgumentList '--selftest-ux-review' -WorkingDirectory 'C:\GitRepositories\my-capture\artifacts\validation\workspace-candidate-r3' -PassThru; Write-Output ('UX_PROCESS_ID=' + $p.Id); $p.WaitForExit(); Write-Output ('UX_SELFTEST_EXIT=' + $p.ExitCode); exit $p.ExitCode
```

### WPF 실측과 증거 수집

이번 보고서 `UX\ux-review-selftest-report.txt`의 확인 결과: 6개 테마 모두 compact viewport **311 DIP**, 변경하지 않은 **304-DIP** 전체 카드 기준 통과, unreadable text elements 0. Organization 대화상자의 **400x360 DIP** 최소 크기와 500x500 기본 크기에서 저장·취소가 모두 노출된다. 갤러리 휠 이동 48 DIP, 중첩 viewer 없음, offscreen 행 가상화가 확인된다. 보고서 마지막은 `RESULT: PASS (fixture rendering and command checks; manual visual review required)`다.

실제 디스플레이는 DISPLAY1 3440x1440 / DISPLAY2 768x1366, 둘 다 96 DPI다. 96/144/192-DPI PNG는 오프스크린 raster-density 검증이며 네이티브 혼합-DPI 입력 검증이 아니다. 시스템 디스플레이 설정을 바꾸지 않았다.

| 증거 작업 | cwd | 실제 명령 본문 | 세션 | 종료 코드 | 확인 결과 |
|---|---|---|---|---:|---|
| 테스트 로그 저장 | ROOT | `Copy-Item -LiteralPath 'build/logs/test.log' -Destination 'artifacts/validation/workspace-r3-test.log' -ErrorAction Stop` | 2f2ae9ff-5fe3-414e-914f-ca1a63695a4b | 0 | 로그 보존 |
| 빌드 로그 저장 | ROOT | `Copy-Item -LiteralPath 'build/logs/build.log' -Destination 'artifacts/validation/workspace-r3-build.log' -ErrorAction Stop` | 15c0c6ba-893c-4496-b758-73a1533944fd | 0 | 로그 보존 |
| 테스트 로그 확인 | ROOT | T1 (아래 원문) | 282cfe98-a8bc-498e-a7bc-e9d656ec4eaf | 0 | 14개 새 Core 케이스 PASS; 기존 태그 테스트 PASS; symlink 1314 SKIP |
| UX 보고서 확인 | ROOT | U1 (아래 원문) | 638119cc-9248-4785-9d2c-ccd4287b8be1 | 0 | 위 실측값과 RESULT 확인 |
| UX 보고서 저장 | ROOT | `Copy-Item -LiteralPath 'C:\Users\sukwo\AppData\Local\Temp\MyCapture-ux-review-wpw4jwfl.fon\ux-review-selftest-report.txt' -Destination 'artifacts/validation/workspace-r3-ux-report.txt' -ErrorAction Stop` | 982f91c8-ca9d-4c32-bf36-bd256c7314d8 | 0 | 보고서 보존 |
| gallery PNG 디코딩 | ROOT | 아래 PNG 명령, path = UX\gallery-compact-midnight-144dpi.png | 23d49bc9-994b-404b-a3d6-f9324f74962b | 0 | 1470x840 px, DPI 143.9926x143.9926 |
| organization PNG 디코딩 | ROOT | 아래 PNG 명령, path = UX\organization-compact-midnight-144dpi.png | 471ad82f-45d5-4959-b8fa-4df2432ef603 | 0 | 600x540 px, DPI 143.9926x143.9926 |

T1 명령 본문:

```text
rg -n "Tags_AreSearchableTogetherWithTitle|Total tests:|Passed:|Skipped:|1314|FinalSourceSymlink|CaptureTagSearchTests" artifacts/validation/workspace-r3-test.log
```

U1 명령 본문:

```powershell
Select-String -LiteralPath 'C:\Users\sukwo\AppData\Local\Temp\MyCapture-ux-review-wpw4jwfl.fon\ux-review-selftest-report.txt' -Pattern 'RESULT|FAIL|gallery|organization|DPI|DISPLAY|viewport|button' -Encoding utf8
```

PNG 검사 실제 본문은 아래와 같으며, 두 호출에서 `$path`만 표의 각 절대 경로였다. 이미지 픽셀의 육안 품질 승인은 수행하지 않았다.

```powershell
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false); $ErrorActionPreference = 'Stop'; Add-Type -AssemblyName System.Drawing; $path = 'C:\Users\sukwo\AppData\Local\Temp\MyCapture-ux-review-wpw4jwfl.fon\gallery-compact-midnight-144dpi.png'; $image = [System.Drawing.Image]::FromFile($path); try { Write-Output ('PNG=' + $path); Write-Output ('PIXELS=' + $image.Width + 'x' + $image.Height + '; DPI=' + $image.HorizontalResolution + 'x' + $image.VerticalResolution) } finally { $image.Dispose() }
```

### 체크포인트/인계 경계

커밋 대상은 검증된 검색 엔진 수정과 Core 회귀, 이번 검증 문서 및 제품 계획의 최신 상태 주석이다. 추가 저장 경합 테스트 초안과 기존 README.md/CONTRIBUTING.md/AGENTS.md는 제외한다. 실제 최종 커밋 SHA와 커밋/상태 검사 결과는 Worker 최종 결과에 기록한다. 이 문서를 포함한 개발 체크포인트는 배포 승인이 아니다.

다음 단계에는 저장 경합 검증을 위한 정상 권한 확보, 해당 케이스의 실제 결과와 필요한 수정, 최종 전체 Release 재검증이 필요하다. GitHub Actions의 실제 file-symlink 필수 케이스는 유지하며, 로컬 오류 1314를 성공으로 바꾸지 않았다. 그 뒤 부모가 설치 앱 종료와 7개 packaged feature 검사, 설치·업데이트·릴리스 게이트를 담당한다. 네이티브 혼합-DPI 입력과 새 PNG 육안 검토도 미완료다.

소스/사용자 데이터/Worker 상태/키/다른 작업/다른 저장소를 정리하거나 삭제하지 않았다. 새 진단 산출물 CANDIDATE와 UX는 증거로 보존했고, 추후 부모의 명시적 정리 승인 시 검토할 후보일 뿐이다. 작업공간 영구 삭제를 하지 않았으며 정리 시 OS Recycle Bin 이외의 영구 삭제 fallback을 사용하지 않는다. 상주 MyCapture 종료, push, tag, release, 설치, 결제·텔레메트리·클라우드·오디오·라이선스·가짜 서명 변경은 수행하지 않았다.
