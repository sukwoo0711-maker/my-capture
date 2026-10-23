# 스토어 스크린샷 합성

MyCapture의 실제 창 레이어를 2560×1440 스토어 이미지 9장으로 합성합니다. `compose.py`는 앱 실행 없이 `scenes.json`의 제목·자르기·배치를 적용합니다. 같은 입력이면 같은 바이트의 PNG가 나오도록 ICC 프로필의 생성 시각을 고정합니다.

2026-10-20 장면을 핀 중심으로 바꿨습니다. 순서는 엑셀 레지스터 표 핀과 코드, `#define` 코드 핀 원문 붙여넣기, 뒤 창 클릭 허용, 캡처 후 카드에서 P, 이미지 핀 글자 복사, 핀 숨기기·다시 열기, 라이브러리, 공유 전 주석·가리기, 비교 카드입니다. 핀을 띄우는 키는 F3 하나로 보여 줍니다. 레이어는 `--selftest-store-layers`가 언어별로 만들고, 2026-10-20 렌더는 F3·Shift+F3이 새 설치 기본값인 4.5.0 핀 흐름 브랜치(`feat/pin-core-flow-450`) 위에서 했습니다. 그 결과는 `artifacts/store/screenshots/4.5.0-pin/{ko,en}`에 있습니다. 2026-10-22 UX 개선(#119) 화면으로 다시 렌더한 결과는 `artifacts/store/screenshots/ux453/{ko,en}`입니다. 바뀐 장은 7번 라이브러리(썸네일 시각 배지와 머리글 '새 폴더' 버튼 제거, '검색 저장' 정리)와 8번 편집기(한 줄 명령 막대, 실행 취소·다시 실행·더보기는 아이콘)이고, 나머지 일곱 장은 `4.5.0-pin`과 픽셀까지 같습니다. 2026-10-23 편집기 점검(#132) 화면으로 다시 렌더한 현재 결과는 `artifacts/store/screenshots/ux461/{ko,en}`입니다. 8번 편집기만 바뀌었고(자르기 옆 회전 단추 두 개, 더보기 메뉴 제거, 평소 크게 가운데 둔 명령 아이콘, 한 줄 속성 막대), 나머지 여덟 장은 `ux453`과 픽셀까지 같습니다. 4.4.0 제출(제출 13)에 쓴 이전 장면 구성은 이 문서의 git 기록과 `artifacts/store/screenshots/4.4.0`에 있습니다.

## 입력과 실행

Python 3.12, Pillow와 다음 가변 글꼴이 필요합니다.

- 한국어: `C:/Windows/Fonts/NotoSansKR-VF.ttf`
- 영어: `C:/Windows/Fonts/SegUIVar.ttf`
- 레이어: `--selftest-store-layers`의 언어별 출력 폴더(아래 '레이어 만들기')
- 검색 카드 근거: `C:/GitRepositories/my-capture/artifacts/strict-review-2026-10-20/evidence/store-search-tall.png`

레이어 폴더의 `manifest.json`에는 `files` 배열을 둡니다. `path`는 해당 폴더 안의 슬래시 구분 상대 경로, `width`·`height`는 실제 PNG 픽셀 크기, `renderScale`은 원본의 논리 픽셀당 렌더 픽셀 수입니다. 모든 사용 파일에 네 필드를 기록합니다. `generator`에는 버전과 언어를 함께 기록하는 것을 권장합니다.

```json
{
  "generator": {"appVersion": "4.4.0", "language": "ko"},
  "files": [
    {"path": "redact/03.png", "width": 3720, "height": 2340, "renderScale": 3},
    {"path": "samples/code.png", "width": 2880, "height": 1800, "renderScale": 2}
  ]
}
```

레이어 만들기: main을 Release로 빌드한 뒤, 언어마다 격리 경로로 실행합니다(레이어 코드는 `src/MyCapture.App/Diagnostics/StoreScreenshotSelfTest.*.cs`에 있습니다). `MYCAPTURE_DATA_ROOT`, `TMP`, `TEMP`는 짧은 역슬래시 경로로 줍니다. 자체 검사는 시스템 클립보드, 사용자 설정·라이브러리, HKCU Run을 건드리지 않고, 끝나면 `_fixtures-*`를 지웁니다.

```powershell
$env:TMP='C:\Users\<user>\AppData\Local\Temp\pl5\ko\t'; $env:TEMP=$env:TMP; $env:MYCAPTURE_DATA_ROOT='C:\Users\<user>\AppData\Local\Temp\pl5\ko\d'
MyCapture.exe --selftest-store-layers C:\GitRepositories\my-capture\artifacts\store\layers\ux461\ko ko
```

합성은 다음 명령입니다. 영어는 경로의 `ko`와 `--lang`을 `en`으로 바꿉니다.

```powershell
python -X utf8 -I build/store-shots/compose.py --layers "C:/GitRepositories/my-capture/artifacts/store/layers/ux461/ko" --lang ko --out "C:/GitRepositories/my-capture/artifacts/store/screenshots/ux461/ko" --scenes "<worktree>/build/store-shots/scenes.json"
```

전체 실행은 `01.png`부터 `09.png`, `contact-sheet.png`, `search-card.png`를 만듭니다. 본 이미지는 RGB·sRGB ICC·알파 없는 최적화 PNG입니다. 연락판은 장당 640×360, 3열로 배치합니다. `search-card.png`에는 현재 등록 이미지, 새 장면 1, 경쟁 앱 두 장을 원본 카드 배율로 나란히 배치합니다. 글꼴 축, 실제 제목 높이, 줄바꿈, 배율과 출력 경로는 표준 출력의 JSON으로 보고합니다.

`--only 1,2`를 덧붙이면 지정한 번호의 본 이미지와 해당 연락판만 만듭니다. 검색 카드 검수를 위해 장면 1은 항상 새로 합성합니다. `--scenes`를 생략하면 스크립트 옆의 `scenes.json`을 읽습니다. 누락 레이어, 잘못된 manifest 크기·자르기, 제목 제한 위반, 필수 글꼴 부재는 오류와 비정상 종료로 처리합니다.

## 좌표와 시각 규칙

`crop`은 원본 레이어 픽셀의 `[left, top, right, bottom]`이며 오른쪽·아래 경계는 포함하지 않습니다. `crop`을 생략하면 레이어 전체를 씁니다. 메뉴와 핀처럼 언어마다 크기가 다른 레이어에 씁니다. manifest의 `windowOpacity`는 제품 창의 투명도이며, 합성기가 레이어와 그림자에 그 값을 곱합니다(`RenderTargetBitmap`은 창 투명도를 담지 않기 때문입니다). `placement`는 최종 캔버스의 `[x, y]`입니다. `scale`은 **원본 픽셀에 곱하는 값**이며, 최종 DIP 배율은 `scale × renderScale`입니다. `z`가 작은 레이어부터 합성합니다. 창 아래나 오른쪽이 캔버스 밖으로 이어지는 배치는 의도된 자르기입니다. 상세 값과 최종 원본 조건은 `scenes.json`을 기준으로 합니다.

모든 시각 상수는 `compose.py` 상단의 `STYLE`에 둡니다.

- 배경은 위 `#16191F`에서 아래 `#101318`로 이어지는 흑연색 수직 그라데이션입니다. 합성기에는 추가 색상 장식·질감·로고·배지·번호를 넣지 않습니다.
- 제목은 132px, 굵기 700, `#F3F5F8`이며 왼쪽 160px·위 104px에서 시작합니다. 영어는 Segoe UI Variable의 Display 광학 크기를 사용합니다. 최대 두 줄, 줄 간격 1.15배이며 보조 문구는 44px·`#9AA5B5`입니다. 제목과 창 사이에는 최소 48px를 둡니다.
- 한국어 제목은 공백 제외 6~14자(영문 단어는 한 글자로 셈), 영어는 공백 포함 최대 34자입니다. 기능을 설명하는 합니다체 또는 명사구를 사용하고 영어 끝 마침표는 생략합니다.
- 그림자는 검정 약 50%·블러 60px·아래 이동 24px입니다. 원본 알파를 따라 흰색 약 9%의 1px 테두리를 적용하여 실제 둥근 모서리를 유지합니다.
- 추가 강조가 필요할 때만 `#E5483B` 2px 외곽선을 한 번 사용합니다. 현재 장면 계획은 추가 외곽선을 사용하지 않습니다. 원본의 상태 색과 주석은 그대로 보존합니다.

검색 이미지 경계는 1280×6000 근거 이미지의 `[115,3170,434,3349]`로 측정한 **319×179px**입니다. 경쟁 앱 경계는 `[480,736,800,916]`, `[845,736,1165,916]`이며 각각 320×180px입니다. MyCapture의 가로 축소율은 `319/2560 = 0.124609375`입니다. 제목의 소문자 `x` 높이가 카드에서 7px 이상이려면 원본에서 약 56.18px 이상이어야 합니다. 합성기는 실제 글꼴 측정값으로 이 기준을 검사합니다.

글꼴 파일의 OS/2 표에 기록된 명목 x-height를 132px에 적용하면 Noto Sans KR은 `132 × 543/1000 = 71.676px`, 카드에서 약 **8.93px**입니다. Segoe UI Variable은 `132 × 1024/2048 = 66px`, 카드에서 약 **8.22px**입니다. 이 값은 글꼴 표의 비율로 계산한 명목값입니다. 실제 렌더에서 잰 한국어 제목의 x 높이는 74px, 검색 카드에서 9.2px이고 한글 높이는 카드에서 15.3px입니다. compose.py는 실행할 때마다 이 값을 다시 재고 7px 미만이면 중단합니다.

## 장면

아래 배율은 원본 픽셀 배율 → DIP 배율 순서입니다. 에디터·터미널·문서 견본은 2×, 제품 창은 3× 입력입니다. 핀 장면은 실제 배율 200% 화면처럼 DIP 배율 2.0으로 맞춥니다. 텍스트·코드 핀은 제품이 96 DPI로 그린 그림이라 2.0배에서 조금 부드럽게 보이며, 실제 200% 화면에서 보이는 모습과 같습니다.

| 번호 | 한국어 제목 / 영어 제목 | 보조 문구(ko) | 레이어별 배율 |
| --- | --- | --- | --- |
| 1 | 사양 표를 띄워 두고 코딩합니다 / Code with the spec table in view | 엑셀에서 Ctrl+C → F3 · Ctrl+휠: 투명도 | `samples/editor-uart.png` 1.00 → 2.0, `pins/table.png` 0.667 → 2.0(창 투명도 0.88) |
| 2 | 구문을 원문 그대로 붙여 넣습니다 / Paste the snippet back as text | 에디터에서 Ctrl+C → F3 · 핀에서 Ctrl+더블클릭 → Ctrl+V | `samples/editor-paste.png` 1.00 → 2.0, `pins/code-define.png` 0.667 → 2.0 |
| 3 | 핀 아래 창을 그대로 씁니다 / Click through to the window below | 핀 오른쪽 클릭 → 뒤 창 클릭 허용 | `samples/terminal.png` 1.00 → 2.0, `pins/table-clickthrough.png` 0.667 → 2.0(0.72) |
| 4 | 캡처한 화면도 F3으로 띄웁니다 / Capture, then press F3 to pin | Ctrl+Shift+C로 캡처하면 클립보드에 복사 → F3 | `samples/editor-uart.png` 1.00 → 2.0, `pins/api-doc.png` 0.60 → 1.8 |
| 5 | 이미지 핀의 글자도 복사합니다 / Copy text from an image pin | 이미지 핀에서 Ctrl+더블클릭 · 한국어·영어 인식 | `samples/editor-ocr.png` 1.00 → 2.0, `pins/datasheet.png` 0.667 → 2.0 |
| 6 | 필요할 때만 꺼내 봅니다 / Hide pins and bring them back | Shift+F3: 핀 모두 숨기기·보이기 · 트레이 메뉴: 닫은 핀 다시 열기 | `samples/editor-uart.png` 1.00 → 2.0, `film/table-plain.png`·`film/code-plain.png`·`menus/tray-pins.png` 0.667 → 2.0 |
| 7 | 지난 캡처를 글자로 찾습니다 / Find past captures by their text | 없음 | `library/midnight-search.png` 0.75 → 2.25 |
| 8 | 공유 전에 주석과 가리기 / Mark up and redact before sharing | Ctrl+Shift+R: 개인정보 가리기 | `share/redact-annotated.png` 0.55 → 1.65 |
| 9 | Windows 캡처 도구에 더해지는 것 / What it adds to Snipping Tool | 없음 | 창 없이 2열 문자만 배치합니다. |

장면 9의 왼쪽(그대로)에는 영역·창·전체 화면 캡처, 클립보드 복사, 이미지 속 글자 복사, 영역 녹화를 넣습니다. 오른쪽(더해지는 것)에는 엑셀 모양 그대로 띄우는 표 핀, 원문을 다시 복사하는 표·코드 핀, 반투명·뒤 창 클릭 허용 핀, 닫은 핀 다시 열기, 스크롤 캡처를 넣습니다. Microsoft의 [Snipping Tool 기능 안내](https://support.microsoft.com/en-us/windows/apps/use-snipping-tool-to-capture-screenshots)에는 화면 위에 띄우는 핀 자체가 없고(2026-10-20 다시 확인, 'Pin to taskbar'는 앱 고정), 스크롤 캡처는 Microsoft의 [2026-02-26 설명](https://www.microsoft.com/en-us/windows/learning-center/how-to-use-snipping-tool-on-windows-screenshots-shortcuts-and-screen-recordings)이 기본 제공하지 않는다고 밝힙니다. 제출 직전 기능 안내를 다시 열어 확인합니다. 출처·보류 사유는 `claim_review`에 보관하며 렌더에는 두 열의 본문만 넣습니다.

## 핀 장면 레이어

`StoreScreenshotSelfTest.PinLayers.cs`가 기존 10장에 더해 20장을 만듭니다(합계 30). 견본(에디터·터미널·API 문서·시트)만 진단 코드가 그리고, 핀·카드·메뉴·편집기는 제품 창을 그대로 렌더합니다.

- 표 핀: 엑셀처럼 그림(PNG)·탭 구분 텍스트·HTML 표를 담은 메모리 안 `DataObject`를 `ClipboardImageReader.CaptureFrom`과 `CompletePinAttemptAsync`에 넣어 `PinContentKind.Table`을 만듭니다. 핀 그림은 넣은 PNG와 픽셀이 같아야 합니다.
- 코드 핀: 에디터처럼 색을 담은 CF_HTML과 텍스트를 같은 경로에 넣어 코드 렌더러의 `PinContentKind.Text`를 만듭니다. `Ctrl`+더블클릭은 기록만 하는 처리기로 받고 제품 안내 `텍스트 복사됨`을 띄웁니다.
- 이미지 핀 글자 복사: 실제 `PinTextCopyService.CopyAsync`에 고정 OCR 결과를 넣고, 복사한 문자열로 `samples/editor-ocr.png`의 주석을 그립니다.
- 클릭 허용·메뉴: 실제 `ApplyClickThrough`, 핀의 `ContextMenu`, `TrayMenuLayout.Build`(새 설치 단축키, 문의하기 포함)를 렌더합니다. 해제 안내는 새 설치 단축키에 따라 정해집니다(4.5.0: `Shift+F3 두 번`).
- 영상용(`film/`): 안내 없는 핀, 투명도 60% 안내, 붙여 넣기 전 에디터, 시트, 핀을 숨긴 트레이 메뉴.

## 4.4.0 렌더 요구 사항(제출 13의 이전 장면)

`--selftest-store-layers <outputDirectory> [ko|en]`은 아래 경로의 원본 레이어 10장과 `manifest.json`, `store-layers-report.txt`를 생성합니다. 기존 `--selftest-store-shots`는 그대로 유지합니다. 실행 전 `MYCAPTURE_DATA_ROOT`, `TMP`, `TEMP`를 모두 작업 트리의 `artifacts\t` 절대 경로로 지정합니다. 예: `MyCapture.exe --selftest-store-layers C:\GitRepositories\my-capture-440\store-scenes\artifacts\layers\ko ko`. 영어는 출력 마지막 폴더와 언어를 `en`으로 바꿉니다.

가리기는 합성 글자의 실제 글꼴 측정 좌표를 OCR 단어 입력으로 제공한 뒤 `PrivacyRedactionService`·`PrivacyDetector`·편집기의 실제 가리기 동작을 실행합니다. 터미널 OCR도 같은 합성 글자와 위치를 사용하며 외부 OCR 모델이나 사용자 캡처를 읽지 않습니다. 임시 라이브러리와 3초 영상은 언어별 출력 폴더의 `_fixtures-*`에만 작성하고, 실행이 끝나면 그 폴더를 지웁니다. 닫히는 창이 파일을 잡고 있어 지우지 못하면 보고서에 경로를 남깁니다.

제품을 있는 그대로 렌더합니다. 4.4.0 빠른 카드는 단축키 글자를 그리지 않고(C/E/P/S/T는 도구 설명과 자동화 이름에만 있음), 영상 편집기의 텍스트 트랙은 제품 색을 씁니다. 두 사항은 보고서에 기록할 뿐 실패로 보지 않으며, 정상 실행은 `RESULT: PASS`·종료 코드 0입니다.

4.4.0 렌더를 보고 배치를 조정했습니다. 장면 2의 핀은 `[1500,770]`, 장면 4의 카드는 배율 1.00(논리 3.00배)으로 키웠습니다. 장면 5는 crop `[0,300,3300,1920]`·배율 0.68, 장면 7은 미리보기 아래쪽부터 타임라인까지 crop `[0,1180,3720,2400]`·배율 0.75입니다. 장면 8은 실제 사용처럼 영상 편집기(배율 0.42) 위에 GIF 내보내기 창(crop `[0,0,1872,1900]`, 배율 0.56, 아래쪽이 캔버스 끝에 닿음)을 겹칩니다.

언어마다 별도 레이어 폴더를 만들고 아래 상대 경로를 그대로 유지합니다. 제품 창은 실제 ko/en 리소스로 렌더하며 창 바깥의 투명 알파를 보존합니다. 합성기는 창 내부 문구나 주석을 새로 그리지 않습니다. 다음 항목은 후속 자체 테스트의 구현 계약입니다.

1. **개인정보 가리기:** `AnnotationEditorWindow` 1240×780 DIP, 3×에서 `redact/00-before.png`와 `redact/03.png`를 각각 3720×2340px로 렌더합니다. 전자는 가리기 전 근거, 후자는 합성 입력입니다. 제목은 `로그 업로드 오류` / `Log upload failure`로 하고, `user@example.com`, `ghp_` 접두사와 36자 합성 토큰, 테스트 카드 `4111 1111 1111 1111`을 원본 y=650..1280에 배치합니다. 실제 가리기 결과로 세 값을 덮고 상단 도구막대와 가림막을 crop 안에 둡니다. 실제 사용자 데이터는 사용하지 않습니다. 보조 문구는 `Ctrl+Shift+R`입니다.
2. **핀:** `samples/code.png`는 1440×900 논리 픽셀의 합성 중성색 코드 창을 2×, 2880×1800px로 렌더합니다. 큰 글씨로 `CaptureService.cs`의 `SaveAsync` 메서드와 `await library.AddAsync(capture)`를 표시합니다. `pins/code.png`는 `SaveAsync` 서명과 반환 규약을 담은 다른 참조 캡처의 실제 `PinWindow`를 436×184 DIP, 3×, 1308×552px로 렌더합니다. 참조 핀은 오른쪽에 두고 코드 편집 줄을 가리지 않습니다. 신규 설치에는 클립보드 핀 단축키가 없으므로 제목·보조 문구에 키 이름을 쓰지 않고, 다른 창 위 유지 동작을 최종 앱에서 확인합니다.
3. **주석:** `annotation/06-step2.png`는 `AnnotationEditorWindow` 1240×780 DIP, 3×, 3720×2340px입니다. 합성 오류 대화상자 또는 실패 테스트 화면의 한 오류 지점을 중심으로 실제 편집기에서 화살표·단계 1과 2·`재시도 후 발생` / `After retry` 텍스트를 추가합니다. 오류와 세 주석은 원본 x=500..2600, y=650..1800에 둡니다. 현재 Office 차트와 형광펜 예제를 교체합니다.
4. **빠른 카드:** 장면 2와 같은 `samples/code.png`와 `QuickAccessWindow`의 `quick-access/dashboard.png`를 사용합니다. 카드는 344×215 DIP, 3×, 1032×645px이며 방금 캡처한 함수·파일명과 같은 썸네일과 실제 버튼을 보여 줍니다. 진단은 카드를 활성화하지 않은 유지 상태로 렌더하며, 단축키(Alt+Q로 카드 선택 뒤 E·P·S·T)는 보조 문구로 알립니다.
5. **텍스트 복사:** `ocr/result-source.png`는 `OcrResultWindow` 1100×640 DIP, 3×, 3300×1920px입니다. 왼쪽 터미널 캡처와 오른쪽 실제 인식 결과에 `dotnet test`, `Failed: 1`, `Expected: 200`, `Actual: 500`을 사용합니다. 줄별 위치와 코드·숫자가 원본과 일치해야 합니다. 제목·버튼은 해당 언어의 제품 리소스를 사용합니다.
6. **라이브러리:** `library/midnight-search.png`는 `LibraryWindow` 1240×760 DIP, 3×, 3720×2280px입니다. 자체 테스트 저장소에 합성 캡처 세 개와 OCR 색인을 준비하고 `timeout`을 검색합니다. 첫 결과는 창 제목 `CaptureService.cs — timeout`, 둘째는 OCR 본문 `Request timeout`, 셋째는 두 조건이 모두 일치합니다. 검색 필드·결과 제목·제목 또는 이미지 글자의 일치 근거를 crop 안에 둡니다. 실제 사용자 라이브러리는 사용하지 않습니다.
7. **영상 편집:** `video/editor.png`는 `VideoEditorWindow` 1240×800 DIP, 3×, 3720×2400px입니다. 자체 생성 프레임으로 터미널 테스트 실패와 수정을 보여 주는 3초 영상을 만들고 0.4~2.6초를 선택합니다. 시간 지정 텍스트 두 개는 `오류 재현` / `Reproduce the error`, `수정 후 확인` / `Verify the fix`입니다. 프리뷰 하부·남는 길이·시작과 끝 핸들·두 텍스트 트랙을 하단 crop에 표시합니다. 실제 녹화 장치를 사용하지 않으며, 텍스트 트랙은 제품이 그리는 색 그대로 둡니다.
8. **GIF 내보내기:** `video/gif-export-dialog.png`는 `VideoExportDialog` 624×1021 DIP, 3×, 1872×3063px입니다. 장면 7 영상 프리뷰, GIF 선택, 960px·10fps 권장 설정, 10MB GitHub 첨부 목표, 제품이 실제 계산한 크기를 표시합니다. 설정은 crop 위쪽, 권장 상태와 크기는 아래쪽에 둡니다. 예상/측정 크기는 실제 산정 결과와 제품 문구를 그대로 쓰며 기존 예제의 229KB를 고정하지 않습니다. 긴 아래 빈 공간과 내보내기 버튼은 crop 밖에 둡니다.
9. **비교:** 창 레이어가 없습니다. `그대로` / `Works the same`, `더해지는 것` / `What MyCapture adds`의 두 열과 검토된 항목만 사용합니다. 열 제목은 44px `#9AA5B5`, 그 아래 흰색 10% 1px 선, 본문은 60px(왼쪽 `#9AA5B5`, 오른쪽 `#F3F5F8`)이며 아이콘·체크표시를 추가하지 않습니다.

## 최종 검수

- [ ] 언어별 manifest의 경로·실제 크기·renderScale과 위 창 크기를 대조합니다. 모든 crop 안에 필요한 기능과 개발 예제가 들어가는지 확인합니다.
- [ ] 4.5.0 핀 흐름 빌드의 ko/en 레이어로 전체 명령을 실행하고, 종료 코드 0과 언어별 본 이미지 9장·연락판·검색 카드를 확인합니다.
- [ ] 표준 출력의 제목 높이와 카드 환산값을 기록하고 7px 기준, 제목 잘림·줄바꿈·보조 문구 간격을 확인합니다.
- [ ] 검색 카드의 현재 이미지와 새 시안을 나란히 열어 제목 가독성을 확인합니다. 연락판과 개별 2560×1440 이미지를 열어 창 크기·그림자·투명 모서리·본문 가독성·필수 정보의 자르기를 확인합니다.
- [ ] 영어 창의 실제 번역, 개인정보 가리기 결과, 핀·카드 단축키, OCR 일치, 라이브러리 검색 근거, 시간 지정 텍스트, GIF 크기 산정을 확인합니다.
- [ ] 장면 9의 기능 차이를 출시 시점 자료로 재검토하고, 최종 앱의 강조색이 이미지당 의미 있는 한 지점에만 나타나는지 확인합니다.
- [ ] 검수 뒤 바꾼 간격·배율은 scenes.json과 STYLE에 반영하고 제출 기록(docs/store/listing.md)에 남깁니다.
