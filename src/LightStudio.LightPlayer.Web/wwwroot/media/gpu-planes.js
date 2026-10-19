const frames = new Map();
const textures = new Map();
const contexts = new WeakMap();
// Every renderer context seen, and the one each decoder's frames were last drawn in, so a frame is uploaded
// ahead of time into the context that will draw it even while another view draws elsewhere.
const renderers = new Set();
const consumers = new Map();
const maximumConsumers = 32;
let nextFrame = 0;
let nextTexture = 0;
let nextContext = 0;
let idleBytes = 0;
let latestGraphics;
const maximumIdleBytes = 128 * 1024 * 1024;
export const diagnostics = { uploads: 0, uploadedBytes: 0, uploadMilliseconds: 0, texturesCreated: 0, resources: 0, managedPixelBytes: 0, importedFrames: 0, earlyUploads: 0 };

function graphics() {
    const runtime = globalThis.getDotnetRuntime?.(0);
    const module = globalThis.Module ?? runtime?.Module;
    const registry = module?.GL ?? globalThis.AvaloniaGL ?? globalThis.SkiaSharpGL;
    const owner = registry?.currentContext;
    if (!owner?.GLctx || owner.GLctx.isContextLost()) throw new Error("The renderer has no current WebGL context.");
    return contextState(registry, owner, owner.GLctx).graphics;
}

function contextState(registry, owner, gl) {
    let state = contexts.get(gl);
    if (!state) {
        state = { id: ++nextContext, generation: 0, idle: new Map(), unpackRowLength: 0 };
        state.graphics = { registry, owner, gl, state };
        contexts.set(gl, state);
        renderers.add(state.graphics);
        if (gl.UNPACK_ROW_LENGTH !== undefined) {
            // Reading the unpack row length back waits for the GPU process to finish every upload before it,
            // so the value set through this context is followed here instead.
            state.unpackRowLength = gl.getParameter(gl.UNPACK_ROW_LENGTH);
            const pixelStorei = gl.pixelStorei;
            gl.pixelStorei = function (name, value) {
                if (name === gl.UNPACK_ROW_LENGTH) state.unpackRowLength = value;
                return pixelStorei.call(this, name, value);
            };
        }
        gl.canvas.addEventListener("webglcontextlost", () => {
            state.generation++;
            for (const slots of state.idle.values()) for (const slot of slots) {
                idleBytes -= slot.bytes;
                slot.registry.textures[slot.name] = null;
            }
            state.idle.clear();
        });
        gl.canvas.addEventListener("webglcontextrestored", () => { state.unpackRowLength = 0; });
    }
    return state;
}

export function currentContext() {
    const current = graphics();
    latestGraphics = current;
    return `${current.state.id}:${current.state.generation}`;
}

function noteConsumer(consumer, current) {
    if (!consumer || consumers.get(consumer) === current) return;
    consumers.delete(consumer);
    consumers.set(consumer, current);
    if (consumers.size > maximumConsumers) consumers.delete(consumers.keys().next().value);
}

function withUploadState({ gl, state }, upload) {
    const started = performance.now();
    const previous = gl.getParameter(gl.TEXTURE_BINDING_2D);
    const alignment = gl.getParameter(gl.UNPACK_ALIGNMENT);
    const rowLength = state.unpackRowLength;
    const skipPixels = gl.getParameter(gl.UNPACK_SKIP_PIXELS);
    const skipRows = gl.getParameter(gl.UNPACK_SKIP_ROWS);
    const conversion = gl.getParameter(gl.UNPACK_COLORSPACE_CONVERSION_WEBGL);
    const flip = gl.getParameter(gl.UNPACK_FLIP_Y_WEBGL);
    const premultiply = gl.getParameter(gl.UNPACK_PREMULTIPLY_ALPHA_WEBGL);
    const unpackBuffer = gl.getParameter(gl.PIXEL_UNPACK_BUFFER_BINDING);
    try {
        gl.pixelStorei(gl.UNPACK_ALIGNMENT, 1);
        gl.pixelStorei(gl.UNPACK_COLORSPACE_CONVERSION_WEBGL, gl.NONE);
        gl.pixelStorei(gl.UNPACK_FLIP_Y_WEBGL, false);
        gl.pixelStorei(gl.UNPACK_PREMULTIPLY_ALPHA_WEBGL, false);
        gl.pixelStorei(gl.UNPACK_SKIP_PIXELS, 0);
        gl.pixelStorei(gl.UNPACK_SKIP_ROWS, 0);
        gl.bindBuffer(gl.PIXEL_UNPACK_BUFFER, null);
        return upload();
    } finally {
        gl.pixelStorei(gl.UNPACK_ALIGNMENT, alignment);
        gl.pixelStorei(gl.UNPACK_ROW_LENGTH, rowLength);
        gl.pixelStorei(gl.UNPACK_SKIP_PIXELS, skipPixels);
        gl.pixelStorei(gl.UNPACK_SKIP_ROWS, skipRows);
        gl.pixelStorei(gl.UNPACK_COLORSPACE_CONVERSION_WEBGL, conversion);
        gl.pixelStorei(gl.UNPACK_FLIP_Y_WEBGL, flip);
        gl.pixelStorei(gl.UNPACK_PREMULTIPLY_ALPHA_WEBGL, premultiply);
        gl.bindBuffer(gl.PIXEL_UNPACK_BUFFER, unpackBuffer);
        gl.bindTexture(gl.TEXTURE_2D, previous);
        diagnostics.uploadMilliseconds += performance.now() - started;
    }
}

export async function retainFrame(output, release) {
    let image;
    try {
        const target = consumers.get(output.consumer) ?? latestGraphics;
        if (target?.gl.isContextLost() && [...renderers].every(renderer => renderer.gl.isContextLost()))
            throw new Error("The renderer WebGL context was lost. Recreate the browser renderer before continuing playback.");
        if (output.webFrame) {
            // The worker normally converts the picture to a bitmap already; a frame it passed on as it was
            // is converted here when the browser needs that.
            const frame = output.webFrame;
            const agent = globalThis.navigator?.userAgent ?? "";
            const requiresBitmap = typeof VideoFrame !== "undefined" && frame instanceof VideoFrame &&
                (frame.displayWidth !== (frame.visibleRect?.width ?? frame.codedWidth) ||
                frame.displayHeight !== (frame.visibleRect?.height ?? frame.codedHeight) ||
                agent.includes("Firefox") || agent.includes("Gecko/") || agent.includes("Chrome/"));
            if (requiresBitmap) {
                image = await createImageBitmap(frame, { colorSpaceConversion: "none", premultiplyAlpha: "none",
                    imageOrientation: "none" });
            } else image = frame;
            output.width = image.width ?? frame.displayWidth ?? frame.codedWidth;
            output.height = image.height ?? frame.displayHeight ?? frame.codedHeight;
            output.length = output.width * output.height * 4;
            output.format = "bgra";
            if (!output.photo) {
                output.pixelAspect = 1;
                output.primaries = 1;
                output.transfer = 13;
            }
            output.matrix = 0;
            output.range = 2;
            output.delivery = "webcodecs-texture";
            if (image !== frame) frame.close();
            delete output.webFrame;
        } else output.delivery ??= "shared-worker-planar";
        const id = ++nextFrame;
        const retained = { ...output, image, release, prepared: new Map() };
        frames.set(id, retained);
        diagnostics.resources++;
        if (target && !target.gl.isContextLost()) prepare(retained, target);
        const { memory, planes, resource, data, ...metadata } = output;
        return { ...metadata, resource: id, planes: planes?.map(plane => ({ ...plane, offset: 0 })) };
    } catch (error) {
        image?.close();
        output.webFrame?.close();
        await release();
        throw error;
    }
}

// Uploads a frame's planes ahead of presentation. A frame that cannot be prepared is uploaded when it is drawn.
function prepare(retained, target) {
    const { registry, owner, state } = target;
    const previous = registry.currentContext;
    if (!registry.makeContextCurrent(owner.handle)) return;
    try {
        const image = retained.image;
        const planeCount = image ? 1 : retained.planes.length;
        const wide = /(?:p(?:10|12|16)(?:le)?|p0(?:10|12|16)le)$/i.test(retained.format);
        const interleaved = /^(?:nv12|nv21|p010le|p012le|p016le)$/i.test(retained.format);
        withUploadState(target, () => {
            for (let index = 0; index < planeCount; index++) {
                const channels = image || retained.format === "bgra" || retained.format === "rgba64le" ? 4 : (wide ? 2 : 1) * (index && interleaved ? 2 : 1);
                const width = image ? retained.width : retained.planes[index].rowBytes / channels;
                const height = image ? retained.height : retained.planes[index].height;
                const uploaded = uploadPlaneData(retained, index, width, height, channels, target);
                retained.prepared.set(index, { uploaded, context: `${state.id}:${state.generation}` });
                diagnostics.earlyUploads++;
            }
        });
    } catch (error) {
        for (const prepared of retained.prepared.values()) releaseTexture(JSON.parse(prepared.uploaded).token);
        retained.prepared.clear();
        console.warn("A decoded frame could not be uploaded ahead of presentation.", error);
    } finally { registry.makeContextCurrent(previous?.handle ?? 0); }
}

export function releaseFrame(id) {
    const frame = frames.get(id);
    if (!frame) return Promise.resolve();
    frames.delete(id);
    for (const prepared of frame.prepared.values()) releaseTexture(JSON.parse(prepared.uploaded).token);
    frame.image?.close();
    diagnostics.resources--;
    return frame.release();
}

export function uploadPlane(id, index, width, height, channels, preparing = false) {
    const frame = frames.get(id);
    if (!frame) throw new Error("The decoded frame has already been released.");
    const current = graphics();
    const { state } = current;
    if (!preparing) noteConsumer(frame.consumer, current);
    if (!preparing && index === 0) diagnostics.importedFrames++;
    const prepared = frame.prepared.get(index);
    if (prepared) {
        frame.prepared.delete(index);
        if (prepared.context === `${state.id}:${state.generation}`) return prepared.uploaded;
        releaseTexture(JSON.parse(prepared.uploaded).token);
    }
    return withUploadState(current, () => uploadPlaneData(frame, index, width, height, channels, current));
}

function uploadPlaneData(frame, index, width, height, channels, { registry, owner, gl, state }) {
    if (typeof gl.texStorage2D !== "function") throw new Error("Planar GPU import requires WebGL 2.");
    const internal = channels === 1 ? gl.R8 : channels === 2 ? gl.RG8 : gl.RGBA8;
    const format = channels === 1 ? gl.RED : channels === 2 ? gl.RG : gl.RGBA;
    const key = `${width}:${height}:${channels}`;
    const idle = state.idle.get(key);
    let slot = idle?.pop();
    if (slot) idleBytes -= slot.bytes;
    try {
        if (!slot) {
            const texture = gl.createTexture();
            const name = registry.getNewId(registry.textures);
            registry.textures[name] = texture;
            texture.name = name;
            slot = { texture, name, internal, width, height, channels, bytes: width * height * channels, key, state,
                generation: state.generation, owner, registry, gl };
            gl.bindTexture(gl.TEXTURE_2D, texture);
            if (frame.image) gl.texImage2D(gl.TEXTURE_2D, 0, internal, width, height, 0, format, gl.UNSIGNED_BYTE, null);
            else gl.texStorage2D(gl.TEXTURE_2D, 1, internal, width, height);
            gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
            gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
            gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
            gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
            diagnostics.texturesCreated++;
        } else gl.bindTexture(gl.TEXTURE_2D, slot.texture);
        if (frame.image) {
            gl.pixelStorei(gl.UNPACK_ALIGNMENT, 4);
            gl.pixelStorei(gl.UNPACK_COLORSPACE_CONVERSION_WEBGL, frame.photo ? gl.NONE : gl.BROWSER_DEFAULT_WEBGL);
            gl.pixelStorei(gl.UNPACK_ROW_LENGTH, 0);
            gl.texSubImage2D(gl.TEXTURE_2D, 0, 0, 0, gl.RGBA, gl.UNSIGNED_BYTE, frame.image);
        } else {
            const plane = frame.planes[index];
            if (!plane || plane.rowBytes !== width * channels || plane.height !== height) throw new Error("Plane geometry does not match its GPU image.");
            if (plane.stride > 0 && plane.stride % channels === 0) {
                gl.pixelStorei(gl.UNPACK_ROW_LENGTH, plane.stride / channels);
                gl.texSubImage2D(gl.TEXTURE_2D, 0, 0, 0, width, height, format, gl.UNSIGNED_BYTE,
                    new Uint8Array(frame.memory, plane.offset, plane.stride * (height - 1) + plane.rowBytes));
            } else {
                gl.pixelStorei(gl.UNPACK_ROW_LENGTH, 0);
                for (let row = 0; row < height; row++) gl.texSubImage2D(gl.TEXTURE_2D, 0, 0, row, width, 1, format, gl.UNSIGNED_BYTE,
                    new Uint8Array(frame.memory, plane.offset + row * plane.stride, plane.rowBytes));
            }
        }
        const token = ++nextTexture;
        textures.set(token, slot);
        diagnostics.uploads++;
        diagnostics.uploadedBytes += slot.bytes;
        return JSON.stringify({ token, name: slot.name, format: internal });
    } catch (error) {
        if (slot) { gl.deleteTexture(slot.texture); registry.textures[slot.name] = null; }
        throw error;
    }
}

export function releaseTexture(token) {
    const slot = textures.get(token);
    if (!slot) return;
    textures.delete(token);
    if (slot.generation !== slot.state.generation || slot.gl.isContextLost()) {
        slot.registry.textures[slot.name] = null;
        return;
    }
    if (idleBytes + slot.bytes <= maximumIdleBytes) {
        const idle = slot.state.idle.get(slot.key) || [];
        idle.push(slot);
        slot.state.idle.set(slot.key, idle);
        idleBytes += slot.bytes;
    } else {
        slot.gl.deleteTexture(slot.texture);
        slot.registry.textures[slot.name] = null;
    }
}
