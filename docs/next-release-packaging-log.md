# 다음 배포 패키징 개발 LOG

2026-10-12. 작성자 `/root/delegation_recovery`, 기준 `c437af1`, 브랜치 `perf/next-release-packaging`.
working directory: `C:\GitRepositories\my-capture\.claude\worktrees\agent-a491402ad82aa2759`.

기존 clean `qa6-library-motion` HEAD `4d75f97`와 common-dir `C:/GitRepositories/my-capture/.git`를 확인했다. HEAD → c437af1 전환에서 삭제되는 파일이 없음을 확인한 뒤 기존 브랜치를 보존하고 기존 worktree를 재사용했다. 새 worktree·원격 변경·사용자 앱 종료·파일 삭제·ExecutionPolicy 변경은 하지 않았다. 공식 OpenAI native 경로는 총괄이 이 세션에서 확인한 결과를 재사용한다. WebGPT transport 실패 후 승인된 native 작성 범위이며 추가 위임은 금지됐다.

## 구현 경계

- `packaged-validation.ps1`의 단일 8종 정의를 runner와 workflow가 재사용한다. runner는 각 process exit/result/report hash를 `result.json`에 보존하며 workflow는 8종 전부를 다시 검사한다. 실패/timeout/missing report/exit 미관측을 통과로 집계하지 않는다.
- Store 입력은 인접한 schema 3 release manifest의 정확한 현재 clean source SHA·버전·x64/self-contained 표시·전체 파일 크기와 SHA256를 대조한다. 중복/안전하지 않은 경로, 추가 파일, reparse point, 2 GiB 초과를 거부하며 패키징 종료 전에 provenance를 다시 검사한다. Store summary에 source SHA와 manifest hash를 남긴다.
- `package.ps1 -StoreOnly`는 동일 publish·법적 고지·runtime·inventory 검사 후 8종 self-test를 실행하고 provenance를 생성한 뒤 ZIP/IExpress 전에 반환한다. 출력은 `artifacts/store-publish/<version>`으로 분리한다. Store 전용 manifest의 self-test JSON 및 보고서 hash도 재검사한다. 기본 GitHub release 경로의 installer/ZIP/6 assets 계약은 유지한다.
- 절약 근거는 소스의 명시적인 조기 반환이다. Store-only 분기에서 `New-InventoryZip`, ZIP 재읽기 검증, payload ZIP 복사, installer bootstrap 복사, IExpress 생성이 실행되지 않는다. 실제 publish wall time 절약량은 아직 측정하지 않았다.
- 기존 LocalTest/GitHub packaged-validation installer harness의 삭제/Bypass 충돌은 이번 범위에서 유지됐다. 현재 Microsoft Store runbook의 필수 gate는 package + 8종 self-test + package-store이다. GitHub 전용 installer gate를 Store runbook에 추가하지 않았다.

## 실제 검증

아래 명령은 위 working directory에서 실행했다. 실패 fixture까지 모두 보존한다.

| 상태 | 실제 명령 | exit code | 결과·근거 / 미실행 사유 |
| --- | --- | --- | --- |
| FAIL | `powershell.exe -NoProfile -NonInteractive -File build/tests/packaged-validation-tests.ps1` (최초 긴 경로 fixture) | 1 | Windows PowerShell Set-Content가 긴 advanced report 경로를 열지 못했다. fixture 루트를 짧은 고유 경로로 수정했다. 실패 fixture `artifacts/validation/packaging-contract-49d516931f76486e9fada5406d2796bf` 보존. |
| PASS | `powershell.exe -NoProfile -NonInteractive -File build/tests/packaged-validation-tests.ps1` | 0 | 최초 수정본 13 checks, 최종본 15 checks. 최종 `artifacts/pct-be7a1fe6` 보존. 합성 fixture로 gate 자체를 실행했으며 실제 앱 self-test 통과 결과가 아니다. |
| PASS | `git diff --check` | 0 | whitespace 오류 없음. CRLF 정규화 알림만 있음. |
| PASS | `[Management.Automation.Language.Parser]::ParseFile(...)` (수정/신규 PowerShell 5개) | 0 | parser 오류 0개. |
| FAIL | 중간 Python 편집 배치 | Python 1 / wrapper 0 | 마지막 README 한국어 marker 검색에서 ValueError. 그 전에 저장된 코드 수정은 보존됐고 README는 apply_patch로 완료했다. 배치의 wrapper exit 0을 편집 성공으로 판정하지 않았다. |
| NOT RUN | `powershell.exe -NoProfile -NonInteractive -File build/package.ps1 -Version 4.1.0 -StoreOnly` | N/A | 총괄이 현재 사용자 앱 실행 승인 대기와 .NET 슬롯 점유를 근거로 실제 publish/앱 실행 보류를 지시했다. 슬롯 배정 후 총괄 검증 예정. |
| NOT RUN | 실제 MSIX 생성 / hosted workflow | N/A | 현재 출시 pipeline과 분리된 다음 배포 브랜치이며 총괄의 후속 검증 범위. |

최종 fixture는 8종 정의/정상 evidence/누락 hotfix-ux/비0 exit/미관측 exit/중복 RESULT/변조 보고서/변조 JSON/중복 inventory/동일 버전 stale SHA/dirty provenance/잘못된 architecture/version/변조 payload/추가 파일을 확인한다. 테스트는 별도 로컬 합성 Git repository만 만들고 모든 자료를 보존한다.

구현·fixture 검증은 완료됐으며 실제 publish/8종 앱 실행·독립 검토·통합·Store 제출은 아직 완료되지 않았다.

## 독립 검토 후 계약 테스트 수정

검토자 `/root/library_motion`이 기존 `ReleaseVersionContractTests`에서 8종 literal을 runner 본문에서 찾는 회귀를 확인했다. 단일 정의 파일에서 동일한 8개 switch를 검사하도록 옮기고 runner의 dot-source/Get-PackagedSelfTests 연결 assertion을 추가했다. 기존 exit 0·handle 보존·exit 미관측 거부·정확한 단일 RESULT: PASS·전체 pass count assertion은 유지했다. workflow의 잔여 `seven` 두 레이블도 `eight`로 수정했다. 다른 correctness finding은 없었으며 실제 publish/앱 실행은 총괄 보류 지시를 유지했다.

동일 working directory에서 `dotnet test tests/MyCapture.Core.Tests/MyCapture.Core.Tests.csproj -c Release --filter FullyQualifiedName~ReleaseVersionContractTests --logger "trx;LogFileName=packaging-contract.trx" --results-directory artifacts/validation/packaging-core-contract` 실행: **PASS, exit 0, 9/9 passed, failed 0, skipped 0**. TRX counters를 직접 대조했다. WPF build·전체 테스트·StoreOnly 실행은 하지 않았다. `git diff --check`도 exit 0이다.
