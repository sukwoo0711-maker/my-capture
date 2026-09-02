# MyCapture visual system

MyCapture is a precision Windows capture tool. Its interface should feel quiet while the
captured pixels stay dominant, then become unmistakably branded at the moments that need
attention: selection, focus, recording state, and the primary action.

## Brand idea: Focus Portal

The mark is two offset panes. The rear pane represents the captured source; the forward
pane represents the region that becomes a floating, editable MyCapture object. It remains
recognisable at 16 px and avoids the generic four-corner crop-frame symbol.

- App plate: graphite rounded square, cyan rear pane, near-white foreground pane.
- Idle/focus: cyan `#58C7F3`.
- Capturing: amber `#F5B942`.
- Busy/exporting: emerald `#45D6A2`.
- Error: coral `#FF6B74`.
- The state is never communicated by colour alone; tooltips and status text remain present.

## Three token layers

### Primitive

- Graphite 980 `#080C12`, 950 `#0B0F17`, 900 `#101722`, 850 `#151E2B`
- Graphite 800 `#1B2636`, 700 `#243246`, 600 `#475973`, 500 `#2B3A50`
- Text 400 `#8E9CAF`, 300 `#C6D0DF`, 100 `#F6F8FC`
- Focus 300 `#7DD7F8`, 400 `#58C7F3`, 600 `#38A8D8`, 900 `#152F3E`

### Semantic

- Canvas/Base/Raised/Overlay form a restrained four-step depth ladder.
- Accent is reserved for the single primary action, current selection, and keyboard focus.
- Text.Primary, Text.Secondary, and Text.Muted carry hierarchy without oversized typography.
- Warning, Success, and Danger are operational states, not decorative accents.
- Timeline layer colours are semantic and distinct in both hue and hatch/label treatment.

### Component

- Hit targets are at least 36 px; compact controls are 32 px only where keyboard alternatives
  and surrounding spacing are available.
- Buttons, icon buttons, tabs, timeline handles, and cards all expose default, hover, pressed,
  focused, selected, and disabled states through shared resources.
- Keyboard focus is an inset 1 px high-contrast ring so layout never shifts.

## Typography and density

- UI: Segoe UI Variable Text, Segoe UI, Malgun Gothic. These are local/system fonts so the
  self-contained application remains fully offline.
- Display: Segoe UI Variable Display; timers and frame values use Cascadia Mono/Consolas.
- Scale: 12 / 13 / 14 / 17 / 24 / 28 px.
- Spacing follows a 4 px base with primary steps at 8 / 12 / 16 / 24 / 32 px.

## Command family

The three primary workflow keys share one memorable modifier family:

- `Ctrl+Shift+C` — capture a region.
- `Ctrl+Shift+X` — start or stop region recording.
- `Ctrl+Shift+Z` — open the library.

Product copy, tooltips, onboarding, and future command surfaces must keep this C/X/Z family
together. A lone `Ctrl+X` must never be described as the recording default because Windows
users reasonably read it as Cut.

## Motion

- Fast 83 ms for press/state feedback, normal 167 ms for window entry, deliberate 250 ms only
  for larger contextual changes.
- Animate opacity and transforms, never capture geometry or timeline dimensions.
- Motion stays interruptible and respects Windows reduced-animation settings.

## Accessibility guardrails

- Normal text pairs target WCAG AA (4.5:1); focus and non-text indicators target at least 3:1.
- Icon-only controls retain descriptive AutomationProperties.Name values and tooltips.
- Colour is reinforced by labels, shapes, or hatching. Drag handles retain buttons/keyboard
  alternatives, and focus must remain visible while timelines scroll.
