# Display languages

Settings → General → Display language uses the existing `GeneralSettings.Language` value. An empty value follows the user's Windows display language, `ko-KR` selects Korean, and `en-US` selects English. Korean Windows variants use Korean; other or unsupported languages fall back to English. The app captures the original Windows display culture before applying any preference, so returning to the system option remains reliable.

Apply saves the choice and displays a restart notice. Existing windows and work remain open in their current language. All windows, tray menus, validation messages, and accessibility labels use the saved choice on the next start. This avoids recreating capture/editor windows or losing unsaved work during a language change.

## Adding UI text

`MyCapture.Core.Localization.UiText.Get(key)` resolves an explicit resource and `UiText.Format(key, arguments)` formats a translated composite string using the UI culture. WPF markup uses `{loc:Text key}` from `MyCapture.App.Localization`. Add each key to both `Strings.resx` (Korean neutral resources) and `Strings.en.resx`. Existing `Text_…` keys are stable identifiers derived from the original source text; keep them stable when improving wording. Resource comments identify the original source for review. New descriptive keys are also supported.

Preserve argument indices, alignment and format specifiers between languages. Keep accelerators and shortcut descriptions accurate. Do not translate serialized enum/property names, log messages, search vocabulary, user titles, OCR results, text annotations, file paths, or existing layer names. Generated default display names use the selected language when created.

Tests and isolated previews can use `using (UiText.UseLanguage("en-US"))`. This override is local to the execution context, flows across await and newly created STA threads, and restores its prior value on disposal. Production startup uses `Configure` once before creating UI; tests should avoid mutating that process-wide state.

## Verification

`LocalizationTests` checks settings persistence, OS fallback, concurrent overrides, Korean/English key and composite-format parity, and every explicit C#/XAML resource reference. Existing Korean caption tests declare a scoped Korean preference rather than depending on the test machine's language. `EnglishLayoutTests` checks native video controls at 760 and 920 pixels.

For production-theme startup, settings selector binding, accessibility, and screenshots, build Release then run the built DLL with `--selftest-localization <output-directory>`. The probe runs before the ordinary shell, uses only in-memory settings and synthetic video metadata, writes its report/screenshots into the supplied directory, and does not access the live installed app's settings or register hotkeys. It also covers the 520-pixel timed-text dialog with translated labels above the time and placement fields.
