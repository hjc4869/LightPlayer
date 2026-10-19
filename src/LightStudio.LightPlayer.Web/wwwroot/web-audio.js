const devices = new Map();
let nextDeviceId = 1;
let activated = false;

// A browser holds an AudioContext suspended until the page has been interacted with. Opening the
// app straight onto a playing page (a reload that restores the video sheet or a link whose hash
// fragment names the video) creates the device before any click has happened, so every context
// that has not been paused is resumed again on the first gesture the page sees.
function onGesture() {
    activated = true;
    for (const device of devices.values()) {
        if (!device.paused && device.context.state === 'suspended') {
            device.context.resume().catch(() => undefined);
        }
    }
}

for (const name of ['pointerdown', 'touchend', 'keydown']) {
    globalThis.addEventListener(name, onGesture, { capture: true, passive: true });
}

// Whether the page may make sound at all yet, so a session restored onto a page nobody has touched
// opens paused rather than running on silently.
export function isUnlocked() {
    return activated || globalThis.navigator?.userActivation?.hasBeenActive === true;
}

function getDevice(deviceId) {
    const device = devices.get(deviceId);
    if (!device) {
        throw new Error(`Unknown WebAudio device ${deviceId}.`);
    }

    return device;
}

function command(device, message, transfer = []) {
    if (device.failure) return Promise.reject(device.failure);
    const id = ++device.nextCommand;
    return new Promise((resolve, reject) => {
        const timer = setTimeout(() => {
            device.commands.delete(id);
            reject(new Error(`AudioWorklet command timed out: ${message.type}`));
        }, 10000);
        device.commands.set(id, { resolve, reject, timer });
        try { device.node.port.postMessage({ ...message, id, generation: device.generation }, transfer); }
        catch (error) { clearTimeout(timer); device.commands.delete(id); reject(error); }
    });
}

export async function create(sampleRate, channelCount) {
    const AudioContextType = globalThis.AudioContext ?? globalThis.webkitAudioContext;
    if (!AudioContextType) {
        throw new Error("This browser does not support Web Audio.");
    }

    const context = new AudioContextType({
        sampleRate,
        latencyHint: "interactive",
    });
    try { return await createDevice(context, channelCount); }
    catch (error) { await context.close(); throw error; }
}

async function createDevice(context, channelCount) {
    await context.audioWorklet.addModule(new URL("./web-audio-worklet.js", import.meta.url));
    const node = new AudioWorkletNode(context, "light-player-pcm", {
        numberOfInputs: 0,
        numberOfOutputs: 1,
        outputChannelCount: [channelCount],
        processorOptions: { channelCount },
    });
    const gain = context.createGain();
    node.connect(gain).connect(context.destination);

    const deviceId = nextDeviceId++;
    const device = {
        context,
        node,
        gain,
        channelCount,
        bufferedFrames: 0,
        playedFrames: 0,
        consumedUntilTime: 0,
        underruns: 0,
        generation: 0,
        paused: false,
        commands: new Map(),
        nextCommand: 0,
        frozenFrames: null,
    };
    node.port.onmessage = event => {
        const message = event.data;
        if (message.type === "ack") {
            const pending = device.commands.get(message.id);
            if (pending) {
                device.commands.delete(message.id);
                clearTimeout(pending.timer);
                if (message.error) pending.reject(new Error(message.error));
                else pending.resolve();
            }
            return;
        }
        if (message.generation !== device.generation) {
            return;
        }

        if (message.type === "consumed") {
            device.bufferedFrames = Math.max(0, device.bufferedFrames - message.frames);
            device.playedFrames += message.frames;
            device.consumedUntilTime = message.until;
        } else if (message.type === "underrun") {
            device.underruns += 1;
        }
    };
    node.onprocessorerror = () => {
        device.failure = new Error("The audio worklet processor failed.");
        for (const pending of device.commands.values()) {
            clearTimeout(pending.timer);
            pending.reject(device.failure);
        }
        device.commands.clear();
    };
    devices.set(deviceId, device);
    // Never awaited: while the autoplay policy holds the context, resume() stays pending until the
    // page is interacted with, so waiting on it here would keep the device and the whole audio pump
    // from ever coming into existence. The gesture listener wakes it instead.
    context.resume().catch(() => undefined);
    return deviceId;
}

export async function start(deviceId) {
    const device = getDevice(deviceId);
    device.paused = false;
    device.frozenFrames = null;
    if (!isUnlocked() && device.context.state === "suspended")
        throw new DOMException("Audio playback requires a user gesture.", "NotAllowedError");
    await device.context.resume();
    await command(device, { type: "active", value: true });
}

export async function pause(deviceId) {
    const device = getDevice(deviceId);
    device.paused = true;
    await command(device, { type: "active", value: false });
    device.frozenFrames = playedFrames(device);
}

export async function reset(deviceId) {
    const device = getDevice(deviceId);
    device.generation += 1;
    await command(device, { type: "reset" });
    device.bufferedFrames = 0;
    device.playedFrames = 0;
    device.consumedUntilTime = 0;
    device.frozenFrames = 0;
}

export async function dispose(deviceId) {
    const device = devices.get(deviceId);
    if (!device) {
        return;
    }

    device.paused = true;
    device.node.port.postMessage({
        type: "active",
        value: false,
        generation: device.generation,
    });
    device.node.disconnect();
    device.gain.disconnect();
    devices.delete(deviceId);
    await device.context.close();
    device.node.port.close();
    for (const pending of device.commands.values()) {
        clearTimeout(pending.timer);
        pending.reject(new Error("WebAudio output closed."));
    }
    device.commands.clear();
}

export async function setVolume(deviceId, volume) {
    const device = getDevice(deviceId);
    device.gain.gain.value = Math.max(0, Math.min(1, volume));
}

// pcm is a view of managed memory that is only valid during the call, so the samples are converted at once.
export function queuePcm16(deviceId, pcm) {
    const device = getDevice(deviceId);
    if (device.failure) throw device.failure;
    const bytes = pcm instanceof Uint8Array ? pcm : pcm.slice();
    const sampleCount = Math.floor(bytes.byteLength / 2);
    const samples = new Float32Array(sampleCount);
    const view = new DataView(bytes.buffer, bytes.byteOffset, sampleCount * 2);
    for (let index = 0; index < sampleCount; index++) {
        samples[index] = view.getInt16(index * 2, true) / 32768;
    }

    const frameCount = Math.floor(sampleCount / device.channelCount);
    if (frameCount === 0) {
        return 0;
    }

    device.bufferedFrames += frameCount;
    command(device,
        {
            type: "queue",
            samples,
            frameCount,
        },
        [samples.buffer]).catch(error => { device.failure = error; });
    return frameCount;
}

export function queuePcm24(deviceId, pcm) {
    const device = getDevice(deviceId);
    if (device.failure) throw device.failure;
    const bytes = pcm instanceof Uint8Array ? pcm : pcm.slice();
    const sampleCount = Math.floor(bytes.byteLength / 3);
    const samples = new Float32Array(sampleCount);
    for (let index = 0; index < sampleCount; index++) {
        const offset = index * 3;
        // Packed little-endian signed 24-bit sample, sign-extended before normalizing to [-1, 1).
        let value = bytes[offset] | (bytes[offset + 1] << 8) | (bytes[offset + 2] << 16);
        if ((value & 0x800000) !== 0) {
            value |= 0xff000000;
        }

        samples[index] = value / 8388608;
    }

    const frameCount = Math.floor(sampleCount / device.channelCount);
    if (frameCount === 0) {
        return 0;
    }

    device.bufferedFrames += frameCount;
    command(device,
        {
            type: "queue",
            samples,
            frameCount,
        },
        [samples.buffer]).catch(error => { device.failure = error; });
    return frameCount;
}

export async function setSourceEnded(deviceId, ended) {
    const device = getDevice(deviceId);
    await command(device, { type: "sourceEnded", value: ended });
}

export async function getSampleRate(deviceId) {
    return getDevice(deviceId).context.sampleRate;
}

export async function getBufferedFrameCount(deviceId) {
    return getDevice(deviceId).bufferedFrames;
}

export async function getPlayedFrameCount(deviceId) {
    const device = getDevice(deviceId);
    return device.frozenFrames ?? playedFrames(device);
}

function playedFrames(device) {
    const context = device.context;
    // The worklet reports frames as it hands them to the graph, one outputLatency before they reach
    // the speaker; on a Bluetooth headset that is most of the audio/video offset. Discounting the
    // audio still in flight also drains it in real time once the source stops feeding the worklet.
    const inFlight = Math.max(0, device.consumedUntilTime + getOutputLatency(context) - context.currentTime);
    return Math.max(0, device.playedFrames - Math.round(inFlight * context.sampleRate));
}

// baseLatency only covers the graph itself, so it is a last resort for browsers without the
// device-side measurement.
function getOutputLatency(context) {
    if (Number.isFinite(context.outputLatency) && context.outputLatency > 0) {
        return context.outputLatency;
    }

    return Number.isFinite(context.baseLatency) ? context.baseLatency : 0;
}

export async function getUnderrunCount(deviceId) {
    return getDevice(deviceId).underruns;
}

export function now() { return performance.now() / 1000; }

/** Writes buffered frames, played frames, underruns and the observation time into a four-element view. */
export function readSnapshot(deviceId, destination) {
    const device = getDevice(deviceId);
    if (device.failure) throw device.failure;
    destination.set(new Float64Array([device.bufferedFrames, device.frozenFrames ?? playedFrames(device), device.underruns, now()]));
}
