// Shared media runtime: playback stays on the managed owner thread; bounded background jobs decode photos
// and inspect media. This script owns source transport, timers, pump scheduling and WebCodecs.
import { dotnet } from "./_framework/dotnet.js";
import { WorkerSources } from "./source-bridge.js";
import { PhotoDecoders } from "./photo-codecs.js";

const moduleName = "lp-media-worker";
const settings = new URL(import.meta.url).searchParams;
const threadLimit = Math.min(10, Math.max(1, Math.floor(globalThis.navigator?.hardwareConcurrency || 2)));
// Viewer photo jobs are admitted apart from library and whole-image jobs; each holds a pool thread while it runs.
const bulkJobs = Math.min(2, Math.max(1, threadLimit - 1));
const viewerJobs = Math.min(2, Math.max(1, threadLimit - 3));
// One more thread than admitted jobs keeps interop completions running without waiting for thread injection.
const poolThreads = bulkJobs + viewerJobs + 1;
const poolLimit = Math.max(poolThreads, threadLimit);
const pumpBudgetMilliseconds = 8;
const statusIntervalMilliseconds = 250;
const webCodecs = new Map();
const payloadMethods = new Set(["probeVideo", "albumCover", "image", "bitmapSubtitles", "photoExport", "photoThumbnail"]);
const frameMethods = new Set(["photoOpen", "photoTiles", "photoAnimationRead"]);
const updates = { allowances: new Map(), releases: [], positions: new Map(), speeds: new Map(), faults: [] };
const pendingFrames = { frames: [], objects: [] };
let runtime;
let worker;
let sources;
let pumpState = "idle";
let pumpAgain = false;
let retryTimer = 0;
let lastStatus = 0;
const photos = new PhotoDecoders(async request => {
    const result = await takePayload(JSON.parse(await worker.Call("photoPackets", JSON.stringify(request))));
    const value = JSON.parse(result.value);
    return value && result.payload ? { ...value, payload: result.payload } : null;
}, codecConfig);

const ready = (async () => {
    runtime = await dotnet
        .withEnvironmentVariables({
            DOTNET_PROCESSOR_COUNT: String(threadLimit),
            DOTNET_ThreadPool_ForceMinWorkerThreads: poolThreads.toString(16),
            DOTNET_ThreadPool_ForceMaxWorkerThreads: poolLimit.toString(16),
        })
        .withConfig({
            pthreadPoolInitialSize: Math.max(poolThreads, Math.min(5, threadLimit)),
            pthreadPoolUnusedSize: 2,
            jsThreadBlockingMode: "ThrowWhenBlockingWait",
        })
        .create();
    sources = new WorkerSources(() => runtime.localHeapViewU8().buffer, message => self.postMessage(message));
    globalThis.lightplayerSourceBridge = {
        request: (...args) => sources.request(...args),
        close: id => {
            if (sources.close(id))
                worker.ReleaseSource(id).catch(async error => console.error("Could not release a closed source.", await describeError(error)));
        },
    };
    runtime.setModuleImports(moduleName, {
        webCodecsCanDecode: id => canDecode(webCodecs.get(id)),
        webCodecsReset: resetVideo,
        webCodecsDecode: decodeVideo,
        webCodecsEnd: endVideo,
        webCodecsClose: closeRecord,
    });
    const exports = await runtime.getAssemblyExports("LightStudio.LightPlayer.WebWorkers");
    worker = exports.LightStudio.LightPlayer.WebWorkers.MediaWorker;
    await worker.Initialize(Number(settings.get("ffmpegLog") ?? 16), Number(settings.get("appLog") ?? 1), bulkJobs, viewerJobs);
})();
ready.catch(async error => self.postMessage({ type: "fatal", error: (await describeError(error)).stack }));

self.onmessage = ({ data }) => {
    handle(data).catch(async error => console.error("The media worker could not handle a message.", await describeError(error)));
};

async function describeError(error) {
    const exception = error instanceof Error ? error : new Error(String(error));
    return { name: exception.name, message: exception.message,
        stack: worker ? await worker.DescribeException(exception) : exception.stack };
}

async function handle(message) {
    await ready;
    switch (message.type) {
        case "call":
            try {
                const { value, payload, frames, memory, objects } = await call(message.method, message.args);
                try {
                    self.postMessage({ type: "result", id: message.id, value, payload, frames, memory, objects },
                        [...(payload ? [payload] : []), ...(objects ?? [])]);
                } catch (error) {
                    for (const object of objects ?? []) object.close();
                    throw error;
                }
            } catch (error) {
                self.postMessage({ type: "result", id: message.id, error: await describeError(error) });
            }
            return;
        case "register-source":
            sources.register(message.source);
            return;
        case "cancel-source":
            sources.cancel(message.source);
            return;
        case "cancel-photo-tiles":
            photos.cancel(message);
            return;
        case "cancel-photo-export":
            await worker.CancelPhotoExport(message.operation);
            return;
        case "source-closed":
            sources.acknowledgeClose(message.source);
            await worker.ReleaseSource(message.source);
            return;
        case "updates":
            mergeUpdates(message.updates);
            requestPump();
            return;
        case "relay":
            // Packets the page read from a source of its own, for a demuxer here.
            await worker.Relay(message.demuxer, message.generation, new Uint8Array(message.data));
            requestPump();
            return;
        default:
            throw new Error(`Unknown media worker message '${message.type}'.`);
    }
}

async function call(method, args) {
    switch (method) {
        case "photoCodecStats":
            return { value: JSON.stringify(photos.stats) };
        case "photoTiles": {
            const decoded = await photos.decode(JSON.parse(args));
            if (decoded) return decoded;
            break;
        }
        case "photoClose":
            photos.close(JSON.parse(args));
            break;
        case "licenseNotice":
            return { value: await worker.LicenseNotice() };
        case "createVideoDecoder":
            return { value: await createVideoDecoder(JSON.parse(args)) };
        case "setOutput": {
            const request = JSON.parse(args);
            const record = webCodecs.get(request.decoder);
            if (record) {
                record.allowance = Math.max(record.allowance, request.capacity);
                record.hasOutput = true;
            }
            break;
        }
        case "recover": {
            // The worker takes over a stream WebCodecs could not finish, so it learns how far the script got.
            const request = JSON.parse(args);
            const record = webCodecs.get(request.decoder);
            if (record) args = JSON.stringify({ ...request, sent: record.sent });
            break;
        }
    }
    const source = method === "photoThumbnail" || method === "photoExport" || method === "probeVideo"
        ? JSON.parse(args).source : null;
    let result;
    try {
        result = await worker.Call(method, args);
    } finally {
        if (source && sources.close(source.id)) await worker.ReleaseSource(source.id);
    }
    requestPump();
    if (frameMethods.has(method)) {
        // The frames' planes stay in this runtime's memory, which the page reads them from.
        const { value, frames } = JSON.parse(result);
        if (method === "photoOpen") photos.register(value.photo);
        return { value: JSON.stringify(value), frames, memory: runtime.localHeapViewU8().buffer };
    }
    return payloadMethods.has(method) ? takePayload(JSON.parse(result)) : { value: result };
}

// Copies the bytes a result carries out of the runtime's memory, encoding a BGRA picture first when the
// result asks for it, and releases their native owner.
async function takePayload({ value, payload }) {
    if (!payload) return { value: JSON.stringify(value) };
    let bytes;
    try {
        bytes = runtime.localHeapViewU8().slice(payload.address, payload.address + payload.length);
    } finally {
        await worker.ReleasePayload(payload.handle);
    }
    if (payload.encode === "jpeg") bytes = await encodeJpeg(bytes, payload.width, payload.height);
    return { value: JSON.stringify(value), payload: bytes.buffer };
}

async function encodeJpeg(bgra, width, height) {
    for (let offset = 0; offset < bgra.length; offset += 4) {
        const blue = bgra[offset];
        bgra[offset] = bgra[offset + 2];
        bgra[offset + 2] = blue;
    }
    const canvas = new OffscreenCanvas(width, height);
    canvas.getContext("2d").putImageData(new ImageData(new Uint8ClampedArray(bgra.buffer, bgra.byteOffset, bgra.length), width, height), 0, 0);
    return new Uint8Array(await (await canvas.convertToBlob({ type: "image/jpeg", quality: 0.82 })).arrayBuffer());
}

function mergeUpdates(incoming) {
    for (const { decoder, memory } of incoming.buffers ?? []) recycleVideoBuffer(webCodecs.get(decoder), memory);
    for (const { decoder, allowance } of incoming.allowances ?? []) {
        updates.allowances.set(decoder, Math.max(updates.allowances.get(decoder) ?? 0, allowance));
        const record = webCodecs.get(decoder);
        if (record && allowance > record.allowance) {
            record.allowance = allowance;
            exportVideo(record);
        }
    }
    for (const id of incoming.releases ?? []) updates.releases.push(id);
    for (const { demuxer, seconds } of incoming.positions ?? []) updates.positions.set(demuxer, seconds);
    for (const { decoder, speed } of incoming.speeds ?? []) updates.speeds.set(decoder, speed);
}

function takeUpdates() {
    const taken = {
        allowances: [...updates.allowances].map(([decoder, allowance]) => ({ decoder, allowance })),
        releases: updates.releases.splice(0),
        positions: [...updates.positions].map(([demuxer, seconds]) => ({ demuxer, seconds })),
        speeds: [...updates.speeds].map(([decoder, speed]) => ({ decoder, speed })),
        faults: updates.faults.splice(0),
    };
    updates.allowances.clear();
    updates.positions.clear();
    updates.speeds.clear();
    return taken;
}

function requestPump() {
    if (!worker) return;
    if (pumpState === "running") {
        pumpAgain = true;
        return;
    }
    if (pumpState === "scheduled") return;
    pumpState = "scheduled";
    setTimeout(runPump, 0);
}

async function runPump() {
    pumpState = "running";
    clearTimeout(retryTimer);
    try {
        do {
            pumpAgain = false;
            const json = await worker.Pump(pumpBudgetMilliseconds, JSON.stringify(takeUpdates()));
            const batch = JSON.parse(json);
            const now = performance.now();
            if (batch.frames.length || batch.notifications.length || batch.decoders.length || batch.relays.length ||
                now - lastStatus >= statusIntervalMilliseconds) {
                lastStatus = now;
                self.postMessage({ type: "batch", batch: json, memory: runtime.localHeapViewU8().buffer, objects: [] });
            }
            if (batch.busy || updates.releases.length || updates.faults.length) pumpAgain = true;
            else if (batch.retryAfter > 0) retryTimer = setTimeout(requestPump, batch.retryAfter);
        } while (pumpAgain);
    } catch (error) {
        console.error("The media worker pump failed.", await describeError(error));
    } finally {
        pumpState = "idle";
    }
}

function postWebCodecsFrames() {
    if (!pendingFrames.frames.length) return;
    const frames = pendingFrames.frames.splice(0);
    const objects = pendingFrames.objects.splice(0);
    const batch = JSON.stringify({ frames, demuxers: [], decoders: [], relays: [], notifications: [], busy: false, retryAfter: 0 });
    self.postMessage({ type: "batch", batch, memory: runtime.localHeapViewU8().buffer, objects }, objects);
}

function base64Bytes(text) {
    const binary = atob(text || "");
    const bytes = new Uint8Array(binary.length);
    for (let index = 0; index < binary.length; index++) bytes[index] = binary.charCodeAt(index);
    return bytes;
}

// ---- WebCodecs ----------------------------------------------------------------------------------
// The stream configuration, decoder pacing and frame handling below keep the behavior the browser
// client has always had with WebCodecs, including its workarounds for individual browsers.

async function createVideoDecoder(request) {
    let chosen = null;
    if (request.hardware && typeof VideoDecoder !== "undefined") {
        const stream = JSON.parse(await worker.Call("describeVideo", String(request.demuxer)));
        stream.extra = base64Bytes(stream.extra);
        const config = codecConfig(stream);
        let configError;
        if (config) {
            const candidates = stream.bits > 8
                ? [{ ...config, hardwareAcceleration: "prefer-software" }]
                : [{ ...config, hardwareAcceleration: "prefer-hardware" }, config];
            for (const candidate of candidates) {
                try {
                    if ((await VideoDecoder.isConfigSupported(candidate))?.supported) {
                        chosen = { config: candidate, video: stream };
                        break;
                    }
                } catch (error) { configError = error; }
            }
        }
        if (request.requireWebCodecs && !chosen) throw new Error("WebCodecs does not support this video stream.", { cause: configError });
    }
    if (chosen) request.webCodecs = chosen.config.codec;
    const result = JSON.parse(await worker.Call("createVideoDecoder", JSON.stringify(request)));
    if (chosen) {
        const inBand = chosen.video.codec === "h264" && !chosen.config.description && chosen.video.extra.length ? chosen.video.extra : null;
        const record = { id: result.decoder, config: chosen.config, video: chosen.video, inBand, decoder: null, frames: [], frameBytes: 0,
            submittedFrames: 0, decoderCapacity: 32, needsKey: true, serial: 0, minimumPts: 0, allowance: 0, sent: 0,
            flushedSerial: -1, endedSerial: -1, exporting: false, exportAgain: false, failed: false, hasOutput: false,
            copyBuffers: [], copyBufferLength: 0, copyBufferCapacity: Math.max(0, request.framePoolSize ?? 0),
            copyBufferByteCapacity: Math.max(0, request.framePoolByteCapacity ?? 0) };
        webCodecs.set(record.id, record);
        openVideo(record);
        result.diagnostics = { ...result.diagnostics, implementation: "WebCodecs worker" };
    }
    return JSON.stringify(result);
}

function codecConfig(stream) {
    if (stream.dolbyVisionProfile > 0 || stream.fieldOrder > 1 || stream.width <= 0 || stream.height <= 0) return null;
    const extra = stream.extra;
    const hex = value => value.toString(16).padStart(2, "0");
    let codec;
    let description;
    const annexB = stream.codec === "h264" && extra[0] !== 1 ? annexBProfile(extra) : null;
    if (stream.codec === "h264" && extra.length >= 4 && extra[0] === 1) {
        codec = `avc1.${hex(extra[1])}${hex(extra[2])}${hex(extra[3])}`;
        description = extra;
    } else if (annexB) {
        // Annex B, as encoders and transport streams deliver it: without a description WebCodecs reads the
        // parameter sets in-band, so key frames carry them.
        codec = `avc1.${hex(annexB[0])}${hex(annexB[1])}${hex(annexB[2])}`;
    } else if (stream.codec === "hevc" && extra.length >= 23 && extra[0] === 1) {
        const profileSpace = ["", "A", "B", "C"][extra[1] >> 6];
        let compatibility = 0;
        for (let bit = 0; bit < 32; bit++) compatibility = (compatibility * 2 + ((extra[2 + (bit >> 3)] >> (7 - (bit & 7))) & 1)) >>> 0;
        let reversed = 0;
        for (let bit = 0; bit < 32; bit++) reversed = (reversed * 2 + ((compatibility >>> bit) & 1)) >>> 0;
        const constraints = [...extra.slice(6, 12)];
        while (constraints.at(-1) === 0) constraints.pop();
        codec = `hvc1.${profileSpace}${extra[1] & 31}.${reversed.toString(16)}.${extra[1] & 32 ? "H" : "L"}${extra[12]}${constraints.map(value => "." + hex(value)).join("")}`;
        description = extra;
    } else if (stream.codec === "vp8") codec = "vp8";
    else if (stream.codec === "vp9") codec = `vp09.${String(Math.max(0, stream.profile)).padStart(2, "0")}.10.${String(stream.bits > 8 ? stream.bits : 8).padStart(2, "0")}`;
    else if (stream.codec === "av1" && extra.length >= 3) {
        const depth = extra[2] & 64 ? (extra[2] & 32 ? 12 : 10) : 8;
        codec = `av01.${extra[1] >> 5}.${String(extra[1] & 31).padStart(2, "0")}${extra[2] & 128 ? "H" : "M"}.${String(depth).padStart(2, "0")}`;
        description = extra;
    }
    if (!codec) return null;
    const config = { codec, codedWidth: stream.width, codedHeight: stream.height };
    if (description?.length) config.description = description;
    if (stream.pixelAspect > 0) {
        config.displayAspectWidth = Math.round(stream.width * Math.max(1, stream.pixelAspect));
        config.displayAspectHeight = Math.round(stream.height / Math.min(1, stream.pixelAspect));
    }
    const colorSpace = {};
    if (stream.range === 1 || stream.range === 2) colorSpace.fullRange = stream.range === 2;
    const primaries = ({ 1: "bt709", 5: "bt470bg", 6: "smpte170m", 9: "bt2020", 12: "smpte432" })[stream.primaries];
    const transfer = ({ 1: "bt709", 6: "smpte170m", 8: "linear", 13: "iec61966-2-1", 16: "pq", 18: "hlg" })[stream.transfer];
    const matrix = ({ 0: "rgb", 1: "bt709", 5: "bt470bg", 6: "smpte170m", 9: "bt2020-ncl" })[stream.matrix];
    if (primaries) colorSpace.primaries = primaries;
    if (transfer) colorSpace.transfer = transfer;
    if (matrix) colorSpace.matrix = matrix;
    if (Object.keys(colorSpace).length) config.colorSpace = colorSpace;
    return config;
}

// Profile, constraint and level bytes of the first H.264 sequence parameter set in Annex B bytes.
function annexBProfile(bytes) {
    for (let index = 2; index + 4 < bytes.length; index++) {
        if (bytes[index] === 1 && bytes[index - 1] === 0 && bytes[index - 2] === 0 && (bytes[index + 1] & 31) === 7)
            return bytes.subarray(index + 2, index + 5);
    }
    return null;
}

function canDecode(record) {
    if (!record || record.failed || !record.decoder || record.decoder.state !== "configured") return false;
    return record.decoder.decodeQueueSize + record.frames.length < 16 &&
        record.submittedFrames + record.frames.length < record.decoderCapacity;
}

function openVideo(record) {
    record.submittedFrames = 0;
    record.decoderCapacity = 32;
    const decoder = new VideoDecoder({
        output(frame) {
            if (record.decoder !== decoder) { frame.close(); return; }
            record.submittedFrames = Math.max(0, record.submittedFrames - 1);
            if (frame.timestamp / 1000000 + 0.000001 < record.minimumPts) { frame.close(); requestPump(); return; }
            if (record.frames.length >= record.decoderCapacity) {
                frame.close();
                failVideo(record, new Error("WebCodecs produced more frames than admitted chunks."));
                return;
            }
            record.frames.push({ frame, serial: record.serial });
            record.frameBytes += frame.displayWidth * frame.displayHeight * 4;
            exportVideo(record);
        },
        error(error) {
            if (record.decoder === decoder) failVideo(record, error);
        },
    });
    record.decoder = decoder;
    decoder.addEventListener("dequeue", () => {
        if (record.decoder === decoder) requestPump();
    });
    decoder.configure(record.config);
    record.needsKey = true;
}

function closeVideo(record) {
    if (record.decoder && record.decoder.state !== "closed") record.decoder.close();
    record.decoder = null;
    for (const { frame } of record.frames) frame.close();
    record.frames.length = 0;
    record.frameBytes = 0;
    record.submittedFrames = 0;
}

function resetVideo(id, serial, minimumPts, continues) {
    const record = webCodecs.get(id);
    if (!record || record.failed) return;
    if (!continues) {
        closeVideo(record);
        openVideo(record);
    }
    record.serial = serial;
    record.minimumPts = minimumPts;
}

function decodeVideo(id, serial, data, length, pts, duration, key) {
    const record = webCodecs.get(id);
    if (!record || record.failed || !record.decoder || serial !== record.serial) return;
    if (record.needsKey && !key) return;
    record.needsKey = false;
    record.submittedFrames++;
    let bytes;
    if (key && record.inBand) {
        bytes = new Uint8Array(record.inBand.length + length);
        bytes.set(record.inBand);
        bytes.set(runtime.localHeapViewU8().subarray(data, data + length), record.inBand.length);
    } else {
        bytes = runtime.localHeapViewU8().slice(data, data + length);
    }
    record.decoder.decode(new EncodedVideoChunk({ type: key ? "key" : "delta",
        timestamp: Math.round(pts * 1000000),
        duration: duration > 0 ? Math.max(1, Math.round(duration * 1000000)) : undefined,
        data: bytes }));
}

function endVideo(id, serial) {
    const record = webCodecs.get(id);
    const decoder = record?.decoder;
    if (!decoder || record.failed) return;
    decoder.flush().then(() => {
        if (record.decoder !== decoder || record.serial !== serial) return;
        record.flushedSerial = serial;
        exportVideo(record);
    }, error => {
        if (record.decoder === decoder && record.serial === serial) failVideo(record, error);
    });
}

function closeRecord(id) {
    const record = webCodecs.get(id);
    if (!record) return;
    webCodecs.delete(id);
    record.failed = true;
    record.copyBuffers.length = 0;
    closeVideo(record);
}

function failVideo(record, error) {
    if (record.failed) return;
    record.failed = true;
    record.copyBuffers.length = 0;
    const notSupported = error?.name === "NotSupportedError";
    if (notSupported) console.warn("WebCodecs cannot preserve this stream's pixels; using FFmpeg.", error);
    else console.error("WebCodecs failed.", error);
    updates.faults.push({ decoder: record.id, message: error?.message || String(error),
        requiredSoftwareFallbackReason: notSupported ? "WebCodecs cannot preserve this stream's pixels" : "" });
    closeVideo(record);
    requestPump();
}

async function exportVideo(record) {
    if (record.exporting) {
        record.exportAgain = true;
        return;
    }
    record.exporting = true;
    try {
        do {
            record.exportAgain = false;
            while (!record.failed && record.hasOutput && record.frames.length && record.sent < record.allowance) {
                const { frame, serial } = record.frames.shift();
                record.frameBytes -= frame.displayWidth * frame.displayHeight * 4;
                let output;
                try {
                    output = await takeVideo(record, frame);
                } catch (error) {
                    failVideo(record, error);
                    return;
                }
                if (serial !== record.serial || record.failed) {
                    output.webFrame?.close();
                    recycleVideoBuffer(record, output.memory);
                    continue;
                }
                output.output = record.id;
                output.serial = serial;
                if (output.webFrame) output.webFrameIndex = pendingFrames.objects.push(output.webFrame) - 1;
                if (output.memory) output.memoryIndex = pendingFrames.objects.push(output.memory) - 1;
                delete output.webFrame;
                delete output.memory;
                pendingFrames.frames.push(output);
                record.sent++;
            }
            if (!record.failed && record.flushedSerial === record.serial && record.endedSerial !== record.serial && !record.frames.length) {
                record.endedSerial = record.serial;
                pendingFrames.frames.push({ output: record.id, kind: 4, serial: record.serial });
            }
        } while (record.exportAgain);
    } finally {
        record.exporting = false;
        postWebCodecsFrames();
        requestPump();
    }
}

const bitmapOptions = { colorSpaceConversion: "none", premultiplyAlpha: "none", imageOrientation: "none" };

// Pictures these browsers upload from a bitmap rather than from the frame itself, as the page does.
function requiresBitmap(frame) {
    const agent = globalThis.navigator?.userAgent ?? "";
    return frame.displayWidth !== (frame.visibleRect?.width ?? frame.codedWidth) ||
        frame.displayHeight !== (frame.visibleRect?.height ?? frame.codedHeight) ||
        agent.includes("Firefox") || agent.includes("Gecko/") || agent.includes("Chrome/");
}

function takeVideoBuffer(record, length) {
    if (record.copyBufferLength !== length) {
        record.copyBuffers.length = 0;
        record.copyBufferLength = length;
    }
    return record.copyBuffers.pop() ?? new ArrayBuffer(length);
}

function recycleVideoBuffer(record, memory) {
    if (!record || record.failed || !(memory instanceof ArrayBuffer) || memory.byteLength !== record.copyBufferLength ||
        record.copyBuffers.length >= record.copyBufferCapacity ||
        (record.copyBuffers.length + 1) * memory.byteLength > record.copyBufferByteCapacity) return;
    record.copyBuffers.push(memory);
}

async function takeVideo(record, frame) {
    let transferred = false;
    let memory;
    try {
        const width = frame.visibleRect?.width ?? frame.codedWidth;
        const height = frame.visibleRect?.height ?? frame.codedHeight;
        const primaries = ({ bt709: 1, bt470bg: 5, smpte170m: 6, bt2020: 9, smpte432: 12 })[frame.colorSpace.primaries] ?? record.video.primaries;
        const transfer = ({ bt709: 1, smpte170m: 6, linear: 8, "iec61966-2-1": 13, pq: 16, hlg: 18 })[frame.colorSpace.transfer] ?? record.video.transfer;
        const matrix = ({ rgb: 0, bt709: 1, bt470bg: 5, smpte170m: 6, "bt2020-ncl": 9 })[frame.colorSpace.matrix] ?? record.video.matrix;
        const range = frame.colorSpace.fullRange == null ? record.video.range : frame.colorSpace.fullRange ? 2 : 1;
        const planar = frame.format?.startsWith("I4") || frame.format === "NV12";
        const frameBits = Number(/P(10|12|16)$/.exec(frame.format || "")?.[1] || (frame.format ? 8 : 0));
        const requiredBits = Math.max(record.video.bits, frameBits);
        if (requiredBits > 8 && (!planar || frameBits < requiredBits))
            throw new DOMException("WebCodecs did not expose high-bit-depth pixels in a readable planar layout.", "NotSupportedError");
        const hdr = [16, 18].includes(record.video.transfer) || [16, 18].includes(transfer);
        const preserveColorEncoding = hdr || [5, 6, 9, 12].includes(record.video.primaries);
        if (hdr && !planar && frame.format !== "BGRA")
            throw new DOMException("WebCodecs did not expose HDR pixels in a layout the tone mapper can read.", "NotSupportedError");
        const duration = (frame.duration ?? 1000000 / (record.video.frameRate || 30)) / 1000000;
        if (requiredBits === 8 && !preserveColorEncoding) {
            // The conversion costs milliseconds per picture, which here stay off the page's thread.
            const image = requiresBitmap(frame) ? await createImageBitmap(frame, bitmapOptions) : frame;
            transferred = image === frame;
            const imageWidth = image.width ?? width;
            const imageHeight = image.height ?? height;
            return { primaries, transfer, matrix, range, chroma: record.video.chroma, fieldOrder: record.video.fieldOrder,
                peak: record.video.peak, minimumLuminance: record.video.minimumLuminance, maximumLuminance: record.video.maximumLuminance,
                kind: 1, webFrame: image, width: imageWidth, height: imageHeight, length: imageWidth * imageHeight * 4,
                pts: frame.timestamp / 1000000, duration,
                rotation: record.video.rotation, mirror: record.video.mirror, pixelAspect: record.video.pixelAspect,
                sourceFormat: frame.format, format: "webcodecs" };
        }
        if (planar) {
            memory = takeVideoBuffer(record, frame.allocationSize());
            const layout = await frame.copyTo(memory);
            const wide = /P(10|12|16)$/.test(frame.format);
            const fullChroma = frame.format.startsWith("I444");
            const halfHeight = frame.format.startsWith("I420") || frame.format === "NV12";
            return { kind: 1, memory, length: memory.byteLength, delivery: "webcodecs-worker-planar",
                width, height, format: frame.format, pts: frame.timestamp / 1000000, duration,
                primaries, transfer, matrix,
                peak: record.video.peak, minimumLuminance: record.video.minimumLuminance, maximumLuminance: record.video.maximumLuminance,
                range, chroma: record.video.chroma, fieldOrder: record.video.fieldOrder,
                rotation: record.video.rotation, mirror: record.video.mirror, pixelAspect: record.video.pixelAspect,
                planes: layout.map((plane, index) => ({ offset: plane.offset, stride: plane.stride,
                    rowBytes: (index && !fullChroma ? Math.ceil(width / 2) : width) * (wide ? 2 : 1) * (index && frame.format === "NV12" ? 2 : 1),
                    height: index && halfHeight ? Math.ceil(height / 2) : height })) };
        }
        memory = takeVideoBuffer(record, width * height * 4);
        const preservePacked = preserveColorEncoding && frame.format === "BGRA";
        await frame.copyTo(memory, { ...(preservePacked ? {} : { format: "BGRA", colorSpace: "srgb" }), layout: [{ offset: 0, stride: width * 4 }] });
        return { kind: 1, memory, length: memory.byteLength, delivery: "webcodecs-worker-packed",
            width, height, format: "bgra", pts: frame.timestamp / 1000000, duration,
            primaries: preservePacked ? primaries : 1, transfer: preservePacked ? transfer : 13,
            peak: record.video.peak, minimumLuminance: record.video.minimumLuminance, maximumLuminance: record.video.maximumLuminance,
            rotation: record.video.rotation, mirror: record.video.mirror, pixelAspect: record.video.pixelAspect,
            matrix: 0, range: 2, chroma: record.video.chroma, fieldOrder: record.video.fieldOrder,
            planes: [{ offset: 0, stride: width * 4, rowBytes: width * 4, height }] };
    } catch (error) {
        recycleVideoBuffer(record, memory);
        throw error;
    } finally {
        if (!transferred) frame.close();
    }
}
