# Text stress and manual input fixture

This standalone WPF host loads an explicit published payload. The project defaults
to `artifacts/qa11-product-readiness`; specify `AppPayload` at build time when that
payload is elsewhere. Compile references are not copied into the host. Bootstrap
resolves managed product dependencies from the runtime `-AppPayload`, so the same
host can compare compatible baseline and after payloads. The friend assembly name
is `MyCapture.App.Tests`; this is a diagnostic executable, not a test-suite runner.

```powershell
dotnet.exe build build/tools/TextStressProbe/TextStress.csproj -c Release -p:AppPayload=C:/absolute/payload -o C:/absolute/new-host-output -v q -nologo
powershell.exe -NoProfile -NonInteractive -File build/tools/TextStressProbe/Run.ps1 -AppPayload C:/absolute/payload -ProbeDll C:/absolute/new-host-output/MyCapture.App.Tests.dll -Case editor -Characters 100000 -Shape lines -Label baseline
```

Use a new host output path and preserve all evidence. The runner creates full-GUID
JSON, process receipt, and runtimeconfig names under `artifacts/text-stress` or an
explicit output directory. It pins Core and Desktop to `10.0.11` with roll forward
disabled. Both runtimes must already be installed. It does not change execution
policy, install software, or use a global MSBuild RuntimeFrameworkVersion property.

The original `artifacts/stress-text/probe/Program.cs` is preserved in the author
worktree. A byte-identical `OriginalProgram.cs.txt` is retained locally as untracked
evidence, outside the committed executable sources. Its SHA256 is
`FF0D78D7BF1F11EECBCAA53FCD0B517AC538E8BD111F2731276D8882FF10D0C5`.
The original artifact is preserved. Workload methods are retained; the portable
host adds payload loading, argument validation, runtime observations and hold modes.

## Measurements

- `editor`: actual image editor, insertion, append, native undo/redo, SelectAll,
  deferred selection/layout settle, no-selection scrolling, snapshot, commit and
  document undo/redo. The default burst keeps no settle between edits.
- `ocr`: actual OCR result, initial result/layout, full selection and settle,
  no-selection scrolling, select-all from a caret, 1,024- and 4,096-character
  selections, a 50-character typing burst, repeated result and settle, close.
  Each phase records whether the IME is on for the result box.
- `editor-render`: synthetic actual editor HWND, then offscreen RenderTargetBitmap
  top/bottom PNGs using the existing recipe. Raster PNGs are not DWM presentation.

Fixtures contain Korean, ASCII and emoji, either one long paragraph or repeated
newlines. The exact requested UTF-16 length is preserved without ending on a high
surrogate. Every original content assertion and SHA256 remains. Stage allocation
is `GC.GetAllocatedBytesForCurrentThread`; it excludes background-thread allocation.
Stage times include the original dispatcher/layout and reflection overhead. Process
receipt CPU/memory samples cover the entire child. Neither metric is OS input-to-
photon latency or normal product startup readiness.

The case has a 60-second/2-GiB owned-process guard. Timer result and external receipt
preserve the last stage on timeout/failure. Only the exact Process object started
by the runner is stopped. Process termination and stream drain each have bounded
five-second confirmation. Guard exits return 124; harness exceptions retain nonzero
exception propagation. Exit zero additionally requires a final `PASS` result.

Optional `-Diagnostic` values preserve the earlier ablations: `readonly-no-ime`,
`bounded-view`, `content-scroll`, `settled-edits`, `line-selection`, and
`no-auto-selection`. Pass multiple values as a PowerShell string array. The last
option is used alone, matching the original exact-option check. Record ablations
separately from actual product behavior. `line-selection` changes a process-wide
WPF compatibility switch and therefore is a diagnostic, not a product recommendation.
`ime-always-on` keeps the OCR box's IME on for every selection, the editable result
without its selection limit. `readonly-no-ime` requires the earlier readonly result.

## Manual hold

```powershell
powershell.exe -NoProfile -NonInteractive -File build/tools/TextStressProbe/Run.ps1 -AppPayload C:/absolute/after-payload -ProbeDll C:/absolute/new-host-output/MyCapture.App.Tests.dll -Case editor-hold -Characters 100000 -Shape lines -Label manual
powershell.exe -NoProfile -NonInteractive -File build/tools/TextStressProbe/Run.ps1 -AppPayload C:/absolute/after-payload -ProbeDll C:/absolute/new-host-output/MyCapture.App.Tests.dll -Case ocr-editor-hold -Characters 100000 -Shape lines -Label manual-ime
```

`editor-hold`, `ocr-hold` and `ocr-editor-hold` show only owned synthetic product
windows visibly in the taskbar, for an operator to exercise Ctrl+Home/End, short
typing, undo and Korean IME. Hold uses at most 100k characters, actual payload
settings without diagnostic overrides, a ten-minute/2-GiB limit and normal Close.
`ocr-editor-hold` is sequential: exercise readonly OCR, close/hide it with Escape
or Alt+F4, and the fixture calls CloseForExit and opens a fresh editor on the next
observation tick. Close the editor to finish. This tests returning from the readonly
input-method scope without prematurely committing an existing live caption on focus
loss. The full ten-minute limit covers both windows. Moving focus out of an active
editor textbox retains the product's existing commit behavior.

The result file reports `HOLD_READY`, periodic SHA/length/suffix (64 characters),
caret/selection/focus/viewport, initial/current IME state and TextCompositionManager
preview start/update/completed event counts. Completed counts also include ordinary
text input; start/update and observed Hangul composition distinguish IME behavior.
No IME state is assigned by the probe. Latest live editor textbox is observed when
the operator starts another caption. UIA text is not polled each second. Close
finishes with `HOLD_CLOSED`, indicating only owned window lifecycle completion;
operator observations and before/after state establish any manual smoke outcome.
Manual hold samples are separate from automated latency measurements.

The host creates a plain WPF Application only for resources. It does not start the
product App, read a user profile, capture the screen, acquire actual OCR, persist
drafts/captures, inject input or access the clipboard. Editor close chooses DontSave
for this synthetic fixture. Product controls still expose their normal buttons;
the operator must stay within the specified keyboard/selection/IME smoke and avoid
Save, clipboard commands and file dialogs. All output and temporary evidence remain.
Run one measurement/GUI fixture at a time under the coordinator's slot.
