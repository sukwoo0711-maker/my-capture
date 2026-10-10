# MyCapture visual direction (superseded palette note)

This filename is retained so historical links do not break. The warm-yellow/charcoal
palette used by early 1.x builds was superseded by the Focus Portal identity, then by Iris,
and now by the **Corner Seal** identity with the Signal Magenta accent.
The authoritative specification is [`design-system/mycapture/MASTER.md`](../../design-system/mycapture/MASTER.md).

## Current direction

- Quiet precision graphite surfaces keep screenshots and video frames visually dominant.
- Signal Magenta identifies selection, keyboard focus, the capture guide, and the one primary
  action. It is absent from the annotation swatches and far in hue from the violet/blue chrome
  of the apps people capture.
- Amber, emerald, and red are reserved for capturing, busy/success, and error/destructive state.
- The Corner Seal (an open selection frame with a seal at the release corner) replaces the
  two-offset-pane mark, which read as the Copy glyph.
- All WPF views consume semantic resources from `Themes/Tokens.xaml`; raw colours are limited
  to documented fallbacks and content-generated media.

## Compatibility

Legacy resource names such as `Primitive.Warm*`, `Primitive.Yellow*`, `Accent.Cool`, and
`Accent.Gradient` remain available so restored XAML and plug-in views do not fail at runtime.
They now alias the Signal Magenta/graphite system and should not be used as the vocabulary for
new components.

## Guardrails

- Dark accent text uses `Text.OnAccent`; white-on-magenta is not allowed for normal-size labels.
- Body text/background pairs target WCAG AA; focus and non-text indicators target at least 3:1.
- Interactive state is not communicated by colour alone, and every drag operation keeps a
  keyboard or button alternative.
- Motion uses the shared 83/167/250 ms cadence, transform/opacity only, and respects the
  Windows reduced-animation setting.
