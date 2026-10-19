# Light Player standalone server
# ==============================
# Builds the headless server, which hosts a local library API or acts only as a
# same-origin relay when LIGHTPLAYER_PROXY_ONLY=true. The WebAssembly browser client is
# left out unless INCLUDE_WEB=true is passed, since serving it is optional and building
# it costs the wasm-tools workload and several hundred megabytes of image.
#
#   docker build -f packaging/docker/Dockerfile -t lightplayer-server .
#   docker build -f packaging/docker/Dockerfile --build-arg INCLUDE_WEB=true \
#     -t lightplayer-server .
#   docker run -p 47832:47832 \
#     -v /path/to/music:/music:ro -v lightplayer-data:/data \
#     -e LIGHTPLAYER_USERNAME=me -e LIGHTPLAYER_PASSWORD=secret \
#     lightplayer-server
#
# Passing "configure" instead runs a terminal UI over the same settings, writing them to
# LIGHTPLAYER_CONFIG (/config/lightplayer.json by default); it can also index the library
# and show what the scan found. It needs an interactive terminal:
#
#   docker run --rm -it -v /path/to/config:/config -v /path/to/music:/music:ro \
#     -v lightplayer-data:/data lightplayer-server configure
#
# Without the client the image serves the API only; point browsers at a separately hosted
# client (see docs/browser-client.md) and list its origin in LIGHTPLAYER_ALLOWED_ORIGINS.
#
# Authentication is mandatory in full-server mode. Proxy-only mode leaves authentication
# to the selected upstream Light Player server.
#
# The server is published as a self-contained NativeAOT binary, so the final image needs
# no .NET runtime - only the C library and the FFmpeg shared objects it dlopen()s.
# NativeAOT cannot cross-compile, so a foreign-architecture build has to run under a
# matching builder (buildx with QEMU, or a native node).

ARG DOTNET_VERSION=10.0
# Bundle the browser client into the image. Only "true" and "false" are accepted: the
# value names the runtime stage below, because a COPY cannot be made conditional.
ARG INCLUDE_WEB=false

# ---- Stage 1: publish the browser client -------------------------------------------
# WebAssembly output is architecture-independent, so build it on the host architecture
# even for a foreign target image.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION}-noble AS web
ARG WEB_VERSION
WORKDIR /src
# The binding generator parses the browser headers with libclang.
RUN apt update && apt install python-is-python3 nodejs libclang-dev -y
RUN dotnet workload install wasm-tools
COPY Directory.Build.props Directory.Build.targets Directory.Packages.props nuget.config LICENSE THIRDPARTY.txt ./
COPY build/ ./build/
COPY src/ ./src/
COPY tools/LightStudio.LightPlayer.Generators/ ./tools/LightStudio.LightPlayer.Generators/
COPY tools/NativeBindings/ ./tools/NativeBindings/
COPY tools/package-web.py ./tools/package-web.py
RUN dotnet publish src/LightStudio.LightPlayer.Web/LightStudio.LightPlayer.Web.csproj \
        -c Release -o /out/web \
    # Pre-compressed copies are only useful behind a server that negotiates them from
    # disk; the host does, but keeping both doubles the image size for no gain here.
    && find /out/web/wwwroot -name '*.gz' -delete \
    && cp src/LightStudio.LightPlayer/Assets/light-logo.ico /out/web/wwwroot/favicon.ico \
    && python3 tools/package-web.py /out/web/wwwroot --version "$WEB_VERSION"

# ---- Stage 2: publish the server -----------------------------------------------------
# Generate bindings against the same Debian FFmpeg headers and libraries as the runtime.
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS server-sdk
FROM debian:forky AS server
ARG TARGETARCH
COPY --from=server-sdk /usr/share/dotnet /usr/share/dotnet
ENV DOTNET_ROOT=/usr/share/dotnet
ENV PATH="/usr/share/dotnet:${PATH}"
WORKDIR /src
# The binding generator needs the FFmpeg development packages, pkg-config, and libclang; ILC links with clang and zlib.
RUN apt-get update \
    && apt-get install --no-install-recommends -y ca-certificates clang libclang-dev pkg-config zlib1g-dev libicu-dev libssl-dev libavcodec-dev libavfilter-dev libavformat-dev libavutil-dev libswresample-dev libswscale-dev \
    && rm -rf /var/lib/apt/lists/*
COPY Directory.Build.props Directory.Build.targets Directory.Packages.props nuget.config LICENSE THIRDPARTY.txt ./
COPY build/ ./build/
COPY src/ ./src/
COPY tools/LightStudio.LightPlayer.Generators/ ./tools/LightStudio.LightPlayer.Generators/
COPY tools/NativeBindings/ ./tools/NativeBindings/
RUN set -eu; \
    case "$TARGETARCH" in \
      amd64) RID=linux-x64;   PLATFORM=x64 ;; \
      arm64) RID=linux-arm64; PLATFORM=ARM64 ;; \
      *) echo "unsupported architecture: $TARGETARCH" >&2; exit 1 ;; \
    esac; \
        case "$(pkg-config --modversion libavcodec)" in \
            62.*) FFMPEG_API_MAJOR=8 ;; \
            63.*) FFMPEG_API_MAJOR=9 ;; \
            *) echo "unsupported FFmpeg codec ABI" >&2; exit 1 ;; \
        esac; \
    dotnet publish src/LightStudio.LightPlayer.Server/LightStudio.LightPlayer.Server.csproj \
        -c Release -r "$RID" -p:Platform="$PLATFORM" --self-contained true \
            -p:FfmpegApiMajor="$FFMPEG_API_MAJOR" \
        -o /out/server; \
    find /out/server \( -name '*.pdb' -o -name '*.dbg' \) -delete

# ---- Stage 3: runtime ----------------------------------------------------------------
FROM debian:forky AS runtime-base
RUN apt-get update \
    && apt-get install --no-install-recommends -y ffmpeg va-driver-all mesa-libgallium libcurl4 ca-certificates \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=server /out/server ./

# No LIGHTPLAYER_* defaults are set here. The binary already defaults to the paths below
# and to 0.0.0.0:47832, and it serves /app/wwwroot only when that directory holds an
# index.html, so setting them would add nothing -- while making every one of them
# override /config/lightplayer.json, which is what the terminal UI writes.

VOLUME ["/music", "/video", "/data"]
EXPOSE 47832

ENTRYPOINT ["/app/LightStudio.LightPlayer.Server"]

# Stage "runtime-true" is the only reference to stage "web", so the client is not built
# at all unless INCLUDE_WEB selects it.
FROM runtime-base AS runtime-false

FROM runtime-base AS runtime-true
COPY --from=web /out/web/wwwroot ./wwwroot/

FROM runtime-${INCLUDE_WEB} AS runtime
