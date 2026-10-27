"""Runs MyCapture's resident probe directly with diagnostic switches (audit, variants, software render).

    python -I -X utf8 run_probe.py <MyCapture.exe> --out C:\\t\\perf\\diag-490-<step> [--runs 1]
        [--audit] [--variant a,b] [--software-render] [--scenarios capture,editor-4k] [--quick]

Each run gets its own folder (run-N) with the probe's resident-probe.json. Like the release runner
(my-capture-perf work/perf-490/resident_perf.py) it records the HKCU Run 'MyCapture' value before and
after every run and restores it exactly if it changed, and counts testhost.exe before and after each
run. TEMP/TMP point at a short backslash folder.
"""
import argparse
import json
import os
import subprocess
import sys
import time
import winreg
from pathlib import Path

RUN_KEY = r"Software\Microsoft\Windows\CurrentVersion\Run"
RUN_VALUE = "MyCapture"


def read_run_key():
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, RUN_KEY) as key:
            value, kind = winreg.QueryValueEx(key, RUN_VALUE)
            return [True, value, kind]
    except FileNotFoundError:
        return [False, None, None]


def restore_run_key(state):
    exists, value, kind = state
    with winreg.OpenKey(winreg.HKEY_CURRENT_USER, RUN_KEY, 0, winreg.KEY_SET_VALUE) as key:
        if exists:
            winreg.SetValueEx(key, RUN_VALUE, 0, kind, value)
        else:
            try:
                winreg.DeleteValue(key, RUN_VALUE)
            except FileNotFoundError:
                pass


def count(image):
    result = subprocess.run(["tasklist", "/FI", f"IMAGENAME eq {image}", "/FO", "CSV", "/NH"],
                            capture_output=True, text=True, errors="replace")
    return sum(1 for line in result.stdout.splitlines() if line.lower().startswith(f'"{image.lower()}"'))


def main():
    sys.stdout.reconfigure(encoding="utf-8")
    parser = argparse.ArgumentParser()
    parser.add_argument("exe")
    parser.add_argument("--out", required=True)
    parser.add_argument("--runs", type=int, default=1)
    parser.add_argument("--audit", action="store_true")
    parser.add_argument("--variant", default=None)
    parser.add_argument("--software-render", action="store_true")
    parser.add_argument("--scenarios", default=None)
    parser.add_argument("--quick", action="store_true")
    parser.add_argument("--temp", default=r"C:\Users\sukwo\AppData\Local\Temp\mdx")
    parser.add_argument("--timeout", type=int, default=1500)
    parser.add_argument("--env", action="append", default=[], help="extra KEY=VALUE for the probe process")
    options = parser.parse_args()

    out = Path(options.out)
    out.mkdir(parents=True, exist_ok=True)
    Path(options.temp).mkdir(parents=True, exist_ok=True)
    before = read_run_key()
    meta = {"exe": options.exe, "runs": [], "run_key_before": before[1] if before[0] else None}
    for index in range(1, options.runs + 1):
        folder = out / f"run-{index}"
        folder.mkdir(parents=True, exist_ok=True)
        args = [options.exe, "--selftest-resident", str(folder)]
        record = {"testhost_before": count("testhost.exe"), "started": time.strftime("%Y-%m-%d %H:%M:%S")}
        if options.audit:
            args.append("--resident-audit")
        if options.quick:
            args.append("--resident-quick")
        if options.variant:
            args += ["--resident-variant", options.variant]
        if options.software_render:
            args.append("--resident-software-render")
        if options.scenarios:
            args += ["--resident-scenarios", options.scenarios]
        env = dict(os.environ, TEMP=options.temp, TMP=options.temp)
        for pair in options.env:
            key, _, value = pair.partition("=")
            env[key] = value
        record["env"] = options.env
        record["args"] = args
        print(f"[{out.name}] run {index}/{options.runs}: {' '.join(args[1:])} (testhost {record['testhost_before']})", flush=True)
        started = time.time()
        process = subprocess.Popen(args, env=env)
        try:
            record["exit_code"] = process.wait(timeout=options.timeout)
        except subprocess.TimeoutExpired:
            process.kill()
            record["exit_code"] = "timeout"
        record["wall_seconds"] = round(time.time() - started, 1)
        record["testhost_after"] = count("testhost.exe")
        after = read_run_key()
        if after != before:
            restore_run_key(before)
            record["run_key"] = f"changed to {after[1]!r}; restored"
        else:
            record["run_key"] = "unchanged"
        meta["runs"].append(record)
        print(f"  exit {record['exit_code']} in {record['wall_seconds']} s, testhost after {record['testhost_after']}, "
              f"Run key {record['run_key']}", flush=True)
    final = read_run_key()
    meta["run_key_after"] = final[1] if final[0] else None
    (out / "runs.json").write_text(json.dumps(meta, indent=2, ensure_ascii=False), encoding="utf-8")
    return 0 if all(run["exit_code"] == 0 for run in meta["runs"]) else 1


if __name__ == "__main__":
    sys.exit(main())
