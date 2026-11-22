// Settings, storage keys and link constants shared by the background and the extension pages.
(() => {
    const api = globalThis.browser ?? globalThis.chrome;

    const defaultPort = 47833;
    const subprotocol = 'lightplayer.browser-link.v1';
    const keys = {
        port: 'port',
        token: 'token',
        pairing: 'pairing',
        capabilities: 'capabilities',
    };

    function endpoint(port) {
        return `ws://127.0.0.1:${port}/browser-link/v1`;
    }

    function isValidPort(port) {
        return Number.isInteger(port) && port >= 1 && port <= 65535;
    }

    async function load() {
        const stored = await api.storage.local.get([keys.port, keys.token, keys.pairing, keys.capabilities]);
        const port = Number(stored[keys.port]);
        return {
            port: isValidPort(port) ? port : defaultPort,
            token: typeof stored[keys.token] === 'string' && stored[keys.token] ? stored[keys.token] : null,
            pairing: stored[keys.pairing] ?? null,
            capabilities: stored[keys.capabilities] ?? null,
        };
    }

    async function savePort(port) {
        await api.storage.local.set({ [keys.port]: port });
    }

    async function savePairing(token, appVersion) {
        await api.storage.local.set({
            [keys.token]: token,
            [keys.pairing]: { state: 'paired', appVersion, pairedAt: Date.now() },
        });
    }

    async function clearPairing(state) {
        await api.storage.local.remove([keys.token, keys.capabilities]);
        if (state) {
            await api.storage.local.set({ [keys.pairing]: { state } });
        } else {
            await api.storage.local.remove(keys.pairing);
        }
    }

    async function saveCapabilities(ready) {
        await api.storage.local.set({ [keys.capabilities]: ready });
    }

    function extensionVersion() {
        return api.runtime.getManifest().version;
    }

    // A name for the application's paired-browsers list, such as "Google Chrome on Linux".
    async function browserName() {
        let name = null;
        if (typeof api.runtime.getBrowserInfo === 'function') {
            const info = await api.runtime.getBrowserInfo();
            name = info.name;
        } else if (globalThis.navigator?.userAgentData) {
            const brands = navigator.userAgentData.brands
                .map((brand) => brand.brand)
                .filter((brand) => !/not.?a.?brand/i.test(brand));
            name = brands.find((brand) => brand !== 'Chromium') ?? brands[0] ?? 'Chromium';
        }
        name ??= 'Browser';
        const platform = globalThis.navigator?.userAgentData?.platform
            || (typeof api.runtime.getPlatformInfo === 'function' ? platformName((await api.runtime.getPlatformInfo()).os) : '');
        return platform ? `${name} on ${platform}` : name;
    }

    function platformName(os) {
        switch (os) {
            case 'win': return 'Windows';
            case 'mac': return 'macOS';
            case 'linux': return 'Linux';
            case 'cros': return 'ChromeOS';
            case 'openbsd': return 'OpenBSD';
            default: return os ?? '';
        }
    }

    globalThis.LightPlayerSettings = Object.freeze({
        defaultPort,
        subprotocol,
        keys,
        endpoint,
        isValidPort,
        load,
        savePort,
        savePairing,
        clearPairing,
        saveCapabilities,
        extensionVersion,
        browserName,
    });
})();
