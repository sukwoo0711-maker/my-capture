# MyCapture visual system

MyCapture is a precision Windows capture tool. Quiet surfaces keep captured pixels dominant;
shared semantic resources give actions, selection and status a consistent hierarchy.
Read this file before design or UI work: its values are checked against the code (see the last section).

## Brand idea: Iris Portal

The Iris Portal artwork is two offset panes on a graphite plate: an iris rear pane for the captured
source and a near-white forward pane for the floating, editable object. The current M06 "fixed corner"
mark is an open capture frame with a pin; its geometry and colours are in `docs/design/brand/README.md`.
The mark keeps its supplied colours: changing the UI accent never recolours the artwork.

The default UI accent is pin red: fill `#E0453C`, hover `#F35B4D`, pressed `#DA433A`, ink `#0A0818`.
Violet is the alternative accent. Focus and selection use neutral outlines.
`ThemeService` computes accessible accent foregrounds per theme. The six themes are Midnight, Glass,
Workspace, Daylight, GlassLight and HighContrast; System follows Windows. Windows high contrast takes
precedence and uses the user's system colours.

## Token layers and baseline values

Primitive → Semantic → Component. Primitive colours (`Primitive.*`) feed role brushes, which feed control
dimensions and styles. The active neutral ramp is `Primitive.Warm*`: 980/950/900/850 are `#080C12`/`#0B0F17`/
`#101722`/`#151E2B`; 600/550 are `#6A7B94`/`#56677F`. Legacy primitive names remain compatibility aliases.
Semantic roles are what UI code uses: `Surface.*` is a four-step depth ladder (Canvas, Base, Raised, Overlay),
`Text.*` carries hierarchy, `State.*` marks warning, success and danger (timeline layers included), and
`Accent.*` belongs to the approved consumers that `AccentUsagePolicyTests` lists. Component values are the
`Size.*`, `Space.*`, `Radius.*`, `Focus.*` and `Motion.*` tokens plus the control styles in `Controls.xaml`.
The table lists the resolved `Tokens.xaml` defaults before runtime palette overrides; sizes are DIP.

<!-- tokens:begin -->
| Token | Value |
| --- | --- |
| Accent.Default | #E0453C |
| Accent.Hover | #F35B4D |
| Accent.Pressed | #DA433A |
| Text.OnAccent | #0A0818 |
| Text.Primary | #FFFFFF |
| Text.Secondary | #C9D1DD |
| Text.Muted | #9AA5B5 |
| Surface.Canvas | #080C12 |
| Surface.Base | #0B0F17 |
| Surface.Raised | #101722 |
| Surface.Overlay | #151E2B |
| State.Warning | #F5B942 |
| State.Success | #45D6A2 |
| State.Danger | #FF6B74 |
| Border.Focus | #FFFFFF |
| Focus.RingWidth | 2 |
| Size.HitTarget | 36 |
| Size.HitTarget.Compact | 32 |
| Size.RailButton | 44 |
| Size.IconGlyph | 16 |
| Motion.Fast | 83 ms |
| Motion.Normal | 167 ms |
| Motion.Deliberate | 250 ms |
| Space.Xs | 4 |
| Space.Sm | 8 |
| Space.Md | 12 |
| Space.Lg | 16 |
| Space.Xl | 24 |
| Space.Xxl | 32 |
| Space.Hero | 40 |
| Radius.Sm | 6 |
| Radius.Md | 8 |
| Radius.Lg | 12 |
| Radius.Xl | 16 |
| FontSize.Micro | 11 |
| FontSize.Caption | 12 |
| FontSize.Control | 14 |
| FontSize.Body | 14 |
| FontSize.BodyLarge | 16 |
| FontSize.Subtitle | 20 |
| FontSize.Title | 28 |
| FontSize.Display | 28 |
<!-- tokens:end -->

## Typography

`ThemeFonts` (`Font.Ui`) uses the bundled Pretendard (Regular, Medium, SemiBold, Bold), then
Segoe UI Variable Text, Segoe UI, Malgun Gothic and sans-serif. `Font.Display` uses the same bundle
with Segoe UI Variable Display in its fallback chain. Text uses Ideal formatting, grayscale
antialiasing, no hinting and literal shaping; call `ThemeFonts.ApplyText` on a surface that draws
text. `Font.Mono` is Cascadia Mono, then Consolas, Malgun Gothic and monospace.

| TypeScale role | DIP | Use |
| --- | --- | --- |
| Micro | 11 | Latin key caps, numeric badges, file-type chips; never Korean text |
| Caption | 12 | Secondary text, status, metadata, compact editor controls |
| Body / Control | 14 | Primary reading text, standard controls, section labels |
| BodyLarge | 16 | Emphasized values, panel headings, message titles |
| Subtitle | 20 | Dialog and secondary-window titles, illustrative key caps |
| Title / Display | 28 | Page and hero titles, large illustration glyphs |

Code uses `TypeScale.X`; XAML uses `FontSize.X`. No numeric font size lives outside `Themes/` (and
`Diagnostics/`): `TypeScalePolicyTests` fails when one appears.
Document and drawing sizes are separate `TypeScale.Content` values, not UI roles: Markdown body, code and
fallback 15/14/13, headings 28/23/19/16; plain-text pins 16, table pins 14, code pins 15; the recording
wall clock defaults to 13, ranges from 10 to 28 and changes by 1. Keep their geometry; never remap them.

## Korean text and product copy

Korean copy that wraps goes through `KoreanWordWrap`, so a word never splits across lines: `{loc:Text}`
and `KoreanWordWrap.Text` in XAML, `KoreanWordWrap.Show(block, text)` in code (also for later status
updates) and `KoreanWordWrap.ShowInlines` for blocks of runs and links. A one-syllable last word joins the
previous word ("자동 저장 켬" never leaves "켬" alone). Accessible names stay plain. User content (OCR, pin
and file text), text boxes and per-item rows of long lists stay plain. `KoreanWordWrapSweepTests` lays out
the windows at default and minimum size and fails on an unjoined block or a one-syllable last line.
Labels and buttons are nouns (저장, 불러오는 중, 저장됨); sentences use 합니다/하십시오체. Never use 해요체,
exclamation marks, interjections or emoji. Write 내려받기, not 다운로드. Add English beside Korean in
`Strings*.resx`, next to related keys rather than at the end.

## Controls, motion and accessibility

Use the spacing and radius tokens above. Hit targets are 36 DIP, compact controls 32 (only with
surrounding space and a keyboard alternative), rail buttons 44, icon glyphs 16. The 2-DIP focus ring is an
overlay, so focus never shifts layout. Buttons, icon buttons, tabs, timeline handles and cards expose
default, hover, pressed, focused, selected and disabled states through shared resources.
Motion is 83/167/250 ms, animates opacity and transforms only (never capture geometry or timeline
dimensions), stays interruptible and respects reduced animation.
Text meets WCAG AA (4.5:1); focus and non-text indicators meet 3:1. Every icon-only control has an
accessible name and a tooltip. Colour is never the only signal: keep labels, shapes or hatching.

## Commands and settled product decisions

New-install defaults (`NewInstallDefaults`, `AppSettings`): `Ctrl+Shift+C` captures a region,
`Ctrl+Shift+X` starts or stops region recording, `F3` pins clipboard content, `Shift+F3` hides or shows
the pins. The library and upload have no shortcut on new installs; existing users keep `Ctrl+Shift+Z` and
`F9`. A lone `Ctrl+X` is never presented as the recording default: Windows users read it as Cut.
Reviews must not reverse these decisions: editor Ctrl+C copies and closes; Alt reveals shortcut labels;
pin hover-bar labels all show or all hide; there are no pin boards. Prefer the smallest change to existing
UI over new overlays or badges. Follow Windows conventions: Explorer menu order, Properties last, and an
icon click opens what that icon shows.

## How this file stays true

`DesignGuidelinesSyncTests` fails when a value in the token table differs from the resolved
`Tokens.xaml` resource, when a `FontSize.*` key or one of the core role keys is missing from it, or when
a font family of the `ThemeFonts` UI chain is not named here. Change a token, font or ramp step and this
file in the same commit.
