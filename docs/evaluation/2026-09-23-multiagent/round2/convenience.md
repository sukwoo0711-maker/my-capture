# 편의성 라운드 2

점수 **4 유지**. GIF 20초 상한은 편의 감점이 아니다. C-10은 functionality로 넘기고 편의에서 다시 깎지 않는다.

## 조율 논점

| ID | 판정 | 편의 결론 |
|---|---|---|
| CL-13 | 합의 | 20초는 설명용 규율이다. `G`는 `ExportGif` → `OpenExport(gif: true, autoTrim: true)`이고, `VideoExportDialog.TryAutoTrimForGif`가 끝을 `MaximumDurationMs`(20_000)로 당긴다. 초과 구간은 `MediaExport_TrimGif`(「선택 구간을 20초 이하로」)를 보이고 계산을 끈다. 예외는 트림 없이 내보낼 때의 가드(`AnimatedGifExporter`)다. 라운드 1 §3의 자동 자르기는 강점으로 유지한다. 상한은 내보내기 화면에 있다. 학습성 몫은 그 화면 이전의 최초 안내뿐이다. 편의에 남는 GIF 마찰은 C-12뿐이다. |
| CL-02 | 병합 | C-10의 주 소유를 functionality로 옮긴다. 형광펜·번호·블러·색 뽑기 공백과 `HighlighterAlpha`·`ColorFormat`은 기능 구멍이다. 단계가 길어지는 것은 그 결과라 편의 점수에 넣지 않는다. 모자이크 블록 14(`ApplyMosaic`의 `blockSize: 14`)는 C-10에 없던 사실이고 F-03 소유다. positioning의 재감점은 받지 않는다. |
| CL-06 | 합의 | 편의 4를 유지한다. 근거는 C-02(빈 기본 키), 영역 녹화의 시작 확인 한 번 더, C-01(완료가 클립보드에 묶임)이다. 학습성 2는 첫 10분에 C/X/Z/F3/F9를 가르치지 않는 점수다. C-02는 그 교육 공백과 다른 사실이다. |
| CL-01 | 합의 | README F4 / 코드 F9는 편의 주장이 아니다. 주 소유 learnability. 편의 점수에 없다. |
| CL-03 | 병합 | C-07의 주 소유를 functionality로 옮긴다. F3가 빈 클립보드에서 풍선만 띄우는 여정은 보조 기록으로 남기고 재감점하지 않는다. 빼도 점수는 4다. |
| CL-04 | 합의 | GDI만 쓰는 사실은 performance. 편의는 밀리초를 점수에 넣지 않는다. |
| CL-05 | 합의 | 커서 강조 링은 편의 감점이 아니다. |
| CL-07 | 합의 | 로그인 자동 실행 기본값은 trust. 편의 점수에 없다. |
| CL-08 | 합의 | Authenticode는 trust. 편의 점수에 없다. |
| CL-09 | 합의 | 오프라인 문장·모델 다운로드·OCR 평문 색인은 편의 감점이 아니다. 라이브러리 검색 여정은 그대로 강점이다. |
| CL-10 | 합의 | `original.png` 잔존은 trust. 편의 점수에 없다. |
| CL-11 | 합의 | 자유 영역이 포인터뿐인 점은 accessibility. 창 스냅 부재는 C-09로 functionality. 둘을 한 감점으로 합치지 않는다. |
| CL-12 | 합의 | 대비·고대비는 accessibility / aesthetics. 편의 점수에 없다. |
| CL-15 | 합의 | 168시간은 trust. C-08은 재감점하지 않는다. |
| CL-16 | 병합 | 버튼이 말하는 M/X와 `HandleShortcut`의 불일치(거짓 이름)는 accessibility. 형광펜(CL-02)과 합치지 않는다. 자르기가 마우스 업에 `ApplyCrop`되는 동작만 편의에 남긴다. |

## C-01 ~ C-14

| ID | 판정 | 합의 후 주 소유 | 편의 처리 |
|---|---|---|---|
| C-01 | 합의 | convenience | 유지. 복사 실패를 `Text_06F58809C083`로 보여주는 막힘은 편의. 문구 보조만 learnability. trust T09(항상 복사)와 점수를 나누지 않고, 막힘만 여기서 감점. |
| C-02 | 합의 | convenience | 유지. `CaptureWindow` `CaptureFullScreen` `RepeatLastRegion` `ToggleClickThrough`가 `Hotkey.None`(`AppSettings.cs`). 반복 작업이 트레이로 간다. 학습성의 첫 10분 침묵과 별개. 이 네 키가 점수 4의 한 축이다. |
| C-03 | 합의 | convenience | 유지. 키 캡처가 아닌 문자열 칸, 충돌 시 이전 조합 복귀. |
| C-04 | 합의 | convenience | 유지. 영역 키를 비우면 `SettingsStore`가 `Ctrl+Shift+C`로 되돌린다. trust는 재감점하지 않는다. |
| C-05 | 합의 | convenience | 유지. `HandleShortcut`의 Ctrl+C는 선택 주석이 아니라 전체 확정·닫기. |
| C-06 | 병합 | accessibility + convenience | 이름 M/X는 accessibility(CL-16). 마우스 업 즉시 자르기(`OnSurfaceMouseUp` → `ApplyCrop`)만 convenience. 이름 불일치로 4를 다시 깎지 않는다. |
| C-07 | 병합 | functionality | CL-03. 편의 재감점 없음. |
| C-08 | 합의 | trust | CL-15. 이미 trust 소유. 재감점 없음. |
| C-09 | 합의 | functionality | 창 스냅 부재는 functionality(CL-11). 창 캡처의 빈 기본 키는 C-02에 이미 포함. 두 번 깎지 않는다. |
| C-10 | 병합 | functionality | CL-02. 편의 재감점 없음. 색 뽑기·`ColorFormat`도 이 병합에 포함. |
| C-11 | 합의 | positioning | 캡처 후 작업 사슬의 범위. 업로드 부재는 감점하지 않는다. |
| C-12 | 합의 | convenience | 유지. 계산 뒤 다른 이름 저장(`VideoExportDialog`). 20초 상한(CL-13)과 분리. |
| C-13 | 합의 | performance | 녹화 선택 100ms의 수치. 편의는 소유하지 않는다. |
| C-14 | 합의 | learnability | 언어는 다음 실행. LRN-13과 같다. |

## 점수 4에 남는 마찰

| 순위 | 사실 | 근거 |
|---|---|---|
| P0 | 영역 녹화는 영역을 놓은 뒤 시작 확인이 한 번 더 있다 | `RecordingControlWindow`, `RegionRecordingCoordinator.Toggle` |
| P0 | 완료·빠른 저장이 클립보드 성공에 닫힘을 건다 | C-01, `CaptureCommitService.CommitAsync` |
| P1 | 창·전체·이전 영역·클릭 통과 기본 키가 비어 있다 | C-02 |
| P1 | 단축키는 문자열 칸이고, 영역 키는 비울 수 없다 | C-03, C-04 |
| P1 | 편집기 Ctrl+C가 편집을 끝낸다. 자르기는 마우스 업에 적용된다 | C-05, C-06의 동작만 |

빠지는 채점: C-07, C-08, C-09의 스냅, C-10, C-11, C-13, C-14, C-06의 거짓 이름, GIF 20초.
