// Library roots retain native directory handles or session-local directory-input selections.
// Temporary roots persist only their identity and name, and require reselection after a reload.

const DatabaseName = "LightPlayerFiles";
const FolderStore = "folders";

/**
 * rootId -> { handle, name, state } for every persisted library folder, plus the transient
 * single-file roots adopted by adoptFiles(), which carry { file } and kind === "file".
 */
const roots = new Map();

/** Resolved directory handles, keyed by "rootId\0relative/path". */
const directories = new Map();

/** Open files, keyed by the id handed to the managed stream. */
const files = new Map();

/** Staged reads, keyed by file id, awaiting a readEnd copy. */
const pending = new Map();
const listings = new Map();

let nextFileId = 1;
let nextListingId = 1;

function openDatabase() {
    return new Promise((resolve, reject) => {
        const request = globalThis.indexedDB.open(DatabaseName, 1);
        request.onupgradeneeded = () => request.result.createObjectStore(FolderStore);
        request.onsuccess = () => resolve(request.result);
        request.onerror = () => reject(request.error);
    });
}

function request(store, operation) {
    return new Promise((resolve, reject) => {
        const pendingRequest = operation(store);
        pendingRequest.onsuccess = () => resolve(pendingRequest.result);
        pendingRequest.onerror = () => reject(pendingRequest.error);
    });
}

async function withStore(mode, operation) {
    const database = await openDatabase();
    try {
        const transaction = database.transaction(FolderStore, mode);
        const completed = new Promise((resolve, reject) => {
            transaction.oncomplete = resolve;
            transaction.onabort = () => reject(transaction.error ?? new Error("Folder storage transaction was aborted."));
            transaction.onerror = () => reject(transaction.error);
        });
        const [result] = await Promise.all([operation(transaction.objectStore(FolderStore)), completed]);
        return result;
    } finally {
        database.close();
    }
}

export function isSupported() {
    return (supportsPersistentFolders() || "webkitdirectory" in document.createElement("input"))
        && typeof globalThis.indexedDB !== "undefined";
}

export function supportsPersistentFolders() {
    return typeof globalThis.showDirectoryPicker === "function";
}

async function permissionState(handle, prompt) {
    try {
        let state = await handle.queryPermission({ mode: "read" });
        if (state === "prompt" && prompt) {
            state = await handle.requestPermission({ mode: "read" });
        }
        return state;
    } catch (error) {
        console.error("Unable to query library folder access.", error);
        return "denied";
    }
}

async function requestReadPermission(handle) {
    try {
        return await handle.requestPermission({ mode: "read" });
    } catch (error) {
        console.error("Unable to request library folder access.", error);
        return "denied";
    }
}

// Opens the browser's directory picker and keeps the chosen folder for later sessions.
// Returns "rootId\tdisplayName\ttemporary", or "" when nothing was picked or kept.
export async function pickDirectory(startIn, pickerId) {
    if (!isSupported()) {
        return "";
    }

    let handle;
    const temporary = !supportsPersistentFolders();
    try {
        handle = temporary
            ? await pickSessionDirectory()
            : await globalThis.showDirectoryPicker({ mode: "read", startIn, id: pickerId });
    } catch (error) {
        if (error.name !== "AbortError") console.error("Unable to pick a library folder.", error);
        return "";
    }

    if (!handle || (!temporary && await permissionState(handle, true) !== "granted")) {
        return "";
    }

    const rootId = newRootId();
    try {
        const entry = temporary ? { name: handle.name, temporary: true } : { handle, name: handle.name };
        await withStore("readwrite", store => request(store, s => s.put(entry, rootId)));
    } catch (error) {
        console.error("Unable to save the library folder.", error);
        return "";
    }

    roots.set(rootId, { handle, name: handle.name, state: "granted", temporary });
    return `${rootId}\t${handle.name}\t${temporary}`;
}

function newRootId() {
    return `${Date.now().toString(36)}${Math.random().toString(36).substring(2, 10)}`;
}

// Opens the browser's file picker and adopts what the user chose. Extensions are comma-separated
// ".mp3,.flac" lists. Unlike the folder picker this needs no persistence and no
// showDirectoryPicker, so browsers without the File System Access API fall back to a plain file
// input and can open media all the same. Returns "rootId\tname" per line.
export async function pickFiles(description, audioExtensions, videoExtensions, imageExtensions) {
    const audio = audioExtensions ? audioExtensions.split(",") : [];
    const video = videoExtensions ? videoExtensions.split(",") : [];
    const image = imageExtensions ? imageExtensions.split(",") : [];
    if (typeof globalThis.showOpenFilePicker === "function") {
        const accept = {};
        if (audio.length > 0) {
            accept["audio/*"] = audio;
        }

        if (video.length > 0) {
            accept["video/*"] = video;
        }

        if (image.length > 0) {
            accept["image/*"] = image;
        }

        try {
            // `id` makes the browser reopen where this picker was last used.
            return adoptFiles(await globalThis.showOpenFilePicker({
                id: "lightplayer-open",
                multiple: true,
                types: [{ description, accept }],
            }));
        } catch (error) {
            if (error.name !== "AbortError") {
                console.error("Unable to open the media file picker.", error);
            }
            return "";
        }
    }

    try {
        return adoptFiles(await pickWithInput(
            [...audio, ...video, ...image, "audio/*", "video/*", "image/*"].join(",")));
    } catch (error) {
        if (error.name !== "AbortError") {
            console.error("Unable to open the media file picker.", error);
        }
        return "";
    }
}

function pickWithInput(accept, directory = false) {
    return new Promise((resolve, reject) => {
        const input = document.createElement("input");
        input.type = "file";
        input.multiple = true;
        input.accept = accept;
        input.webkitdirectory = directory;
        input.hidden = true;

        const finish = selected => {
            input.remove();
            resolve(selected);
        };
        input.addEventListener("cancel", () => finish([]), { once: true });
        input.addEventListener("change", () => finish(input.files ?? []), { once: true });
        document.body.appendChild(input);
        try {
            input.click();
        } catch (error) {
            input.remove();
            reject(error);
        }
    });
}

class SessionDirectory {
    constructor(name) {
        this.name = name;
        this.kind = "directory";
        this.children = new Map();
    }

    entries() { return this.children.entries(); }

    async getDirectoryHandle(name) { return this.child(name, "directory"); }

    async getFileHandle(name) { return this.child(name, "file"); }

    child(name, kind) {
        const entry = this.children.get(name);
        if (entry?.kind !== kind) throw new DOMException("The selected entry is unavailable.", "NotFoundError");
        return entry;
    }
}

async function pickSessionDirectory(expectedName) {
    const selected = await pickWithInput("", true);
    let root = null;
    let count = 0;
    for (const file of selected) {
        const segments = file.webkitRelativePath.split("/");
        if (segments.length < 2 || segments.some(segment => !segment || segment === "." || segment === ".."))
            throw new Error("The selected folder contains an invalid relative path.");
        const name = segments.shift();
        if (expectedName && name !== expectedName)
            throw new Error(`Select the saved library folder "${expectedName}".`);
        root ??= new SessionDirectory(name);
        if (root.name !== name) throw new Error("Select one library folder at a time.");
        let directory = root;
        for (const segment of segments.slice(0, -1)) {
            if (!directory.children.has(segment)) directory.children.set(segment, new SessionDirectory(segment));
            directory = directory.child(segment, "directory");
        }
        directory.children.set(segments.at(-1), { kind: "file", getFile: () => file });
        if (++count % 128 === 0) await new Promise(resolve => setTimeout(resolve, 0));
    }
    return root;
}

// Adopts files the page was handed directly (a file-handler launch, a share, or a file picker)
// rather than found inside a folder the user picked. Each one becomes a root of its own so the
// managed side can name it with the same fsa:// identifier as library media. Entries are
// FileSystemFileHandle or File objects; neither is persisted, because the grant that came with the
// launch dies with the page and a stored handle would only ever prompt again.
// Returns "rootId\tname" per line.
export function adoptFiles(entries) {
    const lines = [];
    for (const entry of entries) {
        if (!entry) {
            continue;
        }

        const rootId = newRootId();
        const isHandle = typeof entry.getFile === "function";
        roots.set(rootId, {
            kind: "file",
            name: entry.name,
            state: "granted",
            handle: isHandle ? entry : null,
            file: isHandle ? null : entry,
        });
        lines.push(`${rootId}\t${entry.name}`);
    }

    return lines.join("\n");
}

async function rootFile(root) {
    root.file ??= await root.handle.getFile();
    return root.file;
}

// Re-opens every persisted folder and reports "rootId\tname\tstate\ttemporary" per line. No permission is
// requested here: that needs a user gesture, which page startup is not.
export async function restore() {
    if (!isSupported()) {
        return "";
    }

    let keys = [];
    let values = [];
    try {
        // Both requests have to be issued in the same turn: an IndexedDB transaction goes inactive
        // as soon as control returns to the event loop.
        [keys, values] = await withStore("readonly", store => Promise.all([
            request(store, s => s.getAllKeys()),
            request(store, s => s.getAll()),
        ]));
    } catch (error) {
        console.error("Unable to restore library folders.", error);
        return "";
    }

    const lines = [];
    for (let index = 0; index < keys.length; index++) {
        const rootId = String(keys[index]);
        const entry = values[index];
        if (!entry?.handle && !entry?.temporary) {
            continue;
        }

        const temporary = entry.temporary === true;
        const name = entry.name ?? entry.handle.name;
        const state = temporary ? "prompt" : await permissionState(entry.handle, false);
        roots.set(rootId, { handle: entry.handle, name, state, temporary });
        lines.push(`${rootId}\t${name}\t${state}\t${temporary}`);
    }

    return lines.join("\n");
}

// Prompts for read access to a restored folder. Must be called from a user gesture.
export async function requestAccess(rootId) {
    const root = roots.get(rootId);
    if (!root) {
        return "missing";
    }

    if (root.temporary) {
        if (root.state === "granted") return root.state;
        try {
            const handle = await pickSessionDirectory(root.name);
            if (handle) {
                clearDirectoryCache(rootId);
                root.handle = handle;
                root.state = "granted";
            }
        } catch (error) {
            if (error.name !== "AbortError") console.error("Unable to reselect the library folder.", error);
        }
    } else {
        root.state = await permissionState(root.handle, true);
    }
    return root.state;
}

// Native permission requests leave together; temporary selections need individual user gestures.
// Returns "rootId\tstate" per line for the tab-separated root ids.
export async function requestAccessMany(rootIds) {
    const ids = rootIds.split("\t").filter(id => roots.has(id));
    const temporaryId = ids.find(id => roots.get(id).temporary && roots.get(id).state !== "granted");
    const states = await Promise.all(ids.map(id => {
        const root = roots.get(id);
        return root.temporary ? (id === temporaryId ? requestAccess(id) : root.state) : requestReadPermission(root.handle);
    }));
    return ids.map((id, index) => {
        roots.get(id).state = states[index];
        return `${id}\t${states[index]}`;
    }).join("\n");
}

export async function forget(rootId) {
    roots.delete(rootId);
    clearDirectoryCache(rootId);

    try {
        await withStore("readwrite", store => request(store, s => s.delete(rootId)));
    } catch (error) {
        console.error("Unable to remove the saved library folder.", error);
    }
}

function clearDirectoryCache(rootId) {
    for (const key of [...directories.keys()]) {
        if (key.startsWith(`${rootId}\0`)) {
            directories.delete(key);
        }
    }
}

async function resolveDirectory(rootId, path) {
    const root = roots.get(rootId);
    if (!root || root.kind === "file" || root.state !== "granted" || !root.handle) {
        return null;
    }

    if (!path) {
        return root.handle;
    }

    const cacheKey = `${rootId}\0${path}`;
    const cached = directories.get(cacheKey);
    if (cached) {
        return cached;
    }

    let handle = root.handle;
    for (const segment of path.split("/")) {
        if (!segment) {
            continue;
        }

        handle = await handle.getDirectoryHandle(segment);
    }

    directories.set(cacheKey, handle);
    return handle;
}

export async function directoryExists(rootId, path) {
    try {
        return await resolveDirectory(rootId, path) !== null;
    } catch {
        return false;
    }
}

export async function beginList(rootId, path) {
    if (listings.size >= 16) throw new Error("Directory enumeration capacity exceeded.");
    const adopted = roots.get(rootId);
    let iterator;
    if (adopted?.kind === "file") {
        if (path) throw new DOMException("The selected file is not a directory.", "NotFoundError");
        iterator = (async function* () { yield [adopted.name, { kind: "file", getFile: () => rootFile(adopted) }]; })();
    } else {
        const directory = await resolveDirectory(rootId, path);
        if (!directory) throw new DOMException("The library directory is unavailable.", "NotFoundError");
        iterator = directory.entries();
    }
    if (listings.size >= 16) throw new Error("Directory enumeration capacity exceeded.");
    const id = nextListingId++;
    listings.set(id, { iterator, staged: null, busy: false });
    return id;
}

export async function readList(id) {
    const listing = listings.get(id);
    if (!listing || listing.busy) throw new Error("Directory enumeration is closed or busy.");
    listing.busy = true;
    try {
        const entries = [];
        let bytes = 32;
        while (entries.length < 128) {
            let entry = listing.staged;
            listing.staged = null;
            if (!entry) {
                const next = await listing.iterator.next();
                if (next.done) return JSON.stringify({ entries, done: true });
                const [name, handle] = next.value;
                if (name.length > 4096) throw new Error("Directory entry name exceeds the text budget.");
                const file = handle.kind === "directory" ? null : await handle.getFile();
                entry = { name, isDirectory: !file, length: String(file?.size ?? 0), lastModified: String(Math.trunc(file?.lastModified ?? 0)) };
            }
            const size = new TextEncoder().encode(JSON.stringify(entry)).length + 1;
            if (bytes + size > 65536) { listing.staged = entry; break; }
            entries.push(entry);
            bytes += size;
        }
        return JSON.stringify({ entries, done: false });
    } finally { listing.busy = false; }
}

export async function endList(id) {
    const listing = listings.get(id);
    listings.delete(id);
    await listing?.iterator.return?.();
}

// Opens a file for reading and returns "fileId\tsize\tlastModified", or "" when it cannot be read.
export async function openFile(rootId, path) {
    const separator = path.lastIndexOf("/");
    const directoryPath = separator < 0 ? "" : path.substring(0, separator);
    const name = separator < 0 ? path : path.substring(separator + 1);

    try {
        const adopted = roots.get(rootId);
        const file = adopted?.kind === "file"
            ? await rootFile(adopted)
            : await openFromDirectory(rootId, directoryPath, name);
        if (!file) {
            return "";
        }

        const fileId = nextFileId++;
        files.set(fileId, file);
        return `${fileId}\t${file.size}\t${Math.trunc(file.lastModified)}`;
    } catch {
        return "";
    }
}

async function openFromDirectory(rootId, directoryPath, name) {
    const directory = await resolveDirectory(rootId, directoryPath);
    if (!directory) {
        return null;
    }

    return await (await directory.getFileHandle(name)).getFile();
}

export async function resolveMediaFile(path) {
    if (!path.startsWith("fsa://")) throw new Error("Not a browser file identity.");
    const parts = path.slice(6).split("/");
    const rootId = parts.shift();
    const adopted = roots.get(rootId);
    const decoded = parts.map(decodeURIComponent);
    const name = decoded.pop() || "";
    const file = adopted?.kind === "file"
        ? await rootFile(adopted)
        : await openFromDirectory(rootId, decoded.join("/"), name);
    if (!file) throw new Error("The browser file is unavailable.");
    return file;
}

// Reads are handed to .NET in two steps: source-generated interop cannot marshal an array out of a
// promise, so the slice is decoded into `pending` and then copied out synchronously by readEnd.
export async function readBegin(fileId, offset, length) {
    const file = files.get(fileId);
    if (!file) {
        return -1;
    }

    try {
        const slice = file.slice(offset, offset + length);
        const data = new Uint8Array(await slice.arrayBuffer());
        pending.set(fileId, data);
        return data.length;
    } catch {
        // The file changed or was removed underneath us; the caller reports a read failure.
        pending.delete(fileId);
        return -1;
    }
}

export async function readEnd(fileId, buffer) {
    const data = pending.get(fileId);
    pending.delete(fileId);
    try {
        if (data) {
            buffer.set(data);
        }
    } finally {
        // A memory view pins the managed array until it is disposed explicitly.
        buffer.dispose();
    }
}

export async function closeFile(fileId) {
    files.delete(fileId);
    pending.delete(fileId);
}
