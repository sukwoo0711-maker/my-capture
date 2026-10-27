"""Retained private memory by owner per scenario (diag-490): owner split and residency from one audit run,
software-render and per-window differentials from the variant runs, GC give-back from the aggressive GC.

    python -I -X utf8 attribution.py <final audit run> hw=<run> sw=<run> sw-selector=<run> no-flight=<run>
"""
import json
import sys
from pathlib import Path

STEPS = ["capture closed", "editor-4k closed", "capture-30 closed", "library closed", "ocr closed", "pins-10 closed"]


def load(folder):
    path = Path(folder)
    return json.loads((path / "resident-probe.json" if path.is_dir() else path).read_text(encoding="utf-8"))


def natural(report, step):
    at = 60 if step == "start" else 10
    return next((r["PrivateMb"] for r in report["Rows"] if r["Step"] == step and r["AtSeconds"] == at), None)


def main():
    sys.stdout.reconfigure(encoding="utf-8")
    final = load(sys.argv[1])
    runs = {k: load(v) for k, v in (arg.split("=", 1) for arg in sys.argv[2:])}
    rows = {(r["Step"], r["AtSeconds"]): r for r in final["Rows"]}
    audits = {a["Step"]: a for a in final["Audits"]}
    start = audits["start"]

    def owner(audit, key, resident=False):
        source = audit["ResidentByOwnerMb" if resident else "PrivateByOwnerMb"]
        if key == "heap":
            return source.get("native heap segments", 0) + source.get("native heap large blocks", 0)
        return source.get(key, 0)

    print("All values MB over the start idle value of the same run (start @60 s or start after forced GC).")
    print("| Owner (evidence) | " + " | ".join(s.replace(" closed", "") for s in STEPS) + " |")
    print("|---|" + "---|" * len(STEPS))

    def line(label, values):
        print(f"| {label} | " + " | ".join("—" if v is None else f"{v:+.0f}" for v in values) + " |")

    line("Private bytes, natural @10 s (final audit run)", [natural(final, s) - natural(final, "start") for s in STEPS])
    line("Private bytes after forced GC (final audit run)",
         [rows[(s + " after forced GC", -1)]["PrivateMb"] - rows[("start after forced GC", -1)]["PrivateMb"] for s in STEPS])
    line("direct VirtualAlloc committed (D3D/driver, pin images)", [owner(audits[s], "direct VirtualAlloc") - owner(start, "direct VirtualAlloc") for s in STEPS])
    line("  of which resident (QueryWorkingSetEx)", [owner(audits[s], "direct VirtualAlloc", True) - owner(start, "direct VirtualAlloc", True) for s in STEPS])
    line("  of which commit only (not resident)",
         [(owner(audits[s], "direct VirtualAlloc") - owner(audits[s], "direct VirtualAlloc", True))
          - (owner(start, "direct VirtualAlloc") - owner(start, "direct VirtualAlloc", True)) for s in STEPS])
    line("managed heap committed (GC)", [owner(audits[s], "managed heap (GC)") - owner(start, "managed heap (GC)") for s in STEPS])
    line("native heaps committed (HeapWalk segments + large blocks)", [owner(audits[s], "heap") - owner(start, "heap") for s in STEPS])
    line("GC.Collect(Aggressive) gives back (private, absolute)",
         [rows[(s + " after aggressive GC", -2)]["PrivateMb"] - rows[(s + " after forced GC", -1)]["PrivateMb"] for s in STEPS])
    if "hw" in runs and "sw" in runs:
        hw, sw = runs["hw"], runs["sw"]
        line("hardware rendering (hw − software-only run, natural @10 s)",
             [None if natural(sw, s) is None else (natural(hw, s) - natural(hw, "start")) - (natural(sw, s) - natural(sw, "start")) for s in STEPS])
    for name in ("sw-selector", "no-flight", "sw-card", "sw-flight"):
        if name in runs and "hw" in runs:
            line(f"{name}: hw − variant (natural @10 s)",
                 [None if natural(runs[name], s) is None else natural(runs["hw"], s) - natural(runs[name], s) for s in STEPS])
    print(f"\nstart idle: private @60 s {natural(final, 'start')}; hw − sw at start "
          f"{natural(runs['hw'], 'start') - natural(runs['sw'], 'start'):+.0f} MB" if "sw" in runs else "")
    print("start after forced GC → aggressive GC:", rows[("start after forced GC", -1)]["PrivateMb"], "→",
          rows[("start after aggressive GC", -2)]["PrivateMb"])


if __name__ == "__main__":
    main()
