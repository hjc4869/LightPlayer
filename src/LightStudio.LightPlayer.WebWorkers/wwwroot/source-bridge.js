// Source bridge between the page and the media worker. source-bridge.cpp reads windows in the
// worker's shared memory, and whoever fills a window swaps its exact
// pending word for writing first, so a reply to a superseded request can never land in reused memory.

export const Pending = 1;
export const Writing = 2;
export const Ready = 3;
export const Failed = 4;
export const Canceled = 6;

const stateMask = 7;
const sequenceShift = 3;
const readTimeoutMilliseconds = 30000;
const encoder = new TextEncoder();

function pendingWord(sequence) {
    return (sequence << sequenceShift) | Pending;
}

function describeError(error) {
    const message = `${error?.name || "Error"}: ${error?.message || String(error)}`;
    return error?.stack ? `${message}\n${error.stack}` : message;
}

// Fills one window if it is still waiting for this request; returns false when it has moved on.
function settle(memory, request, bytes, error) {
    const words = new Int32Array(memory);
    const index = request.word >> 2;
    const expected = pendingWord(request.sequence);
    if (Atomics.compareExchange(words, index, expected, (expected & ~stateMask) | Writing) !== expected) return false;
    let state = Ready;
    let count = 0;
    try {
        if (error) {
            const text = encoder.encode(describeError(error));
            count = Math.min(text.length, request.length);
            new Uint8Array(memory, request.data, count).set(text.subarray(0, count));
            state = Failed;
        } else {
            count = bytes.byteLength ?? bytes.length;
            if (count > request.length) throw new RangeError(`The source returned ${count} bytes for a ${request.length} byte window.`);
            const target = new Uint8Array(memory, request.data, count);
            if (typeof bytes.copyTo === "function") bytes.copyTo(target);
            else target.set(bytes);
        }
    } catch (failure) {
        const text = encoder.encode(describeError(failure));
        count = Math.min(text.length, request.length);
        new Uint8Array(memory, request.data, count).set(text.subarray(0, count));
        state = Failed;
    } finally {
        Atomics.store(words, request.filledLength >> 2, count);
        Atomics.store(words, index, (expected & ~stateMask) | state);
        Atomics.notify(words, index);
    }
    return true;
}

function cancelRequest(memory, request) {
    const words = new Int32Array(memory);
    const index = request.word >> 2;
    const expected = pendingWord(request.sequence);
    if (Atomics.compareExchange(words, index, expected, (expected & ~stateMask) | Canceled) === expected)
        Atomics.notify(words, index);
}

// Worker transport for the native source client.
export class WorkerSources {
    closing = new Set();

    constructor(heapBuffer, post) {
        this.heapBuffer = heapBuffer;
        this.post = post;
        this.sources = new Map();
    }

    // descriptor: { id, length, kind: "page", cancel } from PageSources.register, or { id, length, file | url, headers | bytes }.
    register(descriptor) {
        if (this.sources.has(descriptor.id)) throw new Error(`Source ${descriptor.id} is already open.`);
        const cancel = descriptor.cancel ? new Int32Array(descriptor.cancel) : null;
        this.sources.set(descriptor.id, { ...descriptor, cancel, requests: new Map(), closed: false });
    }

    request(id, sequence, position, length, data, word, filledLength) {
        const request = { sequence, length, data, word, filledLength };
        try {
            const source = this.sources.get(id);
            if (!source) throw new Error(`Source ${id} is closed.`);
            if (source.cancel && Atomics.load(source.cancel, 0) !== 0) {
                cancelRequest(this.heapBuffer(), request);
                return;
            }
            if (source.kind === "page") {
                this.post({ type: "source-read", source: id, position, ...request, memory: this.heapBuffer() });
                return;
            }
            if (source.bytes) {
                settle(this.heapBuffer(), request, source.bytes.subarray(position, position + length), null);
                return;
            }
            source.requests.get(word)?.controller.abort();
            const operation = { ...request, controller: new AbortController() };
            source.requests.set(word, operation);
            this.fill(source, operation, position).catch(error => console.error("Could not settle an asynchronous source read.", error));
        } catch (error) {
            settle(this.heapBuffer(), request, null, error);
        }
    }

    async fill(source, request, position) {
        const timer = setTimeout(() => request.controller.abort(new Error("The media source read timed out.")), readTimeoutMilliseconds);
        request.controller.signal.addEventListener("abort", () => clearTimeout(timer), { once: true });
        try {
            let bytes;
            if (source.file) {
                bytes = new Uint8Array(await source.file.slice(position, position + request.length).arrayBuffer());
            } else {
                const end = position + request.length - 1;
                const headers = new Headers(source.headers);
                headers.set("Range", `bytes=${position}-${end}`);
                const response = await fetch(source.url, { headers, signal: request.controller.signal });
                if (response.status !== 206 || response.headers.get("Content-Range") !== `bytes ${position}-${end}/${source.length}`) {
                    request.controller.abort();
                    throw new Error(`Invalid media range response (${response.status}).`);
                }
                if (!response.body) throw new Error("The media range response has no body.");
                bytes = new Uint8Array(request.length);
                const reader = response.body.getReader();
                let filled = 0;
                try {
                    for (;;) {
                        const { value, done } = await reader.read();
                        if (done) break;
                        if (value.length > bytes.length - filled)
                            throw new Error("The media range response exceeds its requested byte length.");
                        bytes.set(value, filled);
                        filled += value.length;
                    }
                } finally { reader.releaseLock(); }
                if (filled !== request.length)
                    throw new Error("The media range response has an incorrect byte length.");
            }
            if (request.controller.signal.aborted) throw request.controller.signal.reason;
            this.finish(source, request, bytes, null);
        } catch (error) {
            request.controller.abort(error);
            this.finish(source, request, null, error);
        } finally {
            clearTimeout(timer);
            if (source.requests.get(request.word) === request) source.requests.delete(request.word);
        }
    }

    finish(source, request, bytes, error) {
        if (source.closed || source.requests.get(request.word) !== request) return;
        if (source.cancel && Atomics.load(source.cancel, 0) !== 0) cancelRequest(this.heapBuffer(), request);
        else settle(this.heapBuffer(), request, bytes, error);
    }

    cancel(id) {
        const source = this.sources.get(id);
        if (!source?.cancel || Atomics.load(source.cancel, 0) === 0) return;
        for (const request of source.requests.values()) {
            request.controller.abort();
            cancelRequest(this.heapBuffer(), request);
        }
    }

    // Page sources free their windows only after the page confirms the close, see PageSources.handle.
    close(id) {
        const source = this.sources.get(id);
        if (!source) return !this.closing.has(id);
        if (source) {
            source.closed = true;
            for (const request of source.requests.values()) request.controller.abort();
            source.requests.clear();
        }
        this.sources.delete(id);
        if (source?.kind !== "page") return true;
        this.closing.add(id);
        this.post({ type: "source-close", source: id });
        return false;
    }

    acknowledgeClose(id) {
        this.closing.delete(id);
    }
}

// Page half: serves window requests for app streams. read(source, request, position, length) must end
// by calling complete or fail with the request id, synchronously or later.
export class PageSources {
    constructor(post, read) {
        this.post = post;
        this.read = read;
        this.sources = new Map();
        this.requests = new Map();
        this.nextRequest = 0;
    }

    register(id, length) {
        if (this.sources.has(id)) throw new Error(`Source ${id} is already registered.`);
        const cancel = new Int32Array(new SharedArrayBuffer(4));
        this.sources.set(id, { id, length, cancel, tail: Promise.resolve(), closed: false, requests: new Set() });
        return { id, length, kind: "page", cancel: cancel.buffer };
    }

    // Returns true when the message belonged to the bridge.
    handle(message, acknowledge = this.post) {
        if (message.type === "source-read") {
            const source = this.sources.get(message.source);
            if (source) {
                if (Atomics.load(source.cancel, 0) !== 0) {
                    cancelRequest(message.memory, message);
                    return true;
                }
                const id = ++this.nextRequest;
                this.requests.set(id, { source, read: message });
                source.requests.add(id);
                source.tail = source.tail.then(() => this.fill(source, id));
            }
            return true;
        }
        if (message.type === "source-close") {
            const source = this.sources.get(message.source);
            if (source) {
                source.closed = true;
                this.sources.delete(message.source);
                for (const id of source.requests) this.requests.delete(id);
                source.requests.clear();
            }
            acknowledge({ type: "source-closed", source: message.source });
            return true;
        }
        return false;
    }

    async fill(source, id) {
        const request = this.requests.get(id);
        if (!request) return;
        const read = request.read;
        try {
            if (source.closed) return;
            if (Atomics.load(source.cancel, 0) !== 0) {
                cancelRequest(read.memory, read);
                return;
            }
            const words = new Int32Array(read.memory);
            if (Atomics.load(words, read.word >> 2) !== pendingWord(read.sequence)) return;
            await this.read(source.id, id, read.position, read.length);
            if (this.requests.has(id)) this.fail(id, new Error("The source reader finished without delivering bytes."));
        } catch (error) {
            this.fail(id, error);
        } finally {
            this.requests.delete(id);
            source.requests.delete(id);
        }
    }

    complete(id, bytes) {
        const request = this.take(id);
        if (request) settle(request.read.memory, request.read, bytes, null);
    }

    fail(id, error) {
        const request = this.take(id);
        if (request) settle(request.read.memory, request.read, null, error);
    }

    // A canceled source fails every wait in the worker until the flag is cleared again.
    cancel(id, canceled) {
        const source = this.sources.get(id);
        if (!source) return;
        Atomics.store(source.cancel, 0, canceled ? 1 : 0);
        if (!canceled) return;
        for (const requestId of source.requests) {
            const request = this.take(requestId);
            if (request) cancelRequest(request.read.memory, request.read);
        }
    }

    take(id) {
        const request = this.requests.get(id);
        if (!request) return null;
        this.requests.delete(id);
        request.source.requests.delete(id);
        return request.source.closed ? null : request;
    }
}
