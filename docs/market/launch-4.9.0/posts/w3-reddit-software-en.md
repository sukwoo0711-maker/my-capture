# Reddit · r/software

- Community: https://www.reddit.com/r/software
- Rules: https://www.reddit.com/r/software/about/rules
- Historical reference: https://threadfox.vip/rules/software (used by the 2026-10-20 plan, based on a 2026-09-26 reading).
- On 2026-10-27 the original rules URL opened but displayed no rule text. Current self-promotion permission remains unconfirmed.
- Conditional timing: a permitted day in D+14 through D+20. The older record required Wednesday; verify the community's current day/time basis, flair, karma requirement and release eligibility. Also verify the distributed source and license meet the open-source condition. The old plan described an MIT release, but the allowed current feature/release sources do not establish that license here.
- Image candidates: `src/MyCapture.App/Assets/Tutorials/pin-clipboard.en.mp4`, `pin-copy.en.mp4`. Do not post until the conditions above are checked by the account owner.

## Title

```text
I built MyCapture: keeping copied tables and code visible while preserving the text
```

## Body

```text
I built MyCapture, a free Windows tool for pinning copied tables and code above other windows. Here is a workflow for keeping a reference visible and then reusing its original text.

1. Copy the cells or code snippet from the source application. If you need the original text later, copy the content itself rather than only taking a screenshot of it.
2. Press F3 to display the clipboard content as a pin above other windows. Supported VS Code HTML retains code colors and indentation.
3. Work in the other application while referring to the pin. Zoom and opacity controls can help position the reference.
4. Focus a pin with source text and press Ctrl+C. It copies the original text and image together; check which format the destination application pastes.

This distinction matters: a picture of a table and a table copied from Excel can look similar, but they don't necessarily provide the same clipboard text. Screen-text recognition is useful for text you cannot select, while copying the original content is preferable when you already have access to it.

In 4.9.0, a handle at the top right of click-through pins lets you turn click-through off. The update also adds a first-use walkthrough and reorganizes the library and editor. Capture, OCR, region recording, GIF export and editable redaction are available as well. Recordings have no audio, and OCR results need checking against the source.

F3 is the new-install default; existing saved shortcuts remain unchanged. If another application uses that key first, check the startup notice and shortcut settings.

Overview: https://mimiloaf.com/en/mycapture/
Store: https://apps.microsoft.com/detail/9PB33MC095VR?cid=community-en
Project: https://github.com/sukwoo0711-maker/my-capture

Which destination applications produce an unexpected result when you paste the source text back from a pin?
```

## First comment

```text
For a clipboard or formatting issue, please describe the source application, destination application, app version and steps. A small invented table or code sample is more useful than private work content. Contact support is available from the tray menu and Settings > About.
```
