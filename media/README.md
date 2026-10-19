# Media slots

Both home pages share these slots. Pixel sizes below were read from the supplied files. Paths in the Korean page start with `media/`; paths in the English page start with `../media/`. The favicon remains at the site root as supplied.

| Slot | File(s) | Current pixel size | Used in | Source |
| --- | --- | --- | --- | --- |
| Korean hero loop | `hero-ko.webm`, `hero-ko.mp4` | 1920 × 1080, 7.8 s, 30 fps | `index.html`, hero, `video#hero-video` sources | Three 4.4.0 Store composites in order 04 (capture card), 01 (auto-redact), 02 (pin): app areas fitted to 1760 px on the composite background, 2.5 % slow zoom per scene, 0.2 s dip to background between scenes and back into the first scene at the loop point; H.264 CRF 25 / VP9 CRF 42; no audio |
| English hero loop | `hero-en.webm`, `hero-en.mp4` | 1920 × 1080, 7.8 s, 30 fps | `en/index.html`, hero, `video#hero-video` sources | English composites, same scenes, timing and encoding as the Korean loop; no audio |
| Hero posters | `hero-ko.jpg`, `hero-en.jpg` | 1920 × 1080 each | `index.html` / `en/index.html`, `video#hero-video[poster]` | First frame of the matching hero loop (capture card scene) |
| Redaction | `redact-ko.webp`, `redact-en.webp` | 1600 × 740 each | `index.html` / `en/index.html`, `#redact > img` | 4.4.0 Store composite `artifacts/store/screenshots/4.4.0/{ko,en}/01.png` (2560 × 1440), app area below the headline, crop (160, 400)–(2410, 1440), scaled to 1600 px wide |
| Pins | `pin-ko.webp`, `pin-en.webp` | 1600 × 687 each | `index.html` / `en/index.html`, `#pin > img` | 4.4.0 Store composite `artifacts/store/screenshots/4.4.0/{ko,en}/02.png` (2560 × 1440), app area below the headline, crop (160, 410)–(2560, 1440), scaled to 1600 px wide |
| Library | `library-ko.webp`, `library-en.webp` | 1600 × 670 each | `index.html` / `en/index.html`, `#library > img` | 4.4.0 Store composite `artifacts/store/screenshots/4.4.0/{ko,en}/06.png` (2560 × 1440), app area below the headline, crop (160, 360)–(2410, 1302), scaled to 1600 px wide |
| Social preview | `og-ko.jpg`, `og-en.jpg` | 1200 × 630 each | `index.html` / `en/index.html`, head, `meta[property="og:image"]` | Pin composite crop scaled to 630 px high, right-most 1200 px |
| Header icon / PNG favicon | `icon-64.png` | 64 × 64 | Both home pages, `.brand > img` (rendered at 32 × 32), and head `link[rel="icon"][type="image/png"]` | 64 px frame of `src/MyCapture.App/Assets/app.ico` |
| Touch icon | `icon-256.png` | 256 × 256 | Both home pages, head, `link[rel="apple-touch-icon"]` | 256 px frame of `src/MyCapture.App/Assets/app.ico` |
| ICO favicon | `../favicon.ico` | 16, 20, 24, 32, 40, 48, 64, 128, 256 px square frames | All three HTML pages, head, `link[rel="icon"][sizes="any"]` | `src/MyCapture.App/Assets/app.ico` |

## Replacing

The media show the 4.4.0 UI (2026-10-20). To refresh them, overwrite the files under the same names; retain both video formats and matching posters. The pages require no generated media paths or build step. The hero loop, stills and README GIFs come from `media/make_media.py` (Pillow, NumPy, ffmpeg): `python make_media.py <artifacts/store/screenshots/4.4.0> <out folder> <ffmpeg.exe>`. Its `CROPS` table holds the crop boxes listed above.

If an aspect ratio changes, update the following exact elements in **both** `index.html` and `en/index.html`:

- `video#hero-video`: `width` and `height` (currently 1920 / 1080). Keep its poster at the same ratio. Also update `.hero-video`'s `aspect-ratio` in `assets/site.css` if the loop is no longer 16:9.
- `#redact > img`: `width` and `height` (1600 / 740).
- `#pin > img`: `width` and `height` (1600 / 687).
- `#library > img`: `width` and `height` (1600 / 670).
- `.brand > img`: keep a 32 px display height and adjust its `width` if the header artwork is no longer square (currently 32 / 32).

When replacing social images, update `meta[property="og:image:width"]` and `meta[property="og:image:height"]` in both pages if their dimensions change. When replacing icon frames, keep the `sizes` declarations on the PNG and touch-icon links accurate. Update the table above with the new measured dimensions and sources.
