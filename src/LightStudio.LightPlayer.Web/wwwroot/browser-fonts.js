// The CJK fallback faces are ~33 MB together. Embedded in the application assembly they pushed it
// past the per-file size limit of the static hosts this client is deployed to, so they are ordinary
// files fetched here instead. main.js starts the download before the runtime boots, which puts it
// in parallel with the .NET download rather than after it.

const sources = [
    "fonts/NotoSansCJKsc-Regular.otf",
    "fonts/NotoSansCJKsc-Bold.otf",
];

let pending = null;
let faces = null;

async function fetchAll() {
    return Promise.all(sources.map(async (url) => {
        const response = await fetch(new URL(url, import.meta.url));
        if (!response.ok) {
            throw new Error(`${url} responded with ${response.status}.`);
        }

        return new Uint8Array(await response.arrayBuffer());
    }));
}

/**
 * Starts the download and resolves with the number of faces. Repeated calls join the first request,
 * so JavaScript and .NET can both ask for the fonts without fetching them twice.
 */
export function beginLoad() {
    pending ??= fetchAll().then((loaded) => {
        faces = loaded;
        return loaded.length;
    });

    return pending;
}

export function size(index) {
    return faces ? faces[index].byteLength : 0;
}

export function copy(index, buffer) {
    try {
        if (faces) {
            buffer.set(faces[index]);
        }
    } finally {
        // An ArraySegment memory view pins the managed array; without this the pin is only
        // released when the JavaScript garbage collector happens to reclaim the view.
        buffer.dispose();
    }
}

/** Drops the JavaScript copies once the bytes are in managed memory. */
export function release() {
    faces = null;
}
