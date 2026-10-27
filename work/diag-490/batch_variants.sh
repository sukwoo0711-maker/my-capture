#!/usr/bin/env bash
# Differential audit runs for the retained-memory attribution (diag-490). Memory attribution only;
# timings from these runs are not used (audit forces collections between steps).
set -u
here="$(cd "$(dirname "$0")" && pwd)"
exe='C:\t\perf\diag-490-bin-d\MyCapture.exe'
run() {
  local name="$1"; shift
  python -I -X utf8 "$here/run_probe.py" "$exe" --out "C:\\t\\perf\\diag-490-var-$name" --audit --runs 1 "$@"
}
run sw-selector --variant sw-selector --scenarios capture,editor-4k,capture-30
run sw-card --variant sw-card --scenarios capture,editor-4k,capture-30
run sw-flight --variant sw-flight --scenarios capture,editor-4k,capture-30
run no-flight --variant no-flight --scenarios capture,editor-4k,capture-30
run sw-layered --variant sw-card,sw-flight --scenarios capture,editor-4k,capture-30
run sw-editor --variant sw-editor --scenarios capture,editor-4k
run frame-cached --variant frame-cached
run gen0-4mb --env DOTNET_GCgen0size=0x400000
echo BATCH-DONE
