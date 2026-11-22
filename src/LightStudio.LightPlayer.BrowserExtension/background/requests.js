// Records the media requests each tab and frame made, so a content script can classify its videos
// and the plain-file path can forward the request headers the browser sent.
(() => {
    const api = globalThis.browser ?? globalThis.chrome;

    const maxMediaRecordsPerFrame = 24;
    const maxManifestRecordsPerFrame = 16;
    const maxPending = 2048;
    const workerRecordAgeMs = 10 * 60 * 1000;
    const tabKeyPrefix = 'requests.';
    const workersKey = 'requests.workers';

    const hlsTypes = new Set(['application/vnd.apple.mpegurl', 'application/x-mpegurl', 'audio/mpegurl', 'audio/x-mpegurl']);
    const dashTypes = new Set(['application/dash+xml']);
    const mediaTypes = new Set(['application/mp4', 'video/mp2t', 'application/webm']);
    const genericTypes = new Set(['', 'application/octet-stream', 'binary/octet-stream']);
    const mediaExtensions = new Set([
        'mp4', 'm4v', 'm4a', 'm4s', 'mov', 'webm', 'mkv', 'ts', 'cmfv', 'cmfa', 'aac', 'mp3', 'ogg', 'ogv',
        'oga', 'opus', 'flac', 'wav',
    ]);

    // requestId -> request headers seen before the response.
    const pending = new Map();
    // tabId -> Map(frameId -> record[])
    const tabs = new Map();
    // initiator origin -> record[] for requests no tab owns, such as a page's service worker.
    let workers = new Map();
    const dirtyTabs = new Set();
    let dirtyWorkers = false;
    let flushTimer = null;

    const restored = restore();

    async function restore() {
        try {
            const stored = await api.storage.session.get(null);
            for (const [key, value] of Object.entries(stored)) {
                if (key === workersKey) {
                    workers = new Map(Object.entries(value));
                } else if (key.startsWith(tabKeyPrefix)) {
                    const tabId = Number(key.slice(tabKeyPrefix.length));
                    const frames = new Map(Object.entries(value).map(([frameId, records]) => [Number(frameId), records]));
                    const current = tabs.get(tabId);
                    if (current) {
                        // Requests recorded since this context started are newer than the stored ones.
                        for (const [frameId, records] of current) {
                            frames.set(frameId, trim([...(frames.get(frameId) ?? []), ...records]));
                        }
                    }
                    tabs.set(tabId, frames);
                }
            }
        } catch (error) {
            console.error('Light Media Center: restoring request records failed', error);
        }
    }

    function scheduleFlush() {
        if (flushTimer === null) {
            flushTimer = setTimeout(flush, 250);
        }
    }

    async function flush() {
        flushTimer = null;
        await restored;
        const set = {};
        const remove = [];
        for (const tabId of dirtyTabs) {
            const frames = tabs.get(tabId);
            if (frames && frames.size) {
                set[tabKeyPrefix + tabId] = Object.fromEntries(frames);
            } else {
                remove.push(tabKeyPrefix + tabId);
            }
        }
        dirtyTabs.clear();
        if (dirtyWorkers) {
            set[workersKey] = Object.fromEntries(workers);
            dirtyWorkers = false;
        }
        try {
            if (remove.length) {
                await api.storage.session.remove(remove);
            }
            if (Object.keys(set).length) {
                await api.storage.session.set(set);
            }
        } catch (error) {
            console.error('Light Media Center: saving request records failed', error);
        }
    }

    function headerValue(headers, name) {
        const header = headers?.find((h) => h.name.toLowerCase() === name);
        return header ? (header.value ?? '') : null;
    }

    function extensionOf(url) {
        try {
            const path = new URL(url).pathname;
            const dot = path.lastIndexOf('.');
            return dot > path.lastIndexOf('/') ? path.slice(dot + 1).toLowerCase() : '';
        } catch {
            return '';
        }
    }

    // The kind of a response: an HLS or DASH manifest, media bytes, or null for anything else.
    function kindOf(type, url, contentType) {
        const extension = extensionOf(url);
        if (hlsTypes.has(contentType) || ((genericTypes.has(contentType) || contentType.startsWith('text/')) && (extension === 'm3u8' || extension === 'm3u'))) {
            return 'hls';
        }
        if (dashTypes.has(contentType) || ((genericTypes.has(contentType) || contentType.endsWith('/xml')) && extension === 'mpd')) {
            return 'dash';
        }
        if (type === 'media' || contentType.startsWith('video/') || contentType.startsWith('audio/') || mediaTypes.has(contentType)) {
            return 'media';
        }
        if (genericTypes.has(contentType) && mediaExtensions.has(extension)) {
            return 'media';
        }
        return null;
    }

    function initiatorOrigin(details) {
        const initiator = details.initiator ?? details.originUrl ?? details.documentUrl;
        if (!initiator) {
            return null;
        }
        try {
            const origin = new URL(initiator).origin;
            return origin.startsWith('http') ? origin : null;
        } catch {
            return null;
        }
    }

    function onSendHeaders(details) {
        if (pending.size >= maxPending) {
            pending.delete(pending.keys().next().value);
        }
        const previous = pending.get(details.requestId);
        if (previous) {
            // A redirect leg: keep the headers of the request the page made.
            return;
        }
        const headers = details.requestHeaders;
        pending.set(details.requestId, {
            url: details.url,
            referer: headerValue(headers, 'referer'),
            origin: headerValue(headers, 'origin'),
            userAgent: headerValue(headers, 'user-agent'),
            cookies: headerValue(headers, 'cookie') !== null,
        });
    }

    function onHeadersReceived(details) {
        if (details.statusCode >= 300 && details.statusCode < 400) {
            return;
        }
        const sent = pending.get(details.requestId);
        pending.delete(details.requestId);
        if (details.statusCode < 200 || details.statusCode >= 300) {
            return;
        }
        const contentType = (headerValue(details.responseHeaders, 'content-type') ?? '').split(';')[0].trim().toLowerCase();
        const url = sent?.url ?? details.url;
        const kind = kindOf(details.type, details.url, contentType) ?? kindOf(details.type, url, contentType);
        if (!kind) {
            return;
        }
        const record = {
            url,
            finalUrl: details.url !== url ? details.url : null,
            type: details.type,
            kind,
            contentType,
            method: details.method,
            referer: sent?.referer ?? null,
            origin: sent?.origin ?? null,
            userAgent: sent?.userAgent ?? null,
            cookies: sent ? sent.cookies : null,
            fromCache: !!details.fromCache,
            time: Date.now(),
        };
        if (details.tabId < 0) {
            addWorkerRecord(details, record);
        } else {
            addRecord(details.tabId, details.frameId, record);
        }
    }

    // Keeps the latest records of each kind, so the manifest a player started from outlives the
    // media requests of a long playback.
    function trim(records) {
        const manifests = records.filter((r) => r.kind !== 'media').slice(-maxManifestRecordsPerFrame);
        const media = records.filter((r) => r.kind === 'media').slice(-maxMediaRecordsPerFrame);
        return records.filter((r) => manifests.includes(r) || media.includes(r));
    }

    function addRecord(tabId, frameId, record) {
        let frames = tabs.get(tabId);
        if (!frames) {
            frames = new Map();
            tabs.set(tabId, frames);
        }
        const records = (frames.get(frameId) ?? []).filter((r) => r.url !== record.url);
        records.push(record);
        frames.set(frameId, trim(records));
        dirtyTabs.add(tabId);
        scheduleFlush();
    }

    function addWorkerRecord(details, record) {
        const origin = initiatorOrigin(details);
        if (!origin) {
            return;
        }
        const cutoff = Date.now() - workerRecordAgeMs;
        const records = (workers.get(origin) ?? []).filter((r) => r.url !== record.url && r.time >= cutoff);
        records.push({ url: record.url, kind: record.kind, time: record.time });
        workers.set(origin, trim(records));
        dirtyWorkers = true;
        scheduleFlush();
    }

    function onDocumentRequest(details) {
        if (details.tabId < 0) {
            return;
        }
        if (details.type === 'main_frame') {
            if (tabs.delete(details.tabId)) {
                dirtyTabs.add(details.tabId);
                scheduleFlush();
            }
        } else {
            const frames = tabs.get(details.tabId);
            if (frames?.delete(details.frameId)) {
                dirtyTabs.add(details.tabId);
                scheduleFlush();
            }
        }
    }

    function onRequestDone(details) {
        pending.delete(details.requestId);
    }

    function onTabRemoved(tabId) {
        if (tabs.delete(tabId)) {
            dirtyTabs.add(tabId);
            scheduleFlush();
        }
    }

    const filter = { urls: ['<all_urls>'], types: ['media', 'xmlhttprequest'] };
    try {
        api.webRequest.onSendHeaders.addListener(onSendHeaders, filter, ['requestHeaders', 'extraHeaders']);
    } catch {
        // Firefox reports every header without the Chromium-only extraHeaders option and rejects it.
        api.webRequest.onSendHeaders.addListener(onSendHeaders, filter, ['requestHeaders']);
    }
    api.webRequest.onHeadersReceived.addListener(onHeadersReceived, filter, ['responseHeaders']);
    api.webRequest.onCompleted.addListener(onRequestDone, filter);
    api.webRequest.onErrorOccurred.addListener(onRequestDone, filter);
    api.webRequest.onBeforeRequest.addListener(onDocumentRequest, { urls: ['<all_urls>'], types: ['main_frame', 'sub_frame'] });
    api.tabs.onRemoved.addListener(onTabRemoved);

    // Media requests a service worker of this origin made recently. They belong to no tab.
    function workerRecords(origin) {
        const cutoff = Date.now() - workerRecordAgeMs;
        return origin ? (workers.get(origin) ?? []).filter((r) => r.time >= cutoff) : [];
    }

    async function forFrame(tabId, frameId, frameOrigin) {
        await restored;
        const workerRequests = workerRecords(frameOrigin);
        return {
            requests: tabs.get(tabId)?.get(frameId) ?? [],
            serviceWorkerMedia: workerRequests.length > 0,
            workerRequests,
        };
    }

    // The latest request for a URL, from the given frame first and then from the rest of the tab.
    async function find(tabId, frameId, url) {
        await restored;
        const frames = tabs.get(tabId);
        if (!frames) {
            return null;
        }
        const match = (records) => records?.findLast((r) => r.url === url || r.finalUrl === url) ?? null;
        let found = match(frames.get(frameId));
        if (!found) {
            for (const [otherFrameId, records] of frames) {
                if (otherFrameId !== frameId && (found = match(records))) {
                    break;
                }
            }
        }
        return found;
    }

    globalThis.LightPlayerRequests = Object.freeze({ forFrame, find });
})();
