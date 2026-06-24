# Light Player

Light Player is a cross-platform desktop music player built with [Avalonia](https://avaloniaui.net/) and .NET 10. It scans your local folders into a browsable library of songs, albums, and artists, supports playlists and CUE sheets, and provides gapless playback with synchronized lyrics, cover art, and desktop "now playing" / media-key integration. Audio is decoded through FFmpeg and played back via OpenAL.

## Project Status

| Platform | Status |
| --- | --- |
| **Linux** | Complete — the primary, fully supported target, distributed as a Flatpak. |
| **Android** | In active development. |
| **Windows / macOS** | Technically usable — the codebase builds and runs — but not a current focus (untested and unpackaged). |

## Supported Platforms

The primary distribution format is a **Flatpak** for Linux (x86_64 and aarch64). All projects target `net10.0` for both the `x64` and `ARM64` platforms and include a per-OS build facility, so Windows and macOS builds are technically supported even though they are not actively tested or packaged today.

## Project Structure

```
LightStudio.LightPlayer.slnx      Solution file (src + tools projects)
Makefile                          Build orchestration (dotnet + Flatpak targets)
src/                              Application and libraries
tools/                            Command-line diagnostic tools
packaging/flatpak/                Flatpak manifest and desktop-integration assets
artifacts/                        Build output (Flatpak bundle, generated files)
```

### Source projects (`src/`)

- **`LightStudio.LightPlayer`** — The main Avalonia desktop application (`WinExe`, the distributed entry point). Contains the UI (`Views` / `ViewModels` in an MVVM pattern), application services (`Services`), the playback engine (OpenAL output, playback queue, and MPRIS integration on Linux), themes, converters, and assets.
- **`LightStudio.FfmpegShim`** — A thin managed interop layer over FFmpeg (via `FFmpeg.AutoGen`) responsible for audio decoding, media-info probing, and PCM frame reading. The FFmpeg binding version is selected at build time to match the FFmpeg shipped by the target runtime (8.x on the host, 7.1.x inside the Flatpak).
- **`LightStudio.MediaLibraryCore`** — The media-library backend shared by the app and tools: an EF Core / SQLite database, folder scanning and indexing, CUE-sheet parsing, and online lyrics retrieval (using `Jint` to run source scripts).

### Tools (`tools/`)

- **`LightStudio.LightPlayer.Tools.AudioProbe`** — A console utility that exercises the decode/playback pipeline (metadata dump, decode, seek-decode, and OpenAL playback / queue smoke tests) directly through `LightStudio.FfmpegShim`.
- **`LightStudio.LightPlayer.Tools.LibraryProbe`** — A console utility that exercises `LightStudio.MediaLibraryCore` (library scan, query, CUE indexing, cover extraction, and startup diagnostics).

### Supporting files

- **`Makefile`** — Wraps the `dotnet` and Flatpak build workflows (see below).
- **`packaging/flatpak/`** — The Flatpak manifest (`im.hjc.LightPlayer.yml`), the `.desktop` launcher, AppStream metainfo, `.cue` MIME registration, and application icons.
- **`artifacts/flatpak/`** — Generated Flatpak build state, the pinned offline NuGet source list, and the final `.flatpak` bundle. Safe to delete.

## Dependencies

The following NuGet packages are restored automatically during build.

**Application — `LightStudio.LightPlayer`**
- `Avalonia` 12 (with `Avalonia.Desktop`, `Avalonia.Themes.Fluent`, `Avalonia.Fonts.Inter`, and `Avalonia.Controls.ItemsRepeater`) — the cross-platform UI framework
- `CommunityToolkit.Mvvm` — MVVM helpers
- `OpenTK.Audio.OpenAL` — OpenAL audio-output bindings
- `Tmds.DBus` *(Linux only)* — D-Bus / MPRIS "now playing" integration
- `OpenAL.Soft` *(Windows only)* — bundled OpenAL runtime

**FFmpeg interop — `LightStudio.FfmpegShim`**
- `FFmpeg.AutoGen` — managed FFmpeg bindings (8.1.0 by default; 7.1.1 for the Flatpak)

**Media library — `LightStudio.MediaLibraryCore`**
- `Microsoft.EntityFrameworkCore.Sqlite.Core` + `SQLitePCLRaw` — SQLite persistence using the system SQLite (`winsqlite3` on Windows)
- `Microsoft.EntityFrameworkCore.Tools` — EF Core migrations
- `Jint` — JavaScript engine used to run lyric-source scripts
- `Microsoft.Extensions.*` — logging, configuration, and in-memory caching

Native runtime dependencies (not NuGet): **FFmpeg** shared libraries, **OpenAL** (`libopenal.so.1`), and **SQLite** are provided by the host OS or, for the Flatpak, by `org.freedesktop.Platform` 25.08.

## Development

Development is done on the host OS: the `make` targets wrap the standard `dotnet` CLI and build against the host's .NET, FFmpeg, and OpenAL.

### Prerequisites (host build)

- **.NET 10 SDK**
- **FFmpeg 8.x** shared libraries (`libavcodec`, `libavformat`, `libavutil`, …) on the library path
- **OpenAL** runtime (`libopenal.so.1`)
- **SQLite** (system `libsqlite3`; standard on most Linux distributions)
- **GNU Make** (optional, to use the `Makefile` targets)

### Build & run

```sh
make            # build the app in Release (same as `make build`)
make run        # build and run the app
make clean      # remove build artifacts
```

Override the configuration or target architecture as needed:

```sh
make CONFIGURATION=Debug
make RID=linux-arm64
```

The equivalent commands without Make (the explicit `Platform` matches the projects' `<Platforms>` declaration):

```sh
dotnet build src/LightStudio.LightPlayer/LightStudio.LightPlayer.csproj -c Release -p:Platform=x64
dotnet run   --project src/LightStudio.LightPlayer/LightStudio.LightPlayer.csproj -c Release -p:Platform=x64
```

The diagnostic tools are run the same way, for example:

```sh
dotnet run --project tools/LightStudio.LightPlayer.Tools.LibraryProbe -p:Platform=x64 -- --help
dotnet run --project tools/LightStudio.LightPlayer.Tools.AudioProbe   -p:Platform=x64 -- --help
```

## Building a Flatpak

The app is built and published **self-contained inside the Flatpak sandbox** using the `dotnet10` SDK extension — the host `dotnet` is not used. NuGet restore runs offline from a pinned source list (`nuget-sources.json`) that `make flatpak` regenerates whenever a `.csproj` changes.

### Prerequisites (Flatpak build)

- **`flatpak`** and **`flatpak-builder`**
- **`python3`** — runs `flatpak-dotnet-generator.py` to pin the offline NuGet feed
- **`curl`** or **`wget`** — downloads the generator on first run
- The **flathub** remote and the following runtimes (installed automatically per-user by `make flatpak-deps`):
  - `org.freedesktop.Platform` // 25.08
  - `org.freedesktop.Sdk` // 25.08
  - `org.freedesktop.Sdk.Extension.dotnet10` // 25.08

### Build the bundle

```sh
make flatpak
```

This produces a single-file bundle at:

```
artifacts/flatpak/im.hjc.LightPlayer.flatpak
```

Override the target architecture or toolchain versions if needed (these must match the manifest):

```sh
make flatpak RID=linux-arm64
make flatpak DOTNET_SDK_VERSION=10 FREEDESKTOP_VERSION=25.08
```

## Installation

Build and install the Flatpak into your per-user installation in one step:

```sh
make flatpak-install
```

Then launch it from your desktop's application menu, or from a terminal:

```sh
flatpak run im.hjc.LightPlayer
```

To install an already-built bundle manually:

```sh
flatpak install --user artifacts/flatpak/im.hjc.LightPlayer.flatpak
```

## License

Copyright © 2026 David Huang. **All rights reserved.**

This project is **source-available for reference and private, personal, non-redistributable use only**; it is currently *not* FOSS licensed. See [LICENSE](LICENSE) for the full terms.
