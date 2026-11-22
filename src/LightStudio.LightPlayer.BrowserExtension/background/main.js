// Answers one-off requests from content scripts and extension pages.
(() => {
    const api = globalThis.browser ?? globalThis.chrome;
    const settings = globalThis.LightPlayerSettings;
    const launch = globalThis.LightPlayerLaunch;
    const requests = globalThis.LightPlayerRequests;
    const sessions = globalThis.LightPlayerSessions;
    const takeover = globalThis.LightPlayerTakeoverTabs;
    const firefox = api.runtime.getURL('').startsWith('moz-extension:');

    // Scripts of the extension-owned player, loaded into a frame only when it redirects a video.
    const playerScripts = {
        remote: [
            'remote/core.js',
            'remote/type-support.js',
            'remote/media-source.js',
            'remote/media-element.js',
            'remote/session.js',
            'remote/realm.js',
        ],
        hls: ['vendor/hls.min.js'],
        dash: ['vendor/dash.mediaplayer.min.js'],
    };

    function originOf(url) {
        try {
            return new URL(url).origin;
        } catch {
            return null;
        }
    }

    // The tab a request is about: the tab an extension page such as the popup names, which may run
    // in a tab of its own, or the sender's own tab for a content script.
    function tabOf(message, sender) {
        const extensionPage = typeof sender.url === 'string' && sender.url.startsWith(api.runtime.getURL(''));
        if (extensionPage) {
            return Number.isInteger(message.tabId) ? message.tabId : null;
        }
        return sender.tab ? sender.tab.id : null;
    }

    // Arming follows a user gesture in extension UI: the popup, or the control in a closed shadow
    // tree whose click handler accepts trusted events only.
    async function armTab(message, sender) {
        const tabId = tabOf(message, sender);
        if (tabId === null) {
            return { ok: false, reason: 'error' };
        }
        try {
            await takeover.arm(tabId);
            return { ok: true };
        } catch (error) {
            if (!error.reason) {
                throw error;
            }
            return { ok: false, reason: error.reason };
        }
    }

    // Opens the application's URL for a tab after a user's request in extension UI. Chromium shows
    // its prompt over the page and leaves the page in place. In Firefox the tab's top frame opens it
    // in a hidden frame, because a tab navigated to a scheme nothing handles shows an error page.
    async function openApp(tabId) {
        if (!firefox) {
            await api.tabs.update(tabId, { url: launch.url });
            return true;
        }
        try {
            return await api.tabs.sendMessage(tabId, { k: 'openApp' }, { frameId: 0 }) === true;
        } catch (error) {
            // Browser pages and stores run no content scripts.
            console.warn('Light Media Center: the tab cannot open the application', error);
            return false;
        }
    }

    const handlers = {
        // With `alternatives`, an application that does not answer on the extension's port is also
        // looked for on the default port. These checks repeat while the application starts; in
        // Firefox a socket opens only to a port where something answers HTTP.
        status: async (message) => {
            let status;
            if (message.alternatives && firefox) {
                const { port } = await settings.load();
                status = await sessions.answers(port) ? await sessions.probe() : { port, reachable: false, paired: false };
            } else {
                status = await sessions.probe();
            }
            if (message.alternatives && !status.reachable && status.port !== settings.defaultPort
                && (!firefox || await sessions.answers(settings.defaultPort))
                && await sessions.listens(settings.defaultPort)) {
                status.otherPort = settings.defaultPort;
            }
            return status;
        },
        // `opened` says that the sender already opened the URL in a frame of its own.
        startApp: async (message, sender) => {
            const tabId = tabOf(message, sender);
            if (tabId !== null) {
                // Frames with a redirect that waits for the application continue it once it answers.
                // Chromium holds back messages to a tab that navigates to the URL until the user
                // answers the prompt, so they hear it first. A tab without content scripts, such as
                // the options page, has no receiver.
                api.tabs.sendMessage(tabId, { k: 'appStarting' }).catch(() => {});
            }
            if (!message.opened && (tabId === null || !await openApp(tabId))) {
                return { ok: false };
            }
            return { ok: true };
        },
        usePort: async (message) => {
            if (!settings.isValidPort(message.port)) {
                return false;
            }
            await settings.savePort(message.port);
            return true;
        },
        capabilities: () => sessions.cachedCapabilities(),
        frameRequests: (message, sender) => {
            if (!sender.tab) {
                return null;
            }
            return requests.forFrame(sender.tab.id, sender.frameId, originOf(sender.url));
        },
        fileRequest: async (message, sender) => {
            if (!sender.tab) {
                return null;
            }
            const request = await requests.find(sender.tab.id, sender.frameId, message.url);
            return {
                request,
                tabTitle: sender.tab.title ?? null,
                tabUrl: sender.tab.url ?? null,
            };
        },
        manifestRequest: async (message, sender) => {
            if (!sender.tab) {
                return null;
            }
            const records = await requests.forFrame(sender.tab.id, sender.frameId, originOf(sender.url));
            return {
                ...records,
                tabTitle: sender.tab.title ?? null,
                tabUrl: sender.tab.url ?? null,
            };
        },
        loadPlayer: async (message, sender) => {
            const files = Object.hasOwn(playerScripts, message.part) ? playerScripts[message.part] : null;
            if (!sender.tab || !files) {
                return false;
            }
            await api.scripting.executeScript({ target: { tabId: sender.tab.id, frameIds: [sender.frameId] }, files });
            return true;
        },
        takeoverArm: armTab,
        takeoverDisarm: async (message, sender) => {
            const tabId = tabOf(message, sender);
            if (tabId !== null) {
                await takeover.disarm(tabId, true);
            }
            return tabId !== null;
        },
        takeoverState: async (message, sender) => {
            const tabId = tabOf(message, sender);
            if (tabId === null) {
                return { armed: false };
            }
            return { ...await takeover.state(tabId), tabTitle: sender.tab?.title ?? null, tabUrl: sender.tab?.url ?? null };
        },
        openOptions: async () => {
            await api.runtime.openOptionsPage();
            return null;
        },
    };

    api.runtime.onMessage.addListener((message, sender, sendResponse) => {
        const handler = Object.hasOwn(handlers, message?.k) ? handlers[message.k] : null;
        if (sender.id !== api.runtime.id || !handler) {
            return false;
        }
        Promise.resolve().then(() => handler(message, sender)).then(sendResponse, (error) => {
            console.error('Light Media Center: handling a runtime message failed', error);
            sendResponse(null);
        });
        return true;
    });
})();
