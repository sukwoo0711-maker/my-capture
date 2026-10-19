# Reddit · r/software

- Community: https://www.reddit.com/r/software
- Live rules: https://www.reddit.com/r/software/about/rules
- Rule snapshot supplied in the brief, read September 26, 2026: https://threadfox.vip/rules/software
- Time: D+16, Wednesday 23:00 KST / Wednesday 09:00 US Eastern Standard Time. D assumes a Monday Store release; recheck the weekday and daylight saving offset on the posting date.
- Assets: none; use a text post.

Before posting:

- [ ] Read the live sidebar and confirm the current open-source/self-promotion exception and Wednesday rule. The supplied snapshot is not a live permission check.
- [ ] Confirm it is Wednesday in the US and select the exact release/self-promotion flair named by the current sidebar.
- [ ] Confirm the account meets the community's karma requirement; the threshold is not published in the brief.
- [ ] Confirm the current rules accept this update and that the full tool is free to use.

## Title

```text
I built MyCapture: local screenshot redaction and pins for Windows (free, MIT)
```

## Body

```markdown
I built MyCapture, a free, MIT-licensed Windows screenshot tool. I kept covering contact details by hand when turning customer inquiry screenshots into development issues. I also needed those screenshots beside my code and a way to find the right one later.

One way to redact a screenshot locally is to run OCR on the PC, then check the recognized text for specific patterns. A detector can retain just a category and bounding box for each match, rather than copying the recognized string into its results. Drawing editable cover boxes lets the person preparing the image correct a misplaced box before saving.

Pattern choice matters. In MyCapture, card candidates must be 13–19 digits and pass the Luhn checksum; a run of digits alone isn't enough. IP detection means IPv4, not every possible network address. Secret-key patterns include sk-… API keys, GitHub tokens with ghp_/gho_/ghu_/ghs_/ghr_ or github_pat_ prefixes, AWS AKIA/ASIA access-key IDs, and Bearer tokens. It also checks email addresses, Korean phone numbers and resident-registration-number-shaped strings. A matching shape isn't proof that the value is real or active.

This misses names and street addresses, other formats outside those patterns, and anything OCR fails to read. I still inspect the full image before sharing it. Redact adds cover boxes that can be moved, removed or undone before saving; manual blur and mosaic are available too.

The app uses .NET/WPF with Windows on-device OCR and a local PP-OCRv5 model. F3 pins clipboard images, text or Excel tables above other windows. The library searches captured window titles and text inside images. As of 4.4.0, a small card appears by default after capture, with the image already on the clipboard and E available to open the editor.

There is no app account, advertising, capture limit, watermark, automatic capture upload or analytics telemetry. Missing OCR models download from ModelScope at startup; the optional upscaling model downloads from Amazon S3 on first use. GitHub attachment access is user-initiated.

It requires Windows 11 or Windows 10 22H2 x64; ARM64 PCs need Windows 11. Recordings have no audio. The Store build is signed by Microsoft and updated by the Store. GitHub setup/ZIP files are unsigned, may trigger SmartScreen, and have no in-app updates; check SHA256SUMS.txt.

[Store](https://apps.microsoft.com/detail/9PB33MC095VR?cid=community-en) · [Source](https://github.com/sukwoo0711-maker/my-capture) · [Releases](https://github.com/sukwoo0711-maker/my-capture/releases)

Which false positives or missed formats would you expect in the screenshots you actually share?
```

## First comment

```markdown
I maintain this myself. For a missed detection or a misplaced box, a small example with invented values is more useful than a real customer screenshot. Please include the app version and reproduction steps at [support issues](https://github.com/sukwoo0711-maker/mycapture-support/issues). As of 4.4.0, Contact support in the tray menu or Settings > About can prepare a diagnostic block without user content and open a prefilled GitHub issue; submitting it needs a GitHub account.
```
