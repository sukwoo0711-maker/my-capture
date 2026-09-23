# 라운드 1 겹침 제안 (합의 전)

작성: 진행 조율. 시각 2026-09-23T14:50Z 전후. 이것은 합의문이 아니다. 라운드 2가 각 항목을 `합의` / `병합` / `반박`으로 닫는다.

조율자가 코드를 다시 연 사실 (3.0.0 트리):

- GitHub 이미지 단축키 기본값은 `Hotkey.VkF9` (`AppSettings.cs`). 순정 F4만 F9로 옮긴다 (`SettingsStore.cs`). README 단축키 표는 아직 F4다.
- `EditorTool`에 형광펜이 없다. `IsHighlighter = true`는 테스트와 복사 생성자뿐이고, 렌더러는 플래그가 켜진 펜의 알파를 110 이하로 줄인다. 설정 창에는 `HighlighterAlpha` 입력칸이 있다.
- 모자이크 도구는 `EditorTool.Mosaic`로 있다. 마우스 업은 `blockSize: 14`를 넘긴다. 설정 칸 `MosaicBlockSize`는 XAML에 있다.
- `ClosedWindowRestoreLimit`는 설정 모델·초안·클램프에만 있고 설정 XAML과 `Pinning/` 동작 코드에는 없다.
- `IsFirstRun`은 `SettingsStore`가 저장한다. `src/MyCapture.App`에서는 검색되지 않는다.
- 정지·녹화 획득은 `BitBlt`다. `ScreenCaptureEngine` 주석은 Desktop Duplication을 쓰지 않는다고 적는다.

## 반드시 닫을 논점

| ID | 논점 | 조율 제안 | 반대하면 반박에 코드를 댈 것 |
|---|---|---|---|
| CL-13 | GIF 20초 상한 | 설명용 범위의 규율이다. 기능 점수에서 ScreenToGif 대비 감점하지 않는다. 상한을 제품이 숨기면 학습성이 소유한다. | functionality, positioning, learnability |
| CL-09 | 완전 오프라인 vs PP-OCR 가중치 다운로드, OCR 평문 색인 | 검색 기능은 functionality의 강점. 자동 색인이 `CacheResults == false`여도 평문을 쓰는 것은 trust 감점. 첫 실행 모델 다운로드는 trust의 네트워크 표면이고, positioning은 “항상 오프라인” 문장을 좁힌다. 같은 사실을 두 번 감점하지 않는다. | trust, functionality, positioning |
| CL-02 | 형광펜 없음·모자이크 블록 14 | 빠진 도구와 어긋난 설정의 주 소유는 functionality. positioning은 범위가 중간에 끊긴 증거로만 인용하고 합산 점수에서 한 번 더 깎지 않는다. convenience의 C-10은 functionality로 병합. | functionality, positioning, convenience |
| CL-04 | GDI만 사용 | 주 소유는 performance. 기능 점수는 “캡처가 된다”로 유지한다. | performance, functionality |
| CL-06 | 점수 균형 | 학습성 2는 문서가 아니라 첫 10분 앱 침묵에 대한 점수다. 가이드 품질로 상향하지 않는다. 편의성 4는 유지하되 C-02(빈 기본 키)는 학습성과 별개다. | learnability, convenience |
| CL-08 | Authenticode | 주 소유 trust의 배포 신뢰 2. 다른 축은 재감점 금지. | trust, learnability, positioning |

## 제안된 단일 소유

| 묶음 | 사실 하나 | 주 소유 | 보조 축은 |
|---|---|---|---|
| CL-01 | README F4 / 코드 F9 | learnability (불일치) | trust는 전송 내용만, positioning은 단축키가 로컬 설명 밖인 점만, functionality는 코드 기본값 확인만 |
| CL-03 | 닫은 핀 복구가 저장만 됨 | functionality | convenience·positioning은 재감점 안 함 |
| CL-05 | 커서 강조 링 | 존재=functionality, 비용 미측정=performance, 토큰 밖 색이 프레임에 구워짐=aesthetics | 세 주장을 하나로 합치지 말 것 |
| CL-07 | 로그인 자동 실행 기본값 | trust | learnability는 창 없이 다시 켜지는 설명 공백만 |
| CL-10 | 가리기 뒤 `original.png` 잔존 | trust | 편집 가능한 가림막 자체는 functionality 강점 |
| CL-11 | 자유 영역은 포인터만 | accessibility | 창 스냅 부재(C-09)는 별개, 주 소유 functionality |
| CL-12 | 대비·고대비 | 측정된 텍스트 대비와 OS 고대비 미연동=accessibility. 악센트=경고인 앱 고대비, 뮤트 위계=aesthetics | 이중 감점 금지 |
| CL-15 | 이미지 168시간 보존 | trust | convenience는 재감점 안 함 |
| CL-16 | 단축키 안내 M/X와 `HandleShortcut` 불일치 | accessibility (거짓 이름) | 형광펜 부재(CL-02)와 합치지 말 것. learnability는 같은 사실의 보조 |

라운드 2 산출은 `round2/<페르소나>.md` 하나만. git checkout, commit, push는 하지 않는다.
