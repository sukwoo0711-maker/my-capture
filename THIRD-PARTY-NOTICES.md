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

The portable and installer packages include the authoritative .NET license as
`DOTNET-LICENSE.txt` and the complete .NET third-party notice set as
`DOTNET-THIRD-PARTY-NOTICES.txt`.

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
