# Gallery, GitHub clipboard URL, and frame timing corrections

Status: implemented and locally verified; not released. Baseline: 2.1.0 main cc734fcd6c6608ee08553a4715dcf731cdcc2b9f.

- Gallery card edit buttons now display only a centered 36-DIP pencil icon. Click handlers, tooltips and accessibility labels remain intact.
- F4 already attempted image paste and URL extraction, but used browser title guesses, incomplete field names and an arbitrary Edit fallback. It now checks the foreground browser address against the configured issue, targets a recognized comment/new-issue body, pastes once, and accepts only a newly inserted attachment URL. It never submits a comment. Existing upload placeholders and clipboard changes abort the operation. The final URL write compares the original clipboard sequence under the same native clipboard lock. UI Automation runs outside the WPF dispatcher.
- Frame/image/shape layer properties now support source-time start/end input and preserve asset, placement and identity through a cloned layer. Changes use existing undo/document/preview refresh paths.
- Single-frame timeline bars have a minimum 24-DIP visual width and remain inside the right boundary. Rendering and hit testing share geometry; drag changes are relative to original source times so expanded visual width does not cause a jump.

Validation: Release build passed; Core 407 passed; App 744 passed, 1 local symlink-prerequisite skip (745 total). WPF fixture diagnostic passed and the lead inspected the icon-only gallery screenshot. Independent source review found no remaining P1/P2 after pending-upload and clipboard-commit corrections. Evidence: artifacts/validation/edit-controls.

Limitations: no signed-in real-browser GitHub image upload or native clipboard commit was executed. Browser accessibility exposure remains a compatibility dependency. No new tag, remote push, or release was requested or performed for this follow-up.

Delegation record: AGY analyze run 69b0248b-b133-4adf-a2c5-0d1897aba47e was rejected by its wrapper because the lead changed the source checkout during the run. Its result was not accepted as verification. The lead and separate read-only reviewer inspected the actual changes. The unchanged temporary checkout was removed; wrapper logs remain available.
