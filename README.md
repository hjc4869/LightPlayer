# Light Player

Light Player is a cross-platform media player built with [Avalonia](https://avaloniaui.net/) and
.NET 10. It combines this device's indexed folders with zero or more Light Player servers in one
browsable music/video library, while preserving source-qualified playlists, queues, history,
artwork, subtitles, lyrics, and playback routing. Audio is decoded through FFmpeg and played back
via PulseAudio on Linux, Core Audio on macOS, WASAPI on Windows, AAudio on Android, or Web Audio in
the browser.

## Platform support

| Platform | Status |
| --- | --- |
| **KDE Wayland / macOS / Windows** | The primary, fully supported targets. |
| **Other Linux (e.g. X11, GNOME)** | Technically usable but unsupported and not validated. Expect less UI integration and incomplete features. |
| **Android / iOS** | Usable with demo-level UX and quality. Known issues exist in .NET, Avalonia UI, and Light Player itself. |
| **Browser / WebAssembly** | Basic playback works on the latest Chromium, Firefox, and Safari. Only Chromium-based browsers are considered supported and validated. |

The browser client is available as a [public preview](https://lmp.hjc.im), with a separate
[feature diagnostics page](https://lmp.hjc.im/diag).

## Getting started

- [Build from source](docs/build.md)
- [Installation](docs/installation.md)

## Customization

- [Lyrics source extensions](docs/lyrics.md)
- [Video shader packages](docs/video-shader-packages.md)

## License

Copyright (c) 2026 David Huang. **All rights reserved.**

This project is **source-available for reference and private, personal, non-redistributable use only**; it is currently *not* FOSS licensed. See [LICENSE](LICENSE) for the full terms.
