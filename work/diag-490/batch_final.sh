#!/usr/bin/env bash
# Final audit (full residency map, aggressive-GC check) and the runtimeconfig gen0 checks (diag-490).
set -u
here="$(cd "$(dirname "$0")" && pwd)"
python -I -X utf8 "$here/run_probe.py" 'C:\t\perf\diag-490-bin-f\MyCapture.exe' --out 'C:\t\perf\diag-490-audit-final' --audit --runs 1
python -I -X utf8 "$here/run_probe.py" 'C:\t\perf\diag-490-bin-f-rc-gen0\MyCapture.exe' --out 'C:\t\perf\diag-490-rc-gen0-public' --audit --quick --scenarios capture,library --runs 1
python -I -X utf8 "$here/run_probe.py" 'C:\t\perf\diag-490-bin-f-rc-gen0priv\MyCapture.exe' --out 'C:\t\perf\diag-490-rc-gen0-private' --audit --quick --scenarios capture,library --runs 1
python -I -X utf8 "$here/run_probe.py" 'C:\t\perf\diag-490-bin-f\MyCapture.exe' --out 'C:\t\perf\diag-490-rc-gen0-none' --audit --quick --scenarios capture,library --runs 1
echo BATCH-DONE
