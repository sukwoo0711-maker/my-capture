# 휴지통 Shell IO 예외 경계 보강

2026-10-12. worktree `C:/GitRepositories/my-capture-edit-recovery`, branch `fix/recycle-shell-io-boundary`, base `8225adfa52b3e903bbf240dc9174837661c6b3f9`. 기존 ebdcc 작업 사본 clean 확인 후 새 브랜치로 전환했습니다. 기존 브랜치/파일/산출물 보존.

root 전체 App 실패 TRX `C:/GitRepositories/my-capture-codex/artifacts/next-product-readiness/readiness-app.trx`에서 VideoLibraryService 잠긴 파일 cleanup의 IFileOperation.PerformOperations가 COMException HRESULT 0x80270027을 그대로 전달함을 확인했습니다. 기존 rollback/finalize/startup recovery는 IOException을 처리하고 저널을 보존하므로 예외 계약이 맞지 않았습니다.

WindowsRecycleBin의 공용 STA Shell 경계에서 COMException만 IOException으로 감싸고 InnerException/HRESULT를 보존합니다. 이미 IOException인 실패, TimeoutException 및 다른 예외는 원래 계약을 유지합니다. 실제 휴지통 flags, move/restore, identity 검증, 원본/저널 유지, canary opt-in 조건은 변경하지 않았습니다. permanent-delete fallback 없음. VideoLibraryService 및 기존 테스트 assertion 변경 없음.

공용 영향 읽기 검토: image CapturePersistenceService cleanup/rollback 역시 IOException 기반으로 원본·저널을 유지하므로 정규화가 같은 계약을 복원합니다. RecoverableLibraryService는 recycle/restore 예외를 이미 보존 처리하므로 예외 형식 변경은 결과 처리를 완화하지 않습니다. Restore/Find 경로도 동일 STA 경계이며 제한된 lookup의 TimeoutException은 그대로 전달됩니다.

실제 검증(cwd 위 worktree):

```powershell
dotnet test tests/MyCapture.App.Tests/MyCapture.App.Tests.csproj -c Debug --filter 'FullyQualifiedName~VideoLibraryServiceTests|FullyQualifiedName~WindowsRecycleBinTests' --logger 'trx;LogFileName=video-shell-io.trx' --results-directory C:/GitRepositories/my-capture-edit-recovery/artifacts/recycle-shell-io -nologo -v quiet
```

actual session78704, initial chunk6f32d1, final860b80 exit0. PASS20/FAIL0/SKIP2/total22. 기존 WindowsRecycleBin 실제 round-trip canary 2종은 MYCAPTURE_RECYCLE_CANARY=1 미설정으로 기존 opt-in skip 유지. 전체 VideoLibraryService 검사와 새로운 native-cause/기존 IO·timeout 계약 검사 포함. 다섯 root 실패 시나리오 TRX outcome=Passed 재확인.

```powershell
dotnet test tests/MyCapture.App.Tests/MyCapture.App.Tests.csproj -c Debug --no-build --filter 'FullyQualifiedName~CapturePersistenceTests|FullyQualifiedName~RecoverableLibraryServiceTests' --logger 'trx;LogFileName=shared-shell-io.trx' --results-directory C:/GitRepositories/my-capture-edit-recovery/artifacts/recycle-shell-io -nologo -v quiet
```

actual chunka2e94a exit0. PASS19/FAIL0/SKIP0. 동일 빌드 산출물을 사용한 공유 경계 회귀 검사.

git diff --check: chunke41e06 exit0. LF→CRLF 정보 경고만 있으며 whitespace 오류 없음. 사용자 앱/GUI/입력/프로세스 종료/원격 변경/ExecutionPolicy 변경/파일 삭제 없음. 전체 App suite 및 실제 recycle round-trip 재검사는 root 직렬 슬롯에서 수행합니다.
