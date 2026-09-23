# 조율 메모 (합의 전)

- 2026-09-23T14:49Z: `OcrIndexingService.IndexMissingCoreAsync`는 `OcrSettings`로 인식 요청만 만들고, `CacheResults`를 보지 않은 채 `GalleryController.CacheOcrAsync`로 `OcrText`를 쓴다. 수동 경로 `GalleryWindow.xaml.cs` 866행만 `settings.CacheResults`를 본다. T04는 코드와 맞다.
- GIF 20초는 `AnimatedGifExporter.MaximumDurationMs`이고, `VideoExportDialog`는 초과 구간에 `MediaExport_TrimGif`를 보여주고 계산 버튼을 끄며, 적용 시 트림 끝을 20초로 당긴다. 상한은 내보내기 순간에 숨겨져 있지 않다. CL-13에서 학습성 감점은 “최초 안내 부재”로 한정하고, 내보내기 화면의 문구 부재라고 쓰면 안 된다.
- 3시간 게이트 구독 `evaluation-3h-gate`는 시작 14:33:56Z 기준 약 9837초 뒤 1회.
