# MyCapture 1.9.0 UX completion

Status: in progress. Lead: Astra (`/root`). Baseline: clean `00827d3fd4ba1d89945b3342db045ba7e68f11ae`, after the verified 1.8.4 release. Earlier 1.8.1 work is already merged; do not republish it or overwrite later fixes.

## Outcome and boundaries

- Notify once when the normal resident tray application is ready. Show `MyCapture-{actual version}` in window titles and a discoverable version surface; the user's `0.10.0` is a formatting example, not a version downgrade.
- Pressing the region-capture shortcut while its previous still-image editor is open closes that owned editor and starts a fresh capture. Preserve the recorded original, unrelated library/video windows, cancellation, recording exclusion and one active capture. Verify first capture separately from warm repetition.
- Preserve library virtualization while enabling plain/Ctrl/Shift selection, inclusive ranges, date selection, visible capture time, selected deletion and external-folder copy drag. Clicking total count/size offers a clearly scoped, explicitly confirmed empty-library action, including videos; do not delete real user data during validation.
- Restore an obvious GIF export action and expose percentage-based size/quality controls for image and video export. Explain the actual control semantics and report measured output size; do not promise an exact file-size ratio that the encoder cannot guarantee.
- Rework the video editor's command hierarchy, preview, timeline and export surfaces using the existing Focus Portal tokens and verified licensed external assets. Keep offline operation, Korean/English, keyboard alternatives and minimum-size layout.

Out of scope: unrelated repository changes, replacing existing capture/recording pipelines without measured need, live-user installation or library cleanup during tests, and new runtime services or billing changes.

## Ownership and common contracts

The lead owns this status record, integration, acceptance, version metadata, commits/push coordination and release. Implementation and independent review use separate people/contexts. Independent writers use exclusive worktrees and path boundaries; no worker creates child agents or changes remote state.

- `update_completion`: shell startup, actual-version identity, capture-editor retake; exclusive `shell-retake-v1.9.0` worktree.
- Antigravity Analyze: completed library contract audit at the baseline (run `fd3db9d1-aabb-4658-b54b-dc724d32403a`); no edits. The lead rejected its proposed `Copy | Move` allowed effects because live queue video files must not be moved by the shell. Multi-file drag retains copy-only semantics and requires gesture verification.
- `resume_audit`: library selection, gestures, date headers, visible time, batch removal; exclusive `library-selection-v1.9.0` worktree.
- `video_completion`: accepted video design/research followed by media export implementation; exclusive `video-export-v1.9.0` worktree. No Gallery/App/capture edits.
- Shared localized resource additions use `ShellRetake_*`, `LibrarySelection_*` and `MediaExport_*` prefixes. The lead resolves append-only resource merges. Only the shell writer may modify App.xaml.cs; library changes needing that hook must first agree a narrow contract.

Shared identity: `MyCapture.Core.Platform.AppIdentity.Version`, `.Label`, `.FormatWindowTitle(context)` and `WindowIdentity.Attach(Window)`. Version is derived from compiled metadata. Contextual titles remain identifiable.

Media contract: persistent Export action, MP4/GIF format settings, current timeline and compositor retained, actual same-interval/same-edits standard-encode size as reduction baseline. Percentage calculation is explicit and cancelable; measured result may miss its target and must report that honestly. GIF retains its established 20-second/200-frame/960px limits. Selected Fluent Regular 20 SVGs must be pinned, licensed and visibly used as filled WPF geometry. The existing local stroke icon family is not reattributed.

## Verification and release gates

Primary checks: focused behavioral tests for retake/selection/export boundaries; actual rendered UI review in Korean and English at normal and compact/high-DPI sizes; a first-capture diagnostic; full Release tests; independent code review; exact-head CI and CodeQL; clean-main package validation with all seven native diagnostics and installer/update lifecycle gates. Source changes invalidate affected evidence; skipped/failed/pending results are not passes.

Only one native capture/window diagnostic runs on the workstation at a time. Synthetic fixture data and owned artifact directories isolate testing from the resident app and user library. Final release assets must match clean main, tag, manifest and checksums. User authorization already includes GitHub push and release; no duplicate approval step is added.

## Progress

- Prepared: verified current main and public release v1.8.4, created isolated integration/shell worktrees, accepted title API boundary.
- In progress: three isolated writers under accepted independent path contracts. Shell writer has the sole native-window fixture lease for focused retake/title tests; other writers must request it before such tests.
- Pending: remaining implementation, independent review, integrated verification, push/merge and release 1.9.0.
