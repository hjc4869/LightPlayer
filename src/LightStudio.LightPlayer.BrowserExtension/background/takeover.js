// Armed tabs for takeover. The page-world script is registered only while some tab is armed, and
// each frame of an armed tab carries the armed flag and the capability list in its session storage,
// which the page-world script reads synchronously at document start.
(() => {
    const api = globalThis.browser ?? globalThis.chrome;
    const sessions = globalThis.LightPlayerSessions;

    const storageKey = 'takeoverTabs';
    const flagKey = 'lightplayer.takeover';
    const scriptId = 'lightplayer-takeover';
    const script = {
        id: scriptId,
        js: ['takeover/page.js'],
        matches: ['<all_urls>'],
        allFrames: true,
        runAt: 'document_start',
        world: 'MAIN',
        matchOriginAsFallback: true,
        persistAcrossSessions: false,
    };

    function failure(reason) {
        const error = new Error(reason);
        error.reason = reason;
        return error;
    }

    async function armedTabs() {
        const stored = await api.storage.session.get(storageKey);
        return stored[storageKey] ?? {};
    }

    async function isArmed(tabId) {
        return Number.isInteger(tabId) && Object.hasOwn(await armedTabs(), String(tabId));
    }

    // The part of `ready` the page sees: what the application decodes, without versions or budgets.
    function pageCapabilities(ready) {
        return {
            containers: Array.isArray(ready?.containers) ? ready.containers : [],
            video: Array.isArray(ready?.video) ? ready.video : [],
            audio: Array.isArray(ready?.audio) ? ready.audio : [],
            hdr: ready?.hdr === true,
        };
    }

    async function capabilities() {
        let ready = await sessions.cachedCapabilities();
        if (!ready) {
            const status = await sessions.probe();
            if (!status.reachable) {
                throw failure('unreachable');
            }
            if (!status.paired) {
                throw failure('notPaired');
            }
            ready = await sessions.cachedCapabilities();
        }
        if (!ready) {
            throw failure('unreachable');
        }
        return pageCapabilities(ready);
    }

    // The page-world script exists while any tab is armed.
    async function updateRegistration(armed) {
        const registered = (await api.scripting.getRegisteredContentScripts({ ids: [scriptId] })).length > 0;
        const wanted = Object.keys(armed).length > 0;
        if (wanted && !registered) {
            await api.scripting.registerContentScripts([script]);
        } else if (!wanted && registered) {
            await api.scripting.unregisterContentScripts({ ids: [scriptId] });
        }
    }

    // Writes or removes the flag in every frame of the tab; frames with opaque origins have no
    // session storage and stay native.
    async function setFlags(tabId, value) {
        try {
            await api.scripting.executeScript({
                target: { tabId, allFrames: true },
                func: (key, flag) => {
                    try {
                        if (flag === null) {
                            sessionStorage.removeItem(key);
                        } else {
                            sessionStorage.setItem(key, flag);
                        }
                        return true;
                    } catch (error) {
                        if (error?.name === 'SecurityError') {
                            return false;
                        }
                        throw error;
                    }
                },
                args: [flagKey, value === null ? null : JSON.stringify(value)],
            });
        } catch (error) {
            // A tab showing a browser page or a store accepts no scripts.
            console.warn('Light Media Center: the takeover flag could not be written into the tab', error);
        }
    }

    // Arms the tab and reloads it. Rejects with `unreachable` or `notPaired` when the application
    // does not answer now: the reloaded page's player would otherwise fail with nothing to play in.
    async function arm(tabId) {
        const status = await sessions.probe();
        if (!status.reachable) {
            throw failure('unreachable');
        }
        if (!status.paired) {
            throw failure('notPaired');
        }
        const caps = await capabilities();
        const armed = await armedTabs();
        armed[String(tabId)] = { at: Date.now() };
        await api.storage.session.set({ [storageKey]: armed });
        await updateRegistration(armed);
        await setFlags(tabId, { v: 1, capabilities: caps });
        await api.tabs.reload(tabId);
    }

    async function disarm(tabId, reload) {
        const armed = await armedTabs();
        if (Object.hasOwn(armed, String(tabId))) {
            delete armed[String(tabId)];
            await api.storage.session.set({ [storageKey]: armed });
        }
        await updateRegistration(armed);
        if (reload) {
            await setFlags(tabId, null);
            await api.tabs.reload(tabId);
        }
    }

    // State for a frame of the tab: whether it is armed, and the page's capability list if so.
    async function state(tabId) {
        if (!await isArmed(tabId)) {
            return { armed: false };
        }
        let caps = null;
        try {
            caps = await capabilities();
        } catch (error) {
            if (!error.reason) {
                throw error;
            }
        }
        return { armed: true, capabilities: caps };
    }

    api.tabs.onRemoved.addListener((tabId) => {
        disarm(tabId, false).catch((error) => console.error('Light Media Center: disarming a closed tab failed', error));
    });

    // Registrations do not outlive the browser session, while armed tabs in session storage can
    // outlive the background context; the two are brought in line when it starts.
    armedTabs().then(updateRegistration).catch((error) => console.error('Light Media Center: restoring the takeover registration failed', error));

    globalThis.LightPlayerTakeoverTabs = Object.freeze({ arm, disarm, isArmed, state });
})();
