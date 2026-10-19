# Media slots

Both home pages share these slots. Pixel sizes below were read from the supplied files. Paths in the Korean page start with `media/`; paths in the English page start with `../media/`. The favicon remains at the site root as supplied.

| Slot | File(s) | Current pixel size | Used in | Source |
| --- | --- | --- | --- | --- |
| Korean hero loop | `hero-ko.webm`, `hero-ko.mp4` | 1920 × 1080 | `index.html`, hero, `video#hero-video` sources | `artifacts/promo-2026-10-18/v2/MyCapture-promo-v2-1080p60.mp4`; cuts at 2.2–6.0 s, 12.8–15.2 s and 21.0–23.5 s, with 0.25 s crossfades; 30 fps; H.264 CRF 24 / VP9 CRF 36; no audio |
| English hero loop | `hero-en.webm`, `hero-en.mp4` | 1920 × 1080 | `en/index.html`, hero, `video#hero-video` sources | English `v2-en` promo counterpart; same cuts, crossfades and encoding as the Korean loop; no audio |
| Hero posters | `hero-ko.jpg`, `hero-en.jpg` | 1920 × 1080 each | `index.html` / `en/index.html`, `video#hero-video[poster]` | First frame of the matching hero loop |
| Redaction | `redact-ko.webp`, `redact-en.webp` | 1600 × 1006 each | `index.html` / `en/index.html`, `#redact > img` | Promo render `source/video/film-v2/assets/v2/editor/03-cover3.png` / `source/video/film-v2-en/assets/v2/editor/03-cover3.png`, scaled to 1600 px wide |
| Pins | `pin-ko.webp`, `pin-en.webp` | 1920 × 1080 each | `index.html` / `en/index.html`, `#pin > img` | Matching Korean / English promo frame at 23.3 s |
| Library | `library-ko.webp`, `library-en.webp` | 1600 × 981 each | `index.html` / `en/index.html`, `#library > img` | Matching 4.2.1 Store screenshot `01-library.png`, cropped to the window (1996 × 1224 at 282,108), then scaled to 1600 px wide |
| Social preview | `og-ko.jpg`, `og-en.jpg` | 1200 × 630 each | `index.html` / `en/index.html`, head, `meta[property="og:image"]` | Matching pin frame scaled and cropped to 1200 × 630 |
| Header icon / PNG favicon | `icon-64.png` | 64 × 64 | Both home pages, `.brand > img` (rendered at 32 × 32), and head `link[rel="icon"][type="image/png"]` | 64 px frame of `src/MyCapture.App/Assets/app.ico` |
| Touch icon | `icon-256.png` | 256 × 256 | Both home pages, head, `link[rel="apple-touch-icon"]` | 256 px frame of `src/MyCapture.App/Assets/app.ico` |
| ICO favicon | `../favicon.ico` | 16, 20, 24, 32, 40, 48, 64, 128, 256 px square frames | All three HTML pages, head, `link[rel="icon"][sizes="any"]` | `src/MyCapture.App/Assets/app.ico` |

## Replacing

The app UI changes in 4.4.0. Replace the supplied files with the new Korean and English renders under the same names; retain both video formats and matching posters. The pages require no generated media paths or build step.

If an aspect ratio changes, update the following exact elements in **both** `index.html` and `en/index.html`:

- `video#hero-video`: `width` and `height` (currently 1920 / 1080). Keep its poster at the same ratio. Also update `.hero-video`'s `aspect-ratio` in `assets/site.css` if the loop is no longer 16:9.
- `#redact > img`: `width` and `height` (1600 / 1006).
- `#pin > img`: `width` and `height` (1920 / 1080).
- `#library > img`: `width` and `height` (1600 / 981).
- `.brand > img`: keep a 32 px display height and adjust its `width` if the header artwork is no longer square (currently 32 / 32).

When replacing social images, update `meta[property="og:image:width"]` and `meta[property="og:image:height"]` in both pages if their dimensions change. When replacing icon frames, keep the `sizes` declarations on the PNG and touch-icon links accurate. Update the table above with the new measured dimensions and sources.
