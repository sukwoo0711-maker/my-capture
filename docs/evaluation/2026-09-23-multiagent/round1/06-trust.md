# 라운드 1 — 개인정보·보안·공급망 신뢰

대상: Release 3.0.0, 커밋 `117b0ef` (`main`).
범위: 로컬 처리, 네트워크 표면, 저장·보존, 빠른 가리기 결과, 클립보드, 로그, GitHub 단축키, 업데이트 무결성, Authenticode, 의존성 고지, 단일 인스턴스와 권한.
근거: 제품 코드, `README.md`, `SECURITY.md`, `THIRD-PARTY-NOTICES.md`, `build/package.ps1`. `docs/market/`는 보관 스냅샷이라 점수로 쓰지 않았다. 경쟁 제품의 현재 개인정보 정책·서명 상태는 이번 세션에서 공식 페이지를 열지 않아 단정하지 않는다.

## 1. 점수

**합산 3 / 5.**

| 부분 | 점수 | 이 축에서의 의미 |
|---|---:|---|
| 로컬 프라이버시 | 4 | 계정·텔레메트리·캡처 자동 업로드는 없고, OCR과 가리기 탐지는 기기 안에서 끝난다. 화면 텍스트의 로컬 평문 보존과, 사용자가 누를 때만 열리는 GitHub 전송이 5를 막는다. |
| 배포 신뢰 (서명) | 2 | 업데이터는 HTTPS와 SHA-256을 검사한다. 배포 파일과 체크섬 모두 Authenticode(또는 동등한 게시자 서명)가 없어, 체크섬은 같은 릴리스 채널의 무결성 확인에 머문다. |

합산 3은 두 부분의 평균이 아니다. 로컬 캡처 도구로서의 핵심은 성립하고, 설치·업데이트 경로의 게시자 증명이 체감 공백이다. 이후 라운드에서 로컬 프라이버시 4와 배포 신뢰 2를 각각 다시 감점하지 않는다. 이 문서의 합산 3만 신뢰 축 점수로 쓴다.

점수 기준: 5는 이 제품 목표(로컬 캡처 → 설명 → GIF/MP4) 안에서 신뢰 상위권, 3은 핵심은 되나 체감 공백, 1은 위험하거나 사실상 부재.

## 2. 확인된 네트워크 표면

`src/`의 `telemetry`, `analytics`, `ApplicationInsights`, `Sentry`, `HttpClient`, `https://`를 검색했다. 제품 분석 텔레메트리 호출은 없다. 캡처가 끝날 때마다 이미지를 올리는 자동 업로드도 없다. 아래는 실제로 존재하는 출구다.

| 기능 | 기본값 | 전송 데이터 | 경로 |
|---|---|---|---|
| 제품 분석·크래시 텔레메트리 | 없음 (코드 검색으로 부재) | 없음 | 없음. 로거는 `AddDebug()`만 등록 (`src/MyCapture.App/App.xaml.cs`) |
| 캡처 자동 업로드 | 꺼짐 | 없음 | 캡처 완료는 편집기·로컬 큐로만 간다 (`OnCaptureSelectionCompletedAsync`) |
| PP-OCR 가중치 받기 | 켜짐. 프로세스가 뜨면 `StartOcrModelDownload()` → `OcrModelStore.EnsureAsync`. 파일이 이미 있으면 요청하지 않음 | 모델 바이트를 내려받기만 함. 캡처 픽셀·OCR 문자열은 올리지 않음 | `https://www.modelscope.cn/...` 의 인식 모델·사전(필수), 검출 모델(실패해도 계속). SHA-256 핀 (`src/MyCapture.Ocr/OcrModelCatalog.cs`) |
| Real-ESRGAN x4 | 꺼짐. `OcrQuality.Enhanced`의 첫 인식에서만 `EnsureSuperResolutionAsync` | 모델 ZIP을 내려받기만 함 | `https://qaihub-public-assets.s3.us-west-2.amazonaws.com/...` , SHA-256 핀 |
| GitHub 첨부 URL (브라우저) | 단축키는 기본 등록. 토큰은 빈 문자열이라 이 경로가 기본. 캡처마다 실행되지는 않음 | 클립보드 이미지. 앱의 `HttpClient`는 이미지 바이트를 보내지 않음. 브라우저 UI 자동화로 댓글 초안에 붙여 넣고, 댓글은 제출하지 않음 (`GitHubCommentUploadAutomation`) | 설정의 이슈 URL. 비어 있으면 `https://github.com/sukwoo0711-maker/my-capture/issues/new`. 코드 기본 단축키는 F9 |
| GitHub 첨부 URL (토큰) | 꺼짐. `GitHubSettings.Token`이 있을 때만 | PNG 바이트. `Authorization: Bearer`는 정책 요청에만 붙음 | `{ApiBase}/upload/policies` 후, 응답의 `upload_url`. github.com이면 `https://api.github.com`, 그 외 호스트는 `https://{host}/api/v3` |
| 앱 내 업데이트 | 꺼짐. 설정 창 동작에서만 `UpdateSession.DownloadAsync` | 릴리스 JSON, `SHA256SUMS.txt`, 설치 EXE. 캡처·설정 비밀은 포함되지 않음 | `https://api.github.com/repos/sukwoo0711-maker/my-capture/releases/latest`, 이어서 `github.com/.../releases/download/...`. 리다이렉트는 `release-assets.githubusercontent.com`, `objects.githubusercontent.com`, 같은 저장소 download 경로만 (`GitHubUrlValidator`) |
| Windows OCR | 기본 품질 Fast에서 사용 | 네트워크 호출 아님 | `Windows.Media.Ocr` |
| 소스 빌드용 SDK 부트스트랩 | 출하 실행 파일과 무관 | SDK 설치 스크립트 | `build/bootstrap-sdk.ps1`. 설치된 앱의 런타임 표면이 아님 |

README 개인정보 절은 “캡처 자동 업로드나 제품 분석 텔레메트리가 없다”고 적는다. 그 문장은 코드와 맞다. 같은 절은 시작 시 모델 다운로드, 사용자 단축키의 GitHub 전송, 설정에서 누르는 업데이트를 네트워크 표면으로 적지 않는다. “실행 중 인터넷 연결도 요구하지 않는다”(`README.md` 설치 절)는 캡처 기능에는 맞지만, 첫 실행이 모델 호스트로 HTTPS를 시도하는 사실과 나란히 읽어야 한다. 다운로드 실패는 트레이를 죽이지 않고 Windows OCR로 남는다.

## 3. README·SECURITY.md와 코드

맞는 서술:

- 화면 픽셀, 캡처 기록, 설정, OCR 텍스트는 로컬 파일이다. 데이터 루트는 `%APPDATA%\MyCapture` (`AppPaths.CreateDefault`). 인덱스·설정은 그 루트, 기본 캡처 디렉터리는 `%APPDATA%\MyCapture\captures`, 빠른 저장은 사진 폴더의 `Captures`, OCR 모델은 `%LOCALAPPDATA%\MyCapture\ocr-models`.
- 라이브러리 이미지는 기본 168시간(7일) 뒤 삭제되고, 고정·편집 임대 중인 이미지는 빠진다. 동영상에는 그 만료가 없다. 빠른 저장처럼 큐 밖으로 내보낸 파일은 만료가 지우지 않는다 (`CaptureRetention`, `CaptureQueue.ExpireHistory`, README 저장 위치 절).
- 개수 기본 300, 용량 기본 2GiB. 이 한도는 이미지와 동영상을 함께 보고, 고정 항목은 용량 퇴거에서도 빠진다.
- Authenticode가 없고 SHA-256은 게시자 신원이 아니라는 문장은 `SECURITY.md`, README 영어 요약, `build/package.ps1`의 `Unsigned = $true`와 같다.
- FFmpeg를 배포물에 넣지 않는다는 `THIRD-PARTY-NOTICES.md`는 프로젝트 참조와 `Directory.Packages.props`와 같다. 녹화 인코더는 Media Foundation이다.

어긋나거나 더 좁게 읽어야 하는 서술:

- README 단축키 표는 GitHub 첨부를 **F4**로 적는다. 코드 기본값은 **F9**이고, 스키마 4 미만에서 수정자 없는 F4만 F9로 옮긴다 (`AppSettings.HotkeySettings.UploadGitHubImage`, `SettingsStore.Sanitize`). 사용자가 수정자를 넣어 고른 F4는 유지된다. 2.2.0 이후 기본 흐름의 이름은 F9다.
- “빠른 가리기의 탐지 결과에는 인식 문자열을 복제하지 않고 종류·좌표만 유지”에서, 평문 비보존은 코드와 맞다. 종류는 `PrivacyDetector` 안의 `PrivacyMatch`까지만 있고, `PrivacyRedactionService.FindAsync`는 패딩된 `RectD`만 반환한다. 편집기에 남는 것은 불투명 `RectangleAnnotation`이다. 종류 필드는 디스크에 남지 않는다.
- “로그의 보관 정책도 함께 확인”은 화면 텍스트가 로그 파일에 쌓인다는 뜻으로 읽히기 쉽다. 프로덕션 로깅은 디버그 출력뿐이고, `%APPDATA%\MyCapture\logs`는 디렉터리만 만든다. 화면 문자열이 남는 파일은 로그가 아니라 `index.json`과 캡처별 `meta.json`이다.
- `OcrSettings.CacheResults` 기본값은 true다. 갤러리에서 수동 인식은 이 값을 본다 (`GalleryWindow.RecognizeText`). 시작·저장 뒤 자동 색인(`OcrIndexingService`)은 이 값을 보지 않고 인식 문자열을 레코드에 쓴다.

## 4. 저장 위치와 보존

| 자료 | 위치 | 보존 |
|---|---|---|
| 원본 픽셀 | `{captures}/{yyyy-MM}/{id}/original.png` | 이미지 레코드와 함께. 가리기 커밋 뒤에도 원본 파일은 다시 그리지 않음 (`CapturePersistenceService`) |
| 가림막이 합쳐진 이미지 | 같은 디렉터리 `rendered.png` | 커밋 때 주석을 평탄화한 결과 |
| 주석 | `layers.json` | 가리기 사각형의 좌표·색. 탐지된 문자열 필드 없음 |
| OCR 평문 | `%APPDATA%\MyCapture\index.json`의 `OcrText`, 같은 내용의 `meta.json`, `SourceWindowTitle` | 자동 색인이 성공하면 즉시. 내용 세대가 바뀌면 비운 뒤 다시 색인. 이미지 만료·용량 퇴거·사용자 삭제가 디렉터리를 지울 때 함께 사라짐. 고정 이미지는 7일 만료에서 제외 |
| 동영상 | `source.mp4`, `rendered.mp4` | 7일 만료 없음. 개수·용량 한도와 수동 삭제만 |
| 썸네일 | `thumb.jpg` | 캡처 디렉터리와 수명 동일. 축소 픽셀 |
| 빠른 저장 PNG | `%USERPROFILE%\Pictures\Captures` (재지정 가능) | 큐 만료 밖 |
| 갤러리 드래그 복사본 | `%TEMP%\MyCapture\DragExports` | 기본 2일 (`GalleryDragExportService.DefaultRetention`) |
| 설정 | `settings.json` | 앱이 지우는 주기 없음 |
| GitHub PAT | 같은 파일, `dpapi:v1:` + CurrentUser DPAPI | 내보내기에서는 빈 문자열. 2.4.0 평문은 다음 로드 때 다시 암호화. 메모리 안에서는 평문 (`DpapiSecretConverter`) |
| OCR 모델 | `%LOCALAPPDATA%\MyCapture\ocr-models` | 사용자가 지우기 전까지 |
| 업데이트 스테이징 | `%LOCALAPPDATA%\MyCapture\Updates` | 성공 시 헬퍼가 세션 파일을 지움. 실패 시 `update.log`에 예외 메시지 |

선택 직후 영역 캡처는 큐에 쓰지 않는 초안이다. 디스크 기록은 편집 커밋(완료, 빠른 저장, 다른 이름 저장, 클립보드 복사)에서 일어난다. 자동 색인은 이미지가 큐에 들어간 뒤 돈다. 색인은 `rendered.png`가 있으면 그것을, 없으면 `original.png`를 읽는다. 가림막을 커밋한 뒤에는 가려진 `rendered.png`를 다시 읽도록 되어 있다. `original.png`의 가려지지 않은 픽셀은 그 시점에도 남는다.

## 5. 빠른 가리기가 저장하는 것

`PrivacyToken.Text`는 탐지기 입력이다. `PrivacyMatch`는 종류, 줄·토큰 인덱스, 좌표만 가진다. `FindAsync`는 OCR 단어를 그 입력으로 만든 뒤, 호출자에게 3px 패딩된 사각형만 넘긴다. 테스트 `FindAsync_MapsOcrWordsToPaddedPlaintextFreeRegions`가 결과 타입에 인식 문자열이 없음을 고정한다.

`AddPrivacyRedactions`는 그 사각형을 채우기 `#161616` 사각형 주석으로, 한 번의 실행 취소 묶음에 넣는다. 저장 전에 이동·삭제·실행 취소할 수 있다는 README 문장은 이 주석 모델과 맞다.

가리기는 캡처 직후 자동이 아니다. 편집기 `Ctrl+Shift+R`이다. 커밋 전에는 화면의 원본 비트맵이 편집기 메모리에 있고, 커밋 뒤에는 `original.png`가 남는다. 자동 색인이 가리기 전에 끝난 세대가 있으면 그 평문은 내용 세대가 올라가기 전까지 `OcrText`에 있다. 가리기 커밋은 OCR 캐시를 비우고 색인을 다시 요청한다.

탐지 범위는 방어용으로만 적는다. 같은 줄에서 최대 8토큰, 줄을 넘지 않음. 이메일, 한국 전화, 주민등록번호 형태(13자리·구분 숫자 1–8·달력 날짜, 체크섬 자릿수 검사는 없음), 카드 번호(Luhn), IPv4, 제한된 비밀 접두(`sk-`, `gh*`, `github_pat_`, `AKIA`/`ASIA`, `Bearer`). 주소 분류기는 없고, IPv6도 없다. 클래스 밖 문자열은 가림막이 되지 않는다. OCR 상자가 글자보다 좁거나 회전 인식이 빠지면 사각형이 잉크를 다 덮지 못할 수 있다. 기본 품질 Fast는 회전 탐색을 하지 않는다 (`OcrQualityProfile.SearchRotatedOrientations`).

이 한계는 가리기 버튼의 사용성 문제가 아니라, 가려진 뒤에도 원본 파일·검색 인덱스·미탐 영역에 화면 정보가 남을 수 있다는 신뢰 공백이다. 버튼·실행 취소·단축키의 완성도는 functionality 축의 가리기 도구가 주 소유자다.

## 6. 클립보드

- 영역 캡처(`CaptureOverlayWindow`의 기본 인자)는 편집기 커밋 전에 선택 픽셀을 클립보드에 넣는다. 창·전체·스크롤처럼 `copyToClipboardImmediately: false`인 경로는 그 즉시 복사를 하지 않는다.
- 완료·빠른 저장·다른 이름 저장은 평탄화된 이미지를 항상 클립보드에 복사한다. `ExportSettings.CopyToClipboardOnQuickSave`는 파일에 남지만 `CaptureCommitService`는 그 값을 읽지 않는다.
- GitHub 흐름은 클립보드 비트맵을 읽고, 끝나면 URL 문자열로 클립보드를 바꾼다. 앱이 클립보드 기록을 지우거나 Windows 클립보드 기록(사용자 설정)을 끄지는 않는다.
- 핀의 원문 텍스트는 핀 창 메모리에 있다. 핀 OCR은 캡처 레코드에 캐시하지 않는다 (`App.xaml.cs`의 핀 OCR 주석). 핀을 저장하면 PNG가 빠른 저장 폴더로 나간다.

## 7. 로그와 마스킹

`LogText.SingleLine`은 CR, LF, NEL, 줄 구분 문자를 이스케이프한다. 비밀·OCR 본문·창 제목을 마스킹하지 않는다.

자동 색인 로그는 글자 수만 남긴다 (`Cached OCR text ({Length} chars)`). 인식 본문을 로그 템플릿에 넣지는 않는다.

남는 표면:

- 디버그 로거에 경로가 올라간다. 프로필 경로에는 사용자 이름이 포함될 수 있다.
- 토큰 업로드 실패 시 HTTP 응답 본문 전체를 디버그 경고로 남긴다 (`GitHubTokenUploadService`). 토큰 문자열 자체를 로그에 찍는 코드는 없다. 실패 풍선에는 `ex.Message`가 붙을 수 있다.
- 업데이트 실패 시 `%LOCALAPPDATA%\MyCapture\Updates\...\update.log`에 예외 메시지를 쓴다. 화면 OCR은 포함되지 않는다.
- 처리되지 않은 디스패처 예외는 메시지 상자에 `Exception.Message`를 보인다.

파일 로그에 화면 텍스트가 쌓이는 경로는 확인되지 않았다. 평문 보존의 본체는 인덱스와 메타다.

## 8. GitHub 흐름 (README의 F4, 코드의 F9)

네트워크 전송이 맞다. 다만 캡처 파이프라인에 묶인 자동 업로드는 아니다. 전역 단축키를 눌렀을 때, 클립보드에 이미지가 있을 때만 시작한다.

1. 토큰이 없으면 기본 브라우저로 이슈 URL을 연다. UI 자동화가 주소 표시줄이 그 URL과 맞는 창의 쓰기 칸에 붙여 넣고, 새로 나타난 `user-attachments` URL을 클립보드에 복사한다. 이미지 바이트의 HTTP 전송 주체는 앱이 아니라 로그인된 브라우저 세션이다. 코드는 댓글을 제출하지 않는다. 페이지가 붙여 넣기만으로 첨부 업로드를 수행하는지는 GitHub 웹 동작이라 이 저장소만으로 확정하지 않는다. 앱은 그 URL이 나타날 때까지 최대 60초를 기다린다.
2. 토큰이 있으면 앱이 PNG를 REST로 보낸다. 정책 요청에만 베어러 토큰을 붙이고, 두 번째 POST에는 붙이지 않는다. 정책이 거절되면 브라우저 흐름으로 떨어진다.
3. 기본 이슈 URL은 공개 저장소의 `issues/new`다. 토큰 경로는 번호 있는 이슈만 받는다(`TryParseTarget`). 브라우저 경로는 `issues/new`를 허용한다(`TryNormalize`). 그래서 토큰 없는 기본 설정에서 F9는 그 새 이슈 페이지를 연다.
4. 이슈 URL은 HTTPS, 사용자 정보 없음, 443만 허용한다. 호스트는 github.com에 고정되지 않아 Enterprise Server URL이 통과한다.

업데이터와 달리 토큰 경로의 `HttpClient`는 기본 생성자이고, `upload_url`을 허용 호스트 목록으로 다시 검사하지 않는다. 있는 방어는 토큰을 두 번째 요청에 실어 보내지 않는 것, 이슈 URL 자체의 HTTPS 검사, 디스크의 DPAPI다. 없는 방어는 두 번째 POST 목적지에 대한 업데이터와 같은 호스트 제한이다. 우회 절차는 적지 않는다.

## 9. 업데이트 채널과 Authenticode

있는 방어 (`GitHubUpdateService`, `GitHubUrlValidator`, `UpdateInstaller`, `UpdateHelper.ps1`):

- 시작 시 자동 확인은 없다. 설정 탭의 사용자 동작만 받는다.
- API는 고정된 `https://api.github.com/repos/sukwoo0711-maker/my-capture/releases/latest`만 호출한다. 자동 리다이렉트는 꺼져 있고, 홉은 직접 검사하며 상한은 5다.
- 다운로드 초기는 `github.com/{owner}/{repo}/releases/download/...`만 허용한다. HTTP, 사용자 정보, 443이 아닌 포트는 거절한다.
- 설치 파일은 릴리스의 `SHA256SUMS.txt` 항목과 맞아야 하고, 크기 상한이 있다. 실행 직전에 해시를 다시 계산한다.
- 스테이징 경로의 재분석 지점을 거절한다. 헬퍼는 소유한 설치 루트의 `MyCapture.exe`·`MyCapture.dll` 해시와 버전을 확인한 뒤에 재실행한다.
- 설치 매니페스트는 `Unsigned = $true`를 기록한다. `SECURITY.md`는 체크섬이 신원 증명이 아니라고 적는다.

없는 방어:

- 배포 EXE·DLL의 Authenticode.
- `SHA256SUMS.txt`에 대한 분리된 서명(Authenticode, minisign, cosign 등). 체크섬 파일은 설치 파일과 같은 GitHub 릴리스에서 온다.
- 설치 파일을 실행하기 전의 게시자 인증서 검사. 헬퍼는 해시가 맞은 EXE를 시작한다.
- 헬퍼 PowerShell은 `-ExecutionPolicy Bypass`로, 앱에 포함된 스크립트 파일만 지정해 실행한다. 이건 서명 정책을 대신하지 않는다.

`package.ps1`은 릴리스 노트용 `README-OFFLINE.txt`에 미서명과 SHA-256 확인을 적는다. 이 고지는 코드 서명 공백을 숨기지 않는다. 공백 자체는 남아 있다.

## 10. 의존성 라이선스

`THIRD-PARTY-NOTICES.md`와 `Directory.Packages.props`가 맞는 범위:

- .NET / WPF, Microsoft.Extensions.* 10.0.11, System.Security.Cryptography.ProtectedData 10.0.11 (MIT 계열로 고지).
- RapidOcrNet 4.2.0 (Apache-2.0), Microsoft.ML.OnnxRuntime 1.29.0 (MIT).
- PP-OCRv5 모델 Apache-2.0, Real-ESRGAN BSD-3-Clause, Fluent System Icons MIT. 아이콘 라이선스 전문이 고지 파일에 있다.
- 테스트 패키지(xUnit, Test SDK)는 런타임 배포가 아니라고 구분되어 있다.
- “FFmpeg, Flyleaf, Snipaste, ShareX, ScreenToGif, OBS, ALCapture를 번들하지 않는다”는 문장과 맞는 네이티브 패키지 참조는 `src/` csproj에 없다.

고지 파일은 SkiaSharp를 RapidOcrNet 쪽 구성요소로 적는다. 이번 평가에서 NuGet 잠금 파일의 전이 의존성 전체를 다시 풀지는 않았다. SQLite 네이티브 라이브러리는 `Directory.Packages.props` 주석대로 채택하지 않았다.

## 11. 단일 인스턴스와 권한

- `app.manifest`는 `asInvoker`, `uiAccess=false`. 관리자 승격을 요청하지 않는다.
- 단일 인스턴스는 `Local\MyCapture.SingleInstance.{...}` 뮤텍스다. 두 번째 일반 실행은 이벤트를 켜고 종료하며, 첫 프로세스가 라이브러리를 연다. self-test는 뮤텍스보다 먼저 돌아갈 수 있다.
- 로그인 실행 기본값은 켜짐이고, `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`만 쓴다. HKLM은 열지 않는다 (`RegistryRunKeyStore`).
- 설치 기본 위치는 `%LOCALAPPDATA%\Programs\MyCapture`다. 사용자 데이터는 업데이트·제거 페이로드에 포함되지 않는다고 패키지 안내가 적는다.
- 화면 캡처·녹화는 이 권한 모델에서 다른 창의 픽셀을 읽는다. 그것은 제품 기능이고, 별도 상승된 권한은 아니다.

## 12. 경쟁 비교

이번 세션에서 Snipaste, ShareX, Greenshot, 알캡처, PicPick, ScreenToGif, Windows 11 캡처 도구의 공식 개인정보·서명 페이지를 열지 않았다. 아래 공통점·우위는 그 페이지의 현재 문장으로 단정하지 않는다.

**과제 고유 강점** (무계정, 로컬 OCR, 가리기라는 이 제품 범위 안에서만):

- 계정, 광고 SDK, 제품 분석 텔레메트리, 캡처 자동 업로드가 코드에 없다.
- 기본 OCR은 Windows 온디바이스 엔진이다. Accurate/Enhanced만 추가 로컬 ONNX를 쓰고, 그 가중치는 해시가 맞을 때만 저장한다.
- 빠른 가리기 탐지 결과는 인식 문자열을 결과 객체와 주석 파일에 복사하지 않는다.
- GitHub PAT는 사용자 선택이며, 디스크에서는 DPAPI, 설정 내보내기에서는 제거된다.

**ShareX와의 목적 차이:** ShareX의 제품 목적에는 업로드 자동화가 들어 있다. MyCapture의 GitHub 단축키는 그 자동화 제품이 아니다. 클립보드 이미지를 사용자가 지정한 이슈 초안으로 보내는 선택 기능이고, 기본 상태에서도 캡처 저장과 분리되어 있다. ShareX에 업로더가 있다는 이유로 MyCapture의 신뢰 점수를 깎지 않고, MyCapture에 그 업로더가 없다는 이유로 ShareX보다 업로드 제품으로서 우월하다고 적지 않는다. 있는 사실은 따로다. MyCapture에도 기본으로 등록된 F9 전송 경로가 있다.

**공통점** (로컬 캡처 도구가 같이 가지는 성질):

- 화면 픽셀과 인식 텍스트를 사용자 PC에 남긴다. 클립보드도 공유 OS 표면이다.
- 검색을 위해 텍스트를 색인하면, 그 색인이 곧 민감정보 사본이 된다.
- 코드 서명 없는 데스크톱 배포는 SmartScreen의 알 수 없는 게시자 경고를 만난다. 어느 경쟁 제품이 현재 서명되어 있는지는 미확인이다.

**경쟁 우위** (서명된 배포를 제공하는 도구가 더 잘하는 칸):

- 게시자 인증서로 설치 파일과 업데이트 메타데이터를 묶는 도구는, 같은 채널의 SHA-256만 있는 MyCapture보다 설치 신뢰를 더 제공한다. 특정 제품명을 서명 사실로 적지 않는다.

**범위 밖:** OBS급 스트리밍, 오디오 녹음, 계정 클라우드 동기화. 오디오가 없는 것은 신뢰 감점이 아니다.

## 13. 신뢰 공백

1. **코드 서명.** 설치형·포터블·업데이터가 받는 EXE에 Authenticode가 없다. 고지는 정직하다. 고지가 서명을 대신하지는 않는다.
2. **업데이트 무결성과 신원.** HTTPS, 호스트 제한, SHA-256, 재실행 전 재해시는 있다. 체크섬 파일의 독립 서명은 없다. 릴리스 자산과 체크섬을 함께 바꿀 수 있는 주체를 기술이 구분하지 않는다.
3. **로그가 아니라 큐에 남는 화면 텍스트.** `OcrText`와 `SourceWindowTitle`이 `index.json`·`meta.json`에 평문으로 남고, 자동 색인은 `CacheResults`를 무시한다. `original.png`는 가리기 후에도 남는다. 동영상과 고정 이미지는 7일 만료 밖이다. 빠른 저장과 드래그 복사본은 별도 수명이다.
4. **가리기 오탐·미탐.** 패턴 밖 비밀, 줄 넘김, 체크섬 없는 주민번호 형태, 좁은 OCR 상자에서는 가림막이 없거나 엉뚱한 자리에 생긴다. 가림막은 원본 파일을 파기하지 않는다. 사용자는 한 번의 단축키로 화면 정보가 지워졌다고 보기 쉽다.
5. **기본 GitHub 목적지.** 단축키는 기본으로 등록되고, 기본 URL은 공개 저장소의 새 이슈 페이지다. 토큰이 없으면 브라우저가 그 페이지로 클립보드 이미지를 가져간다.
6. **모델 공급망.** 첫 실행이 제3자 모델 호스트로 나간다. 해시 핀은 있다. 그 핀을 실은 바이너리 자체는 서명되어 있지 않다. 모델용 `HttpClient`는 업데이터처럼 리다이렉트 호스트를 제한하지 않고, 저장 전에 해시를 본다.
7. **토큰 업로드의 두 번째 URL.** 업데이터급 허용 목록이 없다. 토큰은 그 요청에 실리지 않는다.

## 14. 보완

주 소유가 trust인 항목만 적는다. 가리기 도구의 UI는 여기 P 순위에 넣지 않는다.

**P0**

- 릴리스 EXE·DLL과 `SHA256SUMS.txt`에 게시자 서명을 붙이고, 업데이터가 그 서명을 검사한 뒤에만 설치 파일을 실행하게 한다. SHA-256 재검사는 서명 옆에 남긴다.
- 서명 전에는 지금처럼 미서명을 설치 안내의 첫 사실로 유지한다. 이미 `SECURITY.md`와 `package.ps1`이 그렇게 하고 있다.

**P1**

- 자동 OCR 색인이 `CacheResults == false`이면 `OcrText`를 쓰지 않게 한다. 가리기 커밋 시 `original.png`를 가려진 픽셀로 바꿀지, 원본 보존을 설정으로 드러낼지 정한다. 기본이 원본 보존이면 README에 “가리기는 `rendered.png`와 이후 색인만 바꾸고 `original.png`는 만료까지 남는다”를 적는다.
- GitHub 단축키 기본값을 미등록으로 두거나, 번호 있는 이슈를 사용자가 저장하기 전에는 브라우저를 열지 않게 한다. README의 F4 표를 코드의 F9·마이그레이션과 맞춘다.
- 토큰 업로드의 `upload_url`에 업데이터와 같은 HTTPS·호스트 제한을 둔다.
- 모델 다운로드를 Accurate/Enhanced를 처음 쓸 때로 옮기거나, 시작 시 다운로드를 README 네트워크 절에 기본 켜짐으로 적는다.
- 디버그 로그의 GitHub 응답 본문을 상태 코드만 남기도록 줄인다. 파일 로그를 추가할 경우 `LogText` 수준이 아니라 비밀·OCR 본문 마스킹을 먼저 둔다.

**P2**

- 주민번호 형태에 체크 규칙을 넣어 오탐을 줄이는 것은 탐지기 품질이다. 완전 탐지를 약속하는 문구는 넣지 않는다.
- 동영상·고정 이미지에 선택적 최대 보관 시간을 둔다. 지금은 README가 만료 예외를 이미 설명한다.
- `%APPDATA%\MyCapture\logs`를 쓰지 않는다면 개인정보 절의 “로그 보관”을 인덱스·메타 보관으로 고친다.
- 모델 요청 User-Agent의 `MyCapture/2.3.10`을 현재 버전과 맞춘다. 사용자 데이터는 아니다.

**OVERLAP**

- 가리기 도구(단축키, 가림막 편집, 실행 취소, 모자이크와의 관계)의 주 소유는 functionality다. trust는 “탐지 결과가 평문을 저장하지 않음”과 “원본·인덱스에 화면 정보가 남음”만 소유한다. 도구 완성도를 신뢰 점수에 다시 넣지 않는다.
- README F4/F9 표기 불일치는 학습성(learnability)과 겹친다. 단축키를 사용자가 찾는지의 주 소유는 learnability, 그 키가 네트워크로 이미지를 보내는지는 trust다.
- 두 번째 실행이 첫 창을 띄우는 UX가 convenience에 있으면, 권한 모델(`asInvoker`, HKCU Run, Local 뮤텍스)은 trust가 이미 확인한 사실로 두고 편의 감점으로 바꾸지 않는다.
- 자동 색인의 CPU 비용은 이 축의 감점이 아니다. performance로 넘기지 않는다. 색인이 디스크에 쓰는 문자열만 trust 항목이다.

## 15. 미확인

- 실행 중인 프로세스의 디버그 출력과 `%APPDATA%\MyCapture\logs`를 이번 세션에서 비우지 않았다. 파일 로그 부재는 소스에서 프로덕션 `AddDebug()`만 있는 것으로 확인했다.
- 모델 카탈로그의 SHA-256이 현재 원격 파일과 같은지는 다시 받지 않았다. 불일치 시 코드는 저장하지 않고 예외로 빠진다.
- GitHub 웹이 댓글 제출 전에 붙여 넣은 이미지를 업로드하는지는 이 저장소 밖이다.
- Windows 클립보드 기록이 비트맵을 유지하는지는 OS 설정이다. 앱은 그 기능을 끄지 않는다.
- 현재 릴리스 바이너리의 SmartScreen 평판은 저장소에 없다.
- 전이 NuGet 라이선스 전문과 SkiaSharp 실배포 파일 목록은 `THIRD-PARTY-NOTICES.md` 이상으로 다시 풀지 않았다.
- 경쟁 제품의 현재 서명·텔레메트리·업로드 기본값은 공식 페이지를 열지 않아 미확인이다.
- DPAPI CurrentUser 블롭이 로밍 프로필에서 어떻게 따라가는지는 실행으로 확인하지 않았다. 다른 사용자로는 복호화되지 않는다는 코드 주석만 있다.

악용 방법, 우회 절차, 익스플로잇 코드는 작성하지 않았다.
