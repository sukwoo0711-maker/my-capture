# Third-party notices

MyCapture is built on the .NET and WPF open-source ecosystem. This file is a
human-readable index; the corresponding packages and distributed runtime files remain
under their own licenses.

## Runtime dependencies

| Component | Use | License | Source |
| --- | --- | --- | --- |
| .NET runtime and WPF | Self-contained Windows runtime and desktop UI framework | MIT and third-party licenses listed by .NET | <https://github.com/dotnet/runtime>, <https://github.com/dotnet/wpf> |
| Microsoft.Extensions.DependencyInjection 10.0.11 | Dependency injection | MIT | <https://github.com/dotnet/runtime> |
| Microsoft.Extensions.Logging 10.0.11 | Logging abstractions and implementation | MIT | <https://github.com/dotnet/runtime> |
| Microsoft.Extensions.Logging.Debug 10.0.11 | Debug logging provider | MIT | <https://github.com/dotnet/runtime> |
| Microsoft.Web.WebView2 1.0.4022.49 | On-demand, isolated local Mermaid rendering | Microsoft WebView2 SDK license | <https://www.nuget.org/packages/Microsoft.Web.WebView2/1.0.4022.49> |
| Mermaid 11.12.0 | Local diagram rendering in Markdown pins | MIT; bundled dependency notices below | <https://github.com/mermaid-js/mermaid/releases/tag/mermaid%4011.12.0> |
| RapidOcrNet 4.2.0 | PP-OCR ONNX pipeline used for Accurate OCR | Apache-2.0 | <https://github.com/BobLd/RapidOcrNet> |
| Microsoft.ML.OnnxRuntime | ONNX inference for RapidOcrNet | MIT | <https://github.com/microsoft/onnxruntime> |
| SkiaSharp | Image decode/resize for RapidOcrNet | MIT | <https://github.com/mono/SkiaSharp> |
| PaddleOCR / RapidAI PP-OCRv5 models | Downloaded Korean text detector and recognizer | Apache-2.0 | <https://github.com/PaddlePaddle/PaddleOCR>, <https://github.com/RapidAI/RapidOCR> |
| OPUS-MT Korean–English model | Offline Korean-to-English translation model (see below) | Apache-2.0 | <https://huggingface.co/Helsinki-NLP/opus-mt-ko-en> |
| OPUS-MT tc-big English–Korean model | Downloaded English-to-Korean translation model (see below) | CC BY 4.0 | <https://huggingface.co/Helsinki-NLP/opus-mt-tc-big-en-ko> |

The portable and installer packages include the authoritative .NET license as
`DOTNET-LICENSE.txt` and the complete .NET third-party notice set as
`DOTNET-THIRD-PARTY-NOTICES.txt`.

## Bundled Mermaid renderer

The unchanged `dist/mermaid.min.js` from the official `mermaid@11.12.0` npm package
is distributed at `Assets/Mermaid/mermaid.min.js`. No CDN, online diagram renderer,
or content upload is used. The WebView2 Runtime is a separately installed Microsoft
component; a missing runtime leaves the diagram as source with an explanation.

- Source archive: <https://registry.npmjs.org/mermaid/-/mermaid-11.12.0.tgz>
- Archive SHA-512: `ZudVx73BwrMJfCFmSSJT84y6u5brEoV8DOItdHomNLz32uBjNrelm7mg95X7g+C6UoQH/W6mBLGDEDv73JdxBg==`
- Bundle SHA-256: `07e37dfa97b337ccc85365d57eddf99b9706f09db3b59b260d0333b23b343c4b`
- Mermaid's complete MIT license: `Assets/Mermaid/LICENSE-mermaid.txt`.
- The bundle retains its embedded license comments. Complete upstream license and
  notice files for its 71 dependency records are included in `Assets/Mermaid/licenses/`.
  `Assets/Mermaid/bundle-manifest.json` records the exact package sources, versions,
  integrity values and notice-file names. The published source map identifies the
  included packages, including multiple D3/layout-base versions. DOMPurify's
  Apache-2.0/MPL-2.0 terms and all included BSD/MIT notices are retained.
- The WebView2 SDK's license is included as `Assets/Mermaid/LICENSE-WebView2.txt`.
  Its native loader supports the app's win-x64 and win-x86 targets.

## Translation model

The Translate view of the text recognition window downloads an English-to-Korean
translation model when the user turns translation on. The model is not included in the
installer, the portable ZIP or the Microsoft Store package.

- Work: opus-mt-tc-big-en-ko (OPUS-MT, released 2022-07-28) by the Language Technology
  Research Group at the University of Helsinki, from
  <https://huggingface.co/Helsinki-NLP/opus-mt-tc-big-en-ko> at revision
  `ae8606b7b29a495f31ce679cee2007f536a3a5ce`.
- License: Creative Commons Attribution 4.0 International,
  <https://creativecommons.org/licenses/by/4.0/>. The work is provided as is, without
  warranties.
- ONNX export with dynamic int8 quantization: noticemkjung,
  <https://huggingface.co/noticemkjung/opus-mt-tc-big-en-ko-ONNX> at revision
  `aea67600956847d47609f96b364f33a734007d50` (CC BY 4.0).
- Changes made for MyCapture: the int8 weights that the encoder, the decoder and the decoder
  with the key/value cache each carried are stored once in shared external weight files by
  `build/tools/translation-model/dedupe_opus_onnx.py`; tensor values are unchanged. The
  source SentencePiece model and the target vocabulary are redistributed unchanged.
- Reference requested by the model authors: Jörg Tiedemann and Santhosh Thottingal,
  "OPUS-MT – Building open translation services for the World", EAMT 2020,
  <https://aclanthology.org/2020.eamt-1.61>.

## Korean-to-English translation model

Korean-to-English pin translation uses OPUS-MT in the same local Marian/ONNX engine.
The isolated preview profile can include preverified model files. Standard user profiles
keep the two language directions in separate model folders; a model loads only when
translation is requested. No pin text is sent to the model host.

- Work: `Helsinki-NLP/opus-mt-ko-en`, by the Language Technology Research Group at the
  University of Helsinki (OPUS-MT), revision `e42d1f41b66194e6d10512f8a27bebc1f4f5097e`.
- Authoritative model card and license declaration:
  <https://huggingface.co/Helsinki-NLP/opus-mt-ko-en/blob/e42d1f41b66194e6d10512f8a27bebc1f4f5097e/README.md>.
  This model declares Apache License 2.0, <https://www.apache.org/licenses/LICENSE-2.0>;
  it does not use the CC BY 4.0 license of the separate English-to-Korean model above.
  The complete Apache 2.0 text is included at `Assets/Translation/LICENSE-APACHE-2.0.txt`.
- Quantized ONNX export: `Xenova/opus-mt-ko-en`, revision
  `d9fa1ac6008242100fecb4fff9b0a5917a1be81f`,
  <https://huggingface.co/Xenova/opus-mt-ko-en>.
- MyCapture redistributes the three quantized graphs, official `source.spm`, and official
  `vocab.json` unchanged, with descriptive local file names. Shared source/target vocabulary
  ids are mapped from SentencePiece pieces before inference. All five files are pinned by
  byte length and SHA-256 in `TranslationModelCatalog`; no model weights are uploaded to
  a MyCapture GitHub release for this preview.
- Reference: Jörg Tiedemann and Santhosh Thottingal, "OPUS-MT – Building open translation
  services for the World", EAMT 2020, <https://aclanthology.org/2020.eamt-1.61>.
## Bundled fonts

The following original, unmodified fonts are embedded in the application. Each is
licensed under the SIL Open Font License 1.1. The complete license and copyright
notice for each family is distributed at `Assets/Fonts/<folder>/OFL.txt` in both
portable and installed packages. Font names stored in settings resolve to these
bundled copies even when a family with the same name is installed on the PC.

| Font | Version | Copyright | License | Original distribution | License folder |
| --- | --- | --- | --- | --- | --- |
| Maple Mono | v7.9 | Copyright 2022 The Maple Mono Project Authors (https://github.com/subframe7536/maple-font) | SIL OFL 1.1 | <https://github.com/subframe7536/maple-font/releases/tag/v7.9> | `MapleMono` |
| JetBrains Mono | v2.304 | Copyright 2020 The JetBrains Mono Project Authors (https://github.com/JetBrains/JetBrainsMono) | SIL OFL 1.1 | <https://github.com/JetBrains/JetBrainsMono/releases/tag/v2.304> | `JetBrainsMono` |
| D2Coding | 1.4.0 (20261003) | Copyright (c) 2015, NAVER Corporation (https://www.navercorp.com), with Reserved Font Name D2Coding; Copyright (c) 2015, NAVER Corporation (https://www.navercorp.com), with Reserved Font Name D2Coding-Bold. | SIL OFL 1.1 | <https://github.com/naver/d2codingfont> | `D2Coding` |
| Pretendard | v1.3.9 | Copyright (c) 2021, Kil Hyung-jin (https://github.com/orioncactus/pretendard), with Reserved Font Name Pretendard. | SIL OFL 1.1 | <https://github.com/orioncactus/pretendard/releases/tag/v1.3.9> | `Pretendard` |

## Development and test dependencies

These packages are used to build or test MyCapture and are not shipped as application
runtime dependencies.

| Component | License | Source |
| --- | --- | --- |
| xUnit.net 2.9.3 | Apache-2.0 | <https://github.com/xunit/xunit> |
| xunit.runner.visualstudio 2.8.2 | Apache-2.0 | <https://github.com/xunit/visualstudio.xunit> |
| Microsoft.NET.Test.Sdk 17.12.0 | MIT | <https://github.com/microsoft/vstest> |

MyCapture does not bundle FFmpeg, Flyleaf, Snipaste, ShareX, ScreenToGif, OBS Studio,
ALCapture, or their assets. Competitive references in the documentation are product
research links, not incorporated code or artwork.

## Microsoft Fluent System Icons

The media export controls incorporate Arrow Export and Options (20 regular)
from https://github.com/microsoft/fluentui-system-icons at commit
`1035d6e8663d9e92e71d6fc96dc361c1f766c153`. Original SVGs are retained in
`src/MyCapture.App/Assets/Fluent`; their filled paths are used directly in WPF.
The complete license follows and is included in this distributed notice.

MIT License

Copyright (c) 2020 Microsoft Corporation

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
