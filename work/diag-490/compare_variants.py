"""Side-by-side retained memory of audit runs (diag-490): private bytes at each step's natural @10s/@30s
samples and after the forced collection, with the memory map's owner split.

    python -I -X utf8 compare_variants.py label=<run folder> [label=<run folder> ...]
"""
import json
import sys
from pathlib import Path

STEPS = ["start", "capture closed", "editor-4k closed", "capture-30 closed", "library closed", "ocr closed", "pins-10 closed"]


def load(folder):
    path = Path(folder)
    if path.is_dir():
        path = path / "resident-probe.json"
    return json.loads(path.read_text(encoding="utf-8"))


def main():
    sys.stdout.reconfigure(encoding="utf-8")
    runs = []
    for arg in sys.argv[1:]:
        label, _, folder = arg.partition("=")
        runs.append((label, load(folder)))
    print("Private MB 10 s after closing (start: @60 s), then after forced GC; [owner split after GC: direct VirtualAlloc / GC / native heap]")
    print("| Step | " + " | ".join(label for label, _ in runs) + " |")
    print("|---|" + "---|" * len(runs))
    for step in STEPS:
        cells = []
        for _, report in runs:
            rows = [r for r in report["Rows"] if r["Step"] == step]
            natural = next((r["PrivateMb"] for r in rows if r["AtSeconds"] == (60 if step == "start" else 10)), None)
            forced = next((r["PrivateMb"] for r in report["Rows"] if r["Step"] == step + " after forced GC"), None)
            audit = next((a for a in report.get("Audits", []) if a["Step"] == step), None)
            split = ""
            if audit:
                owners = audit["PrivateByOwnerMb"]
                heap = owners.get("native heap segments", 0) + owners.get("native heap large blocks", 0)
                split = f" [{owners.get('direct VirtualAlloc', 0):.0f}/{owners.get('managed heap (GC)', 0):.0f}/{heap:.0f}]"
            cells.append("—" if natural is None else f"{natural:.0f} → {forced:.0f}{split}")
        print(f"| {step} | " + " | ".join(cells) + " |")


if __name__ == "__main__":
    main()
