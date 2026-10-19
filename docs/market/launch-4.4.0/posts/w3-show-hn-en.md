# Hacker News · Show HN

- Submit: https://news.ycombinator.com/submit
- Rules: https://news.ycombinator.com/showhn.html
- URL field: https://github.com/sukwoo0711-maker/my-capture
- Time: D+15, Tuesday 22:00 KST / 08:00 US Eastern Standard Time, after November 1, 2026. D is the confirmed Store release day, assumed Monday; check daylight saving time if the schedule changes.
- Assets: none; submit the repository URL.

Before posting:

- [ ] Confirm that Store and Releases offer the version described and that people can try it without an app account.
- [ ] Stay available to reply for the first three hours.
- [ ] Do not ask anyone to upvote or comment. Use the repository, not a landing page or blog post.

## Title

```text
Show HN: MyCapture – Windows screenshots with local OCR redaction and pins
```

## Body

Leave the text field empty for this URL submission. Paste the explanation below as the first comment.

```text
```

## First comment

```text
I built MyCapture, a free Windows screenshot and pinning tool for developers. In my development work, turning customer inquiries into issues meant covering email addresses and phone numbers by hand. I then needed the screenshot beside my code while fixing the problem, and a way to find it again later. I wanted those steps in one tool that processes the images on my PC.

It is a .NET/WPF desktop app. Redact uses Windows.Media.Ocr and a local PP-OCRv5 model, then runs pattern checks on the recognized text. It looks for email addresses, Korean phone numbers, resident-registration-number-shaped strings, card numbers, IPv4 addresses and common secret-key formats. Detection results keep only the type and position, not a copy of the recognized string. The cover boxes remain editable before saving. Names, street addresses and text OCR cannot read still need manual checking.

F3 pins clipboard images, text and Excel tables above other windows. Supported VS Code HTML retains code colors and indentation. The library holds images and MP4 recordings, with search by captured window title and text recognized inside images. File or project names are searchable when the original app puts them in its title bar.

As of 4.4.0, a small card appears by default after capture; the image is already on the clipboard, and E opens the editor. There is no app account, advertising, capture limit or watermark. The source is MIT-licensed. Missing OCR models download from ModelScope at startup; optional upscaling downloads a model from Amazon S3 on first use. GitHub attachment access happens only when requested. There is no automatic capture upload or analytics telemetry.

It requires Windows 11 or Windows 10 22H2 x64; ARM64 PCs need Windows 11. Recordings intentionally have no audio. I maintain it alone. Microsoft signs the Store build and the Store updates it. GitHub setup/ZIP files are unsigned, may trigger SmartScreen, and have no in-app updates; compare downloads with SHA256SUMS.txt.

Store: https://apps.microsoft.com/detail/9PB33MC095VR?cid=community-en
Releases: https://github.com/sukwoo0711-maker/my-capture/releases

Where would this redaction-and-pin workflow break down in your own issue-writing process?
```
