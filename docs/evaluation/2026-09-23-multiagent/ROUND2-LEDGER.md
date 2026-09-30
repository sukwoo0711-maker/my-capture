# 라운드 2 소유 대장 (확정 전)

2026-09-23T14:55Z. 여덟 축이 같은 논점에 `합의` 또는 `병합`으로 답했다. 반박으로 닫힌 논점은 없다. 이 문서는 최종 합의문이 아니다. 최종본은 시작 시각 2026-09-23T14:33:56Z에서 3시간이 지난 뒤, 아래 근거를 다시 열고 쓴다.

## 유지하는 점수

| 축 | 점수 | 한 번만 깎는 이유 |
|---|---|---|
| 편의성 | 4 | 녹화 시작 확인이 한 번 더 있음. 완료가 클립보드 성공에 묶임. 창·전체·이전 영역·클릭 통과의 기본 키가 빔. 단축키 칸이 문자열. 편집기 Ctrl+C가 전체를 확정. 자르기는 마우스 업에 적용. |
| 기능 | 4 | 형광펜 도구 없음. 모자이크는 설정 12를 무시하고 14로 굳음. 닫은 핀 복구는 저장만 됨. 영상 프레임 주석은 PNG라 벡터로 다시 열리지 않음. |
| 심미성 | 4 | 출고 마크·글래스 알파·작업 공간 틸·녹화 상태 색이 Focus Portal 위계와 어긋남. 대비 숫자는 포함하지 않음. |
| 접근성 | 3 | 코드 감사. 임의 영역은 포인터만. 주석 기하에 키보드 이동이 없음. M/X 이름이 동작과 다름. OS 고대비가 팔레트까지 내려가지 않음. 내레이터 실측 아님. |
| 성능·안정성 | 3 | 획득은 GDI `BitBlt`뿐. 과거 정적 녹화 실효 FPS는 기본 30 아래. 이번 세션 실측 없음. |
| 신뢰 | 합산 3, 로컬 4, 배포 2 | 평문 색인, `original.png` 잔존, 168시간, 로그인 자동 실행, PP-OCR 첫 수신은 로컬 4 안. Authenticode 부재는 배포 2. |
| 학습성 | 2 | 첫 10분이 C/X/Z·F3·F9를 한 세트로 가르치지 않음. 가이드가 좋아도 올리지 않음. |
| 범위 일관성 | 4 | 로컬 캡처 → 설명 → 무음 MP4/짧은 GIF. 끊긴 표면은 이 4의 증거로 이미 반영. |

## 닫힌 논점

| ID | 결정 | 주 소유 | 다시 깎지 않는 축 |
|---|---|---|---|
| CL-01 | README는 F4, 코드 기본값은 F9 | learnability | trust는 전송만, positioning은 기본 전역 키의 높이만, functionality는 기본값 확인만 |
| CL-02 | 형광펜 없음, 모자이크 블록 14 | functionality | positioning의 4점에 증거로만 있음. convenience 재감점 없음 |
| CL-03 | 닫은 핀 복구는 저장만 됨 | functionality | convenience, positioning |
| CL-04 | GDI만 사용하고 30fps 문서를 못 지킴 | performance | functionality는 “캡처가 된다”만 |
| CL-05 | 커서 링은 세 갈래 | 존재=functionality, 비용 미측정=performance, 프레임에 구워진 색=aesthetics | 한 주장으로 합치지 않음. 존재는 감점이 아님 |
| CL-06 | 앱이 침묵하면 학습성 2 | learnability | 빈 기본 키는 convenience 4와 별개 |
| CL-07 | 로그인 자동 실행 기본 켜짐 | trust | learnability는 창 없는 다음 시작을 설명하지 않는 공백만 |
| CL-08 | Authenticode 없음 | trust 배포 2 | learnability, positioning |
| CL-09 | 검색은 강점. 자동 색인은 `CacheResults` 없이 평문을 씀. 첫 PP-OCR만 네트워크 | 평문·수신=trust. 검색=functionality. 오프라인 문장=positioning이 좁힘 | 같은 사실의 이중 감점 없음 |
| CL-10 | 가리기 뒤 `original.png`는 그대로 | trust | 편집 가능한 가림막은 functionality 강점 |
| CL-11 | 임의 사각형은 포인터만 | accessibility | 창 스냅 부재는 functionality |
| CL-12 | 대비 비율·OS 고대비 미연동 | accessibility | 악센트=경고, 뮤트 위계는 aesthetics |
| CL-13 | GIF 20초는 설명 클립의 범위 | positioning | 기능 감점 아님. 내보내기 문구가 상한을 말하므로 학습성 감점 아님 |
| CL-15 | 이미지 기본 168시간 | trust | convenience |
| CL-16 | 모자이크·자르기 이름과 키가 다름 | accessibility | 즉시 자르기 동작만 convenience. 형광펜 없음과 합치지 않음 |
| POS-06 | GitHub 단축키는 설명 공유의 선택 기능 | positioning이 기본 전역 키의 높이만 | ShareX식 자동 업로드만 범위 밖. 전송 내용은 trust |

좁힌 오프라인 문장: 캡처·주석·핀·기본 Windows OCR·무음 MP4·짧은 GIF는 네트워크 없이 끝난다. PP-OCR 가중치가 없을 때만 첫 실행이 가중치를 받으며, 실패해도 기본 OCR은 남는다.

## 이 시각에 다시 연 근거

- `MosaicBlockSize` 기본값 12 (`AppSettings.cs`). 제스처는 `blockSize: 14`.
- `ClosedWindowRestoreLimit`는 `src/MyCapture.App`의 cs/xaml에 없다.
- `MediaExport_GifLimits`가 GIF 칸에 20초·200프레임을 적는다 (`VideoExportDialog.cs`, `Strings.resx`).
- `build/package.ps1`의 `Unsigned = $true`와 “NOT Authenticode-signed” 안내.
