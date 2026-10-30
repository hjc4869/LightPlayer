# Build

## Prerequisites

All heads require the .NET 10 SDK; Android, iOS, and browser heads also require the corresponding .NET workload. FFmpeg declarations are generated at build time from the target's headers and libraries: Linux builds use the system's FFmpeg development packages (`libavcodec`, `libavformat`, `libavutil`, `libavfilter`, `libswresample`, and `libswscale` through pkg-config), and other targets use the restored `LightStudio.Ffmpeg` package. The [native binding build prerequisites](../tools/NativeBindings/README.md#build-prerequisites) list the libclang, compiler, SDK, and library requirements of each build host and target platform.

Linux host builds use FFmpeg 9.x shared libraries, the PulseAudio client library (`libpulse.so.0`),
and system SQLite (`libsqlite3`). GNU Make is optional; the Makefile wraps the standard `dotnet`
CLI and platform packaging tools.

### Linux FFmpeg development packages

Install the FFmpeg headers and libraries, pkg-config, and libclang before developing and testing on Linux with dotnet SDK. Flatpak FreeDesktop / dotnet / LLVM already included all necessary development headers, libraries and tools, see [Flatpak](#flatpak).
The `ffmpeg` command-line package alone does not provide all of these build dependencies.

**Debian and Ubuntu:**

```sh
sudo apt update
sudo apt install pkg-config clang libclang-dev \
  libavcodec-dev libavformat-dev libavutil-dev libavfilter-dev \
  libswresample-dev libswscale-dev
```

**Fedora:**

```sh
sudo dnf install pkgconf-pkg-config clang clang-devel ffmpeg-free-devel
```

Fedora's `ffmpeg-free-devel` provides the development files but supports a restricted set of
codecs. For broader codec support, enable [RPM Fusion](https://rpmfusion.org/Configuration)
and use its `ffmpeg-devel` package instead; keep the FFmpeg development and runtime packages
from the same provider.

**Arch Linux:**

```sh
sudo pacman -Syu --needed pkgconf clang ffmpeg
```

Arch Linux includes FFmpeg headers and pkg-config files in `ffmpeg`, and libclang in `clang`.

Verify that all six FFmpeg libraries are discoverable before building:

```sh
pkg-config --modversion libavcodec libavformat libavutil libavfilter libswresample libswscale
```

## Build and run

```sh
make            # build the app in Release (same as `make build`)
make run        # build and run the app
make clean      # remove build artifacts
```

The equivalent commands without Make (the explicit `Platform` matches the projects' `<Platforms>` declaration) are:

```sh
dotnet build src/LightStudio.LightPlayer.Desktop/LightStudio.LightPlayer.Desktop.csproj -c Release -p:Platform=x64
dotnet run   --project src/LightStudio.LightPlayer.Desktop/LightStudio.LightPlayer.Desktop.csproj -c Release -p:Platform=x64
```

Override the configuration or target architecture as needed:

```sh
make CONFIGURATION=Debug
make RID=linux-arm64
```

### Store releases

Pass `-p:IsStoreRelease=true` when building or publishing a desktop or mobile head for an app store.
The property defaults to `false`. For example:

```sh
dotnet publish src/LightStudio.LightPlayer.Desktop/LightStudio.LightPlayer.Desktop.csproj -c Release -p:Platform=x64 -p:IsStoreRelease=true
```

The MSIX packaging project passes `IsStoreRelease=true` to the desktop head automatically whenever it
produces a `.msixupload` store package, such as with `UapAppxPackageBuildMode=StoreOnly` in Release.
Sideload-only bundles keep the default.

The shared application project defines `STORE_RELEASE` for these builds and makes
`PlatformServices.UpdateAssetName` return `null` regardless of the asset assigned by a platform
head. The local update service consequently reports no support, makes no release-check requests,
and keeps the in-app update check and download UI hidden across desktop and mobile heads.

## GitHub Release Packages

`dotnet restore`, `dotnet build`, and `dotnet publish` automatically download the pinned GitHub
release assets, verify their SHA256 checksums, and prepare a local NuGet feed before package
resolution. `build/GitHubNuGet.targets` accepts ZIP archives containing NuGet packages and individual
`.nupkg` assets through `GitHubNuGetAsset` items in `Directory.Packages.props`. Each item names the
downloaded file and supplies `Url` and `Sha256` metadata. Keep the asset pins and central package
versions in sync when selecting releases.

The Avalonia platform fork comes from [its GitHub releases](https://github.com/hjc4869/Avalonia/releases).
AvaloniaStatic, FFmpeg, Fonts, GeoNames, ONNX, Photos, WebGPU, and sqlite-vec come from
[LightPlayer-Packages releases](https://github.com/hjc4869/LightPlayer-Packages/releases).
`nuget.config` maps all `LightStudio.*` packages to this local feed.
Jint uses the MyGet feed; other public dependencies use NuGet.org.

The initial restore needs network access. Subsequent restores reuse verified assets under
`artifacts/github-nuget/downloads` and packages under `artifacts/github-nuget/packages`. The target
serializes feed initialization across parallel restores and exposes the feed through
`RestoreAdditionalProjectSources`, including when a command supplies `--source`. The Flatpak source
generator evaluates the same asset items and supplies the pinned downloads for offline restoration.

Set `AvaloniaForkVersion` and `AvaloniaForkSha256` together to select the Avalonia fork release.
Set `-p:UseAvaloniaFork=false` to use the public Avalonia versions. Desktop, theme, and font
meta-packages use the public feed.

## Windows

Windows builds require Visual Studio 2026 or Build Tools with the "Desktop development with C++"
workload and a Windows SDK. NativeAOT publishing needs the MSVC tools for each target architecture
(x64 and ARM64 for the release packages). Install LLVM for the build host's architecture, including
`clang.exe`, `libclang.dll`, and `llvm-nm.exe`; for example:

```powershell
winget install --id LLVM.LLVM --exact
```

MSIX packaging additionally requires MSBuild 18 and the Visual Studio DesktopBridge targets.
Use a Visual Studio 2026 Developer PowerShell; the .NET SDK's `dotnet build` cannot build the
`.wapproj`.

### Visual Studio

Install Visual Studio support for .NET desktop development and Windows Application Packaging
Projects (MSIX), then open `LightPlayer-Windows.slnx`. Native toolchain discovery is described
in the [native binding build prerequisites](../tools/NativeBindings/README.md#build-prerequisites).

- **Debugging:** select
  `src/LightStudio.LightPlayer.Desktop/LightStudio.LightPlayer.Desktop.csproj` as the startup
  project, choose `x64` or `ARM64`, and build/start it with the debugger. This runs the unpackaged
  desktop head and is the quickest edit-build-debug loop.
- **Deploying:** select `packaging/LightPlayerMSIX/LightPlayerMSIX.wapproj` as the startup project,
  choose `x64` or `ARM64`, and build/start it. Visual Studio creates the MSIX package and deploys it
  locally.

The package project expects a temporary signing certificate; Visual Studio can create or select
a development certificate when configuring the package. The
[command-line MSIX build](#command-line-msix-build) creates the same temporary certificate used
by CI when it is not already present.

### WiX setup executables

Build both unpackaged installer architectures from PowerShell 7. WiX is restored through its
MSBuild SDK:

```powershell
pwsh -NoProfile -File packaging\LightPlayerSetup\build.ps1
```

To build one architecture or
override the package version:

```powershell
pwsh -NoProfile -File packaging\LightPlayerSetup\build.ps1 -Architecture x64 -Version 2.0.0.0
```

The script publishes the self-contained NativeAOT application, validates the MSI, and creates:

```text
artifacts\release\LightPlayerSetup-x64.exe
artifacts\release\LightPlayerSetup-arm64.exe
```

### Command-line MSIX build

Run the following from the repository root in a Visual Studio 2026 Developer PowerShell to create a signed sideload package containing both x64 and ARM64 variants:

```powershell
$repositoryRoot = (Get-Location).Path
$project = Join-Path $repositoryRoot 'packaging\LightPlayerMSIX\LightPlayerMSIX.wapproj'
$packageRoot = Join-Path $repositoryRoot 'artifacts\msix\AppPackages'
$certificatePath = Join-Path $repositoryRoot 'packaging\LightPlayerMSIX\LightPlayerMSIX_TemporaryKey.pfx'

if (-not (Test-Path $certificatePath)) {
  $cert = New-SelfSignedCertificate `
    -Type Custom `
    -Subject 'CN=Light Studio, O=Light Studio, C=US' `
    -KeyUsage DigitalSignature `
    -FriendlyName 'Light Studio Release Signing Certificate' `
    -CertStoreLocation 'Cert:\CurrentUser\My' `
    -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
  $password = [securestring]::new()
  $cert | Export-PfxCertificate -FilePath $certificatePath -Password $password | Out-Null
}

$env:RuntimeIdentifiers = 'win-x64;win-arm64'
New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null

msbuild $project `
  /restore `
  /m `
  /p:Configuration=Release `
  /p:Platform=x64 `
  /p:GenerateAppxPackageOnBuild=true `
  /p:UapAppxPackageBuildMode=SideloadOnly `
  /p:AppxBundle=Always `
  '/p:AppxBundlePlatforms=x64|ARM64' `
  /p:AppxPackageDir=$packageRoot `
  /p:AppxPackageSigningEnabled=true `
  /p:PackageCertificateKeyFile=$certificatePath `
  /p:PackageCertificatePassword=
```

The generated sideload package is placed under `artifacts\msix\AppPackages`. Its `*_Test` folder contains the `.msixbundle`, public `.cer`, `Add-AppDevPackage.ps1`, and `Install.ps1` deployment files.

## macOS

Desktop builds require Xcode and its command-line tools, which provide Clang, libclang, and the
Apple SDKs used for native bindings and NativeAOT linking.

The Apple Silicon release target publishes a self-contained NativeAOT app and packages it as a ZIP archive. Run it on macOS, where the required `codesign` and `ditto` tools are available:

```sh
make osxapp     # build artifacts/macos/Light\ Player.app and artifacts/release/LightPlayer-osx-arm64.zip
```

The `.app` is ad-hoc signed and not notarized. The ZIP archive preserves the app bundle's metadata.

## Flatpak

The app is built and published **self-contained inside the Flatpak sandbox** using the `dotnet10` SDK extension for Freedesktop 26.08. NuGet source discovery uses the same extension, so the host's installed `dotnet` is not used.

NuGet restore during the publish runs offline from a pinned source list (`nuget-sources.json`)
that `make flatpak` regenerates when its project, build-target, package-version, or feed inputs
change. `packaging/flatpak/nuget-sources.py` includes the Avalonia fork release ZIP with the version
and SHA256 from `Directory.Packages.props`. Flatpak fetches it into the MSBuild archive cache;
the restore target verifies and extracts it inside the offline build sandbox. Other packages
remain pinned individually to the public or private feed recorded by their restore metadata.

### Flatpak prerequisites

- **`flatpak`**, **`flatpak-builder`**, and **`elfutils`** (for `eu-strip`)
- **`python3`** - runs `packaging/flatpak/nuget-sources.py` to pin the offline NuGet feed
- The Flathub remote and these runtime/SDK components:
  - `org.freedesktop.Platform` // 26.08
  - `org.freedesktop.Sdk` // 26.08
  - `org.freedesktop.Sdk.Extension.dotnet10` // 26.08
  - `org.freedesktop.Sdk.Extension.llvm22` // 26.08 (provides libclang)

`make flatpak-deps` configures Flathub and installs the base SDK and .NET extension;
`make flatpak` lets flatpak-builder install the remaining manifest dependencies.
Unprivileged user namespaces must be allowed by the host's kernel and AppArmor policies
for the build sandbox.

### Build the bundle

```sh
make flatpak
```

The target runs `make src` to generate `artifacts/LightPlayer.tar.gz` from tracked
and non-ignored untracked sources. Flatpak extracts this complete source archive,
including desktop integration files under `packaging/flatpak`. The Flatpak-specific `nuget.config` replaces the
archive's feed configuration and clears package source mappings for the offline
`nuget-sources` feed. The Avalonia fork target adds its local feed during restore.

This produces a single-file bundle at:

```
artifacts/flatpak-x86_64/im.hjc.LightPlayer-x86_64.flatpak
```

Override the target architecture or toolchain versions as needed (these must match the manifest):

```sh
make flatpak RID=linux-arm64
make flatpak DOTNET_SDK_VERSION=10 FREEDESKTOP_VERSION=26.08
```

`RID=linux-arm64` writes `artifacts/flatpak-aarch64/im.hjc.LightPlayer-aarch64.flatpak` instead.

## Android APK

Android builds require the .NET `android` workload, JDK 17, and the Android SDK command-line tools.
Install platform-tools, Android SDK Platform 36, Build-Tools 36.0.0, and NDK
28.2.13676358. Set `JAVA_HOME` to the JDK and `ANDROID_HOME` (or `ANDROID_SDK_ROOT`) to the SDK,
and put the SDK's `cmdline-tools/latest/bin` and `platform-tools` directories on `PATH`:

```sh
dotnet workload install android
sdkmanager 'platform-tools' 'platforms;android-36' 'build-tools;36.0.0' 'ndk;28.2.13676358'
sdkmanager --licenses
export ANDROID_NDK_ROOT="${ANDROID_HOME:-$ANDROID_SDK_ROOT}/ndk/28.2.13676358"
```

The NDK supplies libclang and the native target toolchain. After setup:

```sh
make android           # build artifacts/bin/LightStudio.LightPlayer.Android/release
make android-install   # build and `adb install` the signed APK
```

## iOS

The iOS head requires macOS, Xcode with the matching iOS SDK, and the .NET 10 `ios` workload.
Use .NET SDK and workload set `10.0.100` with Xcode `26.0.1`.
With that SDK selected, select Xcode and install the matching workload:

```sh
export DEVELOPER_DIR=/Applications/Xcode_26.0.1.app/Contents/Developer
dotnet workload install ios --version 10.0.100
dotnet build src/LightStudio.LightPlayer.iOS/LightStudio.LightPlayer.iOS.csproj -c Debug -r iossimulator-arm64
xcrun simctl install booted artifacts/bin/LightStudio.LightPlayer.iOS/debug_iossimulator-arm64_static/lmcios.app
xcrun simctl launch booted im.hjc.LightPlayer
```

Use `iossimulator-x64` on an Intel Mac. A build without an explicit RID defaults to the host's
simulator architecture. Simulator bundles use the SDK's default ad-hoc signing.

Every iOS configuration uses NativeAOT. The head sets `PublishAot` and the .NET iOS SDK's
`_IsPublishing` switch so ordinary builds also run NativeAOT compilation.
Simulator bundles are created with `dotnet build`; `dotnet publish` packages device builds:

```sh
dotnet publish src/LightStudio.LightPlayer.iOS/LightStudio.LightPlayer.iOS.csproj -c Release -r ios-arm64
```

Device deployment requires an Apple signing identity and provisioning profile; these can be
selected through the SDK's `CodesignKey` and `CodesignProvision` properties.

The downloadable `LightPlayer-ios-arm64.ipa` is unsigned and must be re-signed with a
signing identity and provisioning profile before installation on a device.

The Make helpers honor `CONFIGURATION` (default: `Release`) and write bundles under
`artifacts/ios/<configuration>/<runtime-identifier>/lmcios.app`:

```sh
make iosapp
make ios-deploy IOS_DEVICE="My iPhone"
make ios-sim CONFIGURATION=Debug
make ios-sim-deploy CONFIGURATION=Debug
```

Both deploy targets build, install, and launch the app. Select a physical device using its name
or identifier from `xcrun devicectl list devices`. Set `IOS_CODESIGN_KEY` and
`IOS_CODESIGN_PROVISION` to override automatic signing selection for device builds.
Simulator deployment defaults to `IOS_SIMULATOR=booted`; set a simulator name or identifier
from `xcrun simctl list devices available` to select and boot another simulator. `IOS_SIM_RID`
defaults to the host architecture and can be set to `iossimulator-arm64` or `iossimulator-x64`.

The head hosts the shared single-view UI with sandboxed settings and caches, local and remote
library services, Audio Queue PCM playback, Now Playing controls, audio interruption handling,
and a native share sheet. Documents is available in Files and is offered as a default library
folder. Files opened through the picker or another app are imported into Documents/Imports;
selected external library folders retain security-scoped bookmarks.

VideoToolbox hardware decoding is enabled by default for new iOS settings. CoreVideo surfaces
can be imported directly into Metal, and iOS 16 or later also supports VideoToolbox resizing and
SDR color conversion when hardware video processing is selected. Unsupported streams fall back
to software decoding. Saved hardware-decoding preferences are preserved.

iOS device and simulator builds include ONNX-based photo indexing, semantic photo search,
and audio separation. CPU and Core ML devices use the shared model management UI; inference
requires an installed compatible model.

### iOS and macOS capabilities

| Area | macOS desktop | iOS |
| --- | --- | --- |
| Libraries, playlists, lyrics, remote-library access | Shared application services | Shared application services with sandboxed storage and folder grants |
| Hardware video and advanced color | VideoToolbox, Metal, extended-range output | VideoToolbox, Metal, extended-range output; native resizing requires iOS 16 |
| Photo search and audio separation | ONNX CPU/Core ML | ONNX CPU/Core ML |
| Audio devices | CoreAudio endpoint selection and exclusive mode | Audio Queue playback with system-managed output routing |
| Separate media windows and picture-in-picture | Desktop window hosts | Requires an iOS scene or AVKit presentation implementation |
| File reveal | Finder integration | Files access and native sharing; no containing-folder reveal implementation |
| Application updates | Desktop release download | Signed iOS deployment |

## Docker image

Build the standalone server image and run a local smoke test:

```sh
make docker
make docker-run
```

The image serves the API only. Add `INCLUDE_WEB=true` to build the WebAssembly client into it and serve it from the same origin:

```sh
make docker INCLUDE_WEB=true
```

The container can host a local library server or run in proxy-only mode for the browser client. See [Browser client](browser-client.md) for deployment details.

## WebAssembly client

Building the WebAssembly output requires the .NET SDK with the `wasm-tools` workload, Node.js,
Python 3, curl, and a host libclang. On Linux, install libclang as described in the
[Linux prerequisites](#linux-ffmpeg-development-packages); on macOS, Xcode provides it.
The `wasm-tools` workload supplies Emscripten Clang, `llvm-nm`, and the sysroot, but not libclang.
The media worker is a threaded .NET WebAssembly project that links the threaded
FFmpeg and Photos NuGet archives. Media and SQLite worker assets are built by the web project's MSBuild targets.
The media worker is published by a separate `dotnet publish` process that runs alongside the web project's own
build, in its own artifacts tree under `artifacts/browser-media-worker/<project>/`:

```sh
make web       # publish to artifacts/web
make web-cf    # package for Cloudflare Workers into artifacts/web-cf
make web-run   # publish without ahead-of-time compilation and serve on localhost
```

Add `MULTI_THREAD_APP=1` to any of them to build the threaded page variant, which runs the client on
the threaded runtime and decodes in process instead of publishing the media worker.

The browser head references `LightStudio.Fonts`, with its version pinned in
`Directory.Packages.props`. The package supplies the Noto Sans CJK SC Regular and Bold
fonts and their SIL Open Font License. MSBuild copies these assets to `wwwroot/fonts`
in both build and publish output; the browser downloads the fonts during startup.
The package and its GitHub Actions workflow live in the
[LightPlayer-Packages repository](https://github.com/hjc4869/LightPlayer-Packages/tree/master/package/fonts).

The container image builds the client when `INCLUDE_WEB=true`. See [self-hosting guide](browser-client.md)
for hosting and secure-context requirements.

## Build source info

[BuildSourceVersion.targets](../build/BuildSourceVersion.targets) captures the nearest reachable
Git tag plus the number of commits since that tag. Without a reachable tag, it uses
`<branch>-<short-commit>` with `-dirty` for a modified working tree.
Detached HEAD uses `HEAD` as the branch name. Without Git or a repository, it uses
the project version from [Directory.Build.props](../Directory.Build.props) followed by a UTC
`yyyyMMddTHHmmssZ` timestamp.
[BuildInfoGenerator.cs](../tools/LightStudio.LightPlayer.Generators/BuildInfoGenerator.cs) emits
this identity as `BuildInfo.SourceVersion`, replacing characters outside `A-Za-z0-9._+-` with
`-`. Both About presentations display the generated constant alongside the assembly version.
