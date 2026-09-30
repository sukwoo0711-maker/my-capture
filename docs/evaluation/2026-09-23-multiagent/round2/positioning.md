# 라운드 2 — positioning

대상: Release 3.0.0, 커밋 `117b0ef`. 작성일: 2026-09-23.
이 문서는 범위 일관성만 닫는다. 일관성 점수는 **4/5를 유지**한다. 아래 어느 항목도 그 점수를 다시 깎거나 올리기 위한 두 번째 감점이 아니다.

가격·점유율·매출은 쓰지 않는다.

## 판정

| ID | 판정 | 결론 | 주 소유 | positioning이 유지하는 것 |
|---|---|---|---|---|
| CL-13 | 합의 | GIF 20초는 설명 클립의 범위 규율이다. ScreenToGif 대비 기능 감점이 아니다. | positioning (규율의 의미). 상한 값의 동작 확인은 functionality가 사실로만 보유 | 포지션 문장의 「짧은 GIF」. 점수 영향 없음 |
| CL-02 | 합의 | 형광펜·모자이크 블록 14를 positioning 점수에서 한 번 더 깎지 않는다. | functionality (F-04, F-03) | 일관성 4의 결손 1점 안에 이미 들어 있는 끊긴 표면의 증거 |
| CL-03 | 합의 | 닫은 핀 복구도 같은 규칙이다. positioning 재감점 없음. | functionality (F-07) | 위와 같은 증거 목록의 한 칸 |
| CL-09 | 합의 | 「항상 오프라인」을 아래 한 문장으로 좁힌다. 네트워크 사실의 감점은 trust, 검색 강점과 엔진 폴백은 functionality. | trust (T02 다운로드, T04 평문). functionality (F-10 검색, F-16 폴백) | 좁힌 문장. 추가 감점 없음 |
| CL-08 | 합의 | Authenticode는 trust 전용이다. | trust (T16, 배포 신뢰 2) | 포지션 문장에 게시자 신뢰를 넣지 않는다는 제한 |
| POS-06 | 병합 | ShareX식 자동 업로드 파이프라인만 범위 밖이다. GitHub 단축키는 설명 공유의 선택 기능으로 남긴다. | 표면이 로컬 설명 밖이라는 점만 positioning. 키 이름 불일치는 learnability (CL-01). 전송 안전은 trust (T11–T14). 기본값 확인은 functionality (F-13) | 기본 전역 키로 올라와 있다는 기존 증거. 추가 감점 없음 |

## CL-13 — GIF 20초

합의한다. 20초 상한은 기능 미완이 아니라 설명용 GIF의 규율이다.

`AnimatedGifExporter`는 `MaximumDurationMs = 20_000`을 두고, 주석이 그 상한을 CPU·파일 크기·팔레트 작업을 예측 가능하게 두는 제품 상한이라고 적는다. README 기능 목록과 `docs/guides/README.md`도 최대 20초를 기능 설명으로 적는다. 내보내기 대화는 구간이 상한을 넘으면 상태 문구를 바꾸고, `VideoEditorWindow`가 `TryAutoTrimForGif`로 끝점을 20초로 당긴다. 상한을 숨기지 않으므로, 「숨기면 학습성이 소유한다」는 조건은 이번 트리에서 성립하지 않는다.

ScreenToGif의 긴 프레임 편집·APNG는 라운드 1에서 범위 밖으로 둔 폭이다. 그 폭이 없다고 기능 점수를 깎지 않는다. F-12(20초를 넘기면 예외)는 동작 사실로 남기고, F-01의 감점 목록에서는 「GIF 20초」를 뺀다. F-01에 남는 형광펜·번호·블러·핀 복구·영상 주석의 PNG화는 functionality의 기능 감점이다.

## CL-02 · CL-03 — 형광펜·핀 복구와 일관성 4

합의한다. 형광펜·핀 복구를 positioning 점수에서 한 번 더 깎지 않는다.

일관성 4의 결손 1점은 이미 「끊긴 표면의 증거」로만 반영되어 있다. 라운드 1은 기능 완성도를 이 축의 점수로 쓰지 않았고, 깎인 1점을 설정·단축키·기본 URL까지 끌어오고 도구를 끝내지 않은 자리에 두었다. 그 목록은 형광펜과 핀만이 아니다.

| 표면 | 코드 | 기능 감점의 주 소유 | 4점 안의 위치 |
|---|---|---|---|
| 형광펜 | `EditorTool`에 형광펜 없음. `BeginDraftGesture`의 `new PenAnnotation`은 `IsHighlighter`를 켜지 않음. 설정 `HighlighterAlpha`는 있음 | functionality F-04 | 이미 포함. 추가 감점 없음 |
| 모자이크 블록 | 도구 `EditorTool.Mosaic`는 있음. 설정 기본 12, 마우스 업은 `blockSize: 14` | functionality F-03 | 이미 포함. CL-02의 나머지 |
| 닫은 핀 복구 | `ClosedWindowRestoreLimit`는 설정 모델에만 있음. `src/MyCapture.App/Pinning/`은 이 값을 읽지 않음 | functionality F-07 (CL-03) | 이미 포함. 추가 감점 없음 |
| 색 형식 | `ColorFormat`은 보존만 됨 | functionality F-14 | 이미 포함 |
| GitHub 기본 전역 키 | 아래 POS-06 | 표면의 높이만 positioning | 이미 포함 |

그래서 4는 기능 점수의 복제가 아니다. 같은 사실을 기능 축이 다시 감점하는 것과 별도로, 범위 문장과 표면이 어긋난 증거가 일관성에서 한 번 세어진 상태다. 주 소유는 functionality로 넘긴다. positioning은 4를 유지하고, 이 표면을 이유로 3으로 내리거나 5로 올리지 않는다.

POS-03·POS-04의 라운드 1 「주 소유 positioning」은 철회한다. 인용 자격만 남긴다.

## CL-09 — 「항상 오프라인」을 한 문장으로

합의한다. 라운드 1 포지션 문장과 README 설치 절의 「실행 중 인터넷 연결도 요구하지 않습니다」를 이 한 문장으로 바꾼다.

계정 없이 PC 안에서 화면을 찍어 주석·핀과 기본 Windows OCR로 설명하고 무음 MP4와 짧은 GIF로 남기며, PP-OCR 가중치가 아직 없으면 첫 실행이 modelscope.cn에서 그 가중치만 받고 실패해도 캡처와 기본 OCR은 그대로 동작한다.

문장이 코드와 맞는 지점만 적는다.

| 사실 | 근거 | 점수 |
|---|---|---|
| 기본 OCR 품질은 Fast이고, Fast는 `Windows.Media.Ocr`이다. Accurate·Enhanced만 신경망을 쓴다 | `OcrSettings.Quality` 기본값, `OcrQualityProfile.UseNeuralModel` | 문장에 포함. 감점 없음 |
| 시작 시 `StartOcrModelDownload` → `EnsureAsync`. 필수 가중치가 있으면 받지 않고, 없으면 `OcrModelCatalog.Required`를 modelscope.cn에서 받는다 | `App.xaml.cs`, `OcrModelStore.EnsureAsync`, `OcrModelCatalog` | trust T02가 네트워크 표면으로 이미 감점. positioning 재감점 없음 |
| 다운로드 실패는 트레이를 죽이지 않고 경고 후 반환한다 | `StartOcrModelDownload`의 catch, `EnsureAsync`의 네트워크 예외 처리 | 문장의 「실패해도 동작」. 감점 없음 |
| 캡처 본문을 그 요청에 실어 보내지 않는다 | trust T02 | trust 소유. 이 문장은 네트워크 필요 여부만 말한다 |
| 자동 색인이 `CacheResults == false`여도 평문을 `index.json`·`OcrText`에 쓴다 | trust T04 | trust 감점. 오프라인 문장에 넣지 않음 |
| 검색이 이미지 색인까지 닫혀 있다 | functionality F-10 | functionality의 강점. positioning 재채점 없음 |
| 모델이 없으면 Accurate/Enhanced가 Windows OCR로 떨어진다 | functionality F-16 | functionality의 사실. 「항상 같은 엔진」을 positioning이 약속하지 않음 |
| Real-ESRGAN은 Enhanced의 첫 인식에서만 받는다 | trust T03 | 이 한 문장 밖. trust |

self-contained 패키지와 .NET 선설치 불필요는 그대로 강점이다. 그 문장은 런타임 네트워크가 없다는 뜻이 아니다.

## CL-08 — Authenticode

합의한다. 배포 파일의 미서명은 trust 전용이다.

`build/package.ps1`는 `Unsigned = $true`이고, README와 `SECURITY.md`는 SHA-256이 게시자 신원이 아니라고 적는다. trust는 이 사실을 배포 신뢰 2(T16)에 이미 넣었다. positioning 일관성 4의 이유 목록에는 Authenticode가 없다.

POS-07은 그대로 둔다. 업데이트 부재는 거짓이고, 설정 확인과 SHA-256 뒤 설치는 구현되어 있다. positioning이 포지션 문장에 더하는 제한은 「게시자 신뢰를 문장에 넣지 않는다」뿐이다. 라운드 1 경쟁 맵에서 Windows 캡처 도구의 열세로 적은 「알 수 없는 게시자」는 포지션 감점에서 뺀다. 설치 순간에 그 경고를 설명하지 않는 공백은 learnability(LRN-17)다.

## POS-06 — GitHub 단축키

병합한다. 범위 밖으로 치우지 않고, 설명 공유의 선택 기능으로 남긴다.

범위 밖은 구현하지 않은 ShareX식 캡처 후 자동 업로드·임의 실행 파일·클라우드 목적지다. 그 폭이 없는 것은 포지션과 맞고 감점하지 않는다.

GitHub 이미지 단축키는 그 폭이 아니다. 코드에 전역 명령, 설정 칸, 기본 이슈 URL이 있다. 캡처가 끝날 때 전송하지 않고, 사용자가 `UploadGitHubImage`를 누를 때만 `HandleGitHubImageUpload`가 열린다. 설명은 로컬에서 끝나고, 이 키는 그 다음의 선택 출구다. 기능을 제품 정의에서 지우라는 라운드 1 문장은 여기서 거둔다.

선택 기능이어도 기본 표면은 높다. 코드 기본키는 F9이고 비어 있으면 다시 F9로 채우며, 빈 이슈 URL은 `https://github.com/sukwoo0711-maker/my-capture/issues/new`다. 이 높이는 일관성 4의 증거에 이미 들어 있다. 다시 깎지 않는다.

| 조각 | 주 소유 |
|---|---|
| 로컬 설명 문장 밖의 목적지가 기본 전역 키라는 점 | positioning (기존 4점의 증거) |
| README F4와 코드 F9의 불일치 | learnability (CL-01, LRN-10) |
| 코드 기본값이 F9이고 순정 F4만 이주한다는 확인 | functionality (F-13) |
| 사용자 누름 전송, 토큰, 브라우저 붙여넣기, DPAPI, 두 번째 URL | trust (T11–T14, T19) |

T19와 맞춘다. 이 키가 선택 전송이라는 목적 차이는 positioning이 가지고, 그 전송이 실제로 무엇을 어디로 보내는지는 trust가 가진다. ShareX 업로드 자동화가 없다는 사실만으로 신뢰 우위를 주지 않는다.

## 점수

| 항목 | 라운드 1 | 라운드 2 |
|---|---|---|
| 일관성 | 4/5 | 4/5 유지 |
| GIF 20초 | 포지션과 일치 | 기능 감점에서 제외 |
| 형광펜·모자이크 14·핀 복구·색 형식 | 4의 증거 | 증거로 유지, 재감점 없음, 기능 주 소유는 functionality |
| 오프라인 | 패키지·매니페스트 수준 | 위의 한 문장. 재감점 없음 |
| Authenticode | trust에 위임 | trust 전용 유지 |
| GitHub 단축키 | 정의 밖으로 내리라는 제안 | 설명 공유의 선택 기능. 파이프라인만 범위 밖 |
