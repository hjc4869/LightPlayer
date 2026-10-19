# Light Player

Light Player is a cross-platform media player built with [Avalonia](https://avaloniaui.net/) and
.NET 10. It combines this device's indexed folders with zero or more Light Player servers in one
browsable music/video library, with support for playlists, queues, history, artwork, subtitles,
and lyrics.

### New features

- **Video playback** -- Play videos from local folders, remote libraries, Blu-ray folders and ISO
  images, and streaming links, with hardware-accelerated decoding, rich subtitle support, chapters,
  Picture-in-Picture, and custom scaling shaders.
- **Photo viewer and library** -- View photos in a wide range of formats, from JPEG, PNG, and HEIC
  to AVIF, JPEG XL, JPEG XR, animated images, and camera RAW, with smooth zooming and gestures. Find
  photos by describing what is in them with on-device smart search.
- **HDR and advanced color** -- Videos and photos show their full brightness and wide color gamut
  on HDR displays, and colors stay accurate on wide-gamut and SDR screens. When HDR output is
  unavailable, HDR content is tone mapped with high-quality, adjustable curves.
- **Dynamic HDR** -- Dolby Vision and HDR10+ videos follow their scene-by-scene metadata for more
  faithful brightness and contrast on both HDR and SDR displays.
- **HDR photos with gain maps** -- HDR photos from modern phones and cameras, such as those taken
  on iPhone, Pixel, and Galaxy devices, light up on HDR displays and adapt to the brightness your
  screen has available, while still looking right on SDR screens.

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
