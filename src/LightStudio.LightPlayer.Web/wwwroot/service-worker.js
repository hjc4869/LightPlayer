// Offline cache and share-target endpoint for the installed app.
//
// Requested versioned assets and fingerprinted runtime files are cached on demand.
// The root entry page and loader are network-first; catalogs, API calls, and ranges bypass caching.

const Version = 'v1';
const RuntimeCache = `lightplayer-${Version}`;
const AssetPrefix = new URL('./', self.location.href).pathname;
const ShareTargetPath = '/share-target';
const ShareDatabaseName = 'LightPlayerShare';
const ShareStore = 'shares';

// No skipWaiting: a replacement takes over only once every tab is gone, so a running app never has
// its runtime swapped for a newer build underneath it.
self.addEventListener('activate', event => {
    event.waitUntil((async () => {
        const names = await caches.keys();
        await Promise.all(names
            .filter(name => name.startsWith('lightplayer-') && name !== RuntimeCache)
            .map(name => caches.delete(name)));
        await self.clients.claim();
    })());
});

self.addEventListener('fetch', event => {
    const request = event.request;
    const url = new URL(request.url);

    if (request.method === 'POST' && url.origin === self.location.origin && url.pathname === ShareTargetPath) {
        event.respondWith(receiveShare(request));
        return;
    }

    if (request.method !== 'GET'
        || url.origin !== self.location.origin
        || url.pathname === '/versions'
        || request.cache === 'no-store'
        || url.pathname.startsWith('/api/')
        || request.headers.has('range')) {
        return;
    }

    const versionedAsset = AssetPrefix !== '/' && url.pathname.startsWith(AssetPrefix);
    event.respondWith(versionedAsset || isFingerprinted(url) ? cacheFirst(event) : networkFirst(event));
});

// `_framework/Avalonia.Base.bncaqasir0.wasm` and friends: the name changes whenever the bytes do.
function isFingerprinted(url) {
    return /\.[a-z0-9]{8,12}\.(wasm|js|mjs|dat|pdb)$/.test(url.pathname);
}

async function cacheFirst(event) {
    const cache = await caches.open(RuntimeCache);
    const hit = await cache.match(event.request);
    if (hit) {
        return hit;
    }

    const response = await fetch(event.request);
    event.waitUntil(store(cache, event.request, response.clone()));
    return response;
}

async function networkFirst(event) {
    const cache = await caches.open(RuntimeCache);
    try {
        const response = await fetch(event.request);
        event.waitUntil(store(cache, event.request, response.clone()));
        return response;
    } catch (error) {
        const hit = await cache.match(event.request)
            ?? (event.request.mode === 'navigate' ? await cache.match('/') : undefined);
        if (hit) {
            return hit;
        }

        throw error;
    }
}

// The body handed back by fetch() is already decoded, but its headers still describe the encoded
// form. Replaying those verbatim would hand the page a plain body labeled `Content-Encoding: br`
// with the compressed length, which is exactly what the Cloudflare and self-hosted heads send for
// the large runtime assets.
async function store(cache, request, response) {
    if (!response.ok || response.type !== 'basic') {
        return;
    }

    const headers = new Headers(response.headers);
    headers.delete('Content-Encoding');
    headers.delete('Content-Length');
    await cache.put(request, new Response(await response.blob(), {
        status: response.status,
        statusText: response.statusText,
        headers,
    }));
}

// A share is a POST the app cannot read after the redirect, so the files are stashed under an id
// the page then claims. Redirecting rather than answering keeps the app on its own start URL.
async function receiveShare(request) {
    const id = `${Date.now().toString(36)}${Math.random().toString(36).substring(2, 10)}`;
    try {
        const form = await request.formData();
        const files = form.getAll('media').filter(entry => entry instanceof File);
        if (files.length > 0) {
            await putShare(id, files);
            return Response.redirect(`/?shared=${encodeURIComponent(id)}`, 303);
        }
    } catch {
        // Fall through: the app opens as if it had been launched normally.
    }

    return Response.redirect('/', 303);
}

function putShare(id, files) {
    return new Promise((resolve, reject) => {
        const open = indexedDB.open(ShareDatabaseName, 1);
        open.onupgradeneeded = () => open.result.createObjectStore(ShareStore);
        open.onerror = () => reject(open.error);
        open.onsuccess = () => {
            const database = open.result;
            const transaction = database.transaction(ShareStore, 'readwrite');
            transaction.objectStore(ShareStore).put({ files }, id);
            transaction.oncomplete = () => { database.close(); resolve(); };
            transaction.onerror = () => { database.close(); reject(transaction.error); };
        };
    });
}
