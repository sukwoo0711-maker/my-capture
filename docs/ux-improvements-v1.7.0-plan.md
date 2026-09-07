# MyCapture 1.7.0 UX improvement boundary

Requested 2026-09-08. Lead: Astra (planning, orchestration, review, integration, release).
Implementation and research are delegated to bounded workers. User authorized Git push and release publication.

## Acceptance map

| # | Outcome | Owner | Primary evidence |
|---|---|---|---|
| 1 | Shape stroke presets and fill transparency, default 100% transparent; 50% uses stroke hue | Annotation worker | Model/render/editor regression and rendered editor |
| 2 | Recording region stays outlined without burning the UI into video | Recording worker | Recording integration/self-test |
| 3 | VS Code rich code remains code; Excel remains a table; commercial benchmark | Pin worker | Clipboard regressions, official-source research |
| 4 | Pins move across virtual desktop including negative coordinates and DPI changes | Pin worker | Geometry/position regression and available-monitor QA |
| 5 | Ctrl+double-click copies text; focused Ctrl+C copies image; no OCR result popup | Pin worker | Gesture/clipboard regression |
| 6 | Recording finalization/conversion has visible progress through completion/failure | Recording worker | Lifecycle tests and UI review |
| 7 | GIF offers smaller output presets | Recording worker | Export/decode tests and editor UI |
| 8 | Text timeline duration can be resized in lower timeline | Recording worker | Timeline regression and UI interaction |
| 9 | App typography, alignment, wrapping, sizing, focus and tone reviewed | UI worker + final QA | Real rendered windows with DPI coverage |
| 10 | Managed gallery captures expire in creation order after seven days; explicit saves preserved | Gallery worker | Retention/path-safety regressions |
| 11 | Titlebar and scrollbars follow current app theme | UI worker | Native/rendered window review |
| 12 | Each capture automatically queues background indexing | Gallery worker | Capture/indexing regression |
| 13 | Library actions: copy, trash, more (pin) | Gallery worker | Gallery action regression and rendered UI |

## Stable constraints

- Preserve the repository's current Focus Portal graphite/cyan design. The attached warm/yellow screenshot predates the current authoritative design system.
- Keep capture/OCR local, retain persisted-data compatibility and explicit user exports, and preserve unrelated changes.
- No new dependencies unless implementation evidence requires one and licensing is documented.
- No skipped or failed gate counts as passing. Publish only a verified clean source commit and matching artifacts.

## Validation and release

Baseline on Windows 11 build 26200, x64, .NET SDK 10.0.400: Release build and 701 tests passed (342 Core, 359 App). Initial sandboxed restore failed at NuGet vulnerability-feed access (NU1900); the authorized network-enabled retry passed without disabling auditing.

Agy read-only run `9fcd0357-1f79-40c3-b6f2-5d8c98104bb4` was rejected by its wrapper as `scope_violation`: the source checkout changed while parallel implementation/branch creation proceeded. No patch was produced or accepted and the failed call was not retried. Implementation remains with the separately scoped Codex workers.

After integration: relevant tests, complete Release suite, real UI inspection, and seven packaged self-tests. Prepare release notes and validation record, push the work branch, follow repository PR/CI workflow, then package and publish version 1.7.0 from the verified integrated commit. Record exact remote actions and artifact provenance in the final validation record.
