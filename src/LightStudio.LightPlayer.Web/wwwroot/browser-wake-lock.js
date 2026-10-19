let sentinel = null;
let wanted = false;

async function acquire() {
    if (!wanted || sentinel) {
        return;
    }

    const wakeLock = globalThis.navigator?.wakeLock;
    if (!wakeLock || globalThis.document.visibilityState !== 'visible') {
        return;
    }

    try {
        sentinel = await wakeLock.request('screen');
        sentinel.addEventListener('release', () => { sentinel = null; });
    } catch {
        // Insecure context, a policy that forbids it, or a browser without the API: the display
        // keeps its own idle behavior.
        sentinel = null;
    }
}

// The browser releases the lock whenever the tab is hidden and never gives it back on its own.
globalThis.document.addEventListener('visibilitychange', () => { void acquire(); });

export async function setKeepScreenOn(value) {
    wanted = value;
    if (value) {
        await acquire();
        return;
    }

    const held = sentinel;
    sentinel = null;
    if (held) {
        try {
            await held.release();
        } catch {
            // Already gone.
        }
    }
}
