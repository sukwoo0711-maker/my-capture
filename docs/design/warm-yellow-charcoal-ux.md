# MyCapture warm-yellow / charcoal desktop UX

Status: implementation source of truth for 0.4.0  
Scope: editor, capture selector, settings, gallery, OCR, pin, countdown, recording  
Platform: Windows WPF, PerMonitorV2, keyboard and pointer

## Identity

MyCapture uses the emotional qualities of familiar Korean consumer messaging products—warmth,
clarity, friendliness, and quick recognition—without copying KakaoTalk UI, proprietary icons,
characters, layouts, or its exact signature colour. The product identity is its own: captured
content sits on warm charcoal, while a saturated sunflower yellow marks the one action or state
that needs attention.

The visual system is deliberately offline-safe. It uses local Windows fonts, original WPF vector
icons, solid colour resources, and no downloaded assets or runtime web content.

## Product principles

1. **Captured content first.** Screenshots and video occupy the largest, deepest surface. Chrome
   never competes with the pixels being edited.
2. **One primary action per context.** Editor: Done. OCR: Copy. Settings: Apply. Gallery tile:
   Edit. Recording: Start/Stop.
3. **Warm, not decorative.** Yellow communicates action, selection, focus, or identity; it is not
   used as a large background wash or ornamental gradient.
4. **Dark ink on yellow.** White text on saturated yellow fails normal-text contrast. Every
   yellow-filled button, checkbox, badge, and glyph uses `Text.OnAccent` (`#191207`).
5. **State never depends on colour alone.** Selection also has an indicator, border/background,
   label or tooltip, and exposed automation state.
6. **Stable interaction.** Hover, pressed, selected, and focus states never change layout bounds
   or border thickness. Focus remains visible for keyboard users.
7. **Direct manipulation is never silent.** Live preview, cursor, contextual inspector, Undo
   availability, and polite status text all confirm what happened.
8. **Progressive disclosure.** Secondary save/export/OCR/delete actions use contextual or overflow
   menus while their keyboard routes remain available.
9. **Windows-native behaviour.** Keep title bars, taskbar behaviour, standard shortcuts, logical
   focus order, high-contrast-safe boundaries, and PerMonitorV2 coordinates.

## Visual system

### Primitive palette

| Role | Value | Purpose |
|---|---:|---|
| Warm 980 | `#14110C` | canvas / deepest content well |
| Warm 950 | `#1B1712` | application base |
| Warm 900 | `#221D17` | rails, cards, inspector |
| Warm 850 | `#2A241C` | controls and overlays |
| Warm 800 | `#342D23` | hover |
| Warm 700 | `#3E362A` | pressed |
| Warm 600 | `#6B6152` | strong boundary |
| Warm 500 | `#40382C` | subtle divider |
| Warm 400 | `#A79A86` | muted text |
| Warm 300 | `#D8CFC0` | secondary text |
| Warm 100 | `#F5F1E8` | primary text |
| Yellow 300 | `#FFE14D` | keyboard focus / selected glyph |
| Yellow 400 | `#FFD400` | primary action |
| Yellow 500 | `#F7C948` | accent boundary |
| Yellow 600 | `#E0AE1F` | pressed action |
| Yellow 900 | `#3A2E10` | selected wash |
| Ink | `#191207` | text/glyph on yellow |

Warning uses warm orange (`#F09A4A`) so brand yellow never ambiguously means caution. Success
uses `#5BC58A`; destructive actions use `#E45C52` / `#FF8178`.

### Accessibility pairings

Automated tests must enforce:

- Ink on Accent Default, Hover, and Pressed: **>= 4.5:1**.
- Primary text on Base: **>= 7:1**.
- Secondary text on Base: **>= 4.5:1**.
- Muted text on Base, Sunken, and Raised: **>= 4.5:1**.
- Focus on Base and Raised: **>= 3:1** non-text contrast.
- Selection border against adjacent canvas/raised surfaces: **>= 3:1**.
- `Accent.Gradient` remains a flat `SolidColorBrush` compatibility alias.

### Shape, type, and rhythm

- 4px spacing base; principal steps 8 / 12 / 16 / 24 / 32.
- Radius 7 for inset details, 10 for controls, 14 for panels/cards, 18 for floating windows.
  Pill radius is reserved for status and tags.
- Segoe UI Variable with Malgun Gothic fallback; Cascadia Mono/Consolas only for dimensions,
  timing, and shortcuts. No network font loading.
- Original 20x20 outline icon family, round caps/joins, consistent 1.6px visual stroke.
- Motion is limited to short colour/opacity feedback and must respect Windows animation settings.

## Editor information architecture

```text
┌ document context / history ───────────────────── Cancel  Copy  Done ┐
├ tools (52) ┬──────────── selected-image canvas ─────────┬ inspector ┤
│ select     │                                             │ Tool      │
│ rectangle  │              direct manipulation            │ colour    │
│ arrow      │                                             │ thickness │
│ pen        │                                             │ guidance  │
│ text       │                                             │ delete    │
│ image      │                                             │           │
├────────────┴─────────────────────────────────────────────┴───────────┤
│ live instruction / completion status        dimensions · shortcuts │
└─────────────────────────────────────────────────────────────────────┘
```

The 52px rail, six 40px icon targets, 232px inspector, 20x20 icon canvases, 1.6px strokes,
980x620 default host, and no-scroll body are compatibility invariants. The inspector collapses
before the canvas becomes unusable. Annotation colour swatches remain expressive drawing colours;
they are not brand chrome and therefore retain red, yellow, green, blue, dark, and white choices.

## Secondary windows

- **Settings:** icon+label navigation; selected row has a yellow indicator, warm selected wash,
  visible border, and semibold label. Shared controls own all focus behaviour.
- **Gallery:** image-first 244px cards on 256px tracks; four cards fill the default window and up
  to six fill wide desktops. Edit remains primary; Copy/Pin are compact; OCR/Delete stay in More.
- **OCR:** uniform warm root and text workspace; Copy is the sole primary action.
- **Countdown:** number plus Korean context on one warm floating surface with polite live updates.
- **Pin:** neutral warm edge at rest and a restrained yellow hover/focus cue.
- **Overlay:** frozen pixels remain dominant; yellow selection and handles provide immediate,
  high-contrast feedback without obscuring the sample.
- **Recording:** yellow region boundary and primary control share the same semantic tokens; video
  preview remains in the deepest canvas surface.

## Acceptance criteria

- Native mouse down/move/up creates a nondegenerate editable annotation, changes rendered pixels,
  enables Undo, and survives persistence/re-edit.
- All existing capture, annotation, layer, clipboard, save/export, gallery, OCR, pin, recording,
  and single-instance workflows remain functional.
- Tool selection updates indicator, glyph, cursor, inspector, and live instruction.
- Every icon-only control has an accessible name, tooltip, focus indication, and keyboard route.
- Yellow fill never uses white text; all documented contrast thresholds pass executable tests.
- No cool-blue brand chrome or Apple-inspired wording remains in active UI/design sources.
- Default editor and minimum settings/gallery sizes show no clipped controls or hidden actions.
