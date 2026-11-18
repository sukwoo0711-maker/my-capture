# MyCapture visual system

MyCapture is a precision Windows capture tool. Quiet surfaces keep captured pixels dominant;
shared semantic resources give actions, selection and status a consistent hierarchy.
Read this file before design or UI work: its values are checked against the code (see the last section).

## Brand idea: Iris Portal

The Iris Portal artwork is two offset panes on a graphite plate: an iris rear pane for the captured
source and a near-white forward pane for the floating, editable object. The current M06 "fixed corner"
mark is an open capture frame with a pin; its geometry and colours are in `docs/design/brand/README.md`.
The mark keeps its supplied colours: changing the UI accent never recolours the artwork.

The product has one UI accent, red. At runtime `AccentPalette.RedOverrides` (`AppAccent.cs`) gives every
theme except high contrast a deep red primary fill with white ink: fill `#C9352C`, hover `#BD3129`,
pressed `#B32E26`, ink `#FFFFFF` (5.2:1, and hover and pressed only darken). The brighter brand red
`#E0453C` stays on what marks the brand or a live recording over other apps: the pin outline and the
recording frame, dot and cue. Old `violet` settings are read as red. Focus and selection use neutral outlines; the capture selector's chosen mode is its one accent-filled toolbar button, and its magnifier rings the centre pixel in the accent.
The startup brushes in `Tokens.xaml` resolve through `PaletteColorExtension` to the canonical
`ThemeCatalog` palette, so startup and live theme changes share the same action and surface values. The six themes are Midnight, Glass,
Workspace, Daylight, GlassLight and HighContrast; System follows Windows. Windows high contrast takes
precedence and uses the user's system colours.

## Token layers and baseline values

Primitive → Semantic → Component. Primitive colours (`Primitive.*`) feed role brushes, which feed control
dimensions and styles. The neutral graphite ramp is Canvas `#111214`, Base `#191B1E`, Raised `#24272B`, Floating
`#30343A`. `ThemeCatalog` owns semantic colour values. `Primitive.Warm*`, `Primitive.Graphite*` and
`Primitive.Slate*` remain compatibility resources; new components consume semantic names.
Semantic roles are what UI code uses: `Surface.*` is a four-step depth ladder (Canvas, Base, Raised, Floating),
`Text.*` carries hierarchy, `State.*` marks warning, success and danger (timeline layers included; warning
and success words take `Text.Warning`/`Text.Success`, which reach 4.5:1 on the surfaces that carry them,
while glyphs, fills and outlines keep `State.*`), and
`Accent.*` belongs to the approved consumers that `AccentUsagePolicyTests` lists. Component values are the
`Size.*`, `Space.*`, `Radius.*`, `Focus.*` and `Motion.*` tokens plus the control styles in `Controls.xaml`.
The table lists the resolved dark startup resources from `ThemeCatalog`; sizes are DIP.

<!-- tokens:begin -->
| Token | Value |
| --- | --- |
| Accent.Default | #C9352C |
| Accent.Hover | #BD3129 |
| Accent.Pressed | #B32E26 |
| Text.OnAccent | #FFFFFF |
| Text.Primary | #FFFFFF |
| Text.Secondary | #C9CDD4 |
| Text.Muted | #A0A6AF |
| Text.Warning | #F5B942 |
| Text.Success | #45D6A2 |
| Surface.Canvas | #111214 |
| Surface.Base | #191B1E |
| Surface.Raised | #24272B |
| Surface.Overlay | #30343A |
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
| Surface.Floating | #30343A |
| Surface.Sunken | #111214 |
| Surface.Hover | #2B2E34 |
| Surface.Pressed | #32353A |
| Surface.Selected | #393C42 |
| Fill.Button | #24272B |
| Stroke.ButtonTop | #44494F |
| Stroke.ButtonBottom | #17191C |
| Fill.Input | #0DFFFFFF |
| Fill.InputHover | #14FFFFFF |
| Fill.InputActive | #111214 |
| Stroke.InputEdge | #8CFFFFFF |
| Stroke.CheckEdge | #8CFFFFFF |
| Stroke.Selected | #A0A6AF |
| Fill.ScrollThumb | #4DFFFFFF |
| Fill.ScrollThumbHover | #80FFFFFF |
| Fill.ScrollThumbPressed | #A6FFFFFF |
| Border.Subtle | #38F6F8FC |
| Border.Control | #858B94 |
| Border.Strong | #A0A6AF |
| Radius.Control | 6 |
| Radius.Floating | 8 |
| Radius.Content | 12 |
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
dimensions), stays interruptible and respects reduced animation. An intended exception: the delayed-capture
countdown's digit lands over 200 ms (`CountdownWindow.LandingDuration`), a beat longer than the 167 ms fade
it lands with, because it is the only thing moving on that surface. Another: the selector's hover frame glides
its geometry over 83/167 ms (`HoverGlide`), because it is a pointer-following cue, not captured geometry;
reduced animation snaps it.
Text meets WCAG AA (4.5:1); focus and non-text indicators meet 3:1. Every icon-only control has an
accessible name and a tooltip. Colour is never the only signal: keep labels, shapes or hatching.

## Commands and settled product decisions

New-install defaults (`NewInstallDefaults`, `AppSettings`): `Ctrl+Shift+C` captures a region,
`Ctrl+Shift+X` starts or stops region recording, `Ctrl+Shift+Z` opens the library, `F3` pins clipboard
content, `Shift+F3` hides or shows the pins, and upload has no shortcut. The first four are taken ahead
of other programs (`ForceCoreShortcuts`), and the first run says so in a "Core shortcuts" notice instead
of asking (2026-11-07 decision). An existing settings file keeps what it stores, and a value it lacks
reads back the earlier default: upload keeps `F9` and priority stays off. An upgrade that never answered
still gets the question. "Reset to defaults" restores these keys but keeps the user's answer, priority
included. A lone `Ctrl+X` is never presented as the recording default: Windows users read it as Cut.
Reviews must not reverse these decisions: editor Ctrl+C copies and closes; Alt reveals shortcut labels;
pin hover-bar labels all show or all hide; there are no pin boards. Prefer the smallest change to existing
UI over new overlays or badges. Follow Windows conventions: Explorer menu order, Properties last, and an
icon click opens what that icon shows.

## 6.0 foundation

Phases 1–5 consume this contract. They may compose existing controls in their assigned screens, add
area-local styles derived from the shared styles, and add evidence scenes in their own partial test
files. They must not redefine global colours, type sizes, control geometry, easing or state templates.
Change a foundation role only through this shared contract and its contrast, resource and focus tests.
`ThemeCatalog` owns colours, `FoundationMetrics` owns shared dimensions, `TypeScale` owns type sizes,
and `Tokens.xaml` exposes their resource names. `ThemeService` derives component aliases on theme changes;
`WorkspaceTheme` delegates to the same palettes. Captured pixels and document typography are outside
these roles and retain their original colours, coordinates and dimensions.

### Surfaces and colour roles

All four chrome surfaces are opaque, including the legacy Glass/GlassLight settings. This contract
does not change the system backdrop. Midnight, Glass and the dark Workspace use the dark column;
Daylight, GlassLight and the gallery Workspace use the light column. System resolves the user's
Windows preference; new installations continue to store System. The Windows contrast pair uses
system roles in either light-on-dark or dark-on-light configurations, never a derived brand tint.

| Role | Dark | Light | Windows high contrast | Use |
| --- | --- | --- | --- | --- |
| Surface.Canvas | #111214 | #E4E7EB | Window | The work area behind the original; never recolour the original itself. |
| Surface.Base | #191B1E | #EDF0F3 | Window | The main window plane and persistent chrome. |
| Surface.Raised | #24272B | #F7F8FA | Window | A subordinate control group that needs a distinct fill. |
| Surface.Floating | #30343A | #FFFFFF | Window | Result cards, popup menus, tooltips and detached floating chrome. |
| Surface.Overlay | Surface.Floating | Surface.Floating | ButtonFace | Compatibility name; new compositions use the four canonical roles. |
| Surface.Sunken | Surface.Canvas | Surface.Canvas | Window | Recessed inputs and existing inset content wells. |
| Surface.Hover | #2B2E34 | #DEE2E7 | Window | Pointer feedback; high contrast also exposes Border.Hover. |
| Surface.Pressed | #32353A | #D0D5DC | Window | A held press, separate from selection. |
| Surface.Selected | #393C42 | #C5CAD1 | Window | Neutral current-item wash. |
| Text.Primary | #FFFFFF | #101318 | WindowText | Main reading and commands. |
| Text.Secondary | #C9CDD4 | #343B46 | WindowText | Supporting labels. |
| Text.Muted | #A0A6AF | #4D5663 | WindowText | Metadata; still at least 4.5:1. |
| Text.Disabled | #7A8290 | #747C89 | GrayText | Disabled controls only. |
| Text.Warning / Text.Success | #F5B942 / #45D6A2 | #885E0C / #167451 | WindowText | Status words, separate from State.* glyph colours. |
| Accent.Default / Hover / Pressed | #C9352C / #BD3129 / #B32E26 | Same | Highlight | The primary action; the selector's existing approved current-mode exception remains. |
| Text.OnAccent | #FFFFFF | #FFFFFF | HighlightText | Ink on the action fill. |
| Border.Focus | Text.Primary | Text.Primary | WindowText | Keyboard-only focus, independent of selection. |
| Border.Control | #858B94 | #7B828C | ButtonText | An accessible boundary when a complete outline identifies the control. |
| Border.Strong | #A0A6AF | #626B78 | Highlight | A stronger boundary; never weaker than Border.Control. |
| Border.Subtle | #38F6F8FC | #20080E16 | WindowText | Decorative separators; not the identifying edge of an input. |
| Stroke.Selected | #A0A6AF | #626B78 | WindowText | The one-DIP neutral selected edge; at least 3:1 on its surroundings. |
| Fill.Button | Surface.Raised | #FFFFFF | ButtonFace | Shared command face. |
| Stroke.ButtonTop / Bottom | #44494F / #17191C | #D7DCE2 / #ADB5BF | ButtonText | The existing command-edge gradient, not a material/backdrop. |
| Fill.Input / InputHover / InputActive | #0DFFFFFF / #14FFFFFF / Surface.Sunken | #FFFFFF / #F9FAFC / #FFFFFF | Window | Input rest, pointer and active fills. |
| Stroke.InputEdge / Stroke.CheckEdge | #8CFFFFFF | #80080E16 | ButtonText | Input bottom edge and unchecked check-box outline. |
| Fill.ScrollThumb / Hover / Pressed | #4DFFFFFF / #80FFFFFF / #A6FFFFFF | #47080E16 / #73080E16 / #99080E16 | Highlight / WindowText / WindowText | Scroll-thumb states. |

WCAG relative-luminance measurements for Canvas → Base → Raised → Floating are 1.0860 / 1.1506 /
1.1985 in dark and 1.0845 / 1.0764 / 1.0626 in light. The distinction comes from the fills rather than
adding panel outlines. Primary text on Base is 17.2576:1 dark and 16.2705:1 light. Muted text's
lowest neutral-surface contrast is 4.5120:1 dark and 4.5078:1 light (Surface.Selected). The existing
primary-text 7:1, ordinary-text 4.5:1 and identifying-edge 3:1 thresholds remain unchanged. White ink
on crimson remains the approved 5.2:1 action pairing; it is distinct from the primary-text threshold.

Selection uses `Selection.Stroke` → Stroke.Selected and `Selection.Thickness` = 1 DIP. Keyboard focus
uses `Focus.RingThickness` / `Focus.RingWidth` = 2 DIP and `KeyboardFocusCue`; pointer input does not leave
a keyboard ring. In contrast themes selection keeps its existing 2-DIP geometry. Preserve the
library's approved 2-DIP crimson selection and filled check, colour-chip outlines, original-content
boundaries, crop handles and timeline category colours. `Pin.Frame` is the existing contrast-adjusted
BrandRed role; `Recording.Live` remains #E0453C. `Accent.Subtle` aliases Surface.Selected; `Accent.Cool`,
`Accent.Foreground`, `Accent.Indicator`, `Border.Accent` and `Border.SelectedTool` use neutral Text.Primary.
`Accent.Gradient` is a flat compatibility alias to Accent.Default. `Surface.Badge` / `Surface.BadgeStrong`
use Surface.Raised with `Text.Badge` = Text.Primary; this defines existing consumers, not new badges.
`Text.SelectionBackground` / `Text.SelectionForeground` preserve the opaque paired text-selection colours
(Highlight / HighlightText in Windows contrast themes).

### Type, spacing and shape

Regular is the default. Use SemiBold only for a title, current item or primary action, plus the
existing high-contrast control emphasis. The type resource and style names are stable:

| DIP | Size resources | Shared text styles | Purpose |
| --- | --- | --- | --- |
| 11 | FontSize.Micro | Text.Micro | Latin keys and compact numeric metadata; never Korean running text. |
| 12 | FontSize.Caption | Text.Caption, Text.MutedBlock, Text.Mono | Supporting text and metadata. |
| 14 | FontSize.Control, FontSize.Body | Text.Control, Text.Body | Commands, values and normal reading. |
| 16 | FontSize.BodyLarge | Text.BodyLarge | Group-level reading and emphasized values. |
| 20 | FontSize.Subtitle | Text.Subtitle, Text.Section | A window-internal or group title. |
| 28 | FontSize.Title, FontSize.Display | Text.Title, Text.Display | The rare large title; not ordinary controls. |

`Font.Ui`, `Font.Display` and `Font.Mono` retain the font chains above. `FontWeight.Regular` is Normal;
`FontWeight.Emphasis` is SemiBold. `FontWeight.ControlLabel` and `Button.FontWeight` are Normal except for
the preserved contrast-theme emphasis. `ControlLabelTypography` binds local chrome to that role;
it never changes source text or user document typography.

`Space.Xs` / `Sm` / `Md` / `Lg` / `Xl` / `Xxl` are 4 / 8 / 12 / 16 / 24 / 32 DIP, respectively.
`Padding.Control` is 12,8,12,8; `Padding.Panel` and `Padding.Card` are 16; `Padding.Toolbar` is 8.
`Size.HitTarget` is 36 and `Size.HitTarget.Compact` is 32. `Size.IconGlyph` is 16. Keep the existing
`Size.RailButton` = 44 and command-button hit-target exceptions where the current screen uses them.
`Space.Hero` = 40 remains for existing large-title compositions.

`Radius.Control` = 6 is the control radius (`Radius.Sm` compatibility alias); `Radius.Floating` = 8 is
the floating-group radius (`Radius.Md`); `Radius.Content` = 12 is the large-content-group radius
(`Radius.Lg`). Inner areas are not wrapped in additional rounded boxes. `Radius.Xl` = 16 and
`Radius.Pill` = 999 remain compatibility resources for their existing consumers. The small check
mark box keeps `Check.Radius` = 4 (5 in contrast mode); `Combo.Radius` uses Radius.Control. Source
image edges and precision handles keep their existing geometry.

### Shared controls and aliases

The implicit styles cover Window, Button, ToggleButton, TextBox, CheckBox, RadioButton, TabControl,
TabItem, ListBox, ListBoxItem, Popup, ProgressBar, ToolTip, ContextMenu, MenuItem, Separator,
ComboBoxItem, ComboBox, Slider and ScrollBar. Use their native keyboard, selection and automation
contracts. `ThemedDialog` composes the same button/input tokens; `Window.Standard` owns shared window
chrome, and `StandardWindowTheme.Apply` applies it to code-created derived windows.

| Family | Named styles/templates and use |
| --- | --- |
| Buttons | Button.Primary (one main action), Button.Secondary (ordinary command), Button.Ghost (quiet command), Button.Danger (destructive command), Button.Compact, Button.GhostCompact, Button.Command, Button.Command.Compact, Button.InputField, Button.Icon, Button.Icon.Compact, Button.Tutorial. |
| Toggles and disclosures | ToggleButton.Tool, ToggleButton.Disclosure, ToggleButton.CheckedLayer, Expander.Options; ThemeService creates Expander.OptionalStyle while preserving legacy contrast metrics. |
| Text inputs | TextBox.InputContentTemplate, TextBox.ControlLanguageTemplate; InputFieldBorder draws the bottom identifying edge independently of the keyboard-focus overlay. |
| Tabs and lists | Tab.Help plus implicit TabItem, ListBoxItem and ComboBoxItem styles use the shared neutral selection roles. |
| State layers | StateLayer, StateLayer.Hover, StateLayer.Pressed, StateLayer.Selected, StatePart.Normal and StatePart.SettlingGlyph are template parts, not independently styled screen panels. |
| Groups | Card, Card.Sunken, Card.Accent; Rail.Panel, Rail.ToolButton; Inspector.Panel, Inspector.SectionLabel, Inspector.Guidance; StatusBar.Panel, StatusBar.Text; StatusChip, StatusChip.Success, StatusChip.Warning, StatusChip.Text. Reuse existing groups without adding new overlays or badges. |
| Sliders | Slider.TrackButton, Slider.TrackButton.Vertical, Slider.Thumb, Slider.Thumb.Vertical, Slider.HorizontalTemplate, Slider.VerticalTemplate. Keep values and pointer geometry immediate. |
| Scroll bars | ScrollBar.PageButton, ScrollBar.Thumb, ScrollBar.ControlLanguageThumb, ScrollBar.ControlLanguageTemplate; keep native track orientation, commands and hit targets. |
| Icons | Icon.Symbol, Icon.Symbol.Primary, Icon.Symbol.Muted, Icon.Symbol.Accent, Icon.Symbol.OnAccent, Icon.Symbol.Danger, Icon.Symbol.Filled; Icon.Glyph, Icon.Glyph.Primary, Icon.Glyph.Muted, Icon.Glyph.Accent, Icon.Glyph.OnAccent, Icon.Glyph.Danger, Icon.Glyph.Filled remain compatibility names. Brand.AppIcon uses Brand.Mark / Brand.Mark16 without tinting their artwork. |
| Menus and separators | The implicit MenuItem and ContextMenu styles plus MenuItem.SeparatorStyleKey share spacing, surfaces and keyboard behaviour. Properties stays last in Explorer-order menus. |

Component aliases are part of the contract. `Input.Fill` / `Input.Border` map to Fill.Input /
Border.Subtle (Surface.Sunken / Border.Control in contrast mode). `Check.Fill`, `Check.Edge`,
`Check.HoverFill`, `Check.HoverEdge`, `Check.CheckedFill`, `Check.CheckedEdge`, `Check.Mark`, `Radio.Mark`,
`Check.CheckedThickness` and `Check.MarkMargin` keep mark/fill/edge pairing. A normal checked box is
Text.Primary-filled with Base-colour ink; contrast keeps the original outlined geometry.
`Toggle.CheckedFill`, `Toggle.CheckedStroke`, `Toggle.CheckedThickness`, `Toggle.CheckedForeground` and
`Toggle.CheckedFocusStroke` separate the selected fill, one-DIP edge, text and keyboard focus.
`Combo.Fill`, `Combo.Edge`, `Combo.FocusEdge`, `Combo.HoverFill`, `Combo.HoverEdge`, `Combo.PressedFill`
and `Combo.Chevron` follow shared button/state roles; `Scroll.Lane` stays clear outside contrast mode.
`ControlLanguage.Enabled` selects these normal templates while preserving contrast behaviour.

`Button.Fill` / `Button.BorderBrush` use the shared face and edge tokens. `Button.ChromeInset` and
`Button.ChromeInset.Compact` are 0,4,0,4 / 0,2,0,2; `Button.FocusInset` and its .Compact variant are
1,5,1,5 / 1,3,1,3; `Button.PrimaryFocusInset` and its .Compact variant are 2,6,2,6 / 2,4,2,4.
Contrast themes keep the existing uninset face and corresponding 1/2-DIP focus insets. `Button.Height`
and Size.HitTarget both resolve `FoundationMetrics.ControlHeight` through `x:Static`;
`Button.DisabledOpacity` retains 0.46. `CommandButton.Height.Standard` / `CommandButton.Height.Compact`
retain 44 / 36 and `CommandButton.FontSize` / `CommandButton.IconSize` retain Body / 16. The existing
`CommandButton.Padding.Standard`, `CommandButton.Padding.StandardIconOnly`, `CommandButton.Padding.Compact`, `CommandButton.Padding.CompactIconOnly` and
`CommandButton.IconLabelGap` preserve the established hit-area/content relationship.
`Border.DecorativeThickness` / `Border.ToolThickness` are zero normally and one in contrast mode.
`Border.Hover` is clear normally and Highlight in contrast. `Shadow.Floating` / `Shadow.Card` belong only
to actual floating groups and become invisible effects in contrast mode. Do not add shadows to
inner panels to simulate a hierarchy already expressed by the four surface fills.

### Motion

`Motion.Fast` = 83 ms, `Motion.Normal` = 167 ms, `Motion.Deliberate` = 250 ms. Reuse
`Motion.Ease.Standard` (Cubic EaseOut) and `Motion.Ease.SoftLanding` (Quadratic EaseOut) through
`FluidMotion`, retaining cancellation and reduced-animation behaviour. Animate chrome opacity or
transforms only. Captured-content position, zoom, selection coordinates, timeline handles and Alt
mnemonic captions update immediately; never animate them. The established countdown and hover-guide
exceptions in the motion section above remain local exceptions, not permission for new ones.

## How this file stays true

`DesignGuidelinesSyncTests` fails when a value in the token table differs from the resolved
`Tokens.xaml` resource, when a `FontSize.*` key or one of the core role keys is missing from it, or when
a font family of the `ThemeFonts` UI chain is not named here. Change a token, font or ramp step and this
file in the same commit.
