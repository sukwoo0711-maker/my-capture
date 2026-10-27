#!/usr/bin/env bash
# First-capture prewarm experiments (diag-490): baseline and deeper-prewarm variants alternated, each run
# started only when no testhost.exe is running (polled every minute, up to 20 minutes), then the
# existing-prewarm memory check.
set -u
here="$(cd "$(dirname "$0")" && pwd)"
exe='C:\t\perf\diag-490-bin-h\MyCapture.exe'
testhosts() { tasklist //FI "IMAGENAME eq testhost.exe" //NH | grep -ci "testhost.exe"; }
wait_quiet() {
  for minute in $(seq 1 20); do
    [ "$(testhosts)" = "0" ] && return 0
    echo "testhost running; waiting (minute $minute)"
    sleep 60
  done
  echo "testhost still running after 20 minutes; running anyway (overlap recorded)"
}
for round in 1 2 3; do
  wait_quiet
  python -I -X utf8 "$here/run_probe.py" "$exe" --out "C:\\t\\perf\\diag-490-prewarm-base-$round" --scenarios capture,capture-30 --runs 1
  wait_quiet
  python -I -X utf8 "$here/run_probe.py" "$exe" --out "C:\\t\\perf\\diag-490-prewarm-deep-$round" --scenarios capture,capture-30 --variant prewarm-selector-hwnd,prewarm-readers --runs 1
done
python -I -X utf8 "$here/run_probe.py" "$exe" --out 'C:\t\perf\diag-490-var-no-prewarm' --audit --quick --scenarios capture --variant no-overlay-prewarm,no-editor-prewarm --runs 1
python -I -X utf8 "$here/run_probe.py" "$exe" --out 'C:\t\perf\diag-490-var-prewarm-default-quick' --audit --quick --scenarios capture --runs 1
echo BATCH-DONE
