let pendingResolve = null;
let missedChange = false;

function element() {
    return globalThis.document.documentElement;
}

function fullScreenElement() {
    const doc = globalThis.document;
    return doc.fullscreenElement ?? doc.webkitFullscreenElement ?? null;
}

function onChange() {
    if (pendingResolve) {
        const resolve = pendingResolve;
        pendingResolve = null;
        resolve();
    } else {
        // The app is between waits; remember the change so the next wait returns immediately.
        missedChange = true;
    }
}

globalThis.document.addEventListener('fullscreenchange', onChange);
globalThis.document.addEventListener('webkitfullscreenchange', onChange);

// Managed code awaits this instead of being called back: a JS callback into managed code is a
// synchronous JS export, which multithreaded WebAssembly refuses, while promises marshal fine.
export function waitForChange() {
    if (missedChange) {
        missedChange = false;
        return Promise.resolve();
    }

    return new Promise(resolve => { pendingResolve = resolve; });
}

export async function isFullScreen() {
    return fullScreenElement() !== null;
}

export async function setFullScreen(value) {
    const doc = globalThis.document;
    try {
        if (value) {
            if (fullScreenElement()) {
                return;
            }

            const target = element();
            const request = target.requestFullscreen ?? target.webkitRequestFullscreen;
            if (request) {
                await request.call(target);
            }
        } else if (fullScreenElement()) {
            const exit = doc.exitFullscreen ?? doc.webkitExitFullscreen;
            if (exit) {
                await exit.call(doc);
            }
        }
    } catch {
        // Refused for want of a recent user gesture, or unsupported; the picture simply stays put.
    }
}
