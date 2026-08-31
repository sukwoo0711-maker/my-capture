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
