# MyCapture visual direction (superseded palette note)

This filename is retained so historical links do not break. The warm-yellow/charcoal
palette used by early 1.x builds has been superseded by the **Focus Portal** identity.
The authoritative specification is [`design-system/mycapture/MASTER.md`](../../design-system/mycapture/MASTER.md).

## Current direction

- Quiet precision graphite surfaces keep screenshots and video frames visually dominant.
- Focus cyan identifies selection, keyboard focus, the current tool, and the one primary action.
- Amber, emerald, and coral are reserved for capturing, busy/success, and error/destructive state.
- The two-offset-pane Focus Portal replaces the generic crop-corners-and-dot app symbol.
- All WPF views consume semantic resources from `Themes/Tokens.xaml`; raw colours are limited
  to documented fallbacks and content-generated media.

## Compatibility

Legacy resource names such as `Primitive.Warm*`, `Primitive.Yellow*`, `Accent.Cool`, and
`Accent.Gradient` remain available so restored XAML and plug-in views do not fail at runtime.
They now alias the Focus Blue/graphite system and should not be used as the vocabulary for
new components.

## Guardrails

- Dark accent text uses `Text.OnAccent`; white-on-cyan is not allowed for normal-size labels.
- Body text/background pairs target WCAG AA; focus and non-text indicators target at least 3:1.
- Interactive state is not communicated by colour alone, and every drag operation keeps a
  keyboard or button alternative.
- Motion uses the shared 83/167/250 ms cadence, transform/opacity only, and respects the
  Windows reduced-animation setting.
