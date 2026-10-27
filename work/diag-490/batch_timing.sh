#!/usr/bin/env bash
# Timing runs (no audit) for the first-capture breakdown (diag-490): the ReadyToRun self-contained publish
# (as shipped) and the JIT build-output (as the gate measures), same scenarios, back to back.
set -u
runner='C:\GitRepositories\my-capture-perf\work\perf-490\resident_perf.py'
temp='C:\Users\sukwo\AppData\Local\Temp\mdx'
python -I -X utf8 "$runner" 'C:\t\perf\diag-490-pub' --runs 3 --scenarios capture,editor-4k,capture-30 --label diag-490-r2r --out 'C:\t\perf\diag-490-timing-r2r' --temp "$temp"
python -I -X utf8 "$runner" 'C:\t\perf\diag-490-bin-f' --runs 3 --scenarios capture,editor-4k,capture-30 --label diag-490-jit --out 'C:\t\perf\diag-490-timing-jit' --temp "$temp" --compare 'C:\t\perf\diag-490-timing-r2r\summary.json'
echo BATCH-DONE
