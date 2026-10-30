# LightStudio.LightPlayer
# =======================
#   make                            Build the main Avalonia desktop app with dotnet.
#   make flatpak                    Build the app and produce a Flatpak bundle (Linux).
#   make flatpak-install            Build the Flatpak and install it (user installation).
#   make osxapp                     Build the macOS Apple Silicon .app bundle and ZIP archive.
#   make iosapp                     Build the signed iOS device .app bundle.
#   make ios-deploy                 Build, install, and launch on IOS_DEVICE.
#   make ios-sim                    Build the iOS simulator .app bundle.
#   make ios-sim-deploy             Build, install, and launch on IOS_SIMULATOR.
#   make run                        Build and run the app.
#   make web                        Build the full shared UI for WebAssembly.
#   make web-cf                     Package the WebAssembly build for Cloudflare Workers.
#   make web-run                    Build and serve the full shared UI WebAssembly app on localhost.
#   make docker                     Build the standalone server container image.
#                                   (INCLUDE_WEB=true also bundles the browser client.)
#   make src                        Create artifacts/LightPlayer.tar.gz from working-tree sources.
#   make clean                      Remove build artifacts.
#
# The Flatpak targets build the desktop app *inside the Flatpak sandbox* with the
# org.freedesktop.Sdk.Extension.dotnet<N> SDK extension (build-only). The desktop app
# serves the REST API only; the WebAssembly client is published separately (see the
# docker target with INCLUDE_WEB=true, which serves it from the same origin as the API). The desktop publish
# stays self-contained (the .NET runtime is bundled, so
# no runtime is added to the Flatpak). Restore inside the sandbox is offline:
# `make flatpak` first pins every NuGet package into nuget-sources.json using
# packaging/flatpak/nuget-sources.py.
#
# Overridable variables (e.g. `make CONFIGURATION=Debug`, `make RID=linux-arm64`):
#   CONFIGURATION        dotnet configuration (default: Release)
#   RID                  .NET runtime identifier for the Flatpak publish (default: linux-x64)
#   INCLUDE_WEB          bundle the browser client in the container image (default: false)
#   WEB_RUN_PORT         localhost port that web-run serves on (default: 5000)
#   MULTI_THREAD_APP     build the browser client on the threaded .NET runtime, decoding in
#                        process instead of in the media worker (default: 0)
#   DOTNET_SDK_VERSION   dotnet SDK extension major version (default: 10; must match the manifest)
#   FREEDESKTOP_VERSION  freedesktop runtime/SDK version (default: 26.08; must match the manifest)
#   IOS_DEVICE           device identifier or name for ios-deploy (required)
#   IOS_SIMULATOR        simulator identifier or name (default: booted)
#   IOS_SIM_RID          simulator runtime identifier (default: host architecture)
#   IOS_CODESIGN_KEY     optional Apple signing identity for device builds
#   IOS_CODESIGN_PROVISION optional provisioning profile for device builds

APP_ID                          := im.hjc.LightPlayer
AVALONIA_PROJECT                := src/LightStudio.LightPlayer.Desktop/LightStudio.LightPlayer.Desktop.csproj
ANDROID_PROJECT                 := src/LightStudio.LightPlayer.Android/LightStudio.LightPlayer.Android.csproj
IOS_PROJECT                     := src/LightStudio.LightPlayer.iOS/LightStudio.LightPlayer.iOS.csproj
WEB_PROJECT                     := src/LightStudio.LightPlayer.Web/LightStudio.LightPlayer.Web.csproj
WEB_OUTPUT                      := artifacts/web
WEB_CF_OUTPUT                   := artifacts/web-cf
WEB_CF_SOURCE                   := packaging/cloudflare
WEB_RUN_OUTPUT                  := artifacts/web-run
WEB_RUN_PORT                    ?= 5000
MULTI_THREAD_APP                ?= 0
WEB_BUILD_PROPERTIES            := $(if $(filter 1 true,$(MULTI_THREAD_APP)),-p:MultiThreadApp=true)
# Cloudflare rejects any single static asset larger than this.
WEB_CF_ASSET_LIMIT              := 26214400
SERVER_PROJECT                  := src/LightStudio.LightPlayer.Server/LightStudio.LightPlayer.Server.csproj
VIDEODEMO_PROJECT               := tools/videodemo/videodemo.csproj
VIDEODEMO_MEDIA                 ?= artifacts/test.mkv
CONFIGURATION                   ?= Release
RID                             ?= linux-x64

# The projects declare <Platforms>x64;ARM64</Platforms>, so an explicit MSBuild
# platform is required; map it from the .NET runtime identifier.
ifeq ($(RID),linux-arm64)
PLATFORM         := ARM64
FLATPAK_ARCH     := aarch64
else
PLATFORM         := x64
FLATPAK_ARCH     := x86_64
endif

# Flatpak build layout (all under artifacts/, safe to delete).
FLATPAK_DIR      := artifacts/flatpak-$(FLATPAK_ARCH)
FLATPAK_STAGING  := $(FLATPAK_DIR)/staging
FLATPAK_BUILDDIR := $(FLATPAK_DIR)/build
FLATPAK_STATEDIR := $(FLATPAK_DIR)/.flatpak-builder
FLATPAK_REPO     := $(FLATPAK_DIR)/repo
FLATPAK_BUNDLE   := $(FLATPAK_DIR)/$(APP_ID)-$(FLATPAK_ARCH).flatpak
FLATPAK_MANIFEST := packaging/flatpak/$(APP_ID).yml
FLATHUB_REPO_URL := https://dl.flathub.org/repo/flathub.flatpakrepo

# Standalone server container.
DOCKER_IMAGE     ?= lightplayer-server
DOCKERFILE       := packaging/docker/Dockerfile
# Bundle the WebAssembly client into the image (`make docker INCLUDE_WEB=true`).
INCLUDE_WEB      ?= false

# macOS build layout (Apple Silicon, matching the GitHub Actions release job).
# The .app bundle assembly, Info.plist versioning, ad-hoc signing and ZIP
# packaging are handled by the osxapp target below without a separate
# packaging script.
MACOS_DIR         := artifacts/macos
MACOS_PUBLISH     := $(MACOS_DIR)/publish
MACOS_APP         := $(MACOS_DIR)/Light Player.app
MACOS_ZIP         := artifacts/release/LightPlayer-osx-arm64.zip
MACOS_EXECUTABLE  := lmc
MACOS_ICON        := packaging/macos/AppIcon.icns
MACOS_INFO_PLIST  := packaging/macos/Info.plist

# Bundle version from Directory.Build.props. macOS caps CFBundleShortVersionString
# at three integers, so derive it from the first three components of the four-part
# build version.
MACOS_VERSION       := $(shell sed -n 's|.*<Version>\(.*\)</Version>.*|\1|p' Directory.Build.props | head -n1)
MACOS_SHORT_VERSION := $(shell printf '%s' '$(MACOS_VERSION)' | cut -d. -f1-3)

# Android build layout
ANDROID_APK      := artifacts/bin/LightStudio.LightPlayer.Android/release/im.hjc.LightPlayer-Signed.apk

IOS_DEVICE              ?=
IOS_SIMULATOR           ?= booted
IOS_SIM_RID             ?= $(if $(filter arm64,$(shell uname -m)),iossimulator-arm64,iossimulator-x64)
IOS_SIM_PLATFORM        = $(if $(filter iossimulator-arm64,$(IOS_SIM_RID)),ARM64,x64)
IOS_CODESIGN_KEY        ?=
IOS_CODESIGN_PROVISION  ?=
IOS_OUTPUT              := artifacts/ios/$(CONFIGURATION)
IOS_APP                 := $(IOS_OUTPUT)/ios-arm64/lmcios.app
IOS_SIM_APP             := $(IOS_OUTPUT)/$(IOS_SIM_RID)/lmcios.app

# Build-only .NET SDK provided by the Flatpak SDK extension. These versions must
# match runtime-version / sdk-extensions in the manifest.
DOTNET_SDK_VERSION    ?= 10
FREEDESKTOP_VERSION   ?= 26.08
FLATPAK_SDK           := org.freedesktop.Sdk
DOTNET_SDK_EXTENSION  := org.freedesktop.Sdk.Extension.dotnet$(DOTNET_SDK_VERSION)

# Offline NuGet feed for the in-sandbox restore. nuget-sources.json pins every package
# (download URL + sha512); flatpak-builder fetches them during its network phase so the
# build itself runs with no network. Regenerated whenever the projects, the central
# package versions or the configured feeds change.
FLATPAK_NUGET_SOURCES := $(FLATPAK_DIR)/nuget-sources.json
FLATPAK_GENERATOR     := packaging/flatpak/nuget-sources.py
CSPROJ_FILES          := $(shell find src tools/LightStudio.LightPlayer.Generators tools/NativeBindings -name '*.csproj')
NUGET_INPUTS          := $(CSPROJ_FILES) Directory.Build.props Directory.Build.targets Directory.Packages.props build/GitHubNuGet.targets build/NativeBindings.Configuration.targets tools/NativeBindings/NativeBindings.props tools/NativeBindings/NativeBindings.Selection.targets tools/NativeBindings/NativeBindings.targets nuget.config

.PHONY: all build run videodemo osxapp macos-publish web web-cf web-run browser-build browser-run browser-media docker docker-run flatpak flatpak-deps flatpak-nuget-sources flatpak-install clean
.PHONY: iosapp ios-deploy ios-sim ios-sim-deploy src

all: build

build:
	dotnet build "$(AVALONIA_PROJECT)" -c "$(CONFIGURATION)" -p:Platform=$(PLATFORM)

src:
	python3 tools/package-source.py

run:
	dotnet run --project "$(AVALONIA_PROJECT)" -c "$(CONFIGURATION)" -p:Platform=$(PLATFORM)

# Standalone harness for the video pipeline in src/LightStudio.LightPlayer/Video.
# Override the media with `make videodemo VIDEODEMO_MEDIA=/path/to/file.mkv`.
videodemo:
	dotnet run --project "$(VIDEODEMO_PROJECT)" -c "$(CONFIGURATION)" -p:Platform=$(PLATFORM) -- "$(VIDEODEMO_MEDIA)"

# Publish the macOS desktop head with the same NativeAOT settings as CI.
macos-publish:
	dotnet publish "$(AVALONIA_PROJECT)" \
		-c "$(CONFIGURATION)" \
		-r osx-arm64 \
		-p:Platform=ARM64 \
		--self-contained true \
		-o "$(MACOS_PUBLISH)"

# Assemble the macOS .app bundle from the self-contained publish: copy the
# payload, trim runtime-unneeded files (debug symbols, satellite assemblies),
# inject the version into Info.plist, copy AppIcon.icns, then ad-hoc code sign
# (required for arm64 to launch; --deep re-signs the bundled native dylibs the
# file moves above invalidated). macOS only: needs codesign/PlistBuddy/ditto.
osxapp: macos-publish
	@test "$$(uname -s)" = "Darwin" || { echo "error: 'make osxapp' requires macOS." >&2; exit 1; }
	rm -rf "$(MACOS_APP)"
	mkdir -p "$(MACOS_APP)/Contents/MacOS" "$(MACOS_APP)/Contents/Resources"
	cp -a "$(MACOS_PUBLISH)/." "$(MACOS_APP)/Contents/MacOS/"
	find "$(MACOS_APP)/Contents/MacOS" \( -name '*.pdb' -o -name '*.dbg' \) -delete
	find "$(MACOS_APP)/Contents/MacOS" -iname '*.dsym' -prune -exec rm -rf {} +
	find "$(MACOS_APP)/Contents/MacOS" -name '*.resources.dll' -delete
	find "$(MACOS_APP)/Contents/MacOS" -mindepth 1 -type d -empty -delete
	chmod +x "$(MACOS_APP)/Contents/MacOS/$(MACOS_EXECUTABLE)"
	cp "$(MACOS_INFO_PLIST)" "$(MACOS_APP)/Contents/Info.plist"
	/usr/libexec/PlistBuddy -c "Set :CFBundleShortVersionString $(MACOS_SHORT_VERSION)" "$(MACOS_APP)/Contents/Info.plist"
	/usr/libexec/PlistBuddy -c "Set :CFBundleVersion $(MACOS_VERSION)" "$(MACOS_APP)/Contents/Info.plist"
	cp "$(MACOS_ICON)" "$(MACOS_APP)/Contents/Resources/AppIcon.icns"
	codesign --force --deep --sign - --timestamp=none "$(MACOS_APP)"
	codesign --verify --deep --strict "$(MACOS_APP)"
	@echo "==> Wrote $(MACOS_APP)"
	mkdir -p "$(dir $(MACOS_ZIP))"
	rm -f "$(MACOS_ZIP)"
	ditto -c -k --sequesterRsrc --keepParent "$(MACOS_APP)" "$(MACOS_ZIP)"
	@echo "==> Wrote $(MACOS_ZIP)"

# Build the complete shared Avalonia UI with browser-local and REST-backed services.
web:
	dotnet publish "$(WEB_PROJECT)" -o "$(WEB_OUTPUT)" -c "$(CONFIGURATION)" $(WEB_BUILD_PROPERTIES)
	python3 tools/package-web.py "$(WEB_OUTPUT)/wwwroot"

# Package the same app for Cloudflare Workers. Assets above the 25 MiB per-file limit ship as
# brotli only; the worker serves those with `encodeBody: "manual"` so the runtime does not compress
# them a second time. Deploy with `wrangler deploy` from $(WEB_CF_OUTPUT).
web-cf:
	@command -v brotli >/dev/null 2>&1 || { echo "error: brotli is required"; exit 1; }
	dotnet publish "$(WEB_PROJECT)" -o "$(WEB_CF_OUTPUT)" -c "$(CONFIGURATION)" $(WEB_BUILD_PROPERTIES)
	python3 tools/package-web.py "$(WEB_CF_OUTPUT)/wwwroot"
	find "$(WEB_CF_OUTPUT)/wwwroot" -type f ! -name '*.br' -size +$(WEB_CF_ASSET_LIMIT)c \
		-exec brotli --quality=6 --rm -- {} +
	cp "$(WEB_CF_SOURCE)/wrangler.jsonc" "$(WEB_CF_SOURCE)/worker.js" "$(WEB_CF_OUTPUT)/"
	cp "$(WEB_CF_SOURCE)/_headers" "$(WEB_CF_OUTPUT)/wwwroot/"
	@oversized=$$(find "$(WEB_CF_OUTPUT)/wwwroot" -type f -size +$(WEB_CF_ASSET_LIMIT)c); \
		if [ -n "$$oversized" ]; then printf 'error: over the 25 MiB asset limit:\n%s\n' "$$oversized"; exit 1; fi
	@echo "==> Wrote $(WEB_CF_OUTPUT) (run 'wrangler deploy' from there)"

# Publish the full shared UI without ahead-of-time compilation and serve it on localhost with the
# cross-origin isolation its threaded media worker needs; Ctrl+C stops the server.
web-run:
	dotnet publish "$(WEB_PROJECT)" -o "$(WEB_RUN_OUTPUT)" -c Release \
		-p:RunAOTCompilation=false -p:BrowserMediaWorkerAot=false $(WEB_BUILD_PROPERTIES)
	python3 tools/package-web.py "$(WEB_RUN_OUTPUT)/wwwroot"
	node src/LightStudio.LightPlayer.Web/serve.mjs "$(WEB_RUN_OUTPUT)/wwwroot" "$(WEB_RUN_PORT)"

# Build the standalone server container: publishes the headless API server, and with
# INCLUDE_WEB=true also the WebAssembly client, served from the same image.
docker:
	@command -v docker >/dev/null 2>&1 || { echo "error: docker is required"; exit 1; }
	docker build -f "$(DOCKERFILE)" --build-arg INCLUDE_WEB=$(INCLUDE_WEB) \
		$(if $(filter true,$(INCLUDE_WEB)),--build-arg WEB_VERSION="$$(python3 tools/package-web.py --print-version)") \
		-t "$(DOCKER_IMAGE)" .

# Run the container against ./music with an ephemeral data volume. Override the
# credentials and paths for anything other than a local smoke test.
docker-run: docker
	docker run --rm -it -p 47832:47832 \
		-v "$(CURDIR)/music:/music:ro" \
		-e LIGHTPLAYER_USERNAME=lightplayer \
		-e LIGHTPLAYER_PASSWORD=lightplayer \
		"$(DOCKER_IMAGE)"

# Ensure the Flathub remote, base SDK, and build-only .NET SDK extension are present.
# The Platform runtime is pulled on demand by --install-deps-from.
flatpak-deps:
	@command -v flatpak >/dev/null 2>&1 || { echo "error: flatpak is required"; exit 1; }
	flatpak remote-add --user --if-not-exists flathub $(FLATHUB_REPO_URL)
	@for ref in $(FLATPAK_SDK)//$(FREEDESKTOP_VERSION) $(DOTNET_SDK_EXTENSION)//$(FREEDESKTOP_VERSION); do \
		flatpak info --arch $(FLATPAK_ARCH) "$$ref" >/dev/null 2>&1 || \
		flatpak install --user --arch $(FLATPAK_ARCH) --assumeyes --noninteractive flathub "$$ref"; \
	done

# Pin the offline NuGet sources inside the SDK extension sandbox (this step needs
# network). -p:Flatpak=true captures the FFmpeg 8.1.0 bindings and
# -r $(RID) captures the self-contained runtime packs for the target. flatpak-deps is
# an order-only prereq so the (always-run) phony dep does not force regeneration.
$(FLATPAK_NUGET_SOURCES): $(NUGET_INPUTS) $(FLATPAK_GENERATOR) Makefile | flatpak-deps
	@command -v python3 >/dev/null 2>&1 || { echo "error: python3 is required"; exit 1; }
	# --dotnet-args (argparse REMAINDER) must stay last.
	python3 "$(FLATPAK_GENERATOR)" \
		"$@" "$(AVALONIA_PROJECT)" \
		--dotnet $(DOTNET_SDK_VERSION) --freedesktop $(FREEDESKTOP_VERSION) --runtime $(RID) \
		--dotnet-args -p:Configuration=Release -p:Flatpak=true -p:Platform=$(PLATFORM) -p:SelfContained=true

# Convenience alias to (re)generate the pinned NuGet sources.
flatpak-nuget-sources: $(FLATPAK_NUGET_SOURCES)

# Build the Flatpak. The app is compiled and published self-contained *inside* the
# sandbox by the .NET SDK extension, using the source archive generated by src
# and the pinned offline NuGet feed.
flatpak: flatpak-deps $(FLATPAK_NUGET_SOURCES) src
	@command -v flatpak-builder >/dev/null 2>&1 || { echo "error: flatpak-builder is required (install 'flatpak-builder')"; exit 1; }
	rm -rf "$(FLATPAK_STAGING)"
	mkdir -p "$(FLATPAK_STAGING)"
	cp artifacts/LightPlayer.tar.gz "$(FLATPAK_STAGING)/"
	cp "$(FLATPAK_MANIFEST)" "$(FLATPAK_STAGING)/"
	cp "$(FLATPAK_NUGET_SOURCES)" "$(FLATPAK_STAGING)/nuget-sources.json"
	cp packaging/flatpak/nuget.config "$(FLATPAK_STAGING)/"
	flatpak-builder --force-clean --disable-cache --disable-rofiles-fuse --user \
	    --arch $(FLATPAK_ARCH) \
		--install-deps-from=flathub \
		"--state-dir=$(FLATPAK_STATEDIR)" "--repo=$(FLATPAK_REPO)" \
		"$(FLATPAK_BUILDDIR)" "$(FLATPAK_STAGING)/$(APP_ID).yml"
	flatpak build-bundle --arch $(FLATPAK_ARCH) "$(FLATPAK_REPO)" "$(FLATPAK_BUNDLE)" $(APP_ID)
	@echo "Flatpak bundle written to $(FLATPAK_BUNDLE)"

flatpak-install: flatpak
	flatpak install --user --reinstall --assumeyes "$(FLATPAK_BUNDLE)"

android:
	dotnet build "$(ANDROID_PROJECT)" \
				 --configuration "$(CONFIGURATION)"

android-install: android
	adb install "$(ANDROID_APK)"

iosapp:
	@test "$$(uname -s)" = "Darwin" || { echo "error: 'make iosapp' requires macOS." >&2; exit 1; }
	dotnet build "$(IOS_PROJECT)" -c "$(CONFIGURATION)" -r ios-arm64 -p:Platform=ARM64 \
		-o "$(IOS_OUTPUT)/ios-arm64" \
		$(if $(IOS_CODESIGN_KEY),-p:CodesignKey="$(IOS_CODESIGN_KEY)") \
		$(if $(IOS_CODESIGN_PROVISION),-p:CodesignProvision="$(IOS_CODESIGN_PROVISION)")
	@echo "==> Wrote $(IOS_APP)"

ios-deploy:
	@test -n "$(strip $(IOS_DEVICE))" || { echo "error: set IOS_DEVICE to a device identifier or name from 'xcrun devicectl list devices'." >&2; exit 1; }
	$(MAKE) iosapp
	xcrun devicectl device install app --device "$(IOS_DEVICE)" "$(IOS_APP)"
	xcrun devicectl device process launch --device "$(IOS_DEVICE)" --terminate-existing "$(APP_ID)"

ios-sim:
	@test "$$(uname -s)" = "Darwin" || { echo "error: 'make ios-sim' requires macOS." >&2; exit 1; }
	dotnet build "$(IOS_PROJECT)" -c "$(CONFIGURATION)" -r "$(IOS_SIM_RID)" \
		-p:Platform=$(IOS_SIM_PLATFORM) -o "$(IOS_OUTPUT)/$(IOS_SIM_RID)"
	@echo "==> Wrote $(IOS_SIM_APP)"

ios-sim-deploy: ios-sim
	xcrun simctl bootstatus "$(IOS_SIMULATOR)" -b
	xcrun simctl install "$(IOS_SIMULATOR)" "$(IOS_SIM_APP)"
	xcrun simctl launch --terminate-running-process "$(IOS_SIMULATOR)" "$(APP_ID)"

clean:
	rm -rf artifacts
