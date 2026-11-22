// Starts the application through its URL scheme after the user asked to, and waits for its browser
// link to answer. Loaded by the background, the content scripts and the extension pages.
(() => {
    const api = globalThis.browser ?? globalThis.chrome;

    // The URL names nothing to open: the application only starts or comes forward.
    const url = 'lightplayer:';
    const waitMs = 45000;
    const probeIntervalMs = 1000;
    const frameLifetimeMs = 120000;
    // Firefox replaces the tab's page with an error page when no application handles the scheme,
    // so a hidden frame opens the URL there. Chromium opens it in the tab from the background,
    // which keeps the page. Either way the browser's prompt names the extension.
    const opensInFrame = api.runtime.getURL('').startsWith('moz-extension:');

    let waiting = null;
    const listeners = new Set();

    // Firefox: opens the URL in a hidden frame of this document.
    function openInFrame() {
        const host = document.createElement('div');
        const frame = document.createElement('iframe');
        frame.style.setProperty('display', 'none', 'important');
        frame.src = url;
        host.attachShadow({ mode: 'closed' }).append(frame);
        (document.documentElement ?? document).append(host);
        setTimeout(() => host.remove(), frameLifetimeMs);
    }

    // Opens the URL for a tab: `tabId` names it from an extension page and is null for a content
    // script's own tab. `inTab` says that this document is shown in that tab; the popup is not, and
    // in Firefox the tab's top frame opens the URL for it. Resolves to false when the tab cannot.
    async function open({ tabId = null, inTab = true } = {}) {
        const opened = opensInFrame && inTab;
        if (opened) {
            openInFrame();
        }
        const result = await api.runtime.sendMessage({ k: 'startApp', tabId, opened });
        return result?.ok === true;
    }

    // Asks the background for the status every second until the application answers, another port
    // answers, or the wait is over. `onProgress` receives the part of the wait that has passed.
    function wait(onProgress) {
        if (onProgress) {
            listeners.add(onProgress);
        }
        waiting ??= poll().finally(() => {
            waiting = null;
            listeners.clear();
        });
        return waiting;
    }

    async function poll() {
        const started = Date.now();
        for (;;) {
            const attempt = Date.now();
            const status = await api.runtime.sendMessage({ k: 'status', alternatives: true });
            const elapsed = Date.now() - started;
            if (status?.reachable || status?.otherPort || elapsed >= waitMs) {
                return status ?? { reachable: false, paired: false };
            }
            for (const listener of listeners) {
                listener(elapsed / waitMs);
            }
            await new Promise((resolve) => setTimeout(resolve, Math.max(0, attempt + probeIntervalMs - Date.now())));
        }
    }

    // What a status means for the user: `ready`, `notPaired`, `otherPort` (the application answers
    // on the default port, not on the extension's) or `unreachable`.
    function outcome(status) {
        if (status?.reachable) {
            return status.paired ? 'ready' : 'notPaired';
        }
        return status?.otherPort ? 'otherPort' : 'unreachable';
    }

    globalThis.LightPlayerLaunch = Object.freeze({ url, open, openInFrame, wait, outcome });
})();
