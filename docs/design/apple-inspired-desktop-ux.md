# MyCapture Apple-inspired desktop UX direction

Status: implementation source of truth  
Scope: editor, capture selector, settings, gallery, OCR, pin, countdown  
Platform: Windows WPF (Apple principles adapted to Windows conventions; no imitation of macOS chrome)

## Research basis

Apple's official *Essential Design Principles* session frames interfaces as human interfaces and emphasizes:

- **Feedback:** every action needs clear, immediate, understandable confirmation; status, completion, warning, and error feedback answer “what can I do, what happened, what is happening, what happens next?”
- **Visibility:** key controls and state must remain discoverable; hiding primary navigation or status to make a screen look cleaner reduces usability.
- **Consistency:** use the platform's familiar symbols, terminology, locations, and workflows; internal icon, type, color, and control consistency makes the product feel deliberate.
- **Mapping and direct manipulation:** controls should be placed near what they affect, and manipulating the object directly with the pointer is preferable to indirect settings.
- **Wayfinding and progressive context:** start with understandable destinations, then reveal increasingly specific controls in the current context.

Apple's toolbar guidance recommends simple recognizable symbols and short descriptive labels, grouping related items, and reserving prominence for primary actions. Its macOS guidance also assumes resizable, multiwindow desktop workflows and concurrent keyboard/pointer use.

Official references:

- https://developer.apple.com/videos/play/wwdc2017/802/
- https://developer.apple.com/design/human-interface-guidelines/toolbars
- https://developer.apple.com/design/human-interface-guidelines/icons
- https://developer.apple.com/design/human-interface-guidelines/windows

The local WPF audit additionally requires keyboard access, visible focus, automation names, high-contrast-safe boundaries, and no mouse-only command.

## Current audit

### Editor — critical

- Pointer events are registered on `_viewport`, but mouse capture/release are called on the outer `AnnotationEditorControl`. A real rectangle drag persists a `0×0` layer and leaves Undo disabled.
- Six text tool buttons, three history/object buttons, six color swatches, a slider, and three commit buttons compete in one scrolling row.
- Tool state only changes border color; there is no plain-language instruction or completion feedback.
- Color/thickness controls are always visible even when they do not apply.
- Save commands are only mentioned in tiny footer text; action hierarchy is unclear.
- There is no coherent icon vocabulary.

### Settings

- Navigation is text-only and visually detached from the page title.
- A duplicate “Ctrl+S 적용” badge competes with the actual Apply button.
- Local button templates duplicate shared controls and change border thickness on focus, causing layout instability.
- A large violet callout overemphasizes routine explanatory content.

### Gallery

- Every card exposes five permanent text buttons in two rows, reducing thumbnail prominence and increasing scanning cost.
- Destructive Delete has equal visibility to primary Edit.
- English all-caps eyebrow/copy labels conflict with otherwise Korean UI and add visual noise.
- Search and drag export are useful but over-framed.

### OCR, pin, countdown, overlay

- OCR has explicit dark header/footer but an unowned transparent middle root that can reveal a white system background.
- OCR and countdown use English all-caps eyebrows without functional value.
- Pin discoverability relies heavily on a long tooltip/context menu; status is transient.
- Capture overlay instructions are clear, but decorative treatment must remain subordinate to the frozen content.

## Product design principles

1. **Content first.** Captured pixels occupy the largest, darkest, least decorated region.
2. **One primary action per context.** Editor: Done. OCR: Copy. Settings: Apply. Gallery card: Edit.
3. **Tools communicate state.** Selected symbol, label/tooltip, cursor, contextual inspector, and a live status sentence all agree.
4. **Direct manipulation is never silent.** Creation previews live; release selects the object; Undo enables; status confirms the result.
5. **Progressive disclosure.** Secondary save/export/delete actions move to contextual or overflow menus, while keyboard shortcuts remain.
6. **Windows-native familiarity.** Keep normal Windows title bars, taskbar behavior, focus order, high contrast, and standard shortcuts. Use original WPF vector symbols rather than copying SF Symbols.
7. **Calm hierarchy.** Neutral graphite surfaces, one system-blue accent, restrained borders/shadows, sentence case, and no decorative gradients.
8. **Accessible equivalence.** Every symbol has an AutomationProperties name and tooltip; every pointer action has a keyboard/button alternative; color is never the only state cue.

## Visual system

- Canvas: `#080A0E`; app base: `#0E1117`; raised: `#151922`; overlay: `#1C222D`; hover: `#252D39`.
- Text: primary `#F4F7FB`; secondary `#C7CED8`; muted `#8D98A8`.
- Accent: system blue `#3B82F6`; hover `#60A5FA`; pressed `#2563EB`; subtle `#172A46`.
- Success `#34C759`, warning `#FFB020`, destructive `#FF5A67`.
- 4px base spacing; principal steps 8/12/16/24/32.
- Radius 8 (controls), 12 (panels), 16 (floating only). No pill shape except tags/status.
- Typography: Segoe UI Variable; 12 caption, 13 control, 14 body, 17 section, 24 title. Mono only for dimensions/timing/shortcuts.
- Original outline symbol family: 20×20 viewbox, round line caps/joins, visually consistent 1.7px strokes.
- Motion: only 90–160ms color/opacity transitions; obey system animation preferences.

## Editor information architecture

```
┌ document context / history ───────────────────── Cancel  Copy  Done ┐
├ tools (56) ┬──────────── selected-image canvas ─────────┬ inspector ┤
│ select     │                                             │ Tool      │
│ rectangle  │              direct manipulation            │ color     │
│ arrow      │                                             │ thickness │
│ pen        │                                             │ guidance  │
│ text       │                                             │ delete    │
│ image      │                                             │           │
├────────────┴─────────────────────────────────────────────┴───────────┤
│ live instruction / completion status        dimensions · shortcuts │
└─────────────────────────────────────────────────────────────────────┘
```

- Top command bar: document label and Undo/Redo on the left; Cancel, Copy, Done on the right.
- Left rail: six vector tool buttons with selected indicator, tooltip, shortcut, and automation name.
- Center: image-only viewport; crosshair/move/resize cursors map directly to current manipulation.
- Right inspector: selected tool/object name, contextual instruction, swatches, thickness only where applicable, and object Delete. Collapse below a practical width rather than forcing horizontal scroll.
- Bottom: live status (“사각형 도구 · 이미지 위를 드래그하세요”, then “사각형을 추가했습니다 · Ctrl+Z로 취소”) plus pixel dimensions and compact shortcuts.
- Save/Save As remain keyboard commands and are grouped in a single overflow/export menu rather than footer prose.

## Secondary-window changes

- Settings: icon+label navigation, remove duplicate shortcut badge, shared buttons only, neutral information card, aligned field widths.
- Gallery: larger image-first card, one visible Edit action, Copy/Pin icon actions, OCR/Delete in overflow; remove uppercase eyebrow and permanent red Delete.
- OCR: uniform dark root, sentence-case title, compact status chip, text workspace, Copy as sole primary action.
- Countdown: number + Korean context only, polite live announcement, no decorative English label.
- Pin: restrained blue hover edge, concise contextual menu with consistent symbols, persistent accessible recovery path for click-through.
- Overlay: preserve frozen-frame priority and immediate selection feedback; no ornamental effects that obscure pixels.

## Acceptance criteria

- Real native mouse down/move/up creates a rectangle larger than 20×20 px, visibly changes rendered pixels, enables Undo, and persists an editable layer.
- Arrow and pen gestures, text placement/commit, image insertion, selection move/resize/delete, Undo/Redo, Copy, Done, Quick Save, and Save As retain existing semantics.
- Tool selection always updates symbol state, cursor, contextual inspector, and live instruction.
- All icon-only controls expose a name, tooltip, focus indication, and keyboard route.
- No horizontal toolbar scrolling at the default editor size.
- High-level windows share the same tokens, typography, action hierarchy, and focus behavior.
- Physical-pixel coordinates, selected-image-only rendering, PerMonitorV2, persistence, clipboard, gallery re-edit, and tray lifecycle remain unchanged.
