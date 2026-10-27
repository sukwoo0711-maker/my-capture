"""Tables from resident-probe.json files written with --resident-audit (ResidentProbeMemoryMap) and the
selector breakdown (SelectorTimeline marks).

    python -I -X utf8 analyze.py <run folder or resident-probe.json> [...] [--regions] [--breakdown]
"""
import argparse
import json
import statistics
import sys
from pathlib import Path


def load(path):
    target = Path(path)
    if target.is_dir():
        target = target / "resident-probe.json"
    return json.loads(target.read_text(encoding="utf-8"))


def audit_table(report):
    rows = {row["Step"]: row for row in report["Rows"]}
    natural = {row["Step"] + " @0": row for row in report["Rows"] if row["AtSeconds"] == 0}
    lines = ["| Step (after forced GC; '@0' = natural, closed @0s) | Private MB | Managed MB | GC committed MB | VQ private MB | >=16 MB n/MB | 4-16 MB n/MB | by owner MB |",
             "|---|---|---|---|---|---|---|---|"]
    for audit in report.get("Audits", []):
        row = natural.get(audit["Step"]) or rows.get(audit["Step"] + " after forced GC", {})
        regions = audit["Allocations"]
        big = [a["CommittedMb"] for a in regions if a["CommittedMb"] >= 16]
        mid = [a["CommittedMb"] for a in regions if 4 <= a["CommittedMb"] < 16]
        owners = ", ".join(f"{k} {v:.1f}" for k, v in sorted(audit["PrivateByOwnerMb"].items(), key=lambda kv: -kv[1]))
        lines.append(f"| {audit['Step']} | {row.get('PrivateMb', '?')} | {row.get('ManagedMb', '?')} | {row.get('GcCommittedMb', '?')} | "
                     f"{audit['CommittedPrivateMb']:.1f} | {len(big)}/{sum(big):.1f} | {len(mid)}/{sum(mid):.1f} | {owners} |")
    return "\n".join(lines)


def region_lists(report, minimum):
    out = []
    for audit in report.get("Audits", []):
        out.append(f"\n#### {audit['Step']} (map took {audit['ElapsedMs']} ms; {audit['SmallAllocations']} allocations < 1 MB = {audit['SmallAllocationsMb']} MB)")
        out.append("| Base | Committed MB | Reserved MB | Owner | Heap busy MB | Content |")
        out.append("|---|---|---|---|---|---|")
        for a in audit["Allocations"]:
            if a["CommittedMb"] < minimum:
                continue
            out.append(f"| {a['Base']} | {a['CommittedMb']:.2f} | {a['ReservedMb']:.2f} | {a['Owner']} | {a['HeapBusyMb']:.2f} | {a['Content']} |")
        out.append("\n| Heap | Handle | Committed | Allocated | Busy | Free | Large blocks n/MB | Note |")
        out.append("|---|---|---|---|---|---|---|---|")
        for h in audit["Heaps"]:
            if h["CommittedMb"] < 0.5 and h["BusyMb"] < 0.5:
                continue
            out.append(f"| {h['Index']} | {h['Handle']} | {h['CommittedMb']} | {h['AllocatedMb']} | {h['BusyMb']} | {h['FreeMb']} | "
                       f"{h['LargeBlocks']}/{h['LargeBlocksMb']} | {h['Note']} |")
    return "\n".join(out)


def breakdown_table(reports):
    by_name = {}
    for report in reports:
        for b in report.get("Breakdowns", []):
            by_name.setdefault(b["Name"], []).append(b)
    out = []
    for name, items in by_name.items():
        out.append(f"\n#### {name} (n={len(items)})")
        stages = []
        for item in items:
            for mark in item["Marks"]:
                if mark["Name"] not in stages:
                    stages.append(mark["Name"])
        out.append("| # | selector ms | since dismissal ms | " + " | ".join(stages) + " |")
        out.append("|---|---|---|" + "---|" * len(stages))
        for item in items:
            first = {}
            for mark in item["Marks"]:
                first.setdefault(mark["Name"], mark["Milliseconds"])
            cells = [f"{first[s]:.1f}" if s in first else "" for s in stages]
            out.append(f"| {item['Index']} | {item['SelectorMs']:.1f} | {item['SinceLastDismissalMs']:.0f} | " + " | ".join(cells) + " |")
    return "\n".join(out)


def stage_summary(reports, name, groups):
    """Mean (min-max) ms of each stage delta for breakdowns named `name`, split by settle-required."""
    items = [b for r in reports for b in r.get("Breakdowns", []) if b["Name"] == name]
    out = []
    for settle in (False, True):
        chosen = [b for b in items if any(m["Name"] == "settle-required" for m in b["Marks"]) == settle]
        if not chosen:
            continue
        out.append(f"\n{name}, settle {'yes' if settle else 'no'}, n={len(chosen)}: selector "
                   f"{statistics.mean(b['SelectorMs'] for b in chosen):.1f} ms (min {min(b['SelectorMs'] for b in chosen):.1f}, "
                   f"max {max(b['SelectorMs'] for b in chosen):.1f})")
        for start, end in groups:
            deltas = []
            for b in chosen:
                first = {}
                for m in b["Marks"]:
                    first.setdefault(m["Name"], m["Milliseconds"])
                s = 0.0 if start == "@0" else first.get(start)
                e = first.get(end)
                if s is not None and e is not None:
                    deltas.append(e - s)
            if deltas:
                out.append(f"  {start} -> {end}: {statistics.mean(deltas):.1f} ({min(deltas):.1f}-{max(deltas):.1f}) n={len(deltas)}")
    return "\n".join(out)


STAGES = [("@0", "handler"), ("handler", "card-dismissed"), ("card-dismissed", "guard-passed"), ("guard-passed", "start"),
          ("start", "windows-read"), ("windows-read", "acquire-started"), ("acquire-started", "window-constructed"),
          ("window-constructed", "source-initialized"), ("source-initialized", "hwnd-created-exclusion-applied"),
          ("hwnd-created-exclusion-applied", "hidden-window-ready"), ("acquire-started", "grab-start"), ("grab-start", "grab-done"),
          ("grab-done", "frame-dispatched"), ("hidden-window-ready", "frame-dispatched"), ("frame-dispatched", "frame-attached"),
          ("frame-attached", "shown"), ("shown", "activated"), ("activated", "view-first-measure"),
          ("view-first-measure", "view-render-start"), ("view-render-start", "view-render-done"),
          ("view-render-done", "probe-first-rendering-tick"), ("activated", "probe-first-rendering-tick"),
          ("probe-first-rendering-tick", "content-rendered"), ("@0", "probe-first-rendering-tick"), ("@0", "content-rendered")]


def main():
    sys.stdout.reconfigure(encoding="utf-8")
    parser = argparse.ArgumentParser()
    parser.add_argument("paths", nargs="+")
    parser.add_argument("--regions", type=float, default=None, help="list allocations at least this many MB")
    parser.add_argument("--breakdown", action="store_true")
    parser.add_argument("--stages", action="store_true")
    options = parser.parse_args()
    reports = [load(p) for p in options.paths]
    for path, report in zip(options.paths, reports):
        print(f"\n### {path} — {report.get('Build')} variants {report.get('Variants')} software {report.get('SoftwareRender')}")
        if report.get("Audits"):
            print(audit_table(report))
            if options.regions is not None:
                print(region_lists(report, options.regions))
        if report.get("Failures"):
            print("FAILURES:", report["Failures"])
    if options.breakdown:
        print(breakdown_table(reports))
    if options.stages:
        for name in sorted({b["Name"] for r in reports for b in r.get("Breakdowns", [])}):
            print(stage_summary(reports, name, STAGES))


if __name__ == "__main__":
    main()
