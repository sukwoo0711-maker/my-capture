# 스토어 스크린샷 합성

MyCapture의 실제 창 레이어를 2560×1440 스토어 이미지 9장으로 합성합니다. `compose.py`는 앱 실행 없이 `scenes.json`의 제목·자르기·배치를 적용합니다. 2026-10-20에 4.2.1 한국어 레이어로 두 언어 시안을 렌더해 배치와 검색 카드 가독성을 확인했습니다. 같은 입력이면 같은 바이트의 PNG가 나오도록 ICC 프로필의 생성 시각을 고정합니다. 최종 이미지는 4.4.0 렌더가 나오면 아래 명령으로 다시 만듭니다.

## 입력과 실행

Python 3.12, Pillow와 다음 가변 글꼴이 필요합니다.

- 한국어: `C:/Windows/Fonts/NotoSansKR-VF.ttf`
- 영어: `C:/Windows/Fonts/SegUIVar.ttf`
- 현재 레이어: `C:/GitRepositories/my-capture/artifacts/promo-2026-10-18/source/assets/ui/`
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

다음 명령은 절대 경로를 사용합니다. 최종 렌더에서는 `--layers`를 해당 언어의 4.4.0 레이어 폴더로 바꿉니다.

```powershell
python -X utf8 -I "C:/GitRepositories/my-capture-440/store-shots/build/store-shots/compose.py" --layers "C:/GitRepositories/my-capture/artifacts/promo-2026-10-18/source/assets/ui" --lang ko --out "C:/GitRepositories/my-capture-440/store-shots/artifacts/store-shots/proto/ko" --scenes "C:/GitRepositories/my-capture-440/store-shots/build/store-shots/scenes.json"
python -X utf8 -I "C:/GitRepositories/my-capture-440/store-shots/build/store-shots/compose.py" --layers "C:/GitRepositories/my-capture/artifacts/promo-2026-10-18/source/assets/ui" --lang en --out "C:/GitRepositories/my-capture-440/store-shots/artifacts/store-shots/proto/en" --scenes "C:/GitRepositories/my-capture-440/store-shots/build/store-shots/scenes.json"
```

전체 실행은 `01.png`부터 `09.png`, `contact-sheet.png`, `search-card.png`를 만듭니다. 본 이미지는 RGB·sRGB ICC·알파 없는 최적화 PNG입니다. 연락판은 장당 640×360, 3열로 배치합니다. `search-card.png`에는 현재 등록 이미지, 새 장면 1, 경쟁 앱 두 장을 원본 카드 배율로 나란히 배치합니다. 글꼴 축, 실제 제목 높이, 줄바꿈, 배율과 출력 경로는 표준 출력의 JSON으로 보고합니다.

`--only 1,2`를 덧붙이면 지정한 번호의 본 이미지와 해당 연락판만 만듭니다. 검색 카드 검수를 위해 장면 1은 항상 새로 합성합니다. `--scenes`를 생략하면 스크립트 옆의 `scenes.json`을 읽습니다. 누락 레이어, 잘못된 manifest 크기·자르기, 제목 제한 위반, 필수 글꼴 부재는 오류와 비정상 종료로 처리합니다.

## 좌표와 시각 규칙

`crop`은 원본 레이어 픽셀의 `[left, top, right, bottom]`이며 오른쪽·아래 경계는 포함하지 않습니다. `placement`는 최종 캔버스의 `[x, y]`입니다. `scale`은 **원본 픽셀에 곱하는 값**이며, 최종 DIP 배율은 `scale × renderScale`입니다. `z`가 작은 레이어부터 합성합니다. 창 아래나 오른쪽이 캔버스 밖으로 이어지는 배치는 의도된 자르기입니다. 상세 값과 최종 원본 조건은 `scenes.json`을 기준으로 합니다.

모든 시각 상수는 `compose.py` 상단의 `STYLE`에 둡니다.

- 배경은 위 `#16191F`에서 아래 `#101318`로 이어지는 흑연색 수직 그라데이션입니다. 합성기에는 추가 색상 장식·질감·로고·배지·번호를 넣지 않습니다.
- 제목은 132px, 굵기 700, `#F3F5F8`이며 왼쪽 160px·위 104px에서 시작합니다. 영어는 Segoe UI Variable의 Display 광학 크기를 사용합니다. 최대 두 줄, 줄 간격 1.15배이며 보조 문구는 44px·`#9AA5B5`입니다. 제목과 창 사이에는 최소 48px를 둡니다.
- 한국어 제목은 공백 제외 6~14자(영문 단어는 한 글자로 셈), 영어는 공백 포함 최대 34자입니다. 기능을 설명하는 합니다체 또는 명사구를 사용하고 영어 끝 마침표는 생략합니다.
- 그림자는 검정 약 50%·블러 60px·아래 이동 24px입니다. 원본 알파를 따라 흰색 약 9%의 1px 테두리를 적용하여 실제 둥근 모서리를 유지합니다.
- 추가 강조가 필요할 때만 `#E5483B` 2px 외곽선을 한 번 사용합니다. 현재 장면 계획은 추가 외곽선을 사용하지 않습니다. 원본의 상태 색과 주석은 그대로 보존합니다.

검색 이미지 경계는 1280×6000 근거 이미지의 `[115,3170,434,3349]`로 측정한 **319×179px**입니다. 경쟁 앱 경계는 `[480,736,800,916]`, `[845,736,1165,916]`이며 각각 320×180px입니다. MyCapture의 가로 축소율은 `319/2560 = 0.124609375`입니다. 제목의 소문자 `x` 높이가 카드에서 7px 이상이려면 원본에서 약 56.18px 이상이어야 합니다. 합성기는 실제 글꼴 측정값으로 이 기준을 검사합니다.

글꼴 파일의 OS/2 표에 기록된 명목 x-height를 132px에 적용하면 Noto Sans KR은 `132 × 543/1000 = 71.676px`, 카드에서 약 **8.93px**입니다. Segoe UI Variable은 `132 × 1024/2048 = 66px`, 카드에서 약 **8.22px**입니다. 이 값은 글꼴 표의 비율로 계산한 명목값입니다. 실제 렌더에서 잰 한국어 제목의 x 높이는 74px, 검색 카드에서 9.2px이고 한글 높이는 카드에서 15.3px입니다. compose.py는 실행할 때마다 이 값을 다시 재고 7px 미만이면 중단합니다.

현재 입력은 한국어 4.2.1 사무 예제입니다. `--lang en`도 같은 한국어 창 위에 영어 제목만 배치하는 시안입니다. 원본 영상 편집기의 보라색 트랙과 여러 빨강 상태·주석도 그대로 남습니다. 최종 흑연색·빨강 규칙과 영어 제품 화면은 4.4.0에서 실제로 렌더한 레이어로 충족해야 합니다.

## 장면

아래 배율은 원본 픽셀 배율 → DIP 배율 순서입니다. 코드 배경은 2×, 제품 창은 3× 입력을 기준으로 합니다. 14 DIP 본문은 2.40배에서 33.6px, 2.25배에서 31.5px, 2.10배에서 29.4px, 2.01배에서 28.14px가 됩니다. 보조 맥락인 코드 배경은 1.60배이며 14 DIP 기준 22.4px입니다. 실제 코드 예제에는 더 큰 글씨를 사용하고 원본 글자 크기와 가독성을 함께 검수합니다.

| 번호 | 한국어 제목 / 영어 제목 | 레이어별 배율 |
| --- | --- | --- |
| 1 | 개인정보를 한 번에 가립니다 / Redact personal data in one step | `redact/03.png` 0.75 → 2.25 |
| 2 | 코드 옆에 참고 화면을 띄웁니다 / Pin references above your code | `samples/code.png` 0.80 → 1.60, `pins/code.png` 0.80 → 2.40 |
| 3 | 오류 화면에 바로 표시합니다 / Mark up a bug screenshot | `annotation/06-step2.png` 0.70 → 2.10 |
| 4 | 캡처 카드에서 작업을 고릅니다 / Choose an action on the card | `samples/code.png` 0.80 → 1.60, `quick-access/dashboard.png` 1.00 → 3.00 |
| 5 | 이미지 속 글자를 복사합니다 / Copy text out of an image | `ocr/result-source.png` 0.70 → 2.10 |
| 6 | 지난 캡처를 글자로 찾습니다 / Find past captures by their text | `library/midnight-search.png` 0.75 → 2.25 |
| 7 | 녹화하고 필요한 구간만 남깁니다 / Record a region and trim it | `video/editor.png` 0.67 → 2.01 |
| 8 | GIF 크기를 미리 확인합니다 / Check GIF size before export | `video/gif-export-dialog.png` 0.67 → 2.01 |
| 9 | Windows 캡처 도구에서 옮겨 오면 / Coming from Windows Snipping Tool | 창 없이 2열 문자만 배치합니다. |

장면 9의 왼쪽(그대로)에는 영역·창·전체 화면 캡처, 클립보드 복사, 이미지 속 글자 복사, 영역 녹화를 넣습니다. 오른쪽(더해지는 것)에는 핀, 스크롤 캡처, 블러·모자이크·단계 번호, 창 제목과 글자로 찾는 라이브러리, 카드번호·토큰까지 찾는 가리기를 넣습니다. 오른쪽 다섯 항목은 Microsoft의 [Snipping Tool 기능 안내](https://support.microsoft.com/en-us/windows/apps/use-snipping-tool-to-capture-screenshots)에 없고(빠른 가리기는 이메일·전화번호만 안내), 스크롤 캡처는 Microsoft의 [2026-02-26 설명](https://www.microsoft.com/en-us/windows/learning-center/how-to-use-snipping-tool-on-windows-screenshots-shortcuts-and-screen-recordings)이 기본 제공하지 않는다고 밝힙니다. 제출 직전 기능 안내를 다시 열어 확인합니다. 표 복사는 [공식 Copy as table 발표](https://blogs.windows.com/windows-insider/2024/10/18/announcing-windows-11-insider-preview-build-22635-4371-beta-channel/)가 있어 차별점에서 제외했습니다. 출처·보류 사유는 `claim_review`에 보관하며 렌더에는 두 열의 본문만 넣습니다.

## 4.4.0 렌더 요구 사항

언어마다 별도 레이어 폴더를 만들고 아래 상대 경로를 그대로 유지합니다. 제품 창은 실제 ko/en 리소스로 렌더하며 창 바깥의 투명 알파를 보존합니다. 합성기는 창 내부 문구나 주석을 새로 그리지 않습니다. 다음 항목은 후속 자체 테스트의 구현 계약입니다.

1. **개인정보 가리기:** `AnnotationEditorWindow` 1240×780 DIP, 3×에서 `redact/00-before.png`와 `redact/03.png`를 각각 3720×2340px로 렌더합니다. 전자는 가리기 전 근거, 후자는 합성 입력입니다. 제목은 `로그 업로드 오류` / `Log upload failure`로 하고, `user@example.com`, `ghp_` 접두사와 36자 합성 토큰, 테스트 카드 `4111 1111 1111 1111`을 원본 y=650..1280에 배치합니다. 실제 가리기 결과로 세 값을 덮고 상단 도구막대와 가림막을 crop 안에 둡니다. 실제 사용자 데이터는 사용하지 않습니다. 보조 문구는 `Ctrl+Shift+R`입니다.
2. **핀:** `samples/code.png`는 1440×900 논리 픽셀의 합성 중성색 코드 창을 2×, 2880×1800px로 렌더합니다. 큰 글씨로 `CaptureService.cs`의 `SaveAsync` 메서드와 `await library.AddAsync(capture)`를 표시합니다. `pins/code.png`는 `SaveAsync` 서명과 반환 규약을 담은 다른 참조 캡처의 실제 `PinWindow`를 436×184 DIP, 3×, 1308×552px로 렌더합니다. 참조 핀은 오른쪽에 두고 코드 편집 줄을 가리지 않습니다. 신규 설치에는 클립보드 핀 단축키가 없으므로 제목·보조 문구에 키 이름을 쓰지 않고, 다른 창 위 유지 동작을 최종 앱에서 확인합니다.
3. **주석:** `annotation/06-step2.png`는 `AnnotationEditorWindow` 1240×780 DIP, 3×, 3720×2340px입니다. 합성 오류 대화상자 또는 실패 테스트 화면의 한 오류 지점을 중심으로 실제 편집기에서 화살표·단계 1과 2·`재시도 후 발생` / `After retry` 텍스트를 추가합니다. 오류와 세 주석은 원본 x=500..2600, y=650..1800에 둡니다. 현재 Office 차트와 형광펜 예제를 교체합니다.
4. **빠른 카드:** 장면 2와 같은 `samples/code.png`와 `QuickAccessWindow`의 `quick-access/dashboard.png`를 사용합니다. 카드는 344×215 DIP, 3×, 1032×645px이며 방금 캡처한 함수·파일명과 같은 썸네일, 실제 C/E/P/S/T 동작, 키보드 포커스 진입 상태를 표시합니다. 카드 클릭 또는 Alt+Q 등 실제 활성화 조건과 활성 상태에서의 단일 키 동작을 최종 앱에서 확인합니다.
5. **텍스트 복사:** `ocr/result-source.png`는 `OcrResultWindow` 1100×640 DIP, 3×, 3300×1920px입니다. 왼쪽 터미널 캡처와 오른쪽 실제 인식 결과에 `dotnet test`, `Failed: 1`, `Expected: 200`, `Actual: 500`을 사용합니다. 줄별 위치와 코드·숫자가 원본과 일치해야 합니다. 제목·버튼은 해당 언어의 제품 리소스를 사용합니다.
6. **라이브러리:** `library/midnight-search.png`는 `LibraryWindow` 1240×760 DIP, 3×, 3720×2280px입니다. 자체 테스트 저장소에 합성 캡처 세 개와 OCR 색인을 준비하고 `timeout`을 검색합니다. 첫 결과는 창 제목 `CaptureService.cs — timeout`, 둘째는 OCR 본문 `Request timeout`, 셋째는 두 조건이 모두 일치합니다. 검색 필드·결과 제목·제목 또는 이미지 글자의 일치 근거를 crop 안에 둡니다. 실제 사용자 라이브러리는 사용하지 않습니다.
7. **영상 편집:** `video/editor.png`는 `VideoEditorWindow` 1240×800 DIP, 3×, 3720×2400px입니다. 자체 생성 프레임으로 터미널 테스트 실패와 수정을 보여 주는 3초 영상을 만들고 0.4~2.6초를 선택합니다. 시간 지정 텍스트 두 개는 `오류 재현` / `Reproduce the error`, `수정 후 확인` / `Verify the fix`입니다. 프리뷰 하부·남는 길이·시작과 끝 핸들·두 텍스트 트랙을 하단 crop에 표시합니다. 실제 녹화 장치를 사용하지 않으며 보라색 트랙은 최종 제품의 흑연색·빨강 규칙을 반영한 실제 렌더로 교체합니다.
8. **GIF 내보내기:** `video/gif-export-dialog.png`는 `VideoExportDialog` 624×1021 DIP, 3×, 1872×3063px입니다. 장면 7 영상 프리뷰, GIF 선택, 960px·10fps 권장 설정, 10MB GitHub 첨부 목표, 제품이 실제 계산한 크기를 표시합니다. 설정은 crop 위쪽, 권장 상태와 크기는 아래쪽에 둡니다. 예상/측정 크기는 실제 산정 결과와 제품 문구를 그대로 쓰며 기존 예제의 229KB를 고정하지 않습니다. 긴 아래 빈 공간과 내보내기 버튼은 crop 밖에 둡니다.
9. **비교:** 창 레이어가 없습니다. `그대로` / `Works the same`, `더해지는 것` / `What MyCapture adds`의 두 열과 검토된 항목만 사용합니다. 열 제목은 44px `#9AA5B5`, 그 아래 흰색 10% 1px 선, 본문은 60px(왼쪽 `#9AA5B5`, 오른쪽 `#F3F5F8`)이며 아이콘·체크표시를 추가하지 않습니다.

## 최종 검수

- [ ] 언어별 manifest의 경로·실제 크기·renderScale과 위 창 크기를 대조합니다. 모든 crop 안에 필요한 기능과 개발 예제가 들어가는지 확인합니다.
- [ ] 실제 4.4.0 ko/en 레이어로 전체 명령을 실행하고, 종료 코드 0과 언어별 본 이미지 9장·연락판·검색 카드를 확인합니다.
- [ ] 표준 출력의 제목 높이와 카드 환산값을 기록하고 7px 기준, 제목 잘림·줄바꿈·보조 문구 간격을 확인합니다.
- [ ] 검색 카드의 현재 이미지와 새 시안을 나란히 열어 제목 가독성을 확인합니다. 연락판과 개별 2560×1440 이미지를 열어 창 크기·그림자·투명 모서리·본문 가독성·필수 정보의 자르기를 확인합니다.
- [ ] 영어 창의 실제 번역, 개인정보 가리기 결과, 핀·카드 단축키, OCR 일치, 라이브러리 검색 근거, 시간 지정 텍스트, GIF 크기 산정을 확인합니다.
- [ ] 장면 9의 기능 차이를 출시 시점 자료로 재검토하고, 최종 앱의 강조색이 이미지당 의미 있는 한 지점에만 나타나는지 확인합니다.
- [ ] 검수 뒤 바꾼 간격·배율은 scenes.json과 STYLE에 반영하고 제출 기록(docs/store/listing.md)에 남깁니다.
