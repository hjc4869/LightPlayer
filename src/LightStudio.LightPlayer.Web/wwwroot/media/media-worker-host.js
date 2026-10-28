// Page side of the media workers: starts them, carries engine commands, serves the sources they read
// and imports the frames they decode. Decoded planes stay in the worker's shared memory and are uploaded
// to the renderer's textures from there, so pixels never pass through the page runtime's managed memory.
import { PageSources } from "./worker/source-bridge.js";
import { resolveMediaFile } from "../file-system-access.js";
import { retainFrame, releaseFrame, uploadPlane, releaseTexture, currentContext, diagnostics } from "./gpu-planes.js";
export { releaseFrame, uploadPlane, releaseTexture, currentContext };

const workerUrl = new URL("./worker/lp-media-worker.js", import.meta.url);
const workers = new Map();
const sourceOwners = new Map();
const payloads = new Map();
let nextWorker = 0;
let managed;
let pageSources;

export function getDiagnostics() { return JSON.stringify(diagnostics); }

/** Whether remote video packets are streamed over a WebSocket rather than read with HTTP requests. */
export function usesPacketSocket() { return (globalThis.navigator?.userAgent ?? "").includes("Firefox/"); }

export async function initialize(assembly) {
    const exports = await globalThis.getDotnetRuntime(0).getAssemblyExports(assembly);
    managed = exports.LightStudio.LightPlayer.Web;
    pageSources = new PageSources(
        message => workers.get(sourceOwners.get(message.source))?.worker.postMessage(message),
        (source, request, position, length) => managed.WorkerSourceHost.ReadSourceAsync(source, request, position, length));
}

/** Starts the shared media runtime. */
export function startWorker(appLogLevel) {
    if (!globalThis.crossOriginIsolated) throw new Error("The media worker requires cross-origin isolation.");
    const url = new URL(workerUrl);
    url.searchParams.set("appLog", appLogLevel);
    const id = ++nextWorker;
    const state = { id, worker: new Worker(url, { type: "module", name: "LightPlayer media" }), calls: new Map(), nextCall: 0,
        batches: Promise.resolve(), releases: [], buffers: [], releaseScheduled: false, failure: null };
    state.worker.onmessage = ({ data }) => receive(state, data);
    state.worker.onerror = event => fail(state, new Error(event.message || "The media worker failed."));
    state.worker.onmessageerror = () => fail(state, new Error("A media worker message could not be read."));
    workers.set(id, state);
    return id;
}

export function terminateWorker(id) {
    const state = workers.get(id);
    if (!state) return;
    fail(state, new Error("The media worker was stopped."));
    workers.delete(id);
}

/** Runs one engine command in the worker and resolves with its JSON result. */
export function call(id, method, args) {
    return callCore(id, method, args, 0);
}

/** Like call; bytes the result carries are kept under token until taken or released. */
export function callWithPayload(id, token, method, args) {
    return callCore(id, method, args, token);
}

export function payloadLength(token) {
    return payloads.get(token)?.length ?? -1;
}

export function takePayload(token, destination) {
    const bytes = payloads.get(token);
    payloads.delete(token);
    if (!bytes || bytes.length !== destination.length) throw new Error("The media worker result bytes are missing.");
    destination.set(bytes);
}

export function releasePayload(token) {
    payloads.delete(token);
}

function callCore(id, method, args, token) {
    const state = workers.get(id);
    if (!state) return Promise.reject(new Error("The media worker is not running."));
    if (state.failure) return Promise.reject(state.failure);
    return new Promise((resolve, reject) => {
        const callId = ++state.nextCall;
        state.calls.set(callId, { resolve, reject, token });
        state.worker.postMessage({ type: "call", id: callId, method, args });
    });
}

/** Sends allowances, positions and speeds; the worker applies them at its next pump. */
export function postUpdates(id, json) {
    const state = workers.get(id);
    if (!state || state.failure) return;
    state.worker.postMessage({ type: "updates", updates: JSON.parse(json) });
}

export function cancelPhotoTiles(id, session, gain, operation) {
    const state = workers.get(id);
    if (state && !state.failure) state.worker.postMessage({ type: "cancel-photo-tiles", session, gain, operation });
}

export function cancelPhotoExport(id, operation) {
    const state = workers.get(id);
    if (state && !state.failure) state.worker.postMessage({ type: "cancel-photo-export", operation });
}

/** Hands a batch of packets the page read to a relayed demuxer; the view is only valid during this call. */
export function postRelay(id, demuxer, generation, batch) {
    const state = workers.get(id);
    if (!state || state.failure) return;
    const data = batch.slice().buffer;
    state.worker.postMessage({ type: "relay", demuxer, generation, data }, [data]);
}

/**
 * Makes a source readable by a worker and returns its exact length. kind is "stream" for a page stream
 * served by WorkerSourceHost, "file" for a browser file path, or "url" for an HTTP resource.
 */
export async function registerSource(id, sourceId, kind, path, length) {
    const state = workers.get(id);
    if (!state || state.failure) throw state?.failure ?? new Error("The media worker is not running.");
    let descriptor;
    if (kind === "stream") {
        descriptor = pageSources.register(sourceId, length);
    } else if (kind === "file") {
        const file = await resolveMediaFile(path);
        descriptor = { id: sourceId, length: file.size, file, cancel: new SharedArrayBuffer(4) };
    } else if (kind === "url") {
        const url = new URL(path, globalThis.location.href);
        if (url.protocol !== "http:" && url.protocol !== "https:") throw new Error("Unsupported browser media source.");
        const response = await fetch(url, { method: "HEAD" });
        if (!response.ok) throw new Error(`Media source returned HTTP ${response.status}.`);
        descriptor = { id: sourceId, url: url.href, length: Number(response.headers.get("Content-Length")), cancel: new SharedArrayBuffer(4) };
    } else throw new Error(`Unknown media source kind '${kind}'.`);
    return addSource(state, descriptor);
}

/** Makes bytes readable by a worker from a copy of its own; the view is only valid during this call. */
export function registerBytes(id, sourceId, view) {
    const state = workers.get(id);
    if (!state || state.failure) throw state?.failure ?? new Error("The media worker is not running.");
    const bytes = view.slice();
    return addSource(state, { id: sourceId, bytes, length: bytes.length, cancel: new SharedArrayBuffer(4) });
}

function addSource(state, descriptor) {
    if (!Number.isSafeInteger(descriptor.length) || descriptor.length < 0) throw new Error("Media input must have a known byte length.");
    sourceOwners.set(descriptor.id, state.id);
    state.sources ??= new Map();
    state.sources.set(descriptor.id, descriptor);
    state.worker.postMessage({ type: "register-source", source: descriptor });
    return descriptor.length;
}

/** Fails every current and future wait on the source until it is cleared again. */
export function cancelSource(sourceId, canceled) {
    const state = workers.get(sourceOwners.get(sourceId));
    const descriptor = state?.sources?.get(sourceId);
    if (descriptor?.kind === "page") pageSources.cancel(sourceId, canceled);
    else if (descriptor?.cancel) {
        const flag = new Int32Array(descriptor.cancel);
        Atomics.store(flag, 0, canceled ? 1 : 0);
        Atomics.notify(flag, 0);
    }
    if (canceled) state?.worker.postMessage({ type: "cancel-source", source: sourceId });
}

export function forgetSource(sourceId) {
    const state = workers.get(sourceOwners.get(sourceId));
    state?.sources?.delete(sourceId);
    sourceOwners.delete(sourceId);
}

export function completeSourceRead(request, bytes) {
    try { pageSources.complete(request, bytes); }
    finally { bytes.dispose(); }
}

export function failSourceRead(request, message) {
    pageSources.fail(request, new Error(message));
}

function receive(state, message) {
    if (pageSources.handle(message, reply => state.worker.postMessage(reply))) return;
    switch (message.type) {
        case "fatal":
            fail(state, new Error(message.error));
            return;
        case "result": {
            const pending = state.calls.get(message.id);
            if (!pending) return;
            state.calls.delete(message.id);
            if (message.error) {
                const error = new Error(message.error.message);
                error.name = message.error.name;
                error.stack = message.error.stack || error.stack;
                pending.reject(error);
            } else {
                if (pending.token && message.payload) payloads.set(pending.token, new Uint8Array(message.payload));
                if (message.frames) {
                    importResultFrames(state, message).then(
                        frames => pending.resolve(JSON.stringify({ value: JSON.parse(message.value), frames })), pending.reject);
                    return;
                }
                pending.resolve(message.value);
            }
            return;
        }
        case "batch":
            state.batches = state.batches.then(() => importBatch(state, message)).catch(error => {
                console.error("Could not import media worker frames.", error);
                fail(state, error);
            });
            return;
        default:
            console.warn(`Unknown media worker message '${message.type}'.`);
    }
}

// Retains each decoded picture for the renderer, then hands the batch to the page runtime in the order the
// worker produced it. Sound is copied out of the worker's memory while the runtime takes the batch, and the
// worker reuses those frames only afterwards.
async function importBatch(state, message) {
    const batch = JSON.parse(message.batch);
    if (state.failure) {
        for (const frame of batch.frames) closeObjects(frame, message);
        return;
    }
    const frames = [];
    const sound = [];
    let next = 0;
    try {
        for (; next < batch.frames.length; next++) {
            const frame = batch.frames[next];
            if (frame.kind === 1) frames.push(await retain(state, frame, message, `${state.id}:${frame.output}`));
            else {
                if (frame.kind === 2) sound.push(frame.id);
                frames.push(frame);
            }
        }
        if (state.failure) throw state.failure;
    } catch (error) {
        for (const frame of frames) {
            if (frame.resource) await releaseFrame(frame.resource);
            if (frame.enhancement?.resource) await releaseFrame(frame.enhancement.resource);
        }
        // The frame that failed let go of its own objects; the ones after it were never taken.
        for (const frame of batch.frames.slice(next + 1)) closeObjects(frame, message);
        throw error;
    }
    state.batchMemory = message.memory instanceof SharedArrayBuffer ? message.memory : null;
    try { managed.MediaWorkerConnection.OnBatch(state.id, JSON.stringify({ ...batch, frames })); }
    finally {
        state.batchMemory = null;
        for (const id of sound) queueRelease(state, id);
    }
}

/** Copies sound of the batch being delivered from the worker's memory into the page runtime's. */
export function copyAudio(id, address, length, destination) {
    const memory = workers.get(id)?.batchMemory;
    if (!memory || address < 0 || length < 0 || address + length > memory.byteLength || destination.length !== length)
        throw new Error("The media worker sound is not available.");
    destination.set(new Uint8Array(memory, address, length));
}

function closeObjects(frame, message) {
    if (frame.webFrameIndex != null) message.objects[frame.webFrameIndex]?.close?.();
    if (frame.enhancement) closeObjects(frame.enhancement, message);
}

// Retains the frames a call returned, such as a photo's pictures. On failure every frame is let go again.
async function importResultFrames(state, message) {
    const frames = [];
    let importing = false;
    try {
        for (const frame of message.frames) {
            if (state.failure) throw state.failure;
            importing = true;
            frames.push(await retain(state, frame, message));
            importing = false;
        }
        if (state.failure) throw state.failure;
        return frames;
    } catch (error) {
        for (const frame of frames) await releaseFrame(frame.resource);
        for (const frame of message.frames.slice(frames.length + (importing ? 1 : 0))) {
            closeObjects(frame, message);
            queueRelease(state, frame.id);
        }
        throw error;
    }
}

async function retain(state, frame, message, consumer) {
    const id = frame.id;
    const output = { ...frame, consumer };
    let released = false;
    const release = () => {
        if (!released) {
            released = true;
            queueRelease(state, id, frame.memoryIndex != null ? output.memory : null, frame.output);
        }
        return Promise.resolve();
    };
    if (frame.webFrameIndex != null) output.webFrame = message.objects[frame.webFrameIndex];
    else if (frame.memoryIndex != null) output.memory = message.objects[frame.memoryIndex];
    else output.memory = message.memory;
    delete output.webFrameIndex;
    delete output.memoryIndex;
    let enhancement;
    try {
        if (frame.enhancement) enhancement = await retain(state, frame.enhancement, message, consumer);
    } catch (error) {
        output.webFrame?.close();
        release();
        throw error;
    }
    try {
        const { enhancement: ignored, ...base } = output;
        return { ...await retainFrame(base, release), enhancement };
    } catch (error) {
        if (enhancement) await releaseFrame(enhancement.resource);
        throw error;
    }
}

function queueRelease(state, frame, memory, decoder) {
    if (state.failure) return;
    if (frame) state.releases.push(frame);
    if (memory) state.buffers.push({ decoder, memory });
    if (state.releaseScheduled || (!state.releases.length && !state.buffers.length)) return;
    state.releaseScheduled = true;
    queueMicrotask(() => {
        state.releaseScheduled = false;
        const releases = state.releases.splice(0);
        const buffers = state.buffers.splice(0);
        if (state.failure || (!releases.length && !buffers.length)) return;
        state.worker.postMessage({ type: "updates", updates: { releases, buffers } }, buffers.map(buffer => buffer.memory));
    });
}

function fail(state, error) {
    if (state.failure) return;
    state.failure = error;
    state.worker.terminate();
    for (const source of state.sources?.keys() ?? []) {
        pageSources.handle({ type: "source-close", source });
        sourceOwners.delete(source);
    }
    state.sources?.clear();
    workers.delete(state.id);
    for (const pending of state.calls.values()) pending.reject(error);
    state.calls.clear();
    try { managed?.MediaWorkerConnection.OnWorkerFailed(state.id, error.message || String(error)); }
    catch (failure) { console.error("Could not report the media worker failure.", failure); }
}
