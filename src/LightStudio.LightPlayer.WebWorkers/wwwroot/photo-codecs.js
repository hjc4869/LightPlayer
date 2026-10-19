const bitmapOptions = { colorSpaceConversion: "none", premultiplyAlpha: "none", imageOrientation: "none" };

export class PhotoDecoders {
    constructor(readPackets, codecConfig) {
        this.readPackets = readPackets;
        this.codecConfig = codecConfig;
        this.sources = new Map();
        this.decoded = 0;
        this.fallbacks = 0;
        this.hardwarePreferred = 0;
    }

    register(photo) {
        if (!photo?.session) return;
        for (const [gain, image] of [[false, photo.base], [true, photo.gain]]) {
            if (!image?.canUseWebCodecs) continue;
            image.canUseWebCodecs = typeof VideoDecoder !== "undefined" && typeof createImageBitmap === "function";
            if (image.canUseWebCodecs) this.sources.set(`${photo.session}:${gain}`, {
                decoder: null, configuration: null, failed: false, operations: new Set(), pending: Promise.resolve(), active: null,
            });
        }
    }

    get stats() {
        return { sources: this.sources.size, decoders: [...this.sources.values()].filter(source => source.decoder).length,
            operations: [...this.sources.values()].reduce((count, source) => count + source.operations.size, 0),
            decoded: this.decoded, fallbacks: this.fallbacks, hardwarePreferred: this.hardwarePreferred };
    }

    cancel(request) {
        const source = this.sources.get(`${request.session}:${request.gain}`);
        if (!source) return;
        for (const operation of source.operations) {
            if (request.operation != null && request.operation !== operation.id) continue;
            operation.canceled = true;
            operation.reject?.(new DOMException("Photo decoding was canceled.", "AbortError"));
            if (source.active === operation && source.decoder?.state === "configured") {
                source.decoder.reset();
            }
        }
    }

    close(session) {
        for (const gain of [false, true]) {
            const key = `${session}:${gain}`;
            const source = this.sources.get(key);
            if (!source) continue;
            this.cancel({ session, gain });
            this.closeDecoder(source);
            this.sources.delete(key);
        }
    }

    closeDecoder(source) {
        if (source.decoder?.state !== "closed") source.decoder?.close();
        source.decoder = null;
        source.configuration = null;
    }

    async decode(request) {
        const source = this.sources.get(`${request.session}:${request.gain}`);
        if (!source || source.failed) return null;
        const operation = { id: request.operation, canceled: false, finished: false, images: [], conversions: [], received: new Set() };
        source.operations.add(operation);
        const pending = source.pending.then(() => this.decodeBatch(source, operation, request));
        source.pending = pending.then(() => {}, () => {});
        try { return await pending; }
        finally { source.operations.delete(operation); }
    }

    check(operation) {
        if (operation.canceled || operation.finished) throw new DOMException("Photo decoding was canceled.", "AbortError");
    }

    async decodeBatch(source, operation, request) {
        let timeout;
        try {
            this.check(operation);
            if (source.failed) return null;
            const batch = await this.readPackets(request);
            this.check(operation);
            if (!batch) {
                source.failed = true;
                this.closeDecoder(source);
                return null;
            }
            source.active = operation;
            const interrupted = new Promise((resolve, reject) => {
                operation.reject = reject;
                timeout = setTimeout(() => reject(new Error("WebCodecs photo decoding timed out.")), 15000);
            });
            await Promise.race([this.decodePackets(source, operation, batch), interrupted]);
            this.check(operation);
            const objects = operation.images;
            if (objects.length !== batch.packets.length || batch.packets.some((packet, index) => !objects[index]))
                throw new Error("WebCodecs did not return every photo tile.");
            const frames = objects.map((image, index) => ({ kind: 1, photo: true, webFrameIndex: index,
                width: image.width, height: image.height, length: image.width * image.height * 4,
                format: "bgra", matrix: 0, range: 2, primaries: batch.packets[index].stream.primaries,
                transfer: batch.packets[index].stream.transfer, pixelAspect: 1, fieldOrder: 0 }));
            operation.images = [];
            this.decoded += frames.length;
            return { value: "true", frames, objects };
        } catch (error) {
            if (operation.canceled) throw new DOMException("Photo decoding was canceled.", "AbortError");
            source.failed = true;
            this.fallbacks++;
            this.closeDecoder(source);
            console.warn("WebCodecs photo decoding failed; using FFmpeg.", error);
            return null;
        } finally {
            clearTimeout(timeout);
            operation.finished = true;
            operation.reject = null;
            await Promise.all(operation.conversions);
            for (const image of operation.images) image?.close();
            if (source.active === operation) source.active = null;
        }
    }

    async configure(source, operation, stream) {
        const key = JSON.stringify(stream);
        if (source.configuration === key && source.decoder && source.decoder.state !== "closed") {
            if (source.decoder.state === "unconfigured") source.decoder.configure(source.config);
            return;
        }
        this.closeDecoder(source);
        const extra = Uint8Array.from(atob(stream.extra), character => character.charCodeAt(0));
        const config = this.codecConfig({ ...stream, extra, matrix: stream.matrix === 2 ? stream.height >= 720 ? 1 : 5 : stream.matrix });
        if (!config || stream.bits !== 8) throw new DOMException("The photo codec configuration is unsupported.", "NotSupportedError");
        let chosen;
        for (const candidate of [{ ...config, hardwareAcceleration: "prefer-hardware" }, config]) {
            let supported;
            try { supported = await VideoDecoder.isConfigSupported(candidate); }
            catch (error) {
                console.warn("Could not query a WebCodecs photo configuration.", error);
                continue;
            }
            this.check(operation);
            if (supported?.supported) { chosen = candidate; break; }
        }
        if (!chosen) throw new DOMException("The browser declined the photo codec configuration.", "NotSupportedError");
        this.check(operation);
        const decoder = new VideoDecoder({
            output: frame => {
                const active = source.active;
                if (source.decoder !== decoder || !active || active.canceled || active.finished) { frame.close(); return; }
                const position = frame.timestamp;
                if (!Number.isInteger(position) || position < 0 || position >= active.count || active.received.has(position) ||
                    /P(?:10|12|16)$/.test(frame.format ?? "")) {
                    frame.close();
                    active.reject?.(new Error("WebCodecs returned an unexpected photo tile."));
                    return;
                }
                active.received.add(position);
                active.conversions.push((async () => {
                    try {
                        const image = await createImageBitmap(frame, bitmapOptions);
                        if (active.canceled || active.finished) image.close();
                        else active.images[position] = image;
                    } finally { frame.close(); }
                })().catch(error => active.reject?.(error)));
            },
            error: error => { if (source.decoder === decoder) source.active?.reject?.(error); },
        });
        source.decoder = decoder;
        decoder.configure(chosen);
        source.config = chosen;
        source.configuration = key;
        if (chosen.hardwareAcceleration === "prefer-hardware") this.hardwarePreferred++;
    }

    async decodePackets(source, operation, batch) {
        if (!Array.isArray(batch.packets) || batch.packets.length < 1 || batch.packets.length > 32)
            throw new Error("Invalid compressed photo batch.");
        operation.count = batch.packets.length;
        for (let start = 0; start < batch.packets.length;) {
            const stream = batch.packets[start].stream;
            this.check(operation);
            await this.configure(source, operation, stream);
            this.check(operation);
            let end = start + 1;
            while (end < batch.packets.length && JSON.stringify(batch.packets[end].stream) === source.configuration) end++;
            const extra = Uint8Array.from(atob(stream.extra), character => character.charCodeAt(0));
            const header = stream.codec === "av1" ? extra.subarray(extra[0] === 0x81 ? 4 : 0) : new Uint8Array();
            for (let index = start; index < end; index++) {
                const packet = batch.packets[index];
                const bytes = new Uint8Array(header.length + packet.length);
                bytes.set(header);
                bytes.set(new Uint8Array(batch.payload, packet.offset, packet.length), header.length);
                source.decoder.decode(new EncodedVideoChunk({ type: "key", timestamp: index, data: bytes }));
            }
            await source.decoder.flush();
            this.check(operation);
            start = end;
        }
        await Promise.all(operation.conversions);
    }
}