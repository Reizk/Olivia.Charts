# Third-party notices

Olivia Charts code and documentation are distributed under the [MIT license](https://github.com/Reizk/Olivia.Charts/blob/main/LICENSE). Third-party components retain their own licenses and copyright notices.

## Runtime and demo dependencies

Versions below correspond to the committed NuGet lock files. The Windows demo archive includes the applicable notices in its `licenses` directory. Source copies are available in the [repository license directory](https://github.com/Reizk/Olivia.Charts/tree/main/licenses).

| Component | Version | License and notices |
| --- | --- | --- |
| Avalonia, Desktop, Fluent themes, Inter font wrapper, Skia, HarfBuzz, Win32, Native, X11, FreeDesktop, AtSpi, Remote.Protocol | 12.1.4 | MIT; `licenses/avalonia.txt` |
| Avalonia.BuildServices (build tooling) | 11.3.2 | MIT; Avalonia project copyright as stated in package metadata |
| Avalonia.Angle.Windows.Natives | 2.1.27548.20260419 | ANGLE BSD-style license; `licenses/angle.txt` |
| MicroCom.Runtime | 0.11.6 | MIT; `licenses/microcom.txt` |
| SkiaSharp and platform native assets | 3.119.4 | MIT wrapper; `licenses/skiasharp.txt`, with additional native component licenses in `licenses/skia-harfbuzz-notices.txt` |
| HarfBuzzSharp and platform native assets | 8.3.1.3 | MIT wrapper; `licenses/harfbuzzsharp.txt`, with additional native component licenses in `licenses/skia-harfbuzz-notices.txt` |
| Inter font embedded by Avalonia.Fonts.Inter | As bundled in 12.1.4 | SIL Open Font License 1.1; `licenses/inter-ofl.txt` |
| System.IO.Pipelines | 8.0.0 | MIT; `licenses/dotnet.txt` and `licenses/dotnet-notices.txt` |
| Tmds.DBus.Protocol | 0.94.1 | MIT; `licenses/tmds-dbus.txt` |

Avalonia platform dependencies are selected by the host platform. The Windows demo is framework-dependent: the .NET runtime is installed separately and is not included in the archive.

Test tooling, including xUnit, the Visual Studio test runner, and Microsoft.NET.Test.Sdk, is restored separately and is not included in the demo or library packages. Refer to the test project lock files and the packages' own license metadata for those dependencies.

## Notice sources

- Avalonia notice: the source commit recorded in the 12.1.4 NuGet metadata, `2d79ba55fae1b1e71c69f93a668080c9029e6a31`.
- MicroCom and Inter notices: their upstream project license files.
- Tmds.DBus notice: source commit `b4a7fed0b878f74cb54f7cca84d2889af4e596ba` from the 0.94.1 package metadata.
- ANGLE, SkiaSharp, HarfBuzzSharp, and .NET notices: unmodified files included in the corresponding NuGet packages. The SkiaSharp and HarfBuzzSharp native packages provide the same combined third-party notice file.

Project links: [Avalonia](https://github.com/AvaloniaUI/Avalonia), [ANGLE](https://github.com/AvaloniaUI/angle), [MicroCom](https://github.com/kekekeks/MicroCom), [SkiaSharp and HarfBuzzSharp](https://github.com/mono/SkiaSharp), [Inter](https://github.com/rsms/inter), [.NET](https://github.com/dotnet/runtime), [Tmds.DBus](https://github.com/tmds/Tmds.DBus).

## Demo materials

The Olivia logo was supplied by the repository owner. Demo screenshots show this project's interface with locally generated sample data. No external market feed or business dataset is bundled with the demo.
