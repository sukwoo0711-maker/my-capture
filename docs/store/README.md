# Microsoft Store 배포

MyCapture의 공식 배포 경로는 Microsoft Store 하나입니다. 이 문서는 `package.ps1`이 만든 self-contained 게시본을 MSIX로 묶어 Microsoft Store에 제출하는 절차입니다. Store는 인증을 통과한 패키지를 Microsoft 인증서로 다시 서명합니다. 그래서 Store에서 설치하면 SmartScreen과 "알 수 없는 게시자" 경고가 표시되지 않습니다. GitHub 릴리스는 4.0.2를 마지막으로 게시하지 않으며, 사용자 지원과 개인정보처리방침은 공개 [지원 저장소](https://github.com/sukwoo0711-maker/mycapture-support)에서 관리합니다.

## Store 빌드에서 달라지는 동작

앱은 실행할 때 패키지 ID가 있는지 확인하고, 다음 항목만 Store 방식으로 바꿉니다. 같은 바이너리를 그대로 쓰므로 별도 빌드 구성은 없습니다.

| 항목 | 개발 빌드(비패키지)와 기존 GitHub 설치본 | Store 빌드 |
|---|---|---|
| 업데이트 | 설정의 업데이트 탭에서 GitHub 릴리스를 확인하고 설치 | Microsoft Store가 업데이트. 업데이트 탭에는 안내만 표시 |
| 로그인 시 자동 시작 | HKCU `Run` 키, 기본 켜짐 | 매니페스트 시작 작업(`MyCaptureStartup`), 기본 꺼짐 |
| 설정·라이브러리 | `%APPDATA%\MyCapture` | `%LOCALAPPDATA%\Packages\<패키지 패밀리 이름>\LocalState` |
| OCR 모델 | `%LOCALAPPDATA%\MyCapture\ocr-models` | 같은 패키지 폴더의 `LocalCache\ocr-models` |
| 드래그 내보내기 임시 파일 | `%TEMP%\MyCapture\DragExports` | 같은 패키지 폴더의 `TempState\MyCapture\DragExports` |
| 빠른 저장 | `사진\Captures` | 같음 |

- **업데이트**: Store 빌드에서 GitHub 설치 파일을 받으면 패키지 옆에 비패키지 사본이 하나 더 설치됩니다. 그래서 Store 빌드에서는 업데이트 동작을 막았습니다.
- **자동 시작**: 패키지 앱이 `Run` 키에 쓴 값은 가상화되어 로그인할 때 실행되지 않습니다. Store 정책 10.2.8은 Windows 시작 동작을 바꾸기 전에 사용자 동의를 요구합니다. 그래서 시작 작업은 꺼진 상태로 배포됩니다. 사용자가 MyCapture 설정이나 Windows 설정 > 앱 > 시작 프로그램에서 직접 켭니다. 앱 설정의 체크 상태는 Windows의 실제 상태를 따릅니다.
- **저장 위치**: 패키지 앱이 AppData에 새로 만든 파일은 패키지 전용 위치로 리디렉션됩니다. 이 위치는 탐색기, 사진 앱, 드롭 대상이 원래 경로로 찾을 수 없습니다. 가상화를 끄는 `unvirtualizedResources` 기능은 게임용이어서 Store 승인을 기대하기 어렵습니다. 그래서 Store 빌드는 실제 경로인 패키지 폴더를 사용합니다.
- **제거하면 데이터도 삭제됨**: Store 앱을 제거하면 패키지 폴더의 설정과 라이브러리도 함께 삭제됩니다. 라이브러리를 유지하려면 설정에서 캡처 폴더를 다른 위치로 지정하십시오. `사진\Captures`의 빠른 저장 파일은 남습니다.
- **두 빌드의 관계**: Store 빌드와 개발 빌드·기존 GitHub 설치본은 데이터를 공유하지 않습니다. 설정은 설정 창의 내보내기·가져오기로 옮길 수 있습니다. 두 빌드는 같은 단일 실행 잠금을 쓰므로 동시에 상주하지 않습니다.

## 1. Partner Center 준비 (최초 1회)

1. [Microsoft Store 개발자 등록](https://storedeveloper.microsoft.com)에서 개인 개발자로 가입합니다. 개인 계정은 등록비가 없고, 신분증과 셀카로 본인을 확인합니다.
2. Partner Center에서 새 앱을 만들고 이름을 예약합니다. `MyCapture`를 이미 다른 사람이 쓰고 있으면 다른 이름을 예약합니다. 이때는 패키징할 때 `-DisplayName`에 예약한 이름을 그대로 넘깁니다.
3. **제품 관리 > 제품 ID**(Product identity)에서 다음 세 값을 기록합니다. 대소문자와 공백까지 그대로 써야 합니다.
   - `Package/Identity/Name`
   - `Package/Identity/Publisher` (`CN=`으로 시작)
   - `Package/Properties/PublisherDisplayName`

### 등록된 제품 정보

2026-10-09에 `MyCapture` 이름으로 예약했습니다. 아래 값은 설치된 패키지에서도 보이는 공개 식별 정보입니다.

| 항목 | 값 |
|---|---|
| Store ID | `9PB33MC095VR` |
| Store 주소 | https://apps.microsoft.com/detail/9PB33MC095VR |
| `Package/Identity/Name` | `sukwoo0711.MyCapture` |
| `Package/Identity/Publisher` | `CN=2BAAF359-E7FC-4D15-9E24-F3262AD74474` |
| `Package/Properties/PublisherDisplayName` | `sukwoo0711` |
| 패키지 패밀리 이름 | `sukwoo0711.MyCapture_n98e1tgx2qxb2` |

## 2. 패키지 만들기

릴리스 커밋의 깨끗한 작업 트리에서 실행합니다. `package-store.ps1`은 `package.ps1`이 검증한 게시 폴더(`artifacts\release\<버전>\publish-win-x64`)를 그대로 사용합니다. 따라서 GitHub 릴리스와 Store 패키지의 바이너리가 같습니다.

```powershell
.\build\package.ps1 -Version 4.1.0
.\build\package-store.ps1 -IdentityName 'sukwoo0711.MyCapture' -Publisher 'CN=2BAAF359-E7FC-4D15-9E24-F3262AD74474' -PublisherDisplayName 'sukwoo0711'
```

- **결과물**: `artifacts\store\<버전>\` 아래에 다음 파일이 생깁니다.
  - `<Name>_<버전>.0_x64.msix`
  - `store-package.json`
  - `SHA256SUMS.txt`
  - Store 목록용 300×300 아이콘 `StoreListingIcon-300.png`
- **패키징 도구**: `makeappx`와 `makepri`는 NuGet 패키지 `Microsoft.Windows.SDK.BuildTools`(버전 고정)에서 가져옵니다. Windows SDK를 따로 설치하지 않아도 됩니다.
- **로고**: `build\generate-icons.ps1`이 `app.ico`와 같은 그리기 코드로 로고를 만듭니다.
- **버전**: 패키지 버전은 `<버전>.0`입니다. 네 번째 자리는 Store가 예약한 자리라서 항상 0입니다. 새 제출은 이전 제출보다 버전이 높아야 합니다.
- **서명**: 패키지는 서명하지 않은 채로 올립니다. Store가 인증 후 서명합니다.

## 3. 제출 전 로컬 확인 (선택)

로컬 테스트 ID로 같은 패키지를 만들고, 서명 없이 설치해 봅니다.

```powershell
.\build\package-store.ps1 -LocalTest
```

관리자 권한 PowerShell에서 설치합니다. Windows 11은 서명 없는 패키지를 `-AllowUnsigned`로 설치할 수 있지만, 실행 파일이 든 패키지는 관리자 권한이 필요합니다.

```powershell
Add-AppxPackage -Path .\artifacts\store\4.1.0-localtest\MyCapture.LocalTest_4.1.0.0_x64.msix -AllowUnsigned
```

다음 항목을 확인합니다.

- 시작 메뉴와 작업 표시줄 아이콘
- 설정 > 업데이트 탭의 Store 안내
- 설정에서 로그인 시 자동 시작을 켜면 Windows 설정 > 앱 > 시작 프로그램에 MyCapture가 표시되는지 여부
- 캡처 후 라이브러리에서 탐색기로 드래그하거나 다른 앱에서 열기

확인이 끝나면 다음 명령으로 제거합니다.

```powershell
Get-AppxPackage MyCapture.LocalTest | Remove-AppxPackage
```

관리자 권한을 쓸 수 없으면 Partner Center의 비공개 대상(Private audience) 배포로 확인합니다.

## 4. Partner Center 제출 항목

- **가격 및 가용성**
  - 무료로 설정합니다.
  - 첫 제출은 비공개 대상으로 올려 직접 설치해 보는 것을 권장합니다. 한 번 공개로 게시하면 비공개로 되돌릴 수 없습니다.
- **속성**
  - 범주는 생산성 또는 사진 및 비디오를 고릅니다.
  - 개인정보처리방침 URL에는 지원 저장소의 `https://github.com/sukwoo0711-maker/mycapture-support/blob/main/PRIVACY.md`를 넣습니다. Win32 앱은 개인정보처리방침이 항상 필요합니다(정책 10.5.1). 방침을 고칠 때는 지원 저장소의 `PRIVACY.md`를 수정합니다.
- **연령 등급**: IARC 설문에 답합니다. 사용자 간 소통이나 구매 기능은 없습니다.
- **패키지**: `.msix` 파일을 올립니다.
- **제출 옵션**: 제한 기능 `runFullTrust`의 사용 사유를 적습니다. 문구는 [`listing.md`](listing.md)에 있습니다.
- **Store 목록**
  - 매니페스트가 선언한 한국어(ko-KR)와 영어(en-US) 목록을 모두 작성합니다. 초안은 [`listing.md`](listing.md)에 있습니다.
  - 스크린샷은 1장 이상 필요합니다. 아래 "Store 스크린샷 만들기"로 언어별 10장을 만들어 올립니다.

## Store 스크린샷 만들기

Store 목록용 스크린샷은 실제 MyCapture 창을 렌더링해 만듭니다. 화면에 들어가는 대시보드, 코드, 블로그 글, 고객 문의, 예산표는 모두 합성한 예시 데이터이고, 사용자 라이브러리·설정·화면·클립보드는 읽지 않습니다. Release 빌드의 실행 파일로 언어마다 한 번씩 실행합니다.

```powershell
.\src\MyCapture.App\bin\Release\net10.0-windows10.0.22000.0\MyCapture.exe --selftest-store-shots .\artifacts\store\screenshots\ko ko
.\src\MyCapture.App\bin\Release\net10.0-windows10.0.22000.0\MyCapture.exe --selftest-store-shots .\artifacts\store\screenshots\en en
```

- 결과는 2560×1440 PNG 10장(라이브러리, 주석, 빠른 가리기, 캡처 후 카드, 화면 고정, 텍스트 인식, 영상 편집기, GIF 내보내기, 설정, 밝은 테마 라이브러리)과 `store-screenshots-report.txt`입니다.
- 생성하는 동안 화면 왼쪽 위에 MyCapture 창이 잠깐씩 나타났다 닫힙니다.
- 한국어(ko-KR) 목록에는 `ko`, 영어(en-US) 목록에는 `en` 결과를 올립니다.

## 5. 다음 버전 제출

1. `Directory.Build.props`의 버전을 올리고 릴리스 커밋을 만듭니다.
2. `package.ps1`과 `package-store.ps1`을 차례로 실행합니다.
3. Partner Center에서 새 제출을 만들어 `.msix`를 올립니다.

Store 빌드 사용자는 Store를 통해 새 버전을 받습니다.
