# MyCapture

개발자를 위한 무료 Windows 캡처·핀 도구입니다.

<img src="docs/media/readme-demo-ko.gif" width="720" alt="코드 위에 뜬 캡처 카드, Ctrl+Shift+R로 이메일·토큰·카드번호를 가린 오류 보고, 코드 옆에 핀으로 띄운 API 설명이 차례로 나오는 장면">

<a href="https://apps.microsoft.com/detail/9PB33MC095VR?cid=gh-readme"><img src="https://get.microsoft.com/images/ko%20dark.svg" width="200" alt="Microsoft Store에서 받기"></a>

광고·계정·캡처 횟수 제한이 없습니다. 캡처와 글자 인식은 이 PC 안에서 처리합니다. Windows 11과 Windows 10 버전 22H2(x64)에서 실행하며, ARM64 PC에서는 Windows 11이 필요합니다. 소개 페이지는 <https://sukwoo0711-maker.github.io/my-capture/>입니다.

## 기본 캡처 도구와 다른 점

**개인정보 가리기.** 편집기에서 `Ctrl+Shift+R`을 누르면 화면 속 이메일 주소, 카드번호, IP 주소, 한국 전화번호와 주민등록번호 형태의 숫자, GitHub 토큰·AWS 액세스 키·`sk-`로 시작하는 API 키를 찾아 가림막을 씌웁니다. 가림막은 저장하기 전에 옮기거나 지울 수 있습니다.

**다른 창 위에 띄우는 핀.** 캡처 직후 카드에서 `P`를 누르거나, 클립보드의 이미지·코드·표를 설정에서 지정한 핀 단축키(예: `F3`)로 띄우면 다른 창보다 항상 위에 있는 작은 창이 됩니다. 문의 내용을 보면서 코드를 고치거나, 디자인 시안을 띄워 둔 채 화면을 맞출 때 씁니다.

**창 제목과 글자로 찾는 라이브러리.** 캡처할 때 창 제목을 함께 저장하고, 이미지 속 글자는 이 PC에서 인식해 둡니다. 파일 이름이나 오류 메시지의 한 구절로 캡처를 찾을 수 있고, 녹화한 MP4도 같은 라이브러리에서 재생합니다.

## 기본 단축키

| 동작 | 새 설치 기본값 | 비고 |
|---|---|---|
| 영역 캡처 | `Ctrl+Shift+C` | 4.4.0부터 캡처 직후 작은 카드가 뜹니다. 카드를 클릭하거나 `Alt+Q`를 누른 뒤 `P` 핀, `E` 편집, `C` 복사, `S` 저장, `T` 텍스트 복사 |
| 영역 녹화 시작/중지 | `Ctrl+Shift+X` | 같은 키로 녹화 종료 |
| 클립보드 이미지·텍스트·표를 핀으로 띄우기 | 설정에서 지정(예: `F3`) | PNG 투명도와 텍스트/표 원문을 보존. GitHub 첨부 URL을 만든 뒤에는 추출한 GitHub 이미지 URL을 핀으로 띄움 |
| 라이브러리 열기 | 설정에서 지정(예: `Ctrl+Shift+Z`) | 이미지와 동영상을 함께 조회하고 동영상을 창 안에서 재생 |
| 핀 모두 숨기기/보이기 | 설정에서 지정(예: `Shift+F3`) | 일괄 전환 |
| 클립보드 이미지를 GitHub 첨부 URL로 | 설정에서 지정(예: `F9`) | 설정한 이슈 Write 칸에 붙여 넣고 user-attachments URL을 복사 |
| 편집기 개인정보 가리기 | `Ctrl+Shift+R` | 편집기 안에서. 이 PC에서 인식한 개인정보 후보를 편집 가능한 가림막으로 추가 |
| 편집기 빠른 저장 | `Ctrl+S` | 편집기 안에서. PNG 저장 후 편집된 이미지를 클립보드에 복사 |
| 편집기 다른 이름으로 저장 | `Ctrl+Shift+S` | 편집기 안에서. PNG 저장 후 편집된 이미지를 클립보드에 복사 |
| 핀 이미지 빠른 저장 | `Ctrl+S` | 핀에 포커스가 있을 때 원본 PNG 저장 |
| 핀 이미지 다른 이름으로 저장 | `Ctrl+Shift+S` | 오른쪽 클릭 메뉴에서도 실행 가능 |

새로 설치하면 전역 단축키는 영역 캡처와 영역 녹화 두 개만 정해져 있습니다. `F3`은 파일 탐색기의 검색 키이고 `Ctrl+Shift+Z`는 여러 편집기의 다시 실행 키라서, 핀으로 띄우기·라이브러리 열기·핀 모두 숨기기·GitHub 첨부는 필요한 사람이 설정에서 지정합니다. 이전 버전부터 쓰던 설정은 예전 키(`F3`, `Shift+F3`, `F9`, `Ctrl+Shift+Z`)를 그대로 유지합니다.

단축키는 설정에서 바꿀 수 있습니다. 그 밖의 전역 단축키, 창 제목 기록, 빠른 저장 파일명 규칙은 [기능과 화면](docs/features.md#기본-단축키)에 정리했습니다.

## 문서

- [사용법 가이드](docs/guides/README.md): 기능별 사용법과 녹화 → GIF 내보내기
- [기능과 화면](docs/features.md): 전체 기능 목록과 화면
- [설치와 배포](docs/install.md): Store 설치본과 GitHub 릴리스의 설치 파일·포터블 ZIP, `SHA256SUMS.txt` 확인, 설치 로그와 종료 코드, 사용자 데이터 위치, 개인정보와 네트워크
- [소스에서 빌드](docs/build.md): .NET 10 SDK 준비, 빌드와 테스트, 자체 진단(self-test)
- [4.4.0 릴리스 노트](docs/releases/4.4.0-release-notes.md)와 [이전 릴리스 노트](docs/releases/)
- [지원](https://github.com/sukwoo0711-maker/mycapture-support)과 [개인정보처리방침](https://github.com/sukwoo0711-maker/mycapture-support/blob/main/PRIVACY.md)

보안 문제는 공개 이슈에 재현 자료를 올리지 말고 [`SECURITY.md`](SECURITY.md)의 비공개 신고 절차를 따르십시오. 개발 참여 방법은 [`CONTRIBUTING.md`](CONTRIBUTING.md)를 참고하십시오.

## 라이선스

MyCapture 자체 코드에는 [MIT License](LICENSE)가 적용됩니다. 배포 패키지에 포함되는 .NET/WPF 런타임과 기타 구성요소는 각각의 라이선스를 따르며, 자세한 출처와 배포 고지는 [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md)에서 확인할 수 있습니다.

---

## English

A free Windows screenshot and pinning tool for developers.

<img src="docs/media/readme-demo-en.gif" width="720" alt="The capture card over code, a bug report with its email, token and card number covered by Ctrl+Shift+R, and an API note pinned next to the code">

<a href="https://apps.microsoft.com/detail/9PB33MC095VR?cid=gh-readme"><img src="https://get.microsoft.com/images/en-us%20dark.svg" width="200" alt="Get it from Microsoft Store"></a>

No ads, no account and no capture limits. Capture and text recognition run on this PC. MyCapture runs on Windows 11 and Windows 10 version 22H2 (x64); ARM64 PCs need Windows 11. Website: <https://sukwoo0711-maker.github.io/my-capture/en/>

### What it adds to Snipping Tool

**Redaction.** Press `Ctrl+Shift+R` in the editor and Auto-redact covers email addresses, card numbers, IP addresses, Korean phone and resident registration numbers, and secrets such as GitHub tokens, AWS access keys and API keys that start with `sk-`. You can move or delete each cover before you save.

**Pins that stay on top.** Press `P` on the card that appears after a capture, or pin the image, code or table on your clipboard with a shortcut you assign in Settings (for example `F3`). A pin is a small window that stays above everything else. Keep a bug report in view while you fix the code, or a design mockup while you match the layout.

**A library you search by window title and text.** Each capture keeps the title of the window it came from, and the text in the image is recognized on this PC. Find a capture by a file name or a phrase from an error message; screen recordings play in the same library.

### Default shortcuts

| Action | Default on a new install |
|---|---|
| Region capture | `Ctrl+Shift+C` (from 4.4.0, a small card appears after the capture; click it or press `Alt+Q`, then `P` pin, `E` edit, `C` copy, `S` save, `T` copy text) |
| Start or stop region recording | `Ctrl+Shift+X` |
| Pin the clipboard image, text or table | Assign in Settings (for example `F3`) |
| Open the library | Assign in Settings (for example `Ctrl+Shift+Z`) |
| Hide or show all pins | Assign in Settings (for example `Shift+F3`) |
| Turn the clipboard image into a GitHub attachment URL | Assign in Settings (for example `F9`) |
| Redact personal data (in the editor) | `Ctrl+Shift+R` |
| Save the editor image or the focused pin | `Ctrl+S` |
| Save as | `Ctrl+Shift+S` |

A new install sets only the two global shortcuts for region capture and recording, because `F3` is File Explorer's search key and `Ctrl+Shift+Z` is redo in many editors. Settings from earlier versions keep their old keys.

All shortcuts can be changed in Settings.

### Documents

Most documents are in Korean. [Install and release files](docs/install.md#english-summary) ends with an English summary. Support and the privacy policy are in the [support repository](https://github.com/sukwoo0711-maker/mycapture-support#mycapture-support) ([privacy policy](https://github.com/sukwoo0711-maker/mycapture-support/blob/main/PRIVACY.md#mycapture-privacy-policy)). Report security issues as described in [`SECURITY.md`](SECURITY.md). MyCapture is licensed under the [MIT License](LICENSE).
