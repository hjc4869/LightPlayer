// Owns one WebSocket to the application per redirected session, fed by a runtime port from the
// content script that hosts the session.
(() => {
    const api = globalThis.browser ?? globalThis.chrome;
    const settings = globalThis.LightPlayerSettings;

    const portName = 'lightplayer.session';
    // Sessions that relay a taken-over page player: only `source` sessions, only from armed tabs.
    const takeoverPortName = 'lightplayer.takeover';
    const probeTimeoutMs = 4000;
    const minTickMs = 100;

    const closeCodeReasons = new Map([
        [4400, 'protocol'],
        [4401, 'notPaired'],
        [4403, 'rejected'],
        [4409, 'replaced'],
    ]);

    let capabilities = null;
    const capabilitiesLoaded = settings.load().then((loaded) => {
        capabilities ??= loaded.capabilities;
    });

    async function rememberCapabilities(ready) {
        capabilities = ready;
        await settings.saveCapabilities(ready);
    }

    async function cachedCapabilities() {
        await capabilitiesLoaded;
        return capabilities;
    }

    function writeHeader(frame, buffer, sequence) {
        const view = new DataView(frame.buffer, frame.byteOffset, 8);
        view.setUint32(0, buffer, true);
        view.setUint32(4, sequence, true);
    }

    function decodeBase64Into(target, text) {
        if (typeof target.setFromBase64 === 'function') {
            return target.setFromBase64(text).written;
        }
        const binary = atob(text);
        for (let i = 0; i < binary.length; i++) {
            target[i] = binary.charCodeAt(i);
        }
        return binary.length;
    }

    // A takeover session carries Media Source traffic only: never a file the application would
    // fetch, and never sign-in or pairing messages.
    function takeoverMessageAllowed(message) {
        const type = message?.type;
        if (type === 'open') {
            return message.kind === 'source' && !('urls' in message) && !('headers' in message);
        }
        return typeof type === 'string' && type !== 'hello' && type !== 'pair';
    }

    class Session {
        constructor(port) {
            this.port = port;
            this.takeover = port.name === takeoverPortName;
            this.socket = null;
            this.ready = false;
            this.ended = false;
            this.opened = false;
            this.closeReason = null;
            this.closeMessage = null;
            this.closeSent = false;
            this.queue = [];
            this.assembly = null;
            this.ticker = null;
            port.onMessage.addListener((message) => this.onPortMessage(message));
            port.onDisconnect.addListener(() => this.onPortDisconnect());
        }

        async start() {
            const loaded = await settings.load();
            if (this.ended) {
                return;
            }
            if (this.takeover && !await globalThis.LightPlayerTakeoverTabs.isArmed(this.port.sender?.tab?.id)) {
                this.finish('error');
                return;
            }
            if (!loaded.token) {
                this.finish('notPaired');
                return;
            }
            let socket;
            try {
                socket = new WebSocket(settings.endpoint(loaded.port), settings.subprotocol);
            } catch (error) {
                console.error('Light Media Center: opening the browser link failed', error);
                this.finish('unreachable');
                return;
            }
            socket.binaryType = 'arraybuffer';
            this.socket = socket;
            socket.onopen = () => {
                this.opened = true;
                socket.send(JSON.stringify({
                    type: 'hello',
                    token: loaded.token,
                    extensionVersion: settings.extensionVersion(),
                }));
            };
            socket.onmessage = (event) => this.onSocketMessage(event);
            socket.onclose = (event) => this.onSocketClose(event);
        }

        onSocketMessage(event) {
            if (typeof event.data !== 'string') {
                return;
            }
            let message;
            try {
                message = JSON.parse(event.data);
            } catch (error) {
                console.error('Light Media Center: the application sent a malformed message', error);
                this.socket.close(4400);
                return;
            }
            if (!this.ready) {
                if (message.type !== 'ready') {
                    console.error('Light Media Center: expected ready, received', message.type);
                    this.socket.close(4400);
                    return;
                }
                this.ready = true;
                rememberCapabilities(message).catch((error) => console.error('Light Media Center: caching capabilities failed', error));
                this.post({ k: 'ready', capabilities: message });
                for (const queued of this.queue.splice(0)) {
                    this.onPortMessage(queued);
                }
                return;
            }
            if (message.type === 'close') {
                this.closeReason = typeof message.reason === 'string' ? message.reason : 'error';
                this.closeMessage = typeof message.message === 'string' ? message.message : null;
            }
            this.post({ k: 'json', message });
        }

        onSocketClose(event) {
            if (this.ended) {
                return;
            }
            let reason = this.closeReason;
            if (!reason) {
                if (!this.opened) {
                    reason = 'unreachable';
                } else {
                    reason = closeCodeReasons.get(event.code) ?? 'disconnected';
                }
            }
            if (event.code === 4401) {
                settings.clearPairing('revoked').catch((error) => console.error('Light Media Center: clearing the pairing failed', error));
            }
            this.finish(reason, this.closeMessage);
        }

        onPortMessage(message) {
            if (this.ended || !message) {
                return;
            }
            if (!this.ready) {
                this.queue.push(message);
                return;
            }
            switch (message.k) {
                case 'json':
                    if (this.takeover && !takeoverMessageAllowed(message.message)) {
                        console.error('Light Media Center: a takeover session sent a message it may not send');
                        this.endWith('error');
                        return;
                    }
                    this.sendJson(message.message);
                    break;
                case 'pong':
                    this.sendJson({ type: 'pong' });
                    break;
                case 'ticks':
                    this.setTicks(message.interval);
                    break;
                case 'append':
                    this.sendAppend(message);
                    break;
                case 'chunk':
                    this.sendChunk(message);
                    break;
                default:
                    console.error('Light Media Center: unknown session port message', message.k);
            }
        }

        // Port messages reach a hidden tab's content script on time, while its timers are throttled.
        setTicks(interval) {
            clearInterval(this.ticker);
            this.ticker = null;
            const ms = Number(interval);
            if (ms > 0 && !this.ended) {
                this.ticker = setInterval(() => this.post({ k: 'tick' }), Math.max(minTickMs, ms));
            }
        }

        sendJson(message) {
            if (this.socket?.readyState !== WebSocket.OPEN) {
                return;
            }
            this.socket.send(JSON.stringify(message));
            if (message?.type === 'close') {
                // The content script ends the session; the application hears why before the socket closes.
                this.closeSent = true;
                this.closeReason = typeof message.reason === 'string' ? message.reason : 'stopped';
                this.socket.close(1000);
            }
        }

        // Firefox ports carry the appended bytes themselves.
        sendAppend(message) {
            const data = message.data;
            const bytes = ArrayBuffer.isView(data)
                ? new Uint8Array(data.buffer, data.byteOffset, data.byteLength)
                : new Uint8Array(data);
            const frame = new Uint8Array(8 + bytes.byteLength);
            writeHeader(frame, message.buffer, message.sequence);
            frame.set(bytes, 8);
            this.sendFrame(frame);
        }

        // Chromium ports carry JSON only, so an append arrives as base64 chunks in order.
        sendChunk(message) {
            if (message.offset === 0) {
                const frame = new Uint8Array(8 + message.size);
                writeHeader(frame, message.buffer, message.sequence);
                this.assembly = { buffer: message.buffer, sequence: message.sequence, size: message.size, frame, filled: 0 };
            }
            const assembly = this.assembly;
            if (!assembly || assembly.buffer !== message.buffer || assembly.sequence !== message.sequence || assembly.filled !== message.offset) {
                console.error('Light Media Center: append chunks arrived out of order');
                this.assembly = null;
                this.endWith('error');
                return;
            }
            assembly.filled += decodeBase64Into(assembly.frame.subarray(8 + assembly.filled), message.data);
            if (assembly.filled >= assembly.size) {
                this.assembly = null;
                this.sendFrame(assembly.frame);
            }
        }

        sendFrame(frame) {
            if (this.socket?.readyState === WebSocket.OPEN) {
                this.socket.send(frame);
            }
        }

        endWith(reason) {
            if (this.socket?.readyState === WebSocket.OPEN && !this.closeSent) {
                this.sendJson({ type: 'close', reason });
            } else {
                this.finish(reason);
            }
        }

        onPortDisconnect() {
            if (this.ended) {
                return;
            }
            this.ended = true;
            this.setTicks(0);
            const socket = this.socket;
            if (socket?.readyState === WebSocket.OPEN) {
                if (this.ready && !this.closeSent) {
                    socket.send(JSON.stringify({ type: 'close', reason: 'disconnected' }));
                }
                socket.close(1000);
            } else if (socket?.readyState === WebSocket.CONNECTING) {
                socket.close();
            }
        }

        post(message) {
            try {
                this.port.postMessage(message);
            } catch (error) {
                console.error('Light Media Center: the session port is gone', error);
                this.onPortDisconnect();
            }
        }

        finish(reason, message = null) {
            if (this.ended) {
                return;
            }
            this.post({ k: 'closed', reason, message });
            this.ended = true;
            this.setTicks(0);
            this.port.disconnect();
            if (this.socket && this.socket.readyState <= WebSocket.OPEN) {
                this.socket.close(1000);
            }
        }
    }

    api.runtime.onConnect.addListener((port) => {
        if (port.name !== portName && port.name !== takeoverPortName) {
            return;
        }
        new Session(port).start().catch((error) => {
            console.error('Light Media Center: starting a session failed', error);
            port.disconnect();
        });
    });

    // Connects, reads `ready` and closes, which tells whether the application is reachable and
    // paired and refreshes the cached capabilities.
    async function probe() {
        const loaded = await settings.load();
        return new Promise((resolve) => {
            let socket;
            let opened = false;
            let settled = false;
            const settle = (result) => {
                if (!settled) {
                    settled = true;
                    clearTimeout(timer);
                    resolve({ port: loaded.port, ...result });
                }
            };
            const timer = setTimeout(() => {
                settle({ reachable: opened, paired: false, timedOut: true });
                socket?.close();
            }, probeTimeoutMs);
            try {
                socket = new WebSocket(settings.endpoint(loaded.port), settings.subprotocol);
            } catch (error) {
                console.error('Light Media Center: opening the browser link failed', error);
                settle({ reachable: false, paired: false });
                return;
            }
            socket.onopen = () => {
                opened = true;
                if (!loaded.token) {
                    socket.close(1000);
                    settle({ reachable: true, paired: false, revoked: loaded.pairing?.state === 'revoked' });
                    return;
                }
                socket.send(JSON.stringify({ type: 'hello', token: loaded.token, extensionVersion: settings.extensionVersion() }));
            };
            socket.onmessage = (event) => {
                let message = null;
                try {
                    message = JSON.parse(event.data);
                } catch (error) {
                    console.error('Light Media Center: the application sent a malformed message', error);
                }
                if (message?.type === 'ready') {
                    rememberCapabilities(message).catch((error) => console.error('Light Media Center: caching capabilities failed', error));
                    settle({ reachable: true, paired: true, appVersion: message.appVersion ?? null });
                }
                socket.close(1000);
            };
            socket.onclose = (event) => {
                if (event.code === 4401) {
                    settings.clearPairing('revoked')
                        .catch((error) => console.error('Light Media Center: clearing the pairing failed', error))
                        .finally(() => settle({ reachable: true, paired: false, revoked: true }));
                    return;
                }
                settle({ reachable: opened, paired: false });
            };
        });
    }

    // Whether anything answers HTTP on the port, where the listener answers a request that is not an
    // upgrade. Firefox delays a WebSocket to a port where earlier ones failed, by more each time, so
    // a check repeated while the application starts must not fail sockets.
    async function answers(port) {
        try {
            await fetch(`http://127.0.0.1:${port}/browser-link/v1`, {
                mode: 'no-cors',
                cache: 'no-store',
                credentials: 'omit',
                signal: AbortSignal.timeout(probeTimeoutMs),
            });
            return true;
        } catch (error) {
            // Nothing listens (a network error) or nothing answered in time.
            if (error?.name === 'TypeError' || error?.name === 'TimeoutError' || error?.name === 'AbortError') {
                return false;
            }
            throw error;
        }
    }

    // Whether a browser link listener accepts the handshake on the port. The socket closes without
    // sending anything, so no token reaches a port the user did not choose.
    function listens(port) {
        return new Promise((resolve) => {
            let socket;
            const timer = setTimeout(() => {
                socket?.close();
                resolve(false);
            }, probeTimeoutMs);
            try {
                socket = new WebSocket(settings.endpoint(port), settings.subprotocol);
            } catch (error) {
                clearTimeout(timer);
                console.error('Light Media Center: opening the browser link failed', error);
                resolve(false);
                return;
            }
            socket.onopen = () => {
                clearTimeout(timer);
                socket.close(1000);
                resolve(true);
            };
            socket.onclose = () => {
                clearTimeout(timer);
                resolve(false);
            };
        });
    }

    globalThis.LightPlayerSessions = Object.freeze({ probe, answers, listens, cachedCapabilities });
})();
