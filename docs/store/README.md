# Microsoft Store 배포

지원 하한은 Windows 10 버전 22H2(빌드 19045), x64입니다. ARM64에서 x64 에뮬레이션으로 실행하려면 Windows 11(빌드 22000) 이상이 필요합니다. MSIX `MinVersion`은 `10.0.19045.0`이며, `MaxVersionTested`는 실제 검증한 상위 버전으로 유지합니다. Windows 10에서는 선택적 `uap11` capability를 무시하므로 WGC 녹화 중 노란 캡처 테두리가 표시됩니다.

MyCapture의 공식 배포 경로는 Microsoft Store 하나입니다. 이 문서는 `package.ps1`이 만든 self-contained 게시본을 MSIX로 묶어 Microsoft Store에 제출하는 절차입니다. Store는 인증을 통과한 패키지를 Microsoft 인증서로 다시 서명합니다. 그래서 Store에서 설치하면 SmartScreen과 "알 수 없는 게시자" 경고가 표시되지 않습니다. GitHub 릴리스에는 4.1.2부터 포터블 zip을 함께 게시하지만, 앱 안 업데이트는 Store 설치본만 받습니다. 사용자 지원과 개인정보처리방침은 공개 [지원 저장소](https://github.com/sukwoo0711-maker/mycapture-support)에서 관리합니다.

## Store 빌드에서 달라지는 동작

앱은 실행할 때 패키지 ID가 있는지 확인하고, 다음 항목만 Store 방식으로 바꿉니다. 같은 바이너리를 그대로 쓰므로 별도 빌드 구성은 없습니다.

| 항목 | 개발 빌드(비패키지)와 기존 GitHub 설치본 | Store 빌드 |
|---|---|---|
| 업데이트 | 앱 안의 업데이트 확인·설치 없음. 정보 탭의 "Microsoft Store에서 열기" 버튼으로 Store 설치 페이지 열기 | Store API로 업데이트 확인·설치 요청. 앱에서 시작한 요청의 다운로드·설치 상태를 정보 탭에 표시 |
| 로그인 시 자동 시작 | HKCU `Run` 키, 기본 켜짐 | 매니페스트 시작 작업(`MyCaptureStartup`). 설치 직후 꺼져 있고, 첫 실행 화면의 선택(기본 선택됨)을 적용 |
| 설정·라이브러리 | `%APPDATA%\MyCapture` | `%LOCALAPPDATA%\Packages\<패키지 패밀리 이름>\LocalState` |
| OCR 모델 | `%LOCALAPPDATA%\MyCapture\ocr-models` | 같은 패키지 폴더의 `LocalCache\ocr-models` |
| 번역 모델(번역을 켠 경우만) | `%LOCALAPPDATA%\MyCapture\translation-models` | 같은 패키지 폴더의 `LocalCache\translation-models` |
| 드래그 내보내기 임시 파일 | `%TEMP%\MyCapture\DragExports` | 같은 패키지 폴더의 `TempState\MyCapture\DragExports` |
| 빠른 저장 | `사진\Captures` | 같음 |
| 평가 요청 | 없음 | 성공한 캡처 10회와 최초 실행 후 3일이 지나면 앱 안 평가 안내를 한 번만 표시하고, 사용자가 평가를 고를 때만 Microsoft Store 평가 창을 엶. 설정 > 정보 탭에 "Microsoft Store에서 평가" 버튼 |

- **업데이트**: Store 설치본은 `Windows.Services.Store` API로 업데이트를 확인하고 사용자 요청에 따라 설치합니다. Microsoft Store/Windows가 다운로드·패키지 교체와 설치 동의를 처리합니다. 앱에서 시작한 요청은 정보 탭에서 다운로드·설치 상태를 확인할 수 있습니다. 설치 중 MyCapture가 종료될 수 있으며 다음 실행에서 실제 버전 상승을 확인한 뒤 완료를 안내합니다. Microsoft Store에서 직접 시작한 업데이트의 진행 상태는 Store 화면에서 확인합니다. 같은 패키지 패밀리의 업데이트는 LocalState를 유지하므로 기존 앱을 먼저 제거하지 않습니다. 비패키지 실행본의 버튼은 Store 설치 페이지(`ms-windows-store://pdp/?productid=9PB33MC095VR`)를 열고, 실패하면 웹 주소 https://apps.microsoft.com/detail/9PB33MC095VR 를 엽니다. 두 경로 모두 실패하면 설정 창에 주소를 표시합니다. GitHub 업데이트 코드는 화면에 연결하지 않습니다.
- **기존 데이터 가져오기**: Store 설치본의 설정 > 정보에서 이전 실행본의 사진·영상·주석·폴더를 현재 라이브러리로 복사합니다. 기본 위치는 `%APPDATA%\MyCapture`이며, `MYCAPTURE_DATA_ROOT`로 따로 지정했던 테스트 데이터 폴더도 직접 선택할 수 있습니다. 원본 폴더는 읽기만 하고, 현재 라이브러리의 기존 항목은 덮어쓰지 않습니다. 시작 전에 여유 공간(가져올 파일 크기와 여유분 64MB)을 확인하고, 파일은 라이브러리 폴더에 한 번만 복사합니다. 파일이 없거나 손상된 항목은 원본 폴더에 그대로 두고 건너뛴 개수를 알려 줍니다. 한 번 가져온 항목은 이후 어느 쪽에서 편집·이동·삭제하더라도 다시 가져오지 않습니다. 앱이 가져오기 도중 종료되면 다음 실행에서 마무리하고, 마무리할 수 없으면 다시 시도하거나 가져오기를 취소할 수 있습니다. 취소하면 현재 라이브러리와 원본 폴더는 그대로 유지됩니다. **설정도 불러오기**를 선택하면 설정을 초안으로 불러오며, **적용**을 눌러 저장합니다. 현재 저장 위치·라이브러리 보존 정책·계정 인증 정보와 업로드 대상 이슈 주소·자동 시작 선택은 유지합니다.
- **평가 요청**: 요청 시점을 정하려고 성공한 캡처 수(최대 10), 최초 실행 시각, 직전 캡처 실패 여부, 요청 시각을 `settings.json`의 `storeRating`에만 저장하며 어디에도 전송하지 않습니다. 안내는 오버레이·편집기·녹화·빠른 작업 카드가 모두 닫힌 뒤 사용자가 앱으로 돌아왔을 때 한 번만 표시하고, 결과와 관계없이 다시 묻지 않습니다. Microsoft Store 평가 창은 사용자가 안내에서 평가를 고를 때만 열며, 평가 창과 평가 내용은 Microsoft Store가 처리합니다.
- **PrintScreen 안내**: 단축키에 PrintScreen을 지정하면 `HKCU\Control Panel\Keyboard`의 `PrintScreenKeyForSnippingEnabled` 값을 읽기만 해서, Windows가 PrtSc로 캡처 도구를 여는 상태인지 판단합니다. 값이 없으면 빌드 22621 이상에서 켜짐으로 봅니다. 값을 바꾸지 않고 Windows 설정 바로 가기만 제공합니다.
- **자동 시작**: 패키지 앱이 `Run` 키에 쓴 값은 가상화되어 로그인할 때 실행되지 않습니다. Store 정책 10.2.8은 Windows 시작 동작을 바꾸기 전에 사용자 동의를 요구합니다. 그래서 시작 작업은 꺼진 상태로 배포되고, 첫 실행 화면의 'Windows에 로그인하면 MyCapture 실행' 선택(기본 선택됨, 사용자가 해제 가능)을 받은 뒤에만 켭니다. 이후에는 MyCapture 설정이나 Windows 설정 > 앱 > 시작 프로그램에서 바꿉니다. 앱 설정의 체크 상태는 Windows의 실제 상태를 따릅니다.
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
| `Package/Properties/PublisherDisplayName` | `sukwoo0711` (게시자 표시 이름을 바꾸면 그 이름) |
| 패키지 패밀리 이름 | `sukwoo0711.MyCapture_n98e1tgx2qxb2` |
| 추가로 예약한 이름 | `MyCapture - 캡처·가리기·핀`, `MyCapture - Screenshot, Redact & Pin` (2026-10-20) |

- **게시자 표시 이름**: Store의 게시자 표시는 계정의 게시자 표시 이름이고, 패키지의 `PublisherDisplayName`은 그 이름과 같아야 업로드됩니다. 계정 설정 > Legal info > Contact info의 Publisher name에서 `MyCapture`로 바꿀 수 있지만, 바꾸면 계정이 추가 검토를 받고 이후 패키지를 새 이름으로 다시 만들어 제출해야 합니다(`-PublisherDisplayName 'MyCapture'`). 인증 중인 제출의 패키지는 이전 이름으로 빌드되었으므로 그 제출이 게시된 뒤에 바꿉니다. 패키지 ID(`Package/Identity/Name`, 패키지 패밀리 이름)는 바뀌지 않습니다.
- **추가로 예약한 이름**: Store 정책 10.1.1은 제품 이름에 설명 문구와 키워드를 넣지 못하게 하므로 목록 제품 이름은 `MyCapture`로 둡니다. 예약은 3개월 안에 제출에서 쓰지 않으면 풀릴 수 있습니다.

## 2. 패키지 만들기

병합된 main의 깨끗한 작업 트리에서 실행합니다. `package-store.ps1`은 `package.ps1`이 만든 게시 폴더(`artifacts\release\<버전>\publish-win-x64`)를 그대로 사용합니다. 패키지 self-test 8종을 단독 실행하여 통과한 바이너리를 Store 패키지로 묶습니다.

```powershell
.\build\package.ps1 -Version 4.1.1
.\build\run-packaged-self-tests.ps1 -PublishRoot .\artifacts\release\4.1.1\publish-win-x64
.\build\package-store.ps1 -Version 4.1.1 -IdentityName 'sukwoo0711.MyCapture' -Publisher 'CN=2BAAF359-E7FC-4D15-9E24-F3262AD74474' -PublisherDisplayName 'sukwoo0711'
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

### Store 전용 게시 경로

GitHub ZIP/설치 프로그램이 필요 없는 다음 Store 배포에는 `package.ps1 -StoreOnly`를 사용할 수 있습니다. 같은 self-contained 게시·법적 고지·파일 inventory 검사를 수행하고 필수 self-test 8종을 실행한 뒤 ZIP 압축과 IExpress 생성을 건너뜁니다. 실행 중인 MyCapture가 있으면 검사 gate가 중단됩니다. 기존 출력은 보존하므로 새 버전의 깨끗한 소스에서 실행합니다.

```powershell
.\build\package.ps1 -Version 4.1.1 -StoreOnly
.\build\package-store.ps1 -Version 4.1.1 -PublishDir .\artifacts\store-publish\4.1.1\publish-win-x64 -IdentityName 'sukwoo0711.MyCapture' -Publisher 'CN=2BAAF359-E7FC-4D15-9E24-F3262AD74474' -PublisherDisplayName 'sukwoo0711'
```

`package-store.ps1`은 두 게시 경로 모두에서 인접한 `release-manifest.json`의 clean source SHA·버전·x64 런타임·전체 파일 크기/해시를 확인합니다. Store 전용 경로는 저장된 8종 결과 JSON과 보고서 해시도 다시 확인합니다. 동일 버전이라도 소스나 게시 파일이 달라지면 패키징을 중단합니다. 기존 GitHub release 경로와 필수 installer 검사는 유지됩니다.

## 3. 제출 전 로컬 확인 (선택)

로컬 테스트 ID로 같은 패키지를 만들고, 서명 없이 설치해 봅니다.

```powershell
.\build\package-store.ps1 -LocalTest
```

관리자 권한 PowerShell에서 설치합니다. Windows 11은 서명 없는 패키지를 `-AllowUnsigned`로 설치할 수 있지만, 실행 파일이 든 패키지는 관리자 권한이 필요합니다.

Windows 10에서는 `-AllowUnsigned` 설치 경로를 사용하지 않습니다. Microsoft Store 서명본 또는 리드가 별도로 준비한 신뢰된 테스트 인증서 서명본으로 설치·업데이트를 검증합니다. `-LocalTest`는 Windows 11의 로컬 검증 경로로 유지합니다.

```powershell
Add-AppxPackage -Path .\artifacts\store\4.1.1-localtest\MyCapture.LocalTest_4.1.1.0_x64.msix -AllowUnsigned
```

다음 항목을 확인합니다.

- 시작 메뉴와 작업 표시줄 아이콘
- 설정 > 정보 탭의 "Microsoft Store에서 업데이트 확인" 버튼으로 Store 업데이트 확인·설치 요청과 진행 상태가 표시되는지 여부
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
  - 개인정보처리방침 URL에는 `https://mimiloaf.com/mycapture/privacy/`를 넣습니다(2026-10-24 제출 17부터). Win32 앱은 개인정보처리방침이 항상 필요합니다(정책 10.5.1). 방침을 고칠 때는 지원 저장소의 `PRIVACY.md`를 수정하고, mimiloaf-site에서 `node tools/sync-privacy.mjs`로 사이트 페이지를 다시 만들어 배포합니다.
- **연령 등급**: IARC 설문에 답합니다. 사용자 간 소통이나 구매 기능은 없습니다.
- **패키지**: `.msix` 파일을 올립니다.
- **제출 옵션**: 제한 기능 `runFullTrust`의 사용 사유를 적습니다. 문구는 [`listing.md`](listing.md)에 있습니다.
- **Store 목록**
  - 매니페스트가 선언한 한국어(ko-KR)와 영어(en-US) 목록을 모두 작성합니다. 초안은 [`listing.md`](listing.md)에 있습니다.
  - 스크린샷은 1장 이상 필요합니다. 4.3.1까지는 아래 "Store 스크린샷 만들기"의 10장을, 4.4.0부터는 "4.4.0부터: 창 레이어 합성"의 9장을 언어별로 올립니다.

## Store 스크린샷 만들기

Store 목록용 스크린샷은 실제 MyCapture 창을 렌더링해 만듭니다. 화면에 들어가는 대시보드, 코드, 블로그 글, 고객 문의, 예산표는 모두 합성한 예시 데이터이고, 사용자 라이브러리·설정·화면·클립보드는 읽지 않습니다. Release 빌드의 실행 파일로 언어마다 한 번씩 실행합니다.

```powershell
.\artifacts\release\4.1.1\publish-win-x64\MyCapture.exe --selftest-store-shots .\artifacts\store\screenshots\4.1.1\ko ko
.\artifacts\release\4.1.1\publish-win-x64\MyCapture.exe --selftest-store-shots .\artifacts\store\screenshots\4.1.1\en en
```

- 결과는 2560×1440 PNG 10장(라이브러리, 주석, 빠른 가리기, 캡처 후 카드, 핀, 텍스트 인식, 영상 편집기, GIF 내보내기, 설정, 밝은 테마 라이브러리)과 `store-screenshots-report.txt`입니다.
- 생성하는 동안 화면 왼쪽 위에 MyCapture 창이 잠깐씩 나타났다 닫힙니다.
- 한국어(ko-KR) 목록에는 `ko`, 영어(en-US) 목록에는 `en` 결과를 올립니다.

### 4.4.0부터: 창 레이어 합성

4.4.0 목록은 9장 체계로 바꿉니다. 앱이 렌더한 투명 창 레이어와 `manifest.json`을 [`build/store-shots/compose.py`](../../build/store-shots/compose.py)가 그래파이트 바탕(#16191F→#101318)에 제목 한 줄과 함께 합성합니다. 장면 순서, 두 언어 제목, 자르기·배치, 장면마다 필요한 개발자 소재는 [`build/store-shots/scenes.json`](../../build/store-shots/scenes.json)에 있고, 사용법과 4.4.0 렌더 요구 사항은 [`build/store-shots/README.md`](../../build/store-shots/README.md)에 있습니다.

```powershell
python -X utf8 -I .\build\store-shots\compose.py --layers <레이어 폴더> --lang ko --out .\artifacts\store\screenshots\4.4.0\ko
python -X utf8 -I .\build\store-shots\compose.py --layers <레이어 폴더> --lang en --out .\artifacts\store\screenshots\4.4.0\en
```

- 결과는 `01.png`–`09.png`, 확인용 `contact-sheet.png`와 `search-card.png`(Store 검색 결과 카드 크기 319×179로 줄인 1번과 경쟁 앱 카드 비교)입니다.
- 1번은 개인정보 가리기, 2번은 핀, 9번은 'Windows 캡처 도구에서 옮겨 오면' 비교 카드입니다. Microsoft 제품은 정식 명칭(Windows 캡처 도구, Windows Snipping Tool)으로만 쓰고 우열 표현 없이 그대로인 것과 더해지는 것만 나눕니다. 9번의 비교 항목은 Microsoft의 Snipping Tool 기능 안내로 확인한 것만 쓰며, 제출 직전에 다시 확인합니다. 가리기는 Snipping Tool에도 있으므로 차이는 카드번호·토큰 자동 탐지로 씁니다.
- 레이어를 내보내는 자체 검사 옵션은 4.4.0 장면 소재를 바꿀 때 `StoreScreenshotSelfTest`에 더합니다.

## 5. 다음 버전 제출

1. `Directory.Build.props`의 버전을 올리고 릴리스 커밋을 만듭니다.
2. `package.ps1`과 `package-store.ps1`을 차례로 실행합니다.
3. Partner Center에서 새 제출을 만들어 `.msix`를 올립니다.

Store 빌드 사용자는 Store를 통해 새 버전을 받습니다.
