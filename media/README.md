# Media slots

Both home pages share these slots. Pixel sizes below were read from the supplied files. Paths in the Korean page start with `media/`; paths in the English page start with `../media/`. The favicon remains at the site root as supplied.

| Slot | File(s) | Current pixel size | Used in | Source |
| --- | --- | --- | --- | --- |
| Korean hero loop | `hero-ko.webm`, `hero-ko.mp4` | 1920 × 1080, 7.8 s, 30 fps | `index.html`, hero, `video#hero-video` sources | Three pin-first Store composites (`artifacts/store/screenshots/4.5.0-pin`) in order 01 (Excel register table pinned above code), 02 (snippet pin, original text pasted), 03 (click-through pin above a terminal): app areas fitted to 1760 px on the composite background, 2.5 % slow zoom per scene, 0.2 s dip to background between scenes and back into the first scene at the loop point; H.264 CRF 25 / VP9 CRF 42; no audio |
| English hero loop | `hero-en.webm`, `hero-en.mp4` | 1920 × 1080, 7.8 s, 30 fps | `en/index.html`, hero, `video#hero-video` sources | English composites, same scenes, timing and encoding as the Korean loop; no audio |
| Hero posters | `hero-ko.jpg`, `hero-en.jpg` | 1920 × 1080 each | `index.html` / `en/index.html`, `video#hero-video[poster]` | First frame of the matching hero loop (spec table pin scene) |
| Pins | `pin-ko.webp`, `pin-en.webp` | 1600 × 707 each | `index.html` / `en/index.html`, `#pin > img` | Composite `02.png` (snippet pin pasted into the editor), crop (160, 380)–(2560, 1440), scaled to 1600 px wide |
| Pin controls | `controls-ko.webp`, `controls-en.webp` | 1600 × 707 each | `index.html` / `en/index.html`, `#controls > img` | Composite `03.png` (click-through pin), crop (160, 380)–(2560, 1440), scaled to 1600 px wide |
| Text from images | `text-ko.webp`, `text-en.webp` | 1600 × 707 each | `index.html` / `en/index.html`, `#text > img` | Composite `05.png` (datasheet image pin and pasted text), crop (160, 380)–(2560, 1440), scaled to 1600 px wide |
| Library | `library-ko.webp`, `library-en.webp` | 1600 × 670 each | `index.html` / `en/index.html`, `#library > img` | Composite `07.png` (library search by text), crop (160, 360)–(2410, 1302), scaled to 1600 px wide |
| Markup and redaction | `redact-ko.webp`, `redact-en.webp` | 1600 × 838 each | `index.html` / `en/index.html`, `#redact > img` | Composite `08.png` (step numbers, arrow and Auto-redact), crop (160, 360)–(2206, 1432), scaled to 1600 px wide |
| Social preview | `og-ko.jpg`, `og-en.jpg` | 1200 × 630 each | `index.html` / `en/index.html`, head, `meta[property="og:image"]` | Composite `01.png` (spec table pin) app area scaled to 630 px high, right-most 1200 px |
| Header icon / PNG favicon | `icon-64.png` | 64 × 64 | Both home pages, `.brand > img` (rendered at 32 × 32), and head `link[rel="icon"][type="image/png"]` | 64 px frame of `src/MyCapture.App/Assets/app.ico` |
| Touch icon | `icon-256.png` | 256 × 256 | Both home pages, head, `link[rel="apple-touch-icon"]` | 256 px frame of `src/MyCapture.App/Assets/app.ico` |
| ICO favicon | `../favicon.ico` | 16, 20, 24, 32, 40, 48, 64, 128, 256 px square frames | All three HTML pages, head, `link[rel="icon"][sizes="any"]` | `src/MyCapture.App/Assets/app.ico` |

## Replacing

The media show the pin-first composites rendered on the 4.5.0 pin-flow branch (2026-10-20), where F3 pins the clipboard on new installations. To refresh them, overwrite the files under the same names; retain both video formats and matching posters. The pages require no generated media paths or build step. The hero loop, stills and README GIFs come from `media/make_media.py` (Pillow, NumPy, ffmpeg): `python make_media.py <artifacts/store/screenshots/4.5.0-pin> <out folder> <ffmpeg.exe>`. Its `CROPS` table holds the crop boxes listed above.

If an aspect ratio changes, update the following exact elements in **both** `index.html` and `en/index.html`:

- `video#hero-video`: `width` and `height` (currently 1920 / 1080). Keep its poster at the same ratio. Also update `.hero-video`'s `aspect-ratio` in `assets/site.css` if the loop is no longer 16:9.
- `#pin > img`, `#controls > img`, `#text > img`: `width` and `height` (1600 / 707).
- `#library > img`: `width` and `height` (1600 / 670).
- `#redact > img`: `width` and `height` (1600 / 838).
- `.brand > img`: keep a 32 px display height and adjust its `width` if the header artwork is no longer square (currently 32 / 32).

When replacing social images, update `meta[property="og:image:width"]` and `meta[property="og:image:height"]` in both pages if their dimensions change. When replacing icon frames, keep the `sizes` declarations on the PNG and touch-icon links accurate. Update the table above with the new measured dimensions and sources.
