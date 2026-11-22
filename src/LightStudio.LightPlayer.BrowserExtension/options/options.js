// Options page: port, pairing state, pair and forget. Pairing runs in this page, which stays open
// while the user confirms the code in the application.
(() => {
    const api = globalThis.browser ?? globalThis.chrome;
    const settings = globalThis.LightPlayerSettings;
    const page = globalThis.LightPlayerPage;

    const elements = Object.fromEntries(
        ['status', 'start', 'progress', 'pairing-state', 'pairing-code', 'code', 'pairing-result', 'pair', 'cancel', 'forget', 'forget-hint', 'port', 'port-error', 'access', 'grant']
            .map((id) => [id, document.getElementById(id)]));

    let pairingSocket = null;
    let ownTab = null;

    // The application's URL opens from this page's own tab.
    const status = page.statusLine({ line: elements.status, action: elements.start, progress: elements.progress }, {
        tabId: () => ownTab,
        inTab: true,
        onOpenFailed: () => {
            elements.status.textContent = page.text('messageError');
        },
        onStatus: () => render().catch((error) => console.error('Light Media Center: showing the settings failed', error)),
    });

    function generateCode() {
        // Rejection sampling keeps every six-digit code equally likely.
        const values = new Uint32Array(1);
        const limit = Math.floor(0x100000000 / 1000000) * 1000000;
        do {
            crypto.getRandomValues(values);
        } while (values[0] >= limit);
        return String(values[0] % 1000000).padStart(6, '0');
    }

    function showResult(key, isError) {
        elements['pairing-result'].textContent = page.text(key);
        elements['pairing-result'].classList.toggle('error', !!isError);
        elements['pairing-result'].hidden = false;
    }

    async function render() {
        const loaded = await settings.load();
        if (document.activeElement !== elements.port) {
            elements.port.value = String(loaded.port);
        }
        const paired = !!loaded.token;
        const stateKey = paired ? 'pairingStatePaired' : loaded.pairing?.state === 'revoked' ? 'pairingStateRevoked' : 'pairingStateNone';
        elements['pairing-state'].textContent = page.text(stateKey);
        const pairing = pairingSocket !== null;
        elements.pair.hidden = pairing;
        elements.pair.textContent = page.text(paired ? 'optionsPairAgainButton' : 'optionsPairButton');
        elements.cancel.hidden = !pairing;
        elements.forget.hidden = !paired || pairing;
        elements['forget-hint'].hidden = !paired;
        elements['pairing-code'].hidden = !pairing;
    }

    async function renderAccess() {
        elements.access.hidden = await page.hasSiteAccess();
    }

    async function pair() {
        if (pairingSocket) {
            return;
        }
        elements['pairing-result'].hidden = true;
        const loaded = await settings.load();
        const browser = await settings.browserName();
        const code = generateCode();
        elements.code.textContent = code;
        let socket;
        try {
            socket = new WebSocket(settings.endpoint(loaded.port), settings.subprotocol);
        } catch (error) {
            console.error('Light Media Center: opening the browser link failed', error);
            showResult('messageUnreachable', true);
            return;
        }
        pairingSocket = socket;
        let opened = false;
        let paired = false;
        socket.onopen = () => {
            opened = true;
            socket.send(JSON.stringify({ type: 'pair', code, browser, extensionVersion: settings.extensionVersion() }));
        };
        socket.onmessage = async (event) => {
            let message = null;
            try {
                message = JSON.parse(event.data);
            } catch (error) {
                console.error('Light Media Center: the application sent a malformed message', error);
            }
            if (message?.type === 'paired' && typeof message.token === 'string' && message.token) {
                paired = true;
                await settings.savePairing(message.token, message.appVersion ?? null);
            }
        };
        socket.onclose = async (event) => {
            if (pairingSocket !== socket) {
                return;
            }
            pairingSocket = null;
            if (paired) {
                showResult('optionsPairingSucceeded', false);
            } else if (!opened) {
                showResult('messageUnreachable', true);
            } else if (event.code === 4403) {
                showResult('optionsPairingRejected', true);
            } else if (event.code !== 1000 || event.reason !== 'cancelled') {
                showResult('optionsPairingFailed', true);
            }
            await render();
            await status.refresh();
        };
        await render();
    }

    function cancel() {
        const socket = pairingSocket;
        if (!socket) {
            return;
        }
        pairingSocket = null;
        socket.close(1000, 'cancelled');
        render();
    }

    async function forget() {
        await settings.clearPairing(null);
        elements['pairing-result'].hidden = true;
        await render();
        await status.refresh();
    }

    async function savePort() {
        const port = Number(elements.port.value);
        const valid = settings.isValidPort(port);
        elements['port-error'].hidden = valid;
        if (valid) {
            await settings.savePort(port);
            await status.refresh();
        }
    }

    page.localize();
    elements.pair.addEventListener('click', () => pair().catch((error) => console.error('Light Media Center: pairing failed', error)));
    elements.cancel.addEventListener('click', cancel);
    elements.forget.addEventListener('click', () => forget().catch((error) => console.error('Light Media Center: forgetting the pairing failed', error)));
    elements.port.addEventListener('change', () => savePort().catch((error) => console.error('Light Media Center: saving the port failed', error)));
    elements.grant.addEventListener('click', () => page.requestSiteAccess().then(renderAccess, (error) => console.error('Light Media Center: requesting site access failed', error)));
    api.storage.onChanged.addListener((changes, area) => {
        if (area === 'local' && (changes[settings.keys.token] || changes[settings.keys.pairing])) {
            render();
        }
    });
    Promise.all([render(), api.tabs.getCurrent().then((tab) => {
        ownTab = tab?.id ?? null;
    })]).then(() => status.refresh()).catch((error) => console.error('Light Media Center: showing the settings failed', error));
    renderAccess().catch((error) => console.error('Light Media Center: checking site access failed', error));
})();
