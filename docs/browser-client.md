# Self-hosting the browser client

Light Player can serve a media library and its browser player from one container. This guide
uses Docker Compose for the server and Caddy on the host for HTTPS. The browser player can also
be hosted separately as static files.

See the [project overview](../README.md) for platform support.

## Run with Docker

You need:

- A Linux x64 or ARM64 Docker host, with Docker BuildKit and Docker Compose.
- Git, GNU Make, and Python 3 on the build machine. The container build installs the .NET SDK,
  WebAssembly workload, and native build dependencies.
- Media folders readable by the container and persistent storage for its configuration and index.
- For access from other devices, a domain pointing to the host and an HTTPS reverse proxy.
  The Caddy example below needs inbound ports 80 and 443.

Build on the target architecture, or use a matching Docker builder with emulation.

### 1. Build the image

```sh
git clone https://github.com/hjc4869/LightPlayer.git
cd LightPlayer
make docker INCLUDE_WEB=true
```

This creates the local `lightplayer-server` image with both the library server and browser player.
Without `INCLUDE_WEB=true`, the image serves only the API. If you build on a different machine,
transfer the image to the deployment host before continuing.

### 2. Create the deployment

Keep the deployment files outside the source checkout:

```sh
mkdir -p "$HOME/lightplayer"
cd "$HOME/lightplayer"
```

Create `compose.yaml` with the following contents. Replace the two `/srv/media/...` paths with
existing absolute paths on the host; remove the video mount if you do not need it.

```yaml
services:
  lightplayer:
    image: lightplayer-server
    restart: unless-stopped
    ports:
      - "127.0.0.1:47832:47832"
    volumes:
      - /srv/media/music:/music:ro
      - /srv/media/video:/video:ro
      - lightplayer-data:/data
      - lightplayer-config:/config

volumes:
  lightplayer-data:
  lightplayer-config:
```

Media mounts are read-only. The named volumes retain the library index, server state, cache,
and configuration across container replacements. Port 47832 is reachable only from the host;
the reverse proxy provides access to other devices.

### 3. Configure and start

Open the terminal configuration UI:

```sh
docker compose run --rm lightplayer configure
```

- On **Server**, set **User name** and **New password**. Full-server mode requires both.
  Keep the listen address `0.0.0.0`, port `47832`, and the default data, cache, and browser-client
  folders inside the container.
- On **Library**, use the container paths `/music` and `/video`, not the host paths. Remove
  unused folders. **Scan the library every time the server starts** is enabled by default.
- Choose **Save**, then **Quit**. The password is stored as a hash in
  `/config/lightplayer.json`, not as plaintext.

Start the service and follow its startup and scan logs:

```sh
docker compose up -d
docker compose logs -f lightplayer
```

Ctrl+C exits log following without stopping the server. On the Docker host,
`http://localhost:47832` is suitable for an initial browser check. Plain HTTP on a LAN address
is not a secure browser context; use HTTPS before connecting from another device.

### 4. Put HTTPS in front of it

Install Caddy on the Docker host and add this site to its configuration, replacing
`player.example.com` with your domain:

```caddyfile
player.example.com {
    reverse_proxy 127.0.0.1:47832
}
```

Reload the host's Caddy service after saving the configuration. With DNS and ports 80/443
configured, Caddy obtains a certificate and proxies HTTP and WebSocket traffic to Light Player.
The Light Player server supplies the required browser isolation and service-worker headers;
the proxy must preserve them.

Open `https://player.example.com`, connect to the library at that same URL, and sign in with
the credentials you saved. A player and API served from this same origin need no allowed-origin
entry. Keep the unencrypted backend port private.

## Verify the deployment

Run these checks from a machine that can reach the public URL:

```sh
curl --fail --head https://player.example.com/
curl --fail https://player.example.com/versions
curl --fail https://player.example.com/api/v1/server
```

The root response must include:

```http
Cross-Origin-Opener-Policy: same-origin
Cross-Origin-Embedder-Policy: require-corp
Cross-Origin-Resource-Policy: same-origin
```

`/versions` must return a JSON catalog, not HTML, and `/api/v1/server` must return server
information. Open `https://player.example.com/diag` to check secure-context, shared-memory,
worker, and decoder capabilities independently of the app.

| Symptom | Check |
| --- | --- |
| No browser player at `/` | Build with `INCLUDE_WEB=true` and replace the running container with that image. |
| Workers or shared memory unavailable | Use HTTPS or localhost and check that the proxy preserves the isolation headers. |
| Empty library | Check media mounts, read permissions, container folder paths, and scan logs. |
| Sign-in fails | Check saved credentials and environment overrides. |
| A separately hosted player cannot connect | Check allowed origins, HTTPS/mixed-content rules, and browser local-network permissions. |
| Video will not play | Check `/diag`, the browser console, server logs, and whether the source needs transcoding. |

Capability probes do not guarantee hardware acceleration, smooth playback, or physical HDR output.
Client logs appear in the browser console. Server logs are available through
`docker compose logs`; set `LIGHTPLAYER_LOG_LEVEL=debug` for additional stream-selection details.

## Configuration and maintenance

Settings are read from `/config/lightplayer.json`, or the path in `LIGHTPLAYER_CONFIG`.
Environment variables override saved settings. To let the terminal UI manage a setting, omit
its corresponding environment variable from Compose.

Stop the server before using the configuration UI, especially before scanning there: both
processes write the same library database. Restart to apply saved changes:

```sh
docker compose stop lightplayer
docker compose run --rm lightplayer configure
docker compose up -d
```

For unattended configuration, add an `environment` mapping under the Compose service.
Common settings are:

| Variable | Purpose and default |
| --- | --- |
| `LIGHTPLAYER_USERNAME` | Sign-in name; required in full-server mode unless saved in the config file. |
| `LIGHTPLAYER_PASSWORD_HASH` | Pre-hashed password; takes precedence over `LIGHTPLAYER_PASSWORD`. The terminal UI can generate and save the hash. |
| `LIGHTPLAYER_PASSWORD` | Plaintext password alternative; avoid keeping it in deployment files. |
| `LIGHTPLAYER_LIBRARY_PATHS` | Music folders inside the container; comma-separated, default `/music`. |
| `LIGHTPLAYER_VIDEO_PATHS` | Video folders inside the container; comma-separated, default `/video`. |
| `LIGHTPLAYER_SCAN_ON_STARTUP` | Scan when the server starts; default `true`. |
| `LIGHTPLAYER_ALLOWED_ORIGINS` | Websites allowed to call the API; comma-separated, default empty. |
| `LIGHTPLAYER_STATIC_PATH` | Published browser-client folder; default `/app/wwwroot` in the image. An empty value serves only the API. |
| `LIGHTPLAYER_PROXY_ONLY` | Relay to another Light Player server instead of hosting a library; default `false`. |

### Transcoding

Video re-encoding is off by default. To enable VA-API hardware transcoding on a Linux host,
add the following under the Compose service and ensure the render device exists and is accessible:

```yaml
    devices:
      - /dev/dri:/dev/dri
    environment:
      LIGHTPLAYER_TRANSCODE: "true"
      LIGHTPLAYER_TRANSCODE_HARDWARE: "true"
      LIGHTPLAYER_TRANSCODE_DEVICE_TYPE: vaapi
      LIGHTPLAYER_TRANSCODE_DEVICE: /dev/dri/renderD128
```

The **Transcoding** page in the terminal UI exposes encoder and quality controls.
Software fallback is separately opt-in with `LIGHTPLAYER_TRANSCODE_ALLOW_SOFTWARE=true`;
budget CPU capacity before enabling it. Hardware scaling is also opt-in with
`LIGHTPLAYER_TRANSCODE_HARDWARE_SCALING=true`.

### Updates and backups

Pull the desired source revision and rebuild in the source checkout:

```sh
git pull --ff-only
make docker INCLUDE_WEB=true
```

Then replace the container from the deployment directory:

```sh
cd "$HOME/lightplayer"
docker compose up -d --force-recreate
docker compose logs -f lightplayer
```

Back up the Compose file and both named volumes while the server is stopped. Keep the config
and data backups private. `docker compose down` preserves named volumes; adding `--volumes`
deletes them.

The browser pins its selected deployment. Use **More > Check for updates** to switch to a new
deployment. If the pinned assets are unavailable after an update, choose **Restart with latest
version** on the error screen.

## Other server arrangements

### Separate player and library server

Build the API-only image with `make docker` and host the player using the
[static-hosting instructions](#static-hosting). On the library server, set
`LIGHTPLAYER_ALLOWED_ORIGINS` to the player's full origin, such as `https://player.example.com`.
Allowing an origin does not bypass authentication. Use HTTPS for both services.

The desktop app can also expose a library through **Settings > Remote access**. Enable sign-in,
keep its plain-HTTP endpoint on a trusted private network, and account for browser local-network
permissions. Use an HTTPS API relay when direct browser access is unavailable.

### Proxy-only relay

`LIGHTPLAYER_PROXY_ONLY=true` serves the bundled player and relays API requests to another
Light Player server. Build with `INCLUDE_WEB=true` to include the player. Local library paths and
local credentials are not used; the selected upstream server authenticates users.

Forwarding destinations are client-controlled. Restrict access to the relay with a VPN or
reverse-proxy access controls, and restrict network egress to trusted destinations. Upstream
sign-in is not access control for the relay itself. The relay supports the Light Player API,
not WebDAV.

## Static hosting

For a separately hosted player, install the [WebAssembly build prerequisites](build.md#webassembly-client)
and publish from the repository root:

```sh
dotnet workload install wasm-tools
make web
```

Deploy the entire `artifacts/web/wwwroot` directory to the root of an HTTPS site, including
hidden files if present, both workers, and their runtime assets. Static hosting provides the player,
not a library server or API relay. Use a local folder or connect to a separately configured server.

Configure your static host as follows:

| Requirement | Configuration |
| --- | --- |
| Isolation | Send the three isolation headers shown in [deployment verification](#verify-the-deployment) on client assets and entry pages. |
| MIME types | `.js` and `.mjs`: JavaScript; `.wasm`: `application/wasm`; `.webmanifest`: `application/manifest+json`. |
| Catalog | `/versions`: `application/json`, `Cache-Control: no-store`; a missing catalog must return an error, not the entry page. |
| Entry points | `/`, `/index.html`, and `/version-loader.js`: `Cache-Control: no-cache`. Route `/diag` to the root entry page too. |
| Service-worker scope | Send `Service-Worker-Allowed: /` on `/<version>/service-worker.js`. |
| Versioned assets | Preserve version directory names and serve assets from the requested directory. Missing assets must return an error, not the entry page. |
| Compression | If serving `.br` or `.gz` copies, send the matching `Content-Encoding` and the original file's MIME type. |

### Deployment versions

Each build leaves `index.html`, `version-loader.js`, and the `/versions` JSON catalog at the
site root, with application assets under `/<version>/`. Version directories are immutable:
deploy a new version rather than changing the contents of an existing one.

On first load, the player needs the catalog and selects the newest build. Later loads use the
saved version and check for updates after startup; cached application assets can load offline.
For a multi-version static site, retain older directories and merge their catalog entries into
`/versions` when publishing. Otherwise, users pinned to a removed version need **Restart with
latest version**.

### Cloudflare Workers

Install the Brotli CLI and Cloudflare Wrangler CLI in addition to the WebAssembly prerequisites.
Build the Cloudflare package, authenticate, and deploy it under your chosen Worker name:

```sh
make web-cf
cd artifacts/web-cf
wrangler login
wrangler deploy --name lightplayer
```

The package includes the hosting headers and a Worker for compressed assets. This is static-only
hosting; configure allowed origins on any separate library server.

### WebDAV servers

When connecting the player to WebDAV, the WebDAV server must support exact `206 Partial Content`
range responses and these CORS settings:

| CORS setting | Values |
| --- | --- |
| Allowed origin | The browser player's origin |
| Allowed methods | `GET`, `PROPFIND`, `OPTIONS` |
| Allowed request headers | `Authorization`, `Content-Type`, `Depth`, `Range`, `If-Range` |
| Exposed response headers | `Content-Length`, `Content-Range`, `ETag`, `Last-Modified` |
