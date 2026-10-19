// Origin Private File System (OPFS) backing store for cached media blocks.
//
// The .NET WebAssembly runtime only exposes an in-memory filesystem (MEMFS) to System.IO, so
// writing the block cache through File.WriteAllBytes would keep every block inside the 32-bit wasm
// heap. OPFS is the only browser storage that behaves like a real filesystem and lives outside that
// heap, and it is reachable exclusively from JavaScript.
//
// Every function resolves instead of throwing: the cache is an optimization and must never break
// playback when storage is unavailable, denied, or full.

const directories = new Map();

function key(storeId, name) {
    return `${storeId}\0${name}`;
}

function directoryFor(storeId) {
    return directories.get(storeId) ?? null;
}

export async function open(storeId) {
    try {
        const storage = globalThis.navigator?.storage;
        if (!storage?.getDirectory) {
            return false;
        }

        const root = await storage.getDirectory();
        const parent = await root.getDirectoryHandle('lightplayer-media-blocks', { create: true });
        const safeId = String(storeId).replaceAll('/', '_').replaceAll('\\', '_');
        const directory = await parent.getDirectoryHandle(safeId, { create: true });
        directories.set(storeId, directory);
        return true;
    } catch {
        directories.delete(storeId);
        return false;
    }
}

// Returns "name\tsize\tlastModified" per line so the managed side can rebuild its LRU index
// without a JSON serializer context (the app is published trimmed).
export async function list(storeId) {
    const directory = directoryFor(storeId);
    if (!directory) {
        return "";
    }

    const lines = [];
    try {
        for await (const [name, handle] of directory.entries()) {
            if (handle.kind !== "file") {
                continue;
            }

            try {
                const file = await handle.getFile();
                lines.push(`${name}\t${file.size}\t${Math.trunc(file.lastModified)}`);
            } catch {
                // The entry disappeared or is unreadable; leave it out of the index.
            }
        }
    } catch {
        return "";
    }

    return lines.join("\n");
}

// Blocks are handed to .NET in two steps: source-generated interop cannot marshal an array out of a
// promise, so the file is read asynchronously into `pending` and then copied out synchronously.
const pending = new Map();

export async function readBegin(storeId, name, expectedLength = -1) {
    const pendingKey = key(storeId, name);
    pending.delete(pendingKey);
    const directory = directoryFor(storeId);
    if (!directory) {
        return -1;
    }

    try {
        const handle = await directory.getFileHandle(name);
        const file = await handle.getFile();
        if (!Number.isSafeInteger(file.size) || file.size < 0 || file.size > 2147483647) return -1;
        if (expectedLength >= 0 && file.size !== expectedLength) return file.size;
        const data = new Uint8Array(await file.arrayBuffer());
        pending.set(pendingKey, data);
        return data.length;
    } catch {
        // Missing or unreadable block; the caller falls back to the network.
        pending.delete(pendingKey);
        return -1;
    }
}

export async function readEnd(storeId, name, buffer) {
    const pendingKey = key(storeId, name);
    const data = pending.get(pendingKey);
    pending.delete(pendingKey);
    try {
        if (data) {
            buffer.set(data);
        }
    } finally {
        // An ArraySegment memory view pins the managed array; without this the pin is only
        // released when the JavaScript garbage collector happens to reclaim the view.
        buffer.dispose();
    }
}

export async function readDiscard(storeId, name) {
    pending.delete(key(storeId, name));
}

export async function write(storeId, name, data) {
    const directory = directoryFor(storeId);
    if (!directory) {
        return false;
    }

    try {
        const handle = await directory.getFileHandle(name, { create: true });
        if (handle.createWritable) {
            const writable = await handle.createWritable();
            await writable.write(data);
            await writable.close();
        } else {
            // Safari exposed synchronous access handles before createWritable.
            const access = await handle.createSyncAccessHandle();
            try {
                access.truncate(0);
                access.write(data, { at: 0 });
                access.flush();
            } finally {
                access.close();
            }
        }

        return true;
    } catch {
        // A half-written file would be served as a corrupt block later on.
        await remove(storeId, name);
        return false;
    }
}

export async function remove(storeId, name) {
    const directory = directoryFor(storeId);
    if (!directory) {
        return;
    }

    try {
        await directory.removeEntry(name);
    } catch {
        // Already gone.
    }
}
