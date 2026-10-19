let pendingResolve = null;
let missedPop = false;

globalThis.addEventListener('popstate', () => {
    if (pendingResolve) {
        const resolve = pendingResolve;
        pendingResolve = null;
        resolve();
    } else {
        // The app is between waits; remember the move so the next wait returns immediately.
        missedPop = true;
    }
});

// Managed code awaits this instead of being called back: a JS callback into managed code is a
// synchronous JS export, which multithreaded WebAssembly refuses, while promises marshal fine.
export function waitForNavigation() {
    if (missedPop) {
        missedPop = false;
        return Promise.resolve();
    }

    return new Promise(resolve => { pendingResolve = resolve; });
}

function currentIndex() {
    const state = globalThis.history.state;
    return state && typeof state.i === 'number' ? state.i : -1;
}

function url(location) {
    return globalThis.location.pathname + globalThis.location.search + (location ? '#' + location : '');
}

export async function getLocation() {
    return globalThis.location.hash.replace(/^#/, '');
}

export async function getIndex() {
    return currentIndex();
}

export async function push(location, index) {
    globalThis.history.pushState({ i: index }, '', url(location));
}

export async function replace(location, index) {
    globalThis.history.replaceState({ i: index }, '', url(location));
}

export async function go(delta) {
    globalThis.history.go(delta);
}
