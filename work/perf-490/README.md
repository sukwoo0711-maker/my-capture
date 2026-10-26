# Resident memory and responsiveness measurement (perf-resident-490)

One command measures a MyCapture build the way the resident app is used: start and idle, a region
capture to the quick access card, a 3840 x 2160 capture in the editor, 30 captures, the library, the
OCR result window and 10 image/text pins, each followed by samples 0, 10 and 30 seconds after it
closed. Run it on every release candidate before publishing and compare it with the previous release.

## Run

```
python -I -X utf8 work\perf-490\resident_perf.py <MyCapture.exe or its folder> --runs 3 --label <name> ^
    --compare <earlier summary.json>
```

- Input: the candidate's `MyCapture.exe` or the folder holding it (an unpacked package or a build output).
- Output: `summary.json` and `table.md` in `--out` (default `C:\t\perf\resident-<label>-<time>`), and the
  same table on stdout. `--compare` adds a side-by-side table against earlier summaries (for example
  the 4.8.5 summary kept with the release notes).
- Tables only, no run: `python -I -X utf8 work\perf-490\resident_perf.py --table a\summary.json b\summary.json`.
- One run takes about 6 minutes; use `--runs 3` (or more) for a release table. `--quick` shortens the
  settle windows to 2 and 4 seconds for development only; its table is marked and is not a release result.

## What it does and does not touch

- It launches `MyCapture.exe --selftest-resident <run folder>`. The build runs its whole resident shell
  against a data root inside the run folder, under single-instance names of its own, beside the user's
  running MyCapture. Builds without the probe (4.8.5 and earlier packages) are refused before launch,
  because a normal launch would only wake the user's own instance.
- Desktop frames are synthetic, the size of the virtual desktop (the editor step uses 3840 x 2160), so no
  screen content is read or stored. The clipboard writer is replaced before any product code runs.
  Global shortcuts, the HKCU Run value, update checks, Store rating and the legacy-library import are
  skipped. The runner still records the HKCU Run `MyCapture` value before and after every run and
  restores it exactly if anything changed; the table ends with that record.
- Windows do appear on screen (selector, card, editor, library, OCR window, pins). Do not type into them
  while a run is going. The table records how many `testhost.exe` processes were running before and after
  each run, because other agents' UI tests on the same desktop change timings.

## Reading the table

- Values are mean (min–max) over runs. `CPU %` and `UI p95 ms` cover the interval since the previous row,
  so `@10s` and `@30s` rows are idle windows; `UI p95` is the input-priority round trip on the UI thread
  sampled every 100 ms.
- Timings: shortcut → selector first frame (includes the synthetic grab, reported separately), selection →
  card shown, capture → editor shown, editor closed → next shortcut → selector, library and OCR window shown.
- Targets (perf-resident-490): private bytes 60 s after start ≤ 150 MB; 10 s after closing each window
  ≤ start + 40 MB; capture-30 growth not above 5 MB per 10 captures; shortcut → selector p95 ≤ 150 ms (also
  right after the editor closed); idle UI round trip p95 ≤ 50 ms; idle CPU mean ≤ 0.5 %.
- For root-cause work, run the build directly with `--resident-audit`: after each step it forces a full
  compacting collection, lists the step's windows and images still reachable, and summarises committed
  memory with VirtualQuery (private/mapped/image and the largest private allocations). Audit runs change
  every later sample, so never use them for the release table.

## Requirements

Python 3.10+ on Windows (standard library only). No PowerShell script is involved, so the execution
policy does not matter.
