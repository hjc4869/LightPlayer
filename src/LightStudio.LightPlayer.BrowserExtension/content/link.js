// Session link to the application: one runtime port to the background context, which owns the
// WebSocket. `LightPlayerLink.connect()` resolves once the application answered `ready`.
(() => {
    const api = globalThis.browser ?? globalThis.chrome;

    const portName = 'lightplayer.session';
    const takeoverPortName = 'lightplayer.takeover';
    // Chromium ports carry JSON only; appends travel as base64 chunks of at most this many bytes.
    const chunkBytes = 4 * 1024 * 1024;
    // Firefox ports clone buffers; Chromium ports carry JSON only.
    const binaryPorts = api.runtime.getURL('').startsWith('moz-extension:');

    function isArrayBuffer(value) {
        // A tag check also accepts buffers from the page's realm, which fail `instanceof` in Firefox.
        return Object.prototype.toString.call(value) === '[object ArrayBuffer]';
    }

    function toBase64(bytes) {
        if (typeof bytes.toBase64 === 'function') {
            return bytes.toBase64();
        }
        let binary = '';
        for (let i = 0; i < bytes.length; i += 0x8000) {
            binary += String.fromCharCode.apply(null, bytes.subarray(i, i + 0x8000));
        }
        return btoa(binary);
    }

    class Link {
        constructor(port, onReady, onEarlyClose) {
            this.capabilities = null;
            this.onmessage = null;
            this.onclose = null;
            this.ontick = null;
            this.closed = false;
            this._port = port;
            this._onReady = onReady;
            this._onEarlyClose = onEarlyClose;
            port.onMessage.addListener((message) => this._receive(message));
            port.onDisconnect.addListener(() => this._end('disconnected', null));
        }

        // Sends one JSON control message to the application.
        send(message) {
            this._post({ k: 'json', message });
        }

        // Sends one `appendBuffer` call's bytes as a binary frame.
        append(buffer, sequence, bytes) {
            let view;
            if (ArrayBuffer.isView(bytes)) {
                view = new Uint8Array(bytes.buffer, bytes.byteOffset, bytes.byteLength);
            } else if (isArrayBuffer(bytes)) {
                view = new Uint8Array(bytes);
            } else {
                throw new TypeError('append expects an ArrayBuffer or a view of one');
            }
            if (binaryPorts) {
                const whole = isArrayBuffer(bytes) || (view.byteOffset === 0 && view.byteLength === view.buffer.byteLength);
                // Cloning a view clones its whole buffer, so a partial view is copied first.
                const data = isArrayBuffer(bytes) ? bytes : whole ? view.buffer : view.slice().buffer;
                this._post({ k: 'append', buffer, sequence, data });
                return;
            }
            const size = view.byteLength;
            let offset = 0;
            do {
                const part = view.subarray(offset, offset + chunkBytes);
                this._post({ k: 'chunk', buffer, sequence, size, offset, data: toBase64(part) });
                offset += part.byteLength;
            } while (offset < size);
        }

        // Asks the background for a `tick` every `intervalMs` milliseconds, or for none with 0.
        requestTicks(intervalMs) {
            if (!this.closed) {
                this._post({ k: 'ticks', interval: intervalMs });
            }
        }

        // Ends the session with a reason the application records.
        close(reason = 'stopped') {
            if (this.closed) {
                return;
            }
            this._post({ k: 'json', message: { type: 'close', reason } });
        }

        _post(message) {
            if (this.closed) {
                throw new DOMException('The session has ended', 'InvalidStateError');
            }
            this._port.postMessage(message);
        }

        _receive(message) {
            switch (message?.k) {
                case 'ready':
                    this.capabilities = message.capabilities;
                    this._onReady?.(this);
                    this._onReady = null;
                    break;
                case 'json':
                    if (message.message?.type === 'ping') {
                        // Answering over the port is what keeps the background context alive.
                        this._post({ k: 'pong' });
                        this.ontick?.();
                        return;
                    }
                    this.onmessage?.(message.message);
                    break;
                case 'tick':
                    this.ontick?.();
                    break;
                case 'closed':
                    this._end(message.reason, message.message);
                    break;
            }
        }

        _end(reason, detail) {
            if (this.closed) {
                return;
            }
            this.closed = true;
            try {
                this._port.disconnect();
            } catch {
                // The port is already gone.
            }
            if (this._onReady) {
                this._onReady = null;
                this._onEarlyClose?.(reason, detail);
                return;
            }
            this.onclose?.(reason, detail);
        }
    }

    // `takeover` marks a session that relays a taken-over page player; the background accepts it
    // from an armed tab only.
    function connect({ takeover = false } = {}) {
        return new Promise((resolve, reject) => {
            const port = api.runtime.connect({ name: takeover ? takeoverPortName : portName });
            new Link(port, resolve, (reason, detail) => {
                const error = new Error(detail || reason);
                error.reason = reason;
                reject(error);
            });
        });
    }

    // The last `ready` any session or status check received, or null.
    function cachedCapabilities() {
        return api.runtime.sendMessage({ k: 'capabilities' });
    }

    globalThis.LightPlayerLink = Object.freeze({ connect, cachedCapabilities });
})();
