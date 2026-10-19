# Lyrics source extensions

Light Player loads lyrics sources from JavaScript files in its application-data
`Scripts` directory. Desktop, Android, server, and `lrctest` run scripts with
[Jint](https://github.com/sebastienros/jint); the web client uses the browser's
JavaScript engine. A script must define the two async functions below. It may also define `getSourceInfo`
to provide text and links for source-selection interfaces. See
[`lrclib.js`](../src/LightStudio.LightPlayer/Resource/lrclib.js) for the bundled
implementation.

Scripts are trusted code. Browser scripts execute in the page's JavaScript
environment with access to its origin's resources. Only install extensions from
sources you trust. Each source keeps its own function scope and state between calls.

## Interface

```js
function getSourceInfo() {
    return [
        { text: "Provider information" },
        { text: "Terms of use", url: "https://example.com/terms" }
    ];
}

async function lookupLrcAsync(title, artist, signal) {
    // Return an array of lyric candidates.
}

async function downloadLrcAsync(lrcInfo, signal) {
    // Return the selected lyric content and type.
}
```

`getSourceInfo` is optional. It returns an array of JSON-serializable items. Each
item must contain display `text`; adding an absolute `url` renders that text as
an external link. Scripts without this function return no source information.

`lookupLrcAsync` receives the requested title and artist as strings. It must
return an array of JSON-serializable objects with this shape:

```js
{
    opaque: {},          // Optional source-specific, JSON-serializable data
    title: "Track title",
    artist: "Artist",
    album: "Album",
    duration: 240.5,     // Seconds
    instrumental: false
}
```

Missing properties receive their .NET defaults. Light Player adds the source
name itself. Use `opaque` to carry IDs, URLs, or already-fetched lyrics into
`downloadLrcAsync`; its value is serialized to JSON between calls.

`downloadLrcAsync` receives one candidate and must return:

```js
{ content: "[00:01.00]Lyrics", type: "synced" }
```

`type` is `"synced"` for timestamped LRC. Every other value is treated as
plain lyrics. Empty or whitespace-only `content` means that no lyrics are
available.

Both functions may return values directly or through an async `Promise`.
Calls for one source are serialized, receive an `AbortSignal`, and
have a 120-second promise timeout. Pass the signal to `fetch` so canceled
lookups also stop their network requests. Browser timeouts cannot interrupt
synchronous JavaScript that blocks the page's event loop.

## Runtime APIs

Use standard JavaScript and web APIs. Jint enables its web APIs and `fetch`
support with a 120-second HTTP timeout. The web client uses the page's native
APIs, including `fetch`, `AbortController`, and promises.

For example:

```js
async function lookupLrcAsync(title, artist, signal) {
    const query = `track_name=${encodeURIComponent(title)}&artist_name=${encodeURIComponent(artist)}`;
    const response = await fetch(`https://lrclib.net/api/search?${query}`, { signal });
    if (!response.ok) {
        throw new Error(`LRCLIB request failed with status ${response.status}`);
    }

    return (await response.json()).map(item => ({
        opaque: {
            plainLyrics: item.plainLyrics,
            syncedLyrics: item.syncedLyrics
        },
        title: item.trackName || item.name,
        artist: item.artistName,
        album: item.albumName,
        duration: item.duration,
        instrumental: item.instrumental
    }));
}

async function downloadLrcAsync(lrcInfo, signal) {
    if (lrcInfo.opaque.syncedLyrics) {
        return { content: lrcInfo.opaque.syncedLyrics, type: "synced" };
    }

    return { content: lrcInfo.opaque.plainLyrics || "", type: "plain" };
}
```

### NativeAOT and Browser Hosting

The runtime interface is `IJsDownloadSource`. The installed heads inject the
`JsDownloadSource` implementation from `LightStudio.Lyrics.Jint`, while the web
head injects `BrowserJsDownloadSource`. Candidate and download conversion use
the same source-generated JSON serializers, including under NativeAOT and
WebAssembly trimming. The browser dependency graph contains no Jint packages.

The browser host evaluates trusted script content with `Function`. A custom
Content Security Policy must permit this with `script-src 'unsafe-eval'`.
The current hosting configuration does not set a Content Security Policy.

### HTTP Behavior Per Head

The network implementation and platform policy differ by head. Installed heads
and `lrctest` use Jint `fetch` backed by .NET `HttpClient`; HTTPS certificate
validation uses the platform trust facilities.

| Head | Handler | What differs |
| --- | --- | --- |
| Desktop/Mobile, server, `lrctest` | `SocketsHttpHandler` | HTTP and HTTPS requests follow the host's network policy. |
| Browser (WebAssembly) | Native browser `fetch` | Providers must allow cross-origin requests and any non-safelisted request headers. The browser controls forbidden headers and applies mixed-content, cookie, and credential policies. The browser head provisions `lrclib.js` alone. |

The macOS and iOS HTTP paths use managed sockets rather than `NSURLSession`.
Apple's [App Transport Security (ATS)](https://developer.apple.com/documentation/security/preventing-insecure-network-connections)
applies to the URL Loading System, not lower-level socket connections, so its
HTTPS requirements do not govern these paths. Local-network privacy permissions
are separate from ATS.

Practical consequences for a script:

- Prefer `https://` for providers shared across heads. Browser mixed-content
  rules can block `http://` endpoints even when installed heads allow them.
- Treat transport headers (`Accept-Encoding`, `Connection`, `Host`,
  `Content-Length`) as owned by the network stack. For browser-compatible
  scripts, use only headers permitted by the browser and provider's CORS policy.
- Do not depend on a specific TLS version, cipher, or on a proxy being
  honored; those depend on the runtime, OS, and host configuration.
- Pass the supplied abort signal to each request.

## Testing a script

`tools/lrctest` is a console harness that runs a script through the same
Jint-based `JsDownloadSource` engine the installed apps use, without installing it or starting Light
Player. The script file name (without extension) becomes the source name.

```
dotnet run --project tools/lrctest -- <script.js> <title> [artist] [options]
```

| Option | Meaning |
| --- | --- |
| `-d`, `--download <index>` | Download the candidate at the given zero-based index. |
| `-i`, `--info` | Display the items returned by `getSourceInfo` without performing a lookup. |
| `-o`, `--out <file>` | Write the downloaded lyrics to a file instead of stdout. |
| `-h`, `--help` | Show usage. |

`--info` prints source information without performing a lookup. In normal lookup
mode, omitting `--download` only calls `lookupLrcAsync` and lists the candidates
it returned, so lookup and download can be checked separately:

```console
$ dotnet run --project tools/lrctest -- src/LightStudio.LightPlayer/Resource/lrclib.js --info
Source information from 'lrclib':
    Terms of Use -> https://lrclib.net/docs

$ dotnet run --project tools/lrctest -- src/LightStudio.LightPlayer/Resource/lrclib.js "Bohemian Rhapsody" Queen
20 candidate(s) from 'lrclib':
  [0] Bohemian rhapsody - Queen | album: The greatest hits | 351s
  ...

$ dotnet run --project tools/lrctest -- src/LightStudio.LightPlayer/Resource/lrclib.js "Bohemian Rhapsody" Queen -d 0

type: Synced
[00:00.00]Is this the real life
...
```

Script exceptions and rejected promises are reported with their full .NET stack
trace.

### Testing under NativeAOT

`dotnet run` uses the JIT runtime. The tool is configured for NativeAOT,
so publishing it checks the script and Jint web APIs under trimming:

```
dotnet publish tools/lrctest -c Release -r linux-x64 -p:Platform=x64
./artifacts/publish/lrctest/release_linux-x64/lrctest <script.js> <title> [artist] [options]
```

Use the runtime identifier and matching MSBuild platform for the machine you are
on (`linux-x64`/`x64`, `win-x64`/`x64`, `osx-arm64`/`ARM64`, ...).

Browser validation requires running the script in the web client because
`lrctest` does not exercise browser CORS, CSP, or page-thread behavior.