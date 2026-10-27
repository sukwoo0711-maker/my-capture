"""Shortcut → selector breakdown tables from resident-probe.json runs with SelectorTimeline marks (diag-490).

    python -I -X utf8 breakdown.py <run folder> [<run folder> ...]
"""
import json
import statistics
import sys
from pathlib import Path

STAGES = [
    ('@0', 'handler', 'handler dispatched'),
    ('handler', 'guard-passed', 'card dismiss + capture guard'),
    ('guard-passed', 'start', 'tray icon state'),
    ('start', 'windows-read', 'window list (z-order, titles)'),
    ('windows-read', 'acquire-started', 'page/app readers started, grab scheduled'),
    ('acquire-started', 'window-constructed', 'selector window + view constructed'),
    ('window-constructed', 'source-initialized', 'HWND + HwndSource created (EnsureHandle)'),
    ('source-initialized', 'hidden-window-ready', 'capture exclusion, rest of ShowOverlay'),
    ('acquire-started', 'grab-start', '[worker] wait before grab (settle delay)'),
    ('grab-start', 'grab-done', '[worker] synthetic grab + bitmap'),
    ('hidden-window-ready', 'frame-dispatched', 'UI waits for frame / dispatch'),
    ('frame-dispatched', 'view-render-done', 'attach frame, Show: layout + OnRender'),
    ('view-render-done', 'msg-0x0007', 'Show: ShowWindow, activation, IME, WM_SETFOCUS'),
    ('msg-0x0007', 'msg-0x0005', 'Show: focus handling until WM_SIZE'),
    ('msg-0x0005', 'probe-first-rendering-tick', 'Show: until first render pass (gate metric)'),
    ('probe-first-rendering-tick', 'shown', 'Show returns'),
    ('shown', 'render-wait-start', 'UI busy before next dispatcher turn'),
    ('render-wait-start', 'render-thread-complete', 'render thread still finishing frame 1'),
    ('@0', 'probe-first-rendering-tick', 'TOTAL: shortcut -> first render pass (gate metric)'),
    ('@0', 'render-thread-complete', 'TOTAL: -> render thread done with frame 1 (upper bound of present)'),
    ('@0', 'content-rendered', 'TOTAL: -> ContentRendered (selector takes focus)'),
]


def first(breakdown):
    marks = {}
    for mark in breakdown['Marks']:
        marks.setdefault(mark['Name'], mark['Milliseconds'])
    return marks


def table(title, items):
    print(f'\n#### {title}, n={len(items)}')
    print('| stage | mean ms | min–max |')
    print('|---|---|---|')
    for start, end, label in STAGES:
        values = []
        for item in items:
            marks = first(item)
            if end in marks and (start == '@0' or start in marks):
                values.append(marks[end] - (0 if start == '@0' else marks[start]))
        if values:
            print(f'| {label} (`{start}`→`{end}`) | {statistics.mean(values):.1f} | {min(values):.1f}–{max(values):.1f} |')
    operations = {}
    for item in items:
        for mark in item['Marks']:
            if mark['Name'].startswith('op '):
                name = mark['Name'].split(' from ')[0][3:]
                operations.setdefault(name, []).append(float(mark['Name'].rsplit(' for ', 1)[1]))
    if operations:
        print('\nDispatcher operations of 2 ms or more (name [priority]: per-run count, mean ms, max ms):')
        for name, durations in sorted(operations.items(), key=lambda kv: -sum(kv[1]))[:10]:
            print(f'- {name}: {len(durations) / len(items):.1f}, {statistics.mean(durations):.1f}, {max(durations):.1f}')


def main():
    sys.stdout.reconfigure(encoding='utf-8')
    runs = []
    for arg in sys.argv[1:]:
        path = Path(arg)
        runs.append(json.loads((path / 'resident-probe.json' if path.is_dir() else path).read_text(encoding='utf-8')))
    every = [b for run in runs for b in run.get('Breakdowns', [])]
    table('first capture after start (cold)', [b for b in every if b['Name'] == 'capture'])
    table('capture-30 #1 (warm, more than 750 ms after the last dismissal)',
          [b for b in every if b['Name'] == 'capture-30' and b['Index'] == 1])
    table('capture-30 #2-30 (card closed less than 750 ms before)',
          [b for b in every if b['Name'] == 'capture-30' and b['Index'] > 1])
    table('editor closed -> next shortcut', [b for b in every if b['Name'] == 'editor-4k next'])


if __name__ == '__main__':
    main()
