# ADR 0003 — 캡처 전문(全文) 검색: 유일한 락인 후보 구현

상태: 채택 (외부 상용화 검토 대응)
관련: docs/market/voc-and-moat-roadmap.md(아이디어 #5), 외부 검토 §3.2·§5

## 배경

외부 상용화 검토는 MyCapture의 "해자"라 주장된 5가지 중 대부분을 반박했다.
- OCR 자체: `Windows.Media.Ocr` OS API 래퍼 → 고유 자산 아님(PowerToys와 동일 엔진).
- 오프라인 포지셔닝: Windows 11 Snipping Tool도 온디바이스 OCR·녹화 제공 → 단독 논거 약함.

살아남은 **유일한 락인 후보**: "예전에 찍은 화면을, 그 안의 글자로 검색"
(영속 큐 + 갤러리 + 전문 텍스트 검색의 결합). OS도 PowerToys도 제공하지 않는다.

그러나 검토가 지적했듯, 이 기능은 **구현이 미완**이었다: OCR 텍스트는 사용자가 캡처 하나를
수동으로 OCR할 때만 채워졌고, 축적된 큐 전체를 검색 가능하게 만드는 **일괄/백그라운드 색인**이
없었다. 즉 해자가 "기능"이 아니라 "주장"이었다.

## 결정

세 조각을 추가해 이 락인 후보를 실제 기능으로 만든다.

1. **`MyCapture.Core/Queue/CaptureTextSearch.cs` (순수·테스트 가능)**
   - 다중어 AND 검색: 공백 구분 각 단어가 제목·창제목·OCR 텍스트 중 어디든 있어야 매치.
   - 대소문자 무시, 필드 귀속(`CaptureMatchField`)으로 "무엇이 매치됐는지" 설명 가능.
   - `OcrCoverage`(전체/검색가능/미검색 비율) 통계.
   - 기존 갤러리 검색의 순진한 단일어 `Contains`를 대체(`GalleryController`가 이 로직 사용).

2. **`MyCapture.App/Ocr/OcrIndexingService.cs`**
   - 큐 전체에서 OCR 텍스트가 없는 캡처를 순회하며 기존 `IOcrService`로 인식→`CacheOcr` 영속화.
   - 저사양 보호: 한 건씩 백그라운드, `Task.Yield`로 양보, 취소 가능, 진행률 보고.
   - 엔진 부재 시 `Unavailable`로 즉시·명시적 종료(무한 시도 없음).

3. **`MyCapture.Ocr/OcrAvailability.cs` (순수)**
   - 언어 팩 미설치(`IsAvailable=false`) 상태를 **침묵이 아니라** 사용자 메시지로 변환.
   - "언어 팩이 없어 OCR·전문 검색이 불가하며, 나머지 기능은 정상"임을 명시(환불 리스크 완화).

## 성능/오프라인 원칙 유지

- 새 패키지 0개. `Windows.Media.Ocr`(OS 내장)만 사용, 완전 오프라인 유지.
- 색인은 유휴 시 점진적으로만 동작하도록 설계(캡처 단축키와 경쟁하지 않음).

## 테스트

- Core +9 (`CaptureTextSearchTests`): OCR 텍스트 매치·다중어 AND·필드 귀속·커버리지.
- App +6 (`OcrIndexingAndAvailabilityTests`): 가용성 메시지, 엔진 부재/무작업 시 outcome, 커버리지 노출.
- 합계 Core 231 / App 155 / 386, 0 실패. Debug·Release 빌드 성공.

## 미결(후속): UI 트리거 배선

`OcrIndexingService`의 DI 등록과 "N개 미검색 — 지금 색인" 갤러리 버튼 배선은
**다른 세션이 진행 중인 0.4.0 UX 개편(warm-yellow-charcoal)이 `GalleryWindow.xaml.cs`를
수정 중**이라 충돌을 피하기 위해 보류했다. 검색 개선 자체는 `GalleryController`(본인 소유 파일)를
통해 이미 활성화되어 있다. 0.4.0 개편 커밋 후 트리거 배선을 별도 커밋으로 추가한다.

## 근거 상태

- 코드·테스트: 저장소에서 직접 실행/검증(Core 231·App 155, 0 실패).
- 시장 판단: 외부 검토 기반(웹 출처, 시점 변동 가능).
