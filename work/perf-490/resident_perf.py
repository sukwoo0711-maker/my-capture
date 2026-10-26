"""Resident memory and responsiveness measurement for a MyCapture build.

Runs the app's own resident probe (``MyCapture.exe --selftest-resident <out>``) N times, each in its own
isolated data root, beside any normal MyCapture instance, and writes one table (mean and min-max per
metric) plus the targets of perf-resident-490. See README.md in this folder.

Usage:
    python -I resident_perf.py <MyCapture.exe or its folder> [--runs 3] [--out DIR] [--label NAME]
        [--compare OTHER_SUMMARY.json ...] [--quick] [--scenarios a,b,c]
    python -I resident_perf.py --table SUMMARY.json [SUMMARY.json ...]   (tables only, no run)
"""
from __future__ import annotations

import argparse
import datetime as _dt
import json
import os
import statistics
import subprocess
import sys
import time
import winreg
from pathlib import Path

SWITCH = "--selftest-resident"
RUN_KEY = r"Software\Microsoft\Windows\CurrentVersion\Run"
RUN_VALUE = "MyCapture"
TARGETS = {
    "start_private_mb": 150.0,  # private bytes 60 s after start
    "closed_over_start_mb": 40.0,  # private bytes 10 s after closing, over the start idle value
    "growth_per_10_mb": 5.0,  # private-bytes growth per 10 captures
    "selector_p95_ms": 150.0,
    "ui_p95_ms": 50.0,
    "idle_cpu_percent": 0.5,
}
METRICS = [
    ("WorkingSetMb", "WS MB"), ("PrivateMb", "Private MB"), ("ManagedMb", "Managed MB"),
    ("GdiHandles", "GDI"), ("UserHandles", "USER"), ("Handles", "Handles"), ("Threads", "Threads"),
    ("CpuPercent", "CPU %"), ("UiP95Ms", "UI p95 ms"),
]


def read_run_key() -> tuple[bool, str | None, int | None]:
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, RUN_KEY) as key:
            value, kind = winreg.QueryValueEx(key, RUN_VALUE)
            return True, value, kind
    except FileNotFoundError:
        return False, None, None


def restore_run_key(state: tuple[bool, str | None, int | None]) -> None:
    exists, value, kind = state
    with winreg.OpenKey(winreg.HKEY_CURRENT_USER, RUN_KEY, 0, winreg.KEY_SET_VALUE) as key:
        if exists:
            winreg.SetValueEx(key, RUN_VALUE, 0, kind, value)
        else:
            try:
                winreg.DeleteValue(key, RUN_VALUE)
            except FileNotFoundError:
                pass


def resolve_exe(path: str) -> Path:
    target = Path(path)
    if target.is_dir():
        target = target / "MyCapture.exe"
    if not target.is_file():
        sys.exit(f"MyCapture.exe not found: {path}")
    return target.resolve()


def supports_probe(exe: Path) -> bool:
    """Refuse builds without the probe: a normal launch would only wake the user's own instance."""
    needle = SWITCH.encode("utf-16-le")
    for name in ("MyCapture.dll", "MyCapture.exe"):
        candidate = exe.parent / name
        if candidate.is_file() and needle in candidate.read_bytes():
            return True
    return False


def count_testhosts() -> int:
    result = subprocess.run(["tasklist", "/FI", "IMAGENAME eq testhost.exe", "/FO", "CSV", "/NH"],
                            capture_output=True, text=True, errors="replace")
    return sum(1 for line in result.stdout.splitlines() if line.lower().startswith('"testhost.exe"'))


def run_once(exe: Path, out: Path, quick: bool, scenarios: str | None, temp: str, timeout: int) -> dict:
    out.mkdir(parents=True, exist_ok=True)
    args = [str(exe), SWITCH, str(out)]
    if quick:
        args.append("--resident-quick")
    if scenarios:
        args += ["--resident-scenarios", scenarios]
    env = dict(os.environ, TEMP=temp, TMP=temp)
    testhosts_before = count_testhosts()
    started = time.time()
    process = subprocess.Popen(args, env=env)
    try:
        exit_code = process.wait(timeout=timeout)
    except subprocess.TimeoutExpired:
        process.kill()
        exit_code = "timeout"
    report_path = out / "resident-probe.json"
    report = json.loads(report_path.read_text(encoding="utf-8")) if report_path.is_file() else {"Rows": [], "Timings": []}
    report["ExitCode"] = exit_code
    report["WallSeconds"] = round(time.time() - started, 1)
    report["TesthostsBefore"] = testhosts_before
    report["TesthostsAfter"] = count_testhosts()
    return report


def p95(values: list[float]) -> float:
    ordered = sorted(values)
    return ordered[int((len(ordered) - 1) * 0.95)] if ordered else float("nan")


def summarize(label: str, exe: str, runs: list[dict]) -> dict:
    rows: dict[str, dict[str, list[float]]] = {}
    order: list[str] = []
    for run in runs:
        for row in run.get("Rows", []):
            key = f"{row['Step']} @{row['AtSeconds']}s"
            if key not in rows:
                rows[key] = {name: [] for name, _ in METRICS}
                order.append(key)
            for name, _ in METRICS:
                rows[key][name].append(float(row[name]))
    timings: dict[str, list[float]] = {}
    for run in runs:
        for timing in run.get("Timings", []):
            timings.setdefault(timing["Name"], []).append(float(timing["Milliseconds"]))
    return {
        "Label": label, "Exe": exe, "Build": runs[0].get("Build") if runs else None,
        "Runs": len(runs), "Quick": runs[0].get("Quick") if runs else None,
        "VirtualDesktop": runs[0].get("VirtualDesktop") if runs else None,
        "Monitors": runs[0].get("Monitors") if runs else None,
        "ExitCodes": [run.get("ExitCode") for run in runs],
        "Testhosts": [[run.get("TesthostsBefore"), run.get("TesthostsAfter")] for run in runs],
        "Failures": [failure for run in runs for failure in run.get("Failures", [])],
        "Order": order, "Rows": rows, "Timings": timings,
    }


def cell(values: list[float], digits: int = 1) -> str:
    if not values:
        return "-"
    mean = statistics.fmean(values)
    if len(values) == 1:
        return f"{mean:.{digits}f}"
    return f"{mean:.{digits}f} ({min(values):.{digits}f}–{max(values):.{digits}f})"


def mean(summary: dict, key: str, metric: str) -> float | None:
    values = summary["Rows"].get(key, {}).get(metric)
    return statistics.fmean(values) if values else None


def targets(summary: dict) -> list[tuple[str, str, str]]:
    result: list[tuple[str, str, str]] = []
    start_key = next((key for key in summary["Order"] if key.startswith("start @60s")), None) \
        or next((key for key in reversed(summary["Order"]) if key.startswith("start @")), None)
    start = mean(summary, start_key, "PrivateMb") if start_key else None
    if start is not None:
        result.append((f"Private MB after start ({start_key})", f"{start:.1f}",
                       "PASS" if start <= TARGETS["start_private_mb"] else "MISS"))
    for key in summary["Order"]:
        if " closed @" in key and key.endswith("@10s") or (summary.get("Quick") and " closed @" in key and key.endswith("@2s")):
            value = mean(summary, key, "PrivateMb")
            if value is not None and start is not None:
                over = value - start
                result.append((f"{key}: private over start", f"{over:+.1f} MB",
                               "PASS" if over <= TARGETS["closed_over_start_mb"] else "MISS"))
    growth = [mean(summary, f"capture-30 after {n} @0s", "PrivateMb") for n in (10, 20, 30)]
    if all(value is not None for value in growth):
        steps = [growth[1] - growth[0], growth[2] - growth[1]]
        result.append(("capture-30: private growth per 10 captures (10→20, 20→30)",
                       ", ".join(f"{step:+.1f} MB" for step in steps),
                       "PASS" if not all(step > TARGETS["growth_per_10_mb"] for step in steps) else "MISS"))
    for name, values in summary["Timings"].items():
        if "selector first frame" in name:
            value = p95(values)
            result.append((f"{name} p95 (n={len(values)})", f"{value:.0f} ms",
                           "PASS" if value <= TARGETS["selector_p95_ms"] else "MISS"))
    idle_ui = [value for key in summary["Order"] if not key.endswith("@0s") for value in summary["Rows"][key]["UiP95Ms"]]
    if idle_ui:
        value = p95(idle_ui)
        result.append(("Idle UI thread input round trip p95 (all idle windows)", f"{value:.1f} ms",
                       "PASS" if value <= TARGETS["ui_p95_ms"] else "MISS"))
    idle_cpu = [value for key in summary["Order"] if key.endswith("@30s") or key.endswith("@60s")
                for value in summary["Rows"][key]["CpuPercent"]]
    if idle_cpu:
        value = statistics.fmean(idle_cpu)
        result.append(("Idle CPU mean (10→30 s and 30→60 s windows)", f"{value:.2f} %",
                       "PASS" if value <= TARGETS["idle_cpu_percent"] else "MISS"))
    return result


def table(summary: dict) -> str:
    lines = [f"### {summary['Label']} — {summary['Build']}",
             f"Runs: {summary['Runs']}{' (QUICK settle windows, not for release)' if summary.get('Quick') else ''}; "
             f"desktop {summary['VirtualDesktop']} {summary['Monitors']}; exit codes {summary['ExitCodes']}; "
             f"concurrent testhost.exe before/after each run {summary['Testhosts']}",
             "", "Values are mean (min–max) over runs. CPU % and UI p95 cover the interval since the previous row.", "",
             "| Step | " + " | ".join(title for _, title in METRICS) + " |",
             "|---|" + "---|" * len(METRICS)]
    for key in summary["Order"]:
        lines.append(f"| {key} | " + " | ".join(cell(summary['Rows'][key][name]) for name, _ in METRICS) + " |")
    lines += ["", "| Timing | mean (min–max) ms | p95 ms | n |", "|---|---|---|---|"]
    for name, values in summary["Timings"].items():
        lines.append(f"| {name} | {cell(values, 0)} | {p95(values):.0f} | {len(values)} |")
    lines += ["", "| Target | Value | Result |", "|---|---|---|"]
    lines += [f"| {name} | {value} | {result} |" for name, value, result in targets(summary)]
    if summary["Failures"]:
        lines += ["", "Failures:", *[f"- {failure.splitlines()[0]}" for failure in summary["Failures"]]]
    return "\n".join(lines)


def compare(summaries: list[dict]) -> str:
    base = summaries[0]
    lines = ["### Comparison (mean private / working set MB; timings p95 ms)", "",
             "| Step | " + " | ".join(f"{s['Label']} private" for s in summaries) + " | "
             + " | ".join(f"{s['Label']} WS" for s in summaries) + " |",
             "|---|" + "---|" * (2 * len(summaries))]
    for key in base["Order"]:
        private = [mean(s, key, "PrivateMb") for s in summaries]
        ws = [mean(s, key, "WorkingSetMb") for s in summaries]
        lines.append(f"| {key} | " + " | ".join("-" if v is None else f"{v:.1f}" for v in private) + " | "
                     + " | ".join("-" if v is None else f"{v:.1f}" for v in ws) + " |")
    lines += ["", "| Timing | " + " | ".join(s["Label"] for s in summaries) + " |", "|---|" + "---|" * len(summaries)]
    for name in base["Timings"]:
        lines.append(f"| {name} | " + " | ".join(
            f"{p95(s['Timings'][name]):.0f}" if name in s["Timings"] else "-" for s in summaries) + " |")
    return "\n".join(lines)


def main() -> int:
    sys.stdout.reconfigure(encoding="utf-8")
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("target", nargs="?", help="MyCapture.exe or the folder that holds it")
    parser.add_argument("--runs", type=int, default=3)
    parser.add_argument("--out", default=None)
    parser.add_argument("--label", default=None)
    parser.add_argument("--compare", nargs="*", default=[], help="earlier summary.json files to compare against")
    parser.add_argument("--quick", action="store_true", help="short settle windows for development only")
    parser.add_argument("--scenarios", default=None)
    parser.add_argument("--temp", default=r"C:\t\perf")
    parser.add_argument("--timeout", type=int, default=1200)
    parser.add_argument("--table", nargs="*", default=None, help="print tables of existing summary.json files")
    options = parser.parse_args()

    if options.table is not None:
        summaries = [json.loads(Path(path).read_text(encoding="utf-8")) for path in options.table]
        print("\n\n".join(table(summary) for summary in summaries))
        if len(summaries) > 1:
            print("\n" + compare(summaries))
        return 0
    if not options.target:
        parser.error("target is required unless --table is given")

    exe = resolve_exe(options.target)
    if not supports_probe(exe):
        print(f"{exe} does not contain {SWITCH}; refusing to launch it (it would only wake the running instance).",
              file=sys.stderr)
        return 3
    label = options.label or exe.parent.name
    stamp = _dt.datetime.now().strftime("%Y%m%d-%H%M%S")
    out = Path(options.out or Path(options.temp) / f"resident-{label}-{stamp}")
    out.mkdir(parents=True, exist_ok=True)
    Path(options.temp).mkdir(parents=True, exist_ok=True)

    run_key_before = read_run_key()
    run_key_log = [f"before: {run_key_before[1] if run_key_before[0] else '(absent)'}"]
    runs = []
    for index in range(1, options.runs + 1):
        print(f"[{label}] run {index}/{options.runs} …", flush=True)
        runs.append(run_once(exe, out / f"run-{index}", options.quick, options.scenarios, options.temp, options.timeout))
        after = read_run_key()
        if after != run_key_before:
            run_key_log.append(f"run {index}: changed to {after[1] if after[0] else '(absent)'}; restored")
            restore_run_key(run_key_before)
        else:
            run_key_log.append(f"run {index}: unchanged")
    run_key_log.append(f"after: {read_run_key()[1] if read_run_key()[0] else '(absent)'}")

    summary = summarize(label, str(exe), runs)
    summary["RunKey"] = run_key_log
    (out / "summary.json").write_text(json.dumps(summary, indent=2, ensure_ascii=False), encoding="utf-8")
    others = [json.loads(Path(path).read_text(encoding="utf-8")) for path in options.compare]
    text = table(summary) + "\n\nHKCU Run MyCapture: " + "; ".join(run_key_log)
    if others:
        text += "\n\n" + compare([*others, summary])
    (out / "table.md").write_text(text + "\n", encoding="utf-8")
    print(text)
    print(f"\nWrote {out / 'summary.json'} and {out / 'table.md'}")
    return 0 if all(run.get("ExitCode") == 0 for run in runs) else 1


if __name__ == "__main__":
    sys.exit(main())
