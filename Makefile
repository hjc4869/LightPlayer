# LightStudio.LightPlayer
# =======================
#   make                  Build the main Avalonia desktop app with dotnet.
#   make flatpak          Build the app and produce a Flatpak bundle (Linux).
#   make flatpak-install  Build the Flatpak and install it (user installation).
#   make run              Build and run the app.
#   make clean            Remove build artifacts.
#
# The Flatpak targets build the app *inside the Flatpak sandbox* with the
# org.freedesktop.Sdk.Extension.dotnet<N> SDK extension (build-only) instead of the
# host `dotnet`. The publish stays self-contained (the .NET runtime is bundled, so no
# runtime is added to the Flatpak). Restore inside the sandbox is offline: `make
# flatpak` first pins every NuGet package into nuget-sources.json using
# flatpak-dotnet-generator.py.
#
# Overridable variables (e.g. `make CONFIGURATION=Debug`, `make RID=linux-arm64`):
#   CONFIGURATION        dotnet configuration (default: Release)
#   RID                  .NET runtime identifier for the Flatpak publish (default: linux-x64)
#   DOTNET_SDK_VERSION   dotnet SDK extension major version (default: 10; must match the manifest)
#   FREEDESKTOP_VERSION  freedesktop runtime/SDK version (default: 25.08; must match the manifest)

APP_ID           := im.hjc.LightPlayer
AVALONIA_PROJECT := src/LightStudio.LightPlayer/LightStudio.LightPlayer.csproj
CONFIGURATION    ?= Release
RID              ?= linux-x64

# The projects declare <Platforms>x64;ARM64</Platforms>, so an explicit MSBuild
# platform is required; map it from the .NET runtime identifier.
ifeq ($(RID),linux-arm64)
PLATFORM         := ARM64
else
PLATFORM         := x64
endif

# Flatpak build layout (all under artifacts/, safe to delete).
FLATPAK_DIR      := artifacts/flatpak
FLATPAK_STAGING  := $(FLATPAK_DIR)/staging
FLATPAK_BUILDDIR := $(FLATPAK_DIR)/build
FLATPAK_STATEDIR := $(FLATPAK_DIR)/.flatpak-builder
FLATPAK_REPO     := $(FLATPAK_DIR)/repo
FLATPAK_BUNDLE   := $(FLATPAK_DIR)/$(APP_ID).flatpak
FLATPAK_MANIFEST := packaging/flatpak/$(APP_ID).yml
FLATHUB_REPO_URL := https://dl.flathub.org/repo/flathub.flatpakrepo

# Build-only .NET SDK provided as a Flatpak SDK extension (the host `dotnet` is not
# used for the Flatpak). These must match runtime-version / sdk-extensions in the
# manifest.
DOTNET_SDK_VERSION    ?= 10
FREEDESKTOP_VERSION   ?= 25.08
FLATPAK_SDK           := org.freedesktop.Sdk
DOTNET_SDK_EXTENSION  := org.freedesktop.Sdk.Extension.dotnet$(DOTNET_SDK_VERSION)

# Offline NuGet feed for the in-sandbox restore. nuget-sources.json pins every package
# (URL + sha512); flatpak-builder fetches them during its network phase so the build
# itself runs with no network. Regenerated whenever a .csproj changes.
FLATPAK_NUGET_SOURCES := $(FLATPAK_DIR)/nuget-sources.json
FLATPAK_GENERATOR     := $(FLATPAK_DIR)/flatpak-dotnet-generator.py
FLATPAK_GENERATOR_URL := https://raw.githubusercontent.com/flatpak/flatpak-builder-tools/737c0085912f9f7dabf9341d4608e2a77a51a73a/dotnet/flatpak-dotnet-generator.py
CSPROJ_FILES          := $(shell find src -name '*.csproj')

.PHONY: all build run flatpak flatpak-deps flatpak-nuget-sources flatpak-install clean

all: build

# Build the main desktop app with dotnet.
build:
	dotnet build $(AVALONIA_PROJECT) -c $(CONFIGURATION) -p:Platform=$(PLATFORM)

# Build and run the app.
run:
	dotnet run --project $(AVALONIA_PROJECT) -c $(CONFIGURATION) -p:Platform=$(PLATFORM)

# Ensure the flathub remote and the build-only .NET SDK extension are present in the
# per-user Flatpak installation. The SDK extension (and its base SDK) are needed both
# by flatpak-dotnet-generator.py (to restore the offline sources) and by the
# flatpak-builder build; the Platform runtime is pulled on demand by
# --install-deps-from during the build.
flatpak-deps:
	@command -v flatpak >/dev/null 2>&1 || { echo "error: flatpak is required"; exit 1; }
	flatpak remote-add --user --if-not-exists flathub $(FLATHUB_REPO_URL)
	@for ref in $(FLATPAK_SDK)//$(FREEDESKTOP_VERSION) $(DOTNET_SDK_EXTENSION)//$(FREEDESKTOP_VERSION); do \
		flatpak info "$$ref" >/dev/null 2>&1 || \
		flatpak install --user --assumeyes --noninteractive flathub "$$ref"; \
	done

# Fetch the NuGet source generator into the artifacts dir (kept out of git).
$(FLATPAK_GENERATOR):
	@mkdir -p $(@D)
	@if command -v curl >/dev/null 2>&1; then \
		curl -fsSL $(FLATPAK_GENERATOR_URL) -o $@; \
	elif command -v wget >/dev/null 2>&1; then \
		wget -qO $@ $(FLATPAK_GENERATOR_URL); \
	else \
		echo "error: curl or wget is required to download flatpak-dotnet-generator.py"; exit 1; \
	fi

# Pin the offline NuGet sources by restoring inside the dotnet SDK extension sandbox
# (this step needs network). -p:Flatpak=true captures the FFmpeg 7.1.1 bindings and
# -r $(RID) captures the self-contained runtime packs for the target. flatpak-deps is
# an order-only prereq so the (always-run) phony dep does not force regeneration.
$(FLATPAK_NUGET_SOURCES): $(CSPROJ_FILES) $(FLATPAK_GENERATOR) | flatpak-deps
	@command -v python3 >/dev/null 2>&1 || { echo "error: python3 is required"; exit 1; }
	# The output/project positionals come first: --runtime uses greedy nargs and would
	# otherwise swallow them. --dotnet-args (argparse REMAINDER) must stay last.
	python3 $(FLATPAK_GENERATOR) \
		$@ $(AVALONIA_PROJECT) \
		--dotnet $(DOTNET_SDK_VERSION) --freedesktop $(FREEDESKTOP_VERSION) --runtime $(RID) \
		--dotnet-args -p:Flatpak=true -p:Platform=$(PLATFORM) -p:SelfContained=true

# Convenience alias to (re)generate the pinned NuGet sources.
flatpak-nuget-sources: $(FLATPAK_NUGET_SOURCES)

# Build the Flatpak. The app is compiled and published self-contained *inside* the
# sandbox by the dotnet SDK extension (no host dotnet); the build inputs are the
# project tree (staged without bin/ or obj/) and the pinned offline NuGet feed.
flatpak: flatpak-deps $(FLATPAK_NUGET_SOURCES)
	@command -v flatpak-builder >/dev/null 2>&1 || { echo "error: flatpak-builder is required (install 'flatpak-builder')"; exit 1; }
	rm -rf $(FLATPAK_STAGING)
	mkdir -p $(FLATPAK_STAGING)/src
	cp -a src/. $(FLATPAK_STAGING)/src/
	find $(FLATPAK_STAGING)/src -type d \( -name bin -o -name obj \) -prune -exec rm -rf {} +
	cp $(FLATPAK_MANIFEST) $(FLATPAK_STAGING)/
	cp $(FLATPAK_NUGET_SOURCES) $(FLATPAK_STAGING)/nuget-sources.json
	cp packaging/flatpak/$(APP_ID).desktop $(FLATPAK_STAGING)/
	cp packaging/flatpak/$(APP_ID).metainfo.xml $(FLATPAK_STAGING)/
	cp packaging/flatpak/$(APP_ID).mime.xml $(FLATPAK_STAGING)/
	cp -r packaging/flatpak/icons $(FLATPAK_STAGING)/
	flatpak-builder --force-clean --disable-rofiles-fuse --user \
		--install-deps-from=flathub \
		--state-dir=$(FLATPAK_STATEDIR) --repo=$(FLATPAK_REPO) \
		$(FLATPAK_BUILDDIR) $(FLATPAK_STAGING)/$(APP_ID).yml
	flatpak build-bundle $(FLATPAK_REPO) $(FLATPAK_BUNDLE) $(APP_ID)
	@echo "Flatpak bundle written to $(FLATPAK_BUNDLE)"

# Build and install the Flatpak into the per-user installation.
flatpak-install: flatpak
	flatpak install --user --reinstall --assumeyes $(FLATPAK_BUNDLE)

# Remove build artifacts.
clean:
	-dotnet clean $(AVALONIA_PROJECT) -c $(CONFIGURATION) -p:Platform=$(PLATFORM)
	rm -rf $(FLATPAK_DIR)
