# Clipboard code and floating image behavior

Reviewed 2026-09-08 against vendor documentation. This comparison describes documented behavior; no commercial application was installed or licensed as part of the review.

| Product | Official evidence | Application to MyCapture |
| --- | --- | --- |
| Paste (commercial clipboard manager) | [What Paste captures](https://pasteapp.io/help/what-paste-captures) says formatting is preserved by default; [Paste as Plain Text](https://pasteapp.io/help/paste-as-plain-text) makes formatting removal an explicit choice. | Keep the original Unicode source independently from its visual preview. Preserve supported editor colors rather than guessing a table from tabs. |
| Snagit (commercial capture tool) | [Hotkeys](https://www.techsmith.com/learn/tutorials/snagit/snagit-hotkeys/) documents Copy and Grab Text as separate actions; [text extraction](https://www.techsmith.com/blog/extract-text-from-image/) describes extracting text from captured images. | Separate image copying from text extraction. Ctrl+C copies the image; Ctrl+double-click copies original text or recognized text without opening a result editor. |
| Snipaste (floating capture reference, with Pro edition) | [Product](https://www.snipaste.com/index.html) and [FAQ](https://www.snipaste.com/faq.html) describe converting clipboard text into topmost floating image windows. | Retain the floating bitmap interaction model even when its original payload is text. |

## Implemented decision

Editor HTML with `pre`, `code`, or preformatted whitespace is treated as code before any tab heuristic. Safe, bounded editor markup preserves hexadecimal foreground/background colors and uses a monospace font. The renderer is WPF text drawing, with no browser, script execution, network requests, external resource loading, or dependency addition. XML DTDs and entity resolution are prohibited. Unsupported, malformed, oversized, or text-mismatched markup falls back to a bounded plain-text card. Exact original Unicode remains available for text copying; previews can be truncated.

Explicit HTML tables still accept empty leading spreadsheet cells. Plain TSV retains table rendering when rows have consistent tab-separated columns and no leading indentation. Ambiguous plain text without producer metadata cannot always be identified perfectly; the rule deliberately favors code for leading tabs. HTML from VS Code is the strongest available visual signal. Rich text outside the supported subset and arbitrary CSS are not promised to reproduce exactly.

Floating-window movement now uses physical cursor, window, and virtual desktop coordinates together, including negative monitor origins. WPF DIP values are used for local layout and keyboard step conversion, avoiding a primary-monitor DIP clamp at a mixed-DPI monitor boundary.

## Verification

Regression coverage includes indented code versus TSV/HTML tables, editor background/token pixels, malformed/DTD/mismatched markup fallback, original-text copying, image copy gestures, silent OCR-to-clipboard success/failure, and physical bounds in synthetic mixed-DPI layouts. Native pointer QA with the newly built binary passed crossing fully onto the second monitor and returning: physical positions [3200,100] → [3390,100] → [3580,100] → [3390,100] → [3200,100]. The lead independently confirmed the trace and screenshots in `artifacts/validation/ux-review-v1.7.0-pointer-fixed`. Both connected monitors were 96 DPI; this does not establish actual mixed-DPI pointer behavior. The fix uses the MouseDown event location to calculate the drag anchor instead of a later live cursor location.

Actual focused Ctrl+C produced the expected image-copy event with the original bitmap identity; the fixture replaced clipboard writes to preserve user data. Native Ctrl+double-click was not run because the input tool could not hold the modifier across the required double-click, and its behavior is covered by regression tests. Actual VS Code/Excel GUI clipboard visual interoperability remains outside this completed native test. The commercial comparison above is vendor-documentation research, not an installed-product test.
