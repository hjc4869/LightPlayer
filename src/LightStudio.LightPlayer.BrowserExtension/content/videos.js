// Finds the frame's video elements and classifies each one by the redirect path it needs.
(() => {
    const api = globalThis.browser ?? globalThis.chrome;

    const recordsMaxAgeMs = 1500;
    // Content-script requests skip the page's service worker in Chromium only.
    const skipsServiceWorker = !api.runtime.getURL('').startsWith('moz-extension:');
    const protectedVideos = new WeakSet();
    // video -> the source the manifest path could not play.
    const takeoverSources = new WeakMap();
    const shadowVideos = new Set();
    const ids = new WeakMap();
    const byId = new Map();
    const lightVideos = document.getElementsByTagName('video');
    let nextId = 1;
    let records = null;
    let recordsTime = 0;
    let recordsPromise = null;

    // A page that receives `encrypted` uses a key system for this element.
    document.addEventListener('encrypted', (event) => {
        if (event.target?.nodeName === 'VIDEO') {
            protectedVideos.add(event.target);
        }
    }, true);

    function idOf(video) {
        let id = ids.get(video);
        if (!id) {
            id = nextId++;
            ids.set(video, id);
            byId.set(id, new WeakRef(video));
        }
        return id;
    }

    function findById(id) {
        const video = byId.get(id)?.deref();
        return video?.isConnected ? video : null;
    }

    function shadowRootOf(element) {
        // Only HTML elements host shadow roots; Chromium's dom.openOrClosedShadowRoot throws for others.
        if (!(element instanceof HTMLElement)) {
            return null;
        }
        if (element.shadowRoot) {
            return element.shadowRoot;
        }
        if (typeof element.openOrClosedShadowRoot !== 'undefined') {
            return element.openOrClosedShadowRoot ?? null;
        }
        return api.dom?.openOrClosedShadowRoot?.(element) ?? null;
    }

    function collectShadowVideos(root, depth) {
        if (depth > 8) {
            return;
        }
        const walker = document.createTreeWalker(root, NodeFilter.SHOW_ELEMENT);
        for (let node = walker.nextNode(); node; node = walker.nextNode()) {
            const shadow = shadowRootOf(node);
            if (shadow) {
                for (const video of shadow.querySelectorAll('video')) {
                    shadowVideos.add(video);
                }
                collectShadowVideos(shadow, depth + 1);
            }
        }
    }

    // Adds the videos inside shadow trees along an event path, such as the one under the pointer.
    function collectFromPath(path) {
        let found = false;
        for (const node of path) {
            if (node.nodeName === 'VIDEO' && node.getRootNode() !== document && !shadowVideos.has(node)) {
                shadowVideos.add(node);
                found = true;
            } else if (node.nodeType === Node.ELEMENT_NODE && node.getRootNode() === document) {
                const shadow = shadowRootOf(node);
                if (shadow) {
                    for (const video of shadow.querySelectorAll('video')) {
                        if (!shadowVideos.has(video)) {
                            shadowVideos.add(video);
                            found = true;
                        }
                    }
                }
            }
        }
        return found;
    }

    // Every connected video in the frame. A deep scan also walks shadow trees.
    function all(deep = false) {
        if (deep && document.documentElement) {
            collectShadowVideos(document.documentElement, 0);
        }
        const list = [...lightVideos];
        for (const video of shadowVideos) {
            if (video.isConnected) {
                list.push(video);
            } else {
                shadowVideos.delete(video);
            }
        }
        return list;
    }

    // The media requests the background observed for this frame.
    function frameRecords() {
        if (records && performance.now() - recordsTime < recordsMaxAgeMs) {
            return Promise.resolve(records);
        }
        recordsPromise ??= api.runtime.sendMessage({ k: 'frameRequests' })
            .then((result) => {
                records = result ?? { requests: [], serviceWorkerMedia: false, workerRequests: [] };
                recordsTime = performance.now();
                return records;
            }, (error) => {
                console.error('Light Media Center: reading request records failed', error);
                return { requests: [], serviceWorkerMedia: false, workerRequests: [] };
            })
            .finally(() => {
                recordsPromise = null;
            });
        return recordsPromise;
    }

    function isManifestUrl(url) {
        try {
            const path = new URL(url).pathname.toLowerCase();
            return path.endsWith('.m3u8') || path.endsWith('.m3u') || path.endsWith('.mpd');
        } catch {
            return false;
        }
    }

    function isProtected(video) {
        return protectedVideos.has(video) || !!video.mediaKeys;
    }

    // Marks the video's current source as one only takeover can redirect.
    function markTakeover(video) {
        takeoverSources.set(video, video.currentSrc);
    }

    // One of: protected, none, file, manifest, mediaSource, unsupported.
    async function classify(video) {
        if (isProtected(video)) {
            return 'protected';
        }
        const source = video.currentSrc || '';
        const objectSource = video.srcObject;
        if (objectSource) {
            // A MediaStream (camera, screen, WebRTC) has no file or manifest to send.
            return Object.prototype.toString.call(objectSource) === '[object MediaSourceHandle]' ? 'mediaSource' : 'unsupported';
        }
        if (!source) {
            return 'none';
        }
        let protocol;
        try {
            protocol = new URL(source).protocol;
        } catch {
            return 'unsupported';
        }
        const { requests, serviceWorkerMedia, workerRequests } = await frameRecords();
        const manifest = () => (takeoverSources.get(video) === source || (skipsServiceWorker && serviceWorkerMedia) ? 'mediaSource' : 'manifest');
        const isManifest = (r) => r.kind === 'hls' || r.kind === 'dash';
        if (protocol === 'http:' || protocol === 'https:') {
            const request = requests.findLast((r) => r.url === source || r.finalUrl === source);
            if (request?.kind === 'hls' || request?.kind === 'dash' || isManifestUrl(source)) {
                return manifest();
            }
            return 'file';
        }
        if (protocol === 'blob:') {
            // Where content-script requests go through the page's service worker, its manifests count.
            const loaded = requests.some(isManifest) || (!skipsServiceWorker && workerRequests.some(isManifest));
            return loaded ? manifest() : 'mediaSource';
        }
        return 'unsupported';
    }

    function invalidateRecords() {
        records = null;
    }

    globalThis.LightPlayerVideos = Object.freeze({ all, idOf, findById, collectFromPath, classify, invalidateRecords, markTakeover });
})();
