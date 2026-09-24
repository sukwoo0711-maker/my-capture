# MyCapture library workspace — execution and validation record

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