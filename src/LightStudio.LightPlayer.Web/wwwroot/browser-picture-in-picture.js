// Document picture-in-picture: a real browser window with a document of its own, which Avalonia
// can render a second view into. Chromium only; everywhere else `isSupported` is false and the
// player simply never offers the button.

let host = null;
let pipWindow = null;
let pendingClose = null;
let missedClose = false;

export function isSupported() {
    return "documentPictureInPicture" in globalThis;
}

/// The host element outlives the window it is shown in: Avalonia's view has no way to be torn down,
/// and browsers allow only so many live WebGL contexts, so one view is made and moved between windows.
function adopt(doc) {
    host ??= globalThis.document.createElement("div");
    host.id = "pip-out";
    host.style.cssText = "position:absolute;inset:0;width:100%;height:100%;overflow:hidden";
    doc.body.append(host);
    return host;
}

function onClosed() {
    pipWindow = null;

    // Anything left in the document dies with it, canvas included, so the host comes back here.
    if (host) {
        host.style.cssText = "display:none";
        globalThis.document.body.append(host);
    }

    if (pendingClose) {
        const resolve = pendingClose;
        pendingClose = null;
        resolve();
    } else {
        missedClose = true;
    }
}

// Both views render from this page's animation frames, which stop while the page is hidden even though
// the picture-in-picture window is still on screen. Requests are therefore also placed with that window
// while this page is hidden, and whichever frame comes first runs the callback once. A request keeps the
// identifier of its page frame, so identifiers issued before routing began stay valid.
let framesRouted = false;

function routeAnimationFrames() {
    if (framesRouted) return;
    framesRouted = true;
    const request = globalThis.requestAnimationFrame.bind(globalThis);
    const cancel = globalThis.cancelAnimationFrame.bind(globalThis);
    const pending = new Map();

    const cancelWindow = entry => {
        if (entry.window && !entry.window.target.closed) entry.window.target.cancelAnimationFrame(entry.window.id);
        entry.window = null;
    };
    const run = (entry, time) => {
        if (!pending.delete(entry.id)) return;
        cancel(entry.id);
        cancelWindow(entry);
        entry.callback(time);
    };
    const placeWindow = entry => {
        const target = pipWindow;
        if (target && !target.closed && !entry.window && globalThis.document.visibilityState === "hidden") {
            // The window's frame times count from its own time origin.
            const offset = target.performance.timeOrigin - globalThis.performance.timeOrigin;
            entry.window = { target, id: target.requestAnimationFrame(time => run(entry, time + offset)) };
        }
    };

    globalThis.requestAnimationFrame = callback => {
        const entry = { id: 0, callback, window: null };
        entry.id = request(time => run(entry, time));
        pending.set(entry.id, entry);
        placeWindow(entry);
        return entry.id;
    };
    globalThis.cancelAnimationFrame = id => {
        const entry = pending.get(id);
        if (entry) {
            pending.delete(id);
            cancelWindow(entry);
        }
        cancel(id);
    };
    globalThis.document.addEventListener("visibilitychange", () => {
        if (globalThis.document.visibilityState === "hidden") {
            for (const entry of pending.values()) placeWindow(entry);
        }
    });
}

export async function requestWindow(width, height) {
    if (!isSupported() || pipWindow) {
        return null;
    }

    let win;
    try {
        // Needs a recent user gesture, which the click or key press that asked for this provides.
        win = await globalThis.documentPictureInPicture.requestWindow({
            width: Math.max(200, Math.round(width)),
            height: Math.max(120, Math.round(height)),
        });
    } catch (error) {
        console.warn("The picture-in-picture window was refused.", error);
        return null;
    }

    routeAnimationFrames();
    pipWindow = win;
    missedClose = false;

    // The new document inherits no stylesheet, so the little that matters is set here.
    const style = "width:100%;height:100%;margin:0;padding:0;overflow:hidden;background:#000";
    win.document.documentElement.style.cssText = style;
    win.document.body.style.cssText = style;
    win.addEventListener("pagehide", onClosed, { once: true });

    return adopt(win.document);
}

/// Awaited rather than pushed through a callback, so the window closing by itself and the app closing it
/// reach the page runtime the same way.
export function waitForClose() {
    if (missedClose) {
        missedClose = false;
        return Promise.resolve();
    }

    return new Promise(resolve => { pendingClose = resolve; });
}

export async function close() {
    pipWindow?.close();
}
