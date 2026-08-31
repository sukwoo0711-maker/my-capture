# MyCapture에 기여하기

버그 수정, 접근성 개선, 테스트, 문서와 성능 개선 기여를 환영합니다. 변경은 Windows 캡처 도구의 핵심 원칙인 빠른 반응, 로컬 우선 처리, 예측 가능한 저장 동작을 유지해야 합니다.

## 개발 환경

- Windows 11
- .NET 10 SDK `10.0.400` 이상
- Windows PowerShell 5.1 이상
- Git

저장소를 복제한 뒤 먼저 진단과 전체 테스트를 실행하세요.

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File build\doctor.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File build\build.ps1 -Configuration Release -Test
```

`global.json`이 SDK 선택을 고정합니다. `dotnet --info`에 런타임만 보이고 SDK가 없다면 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)를 설치해야 합니다. 빌드 로그는 `build\logs\`에 생성되며 Git에서 제외됩니다.

## 변경 원칙

- 요청 하나에는 검토 가능한 하나의 목적을 담고, 관련 없는 형식 변경을 섞지 마세요.
- nullable 경고와 분석기 경고를 포함해 빌드 warning을 새로 만들지 마세요. 이 저장소는 경고를 오류로 처리합니다.
- UI 스레드에서 파일 인코딩, 긴 재시도, 영상 처리를 수행하지 말고 취소와 실패 경로를 명시하세요.
- 애니메이션은 짧고 중단 가능해야 하며 Windows의 애니메이션 감소 설정을 존중해야 합니다.
- 키보드 탐색, 포커스 표시, Automation 이름과 고대비 사용성을 함께 확인하세요.
- 캡처와 OCR은 로컬 우선 원칙을 유지하세요. 네트워크 전송이나 텔레메트리를 추가한다면 명시적 동의, 데이터 최소화와 문서화를 설계에 포함해야 합니다.
- 새 NuGet 패키지, 네이티브 바이너리, 글꼴, 아이콘 또는 이미지에는 필요성, 출처, 버전, 라이선스와 배포 의무를 기록하세요. 권리가 불명확하거나 호환되지 않는 코드를 복사하지 마세요.
- 설정 파일과 저장 데이터의 하위 호환성을 보존하고, 원자적 저장 및 경로 안전성 테스트를 추가하세요.

## 테스트

가장 작은 관련 테스트부터 실행한 뒤 제출 전 전체 Release 테스트를 실행하세요.

```powershell
# Core 테스트만
dotnet test tests\MyCapture.Core.Tests\MyCapture.Core.Tests.csproj -c Release

# WPF/App 테스트만
dotnet test tests\MyCapture.App.Tests\MyCapture.App.Tests.csproj -c Release

# 최종 전체 검증
powershell.exe -NoProfile -ExecutionPolicy Bypass -File build\build.ps1 -Configuration Release -Test
```

실제 Windows API, OCR, 녹화 또는 패키징 경로를 바꿨다면 [`README.md`](README.md#자체-진단self-test)의 관련 self-test도 실행하고, 환경과 결과를 변경 설명에 기록하세요. UI를 바꿨다면 일반 DPI와 고배율 DPI, 키보드 조작, 애니메이션 감소 설정에서 확인한 결과나 스크린샷을 첨부하세요.

## 변경 제안과 제출

큰 기능이나 저장 형식 변경은 구현 전에 이슈에서 사용 사례와 범위를 먼저 공유해 주세요. 변경 설명에는 다음 내용을 포함하면 검토가 빨라집니다.

- 해결하려는 사용자 문제와 변경 전/후 동작
- 의도적으로 포함하지 않은 범위
- 실행한 빌드, 테스트와 self-test의 정확한 결과
- UI, 성능, 개인정보, 호환성 또는 새 의존성에 미치는 영향
- 알려진 한계와 후속 작업

보안 취약점은 이 절차 대신 [`SECURITY.md`](SECURITY.md)의 비공개 신고 채널을 사용하세요.
