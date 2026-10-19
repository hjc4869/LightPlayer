// Files the operating system hands to the installed app, rather than files found inside a folder
// the user picked in the library settings.
//
// Two routes lead here. A desktop PWA registered as a file handler is launched with a
// FileSystemFileHandle per file in `launchQueue`; a share on Android arrives as a POST to
// /share-target, which the service worker turns into File objects stashed in IndexedDB plus a
// redirect carrying their id. Both end as fsa:// identifiers, so the shell opens them through the
// same path as local library media.
//
// The consumer is registered from main.js, before the .NET runtime is even downloaded: launchQueue
// buffers until someone claims it, but a share redirect is a plain page load whose query parameter
// has to be taken off the address before the shell starts writing history entries over it.

import { adoptFiles } from './file-system-access.js';

const ShareDatabaseName = 'LightPlayerShare';
const ShareStore = 'shares';

/** FileSystemFileHandle / File objects waiting to be claimed by the managed side. */
const pending = [];

let waiting = null;
let stopReceiving = null;

function promisify(request) {
    return new Promise((resolve, reject) => {
        request.onsuccess = () => resolve(request.result);
        request.onerror = () => reject(request.error);
    });
}

function openShareDatabase() {
    return new Promise((resolve, reject) => {
        const request = globalThis.indexedDB.open(ShareDatabaseName, 1);
        request.onupgradeneeded = () => request.result.createObjectStore(ShareStore);
        request.onsuccess = () => resolve(request.result);
        request.onerror = () => reject(request.error);
    });
}

// Reads a share the service worker stashed, and removes it: a share is delivered once, and the
// files would otherwise pin their bytes for as long as the origin's storage lives.
async function takeShare(id) {
    const database = await openShareDatabase();
    try {
        // Both requests have to be issued in the same turn: an IndexedDB transaction goes inactive
        // as soon as control returns to the event loop.
        const store = database.transaction(ShareStore, 'readwrite').objectStore(ShareStore);
        const [shared] = await Promise.all([
            promisify(store.get(id)),
            promisify(store.delete(id)),
        ]);
        return shared?.files ?? [];
    } finally {
        database.close();
    }
}

function deliver(entries) {
    const usable = Array.from(entries ?? []).filter(Boolean);
    if (usable.length === 0) {
        return;
    }

    pending.push(...usable);
    if (waiting) {
        const resolve = waiting;
        waiting = null;
        resolve();
    }
}

async function collectSharedFiles(receive) {
    const url = new URL(globalThis.location.href);
    const id = url.searchParams.get('shared');
    if (!id) {
        return;
    }

    url.searchParams.delete('shared');
    globalThis.history.replaceState(globalThis.history.state, '', url.pathname + url.search + url.hash);
    try {
        receive(await takeShare(id));
    } catch (error) {
        console.error('Unable to collect shared files.', error);
    }
}

function forwardFiles(channel, files) {
    if (!channel) {
        return Promise.reject(new Error('Cross-window file delivery is unavailable in this browser.'));
    }

    return new Promise((resolve, reject) => {
        const message = { type: 'files', id: crypto.randomUUID(), files };
        const timeout = setTimeout(() => finish(new Error('The open Light Player instance did not acknowledge the files.')), 10000);
        const retry = setInterval(send, 500);

        function finish(error) {
            clearTimeout(timeout);
            clearInterval(retry);
            channel.removeEventListener('message', acknowledge);
            if (error) {
                reject(error);
            } else {
                resolve();
            }
        }

        function acknowledge(event) {
            if (event.data?.type === 'ack' && event.data.id === message.id) {
                finish();
            }
        }

        function send() {
            try {
                channel.postMessage(message);
            } catch (error) {
                finish(error);
            }
        }

        channel.addEventListener('message', acknowledge);
        send();
    });
}

export function registerFileHandlers({ forward = false, onForwarding, onForwarded, onForwardError } = {}) {
    if (stopReceiving) {
        return stopReceiving;
    }

    const channel = typeof BroadcastChannel === 'function'
        ? new BroadcastChannel('lightplayer.files')
        : null;
    let active = true;
    let forwarding = Promise.resolve();
    stopReceiving = () => {
        active = false;
        channel?.close();
    };

    if (channel && !forward) {
        const received = new Set();
        channel.onmessage = event => {
            const message = event.data;
            if (!active || message?.type !== 'files' || typeof message.id !== 'string'
                || !Array.isArray(message.files) || message.files.length === 0) {
                return;
            }

            if (!received.has(message.id)) {
                deliver(message.files);
                received.add(message.id);
            }

            channel.postMessage({ type: 'ack', id: message.id });
        };
    }

    function receive(entries) {
        const files = Array.from(entries ?? []).filter(Boolean);
        if (!active || files.length === 0) {
            return;
        }

        if (!forward) {
            deliver(files);
            return;
        }

        forwarding = forwarding.then(async () => {
            try {
                onForwarding?.();
                await forwardFiles(channel, files);
                onForwarded?.();
            } catch (error) {
                console.error('Unable to forward files to the open Light Player instance.', error);
                onForwardError?.(error);
            }
        });
    }

    if ('launchQueue' in globalThis && typeof LaunchParams !== 'undefined' && 'files' in LaunchParams.prototype) {
        globalThis.launchQueue.setConsumer(params => receive(params.files));
    }

    void collectSharedFiles(receive);
    return stopReceiving;
}

// Managed code awaits this instead of being called back: a JS callback into managed code is a
// synchronous JS export, which multithreaded WebAssembly refuses, while promises marshal fine.
export function waitForFiles() {
    return pending.length > 0
        ? Promise.resolve()
        : new Promise(resolve => { waiting = resolve; });
}

// Claims everything delivered so far as fsa:// roots, and reports "rootId\tname" per line.
export function takeFiles() {
    return adoptFiles(pending.splice(0, pending.length));
}
