const rootName = "lightplayer-media-blocks";
const maximumDatabaseBytes = 2 * 1024 * 1024 * 1024;

class SessionFile {
    constructor() {
        this.bytes = new Uint8Array(0);
        this.length = 0;
    }

    getSize() { return this.length; }
    flush() { }
    close() { }
    read(target, { at }) {
        const count = Math.max(0, Math.min(target.byteLength, this.length - at));
        target.set(this.bytes.subarray(at, at + count));
        return count;
    }
    write(source, { at }) {
        this.truncate(Math.max(this.length, at + source.byteLength));
        this.bytes.set(source, at);
        return source.byteLength;
    }
    truncate(length) {
        if (length > maximumDatabaseBytes) throw new DOMException("The SQLite database exceeds its size limit.", "QuotaExceededError");
        if (length > this.bytes.length) {
            const bytes = new Uint8Array(Math.max(length, Math.min(maximumDatabaseBytes, Math.max(4096, this.bytes.length * 2))));
            bytes.set(this.bytes.subarray(0, this.length));
            this.bytes = bytes;
        } else if (length < this.length) {
            this.bytes.fill(0, length, this.length);
        }
        this.length = length;
    }
}

export class SqliteStorage {
    constructor(module) {
        this.module = module;
        this.handles = new Map();
        this.names = new Map();
        this.sessionFiles = new Map();
        this.root = null;
        this.sequence = 0;
        this.lastError = null;
    }

    async location(path) {
        this.root ??= (async () => {
            const storage = globalThis.navigator?.storage;
            if (!storage?.getDirectory) return null;
            try { return await storage.getDirectory(); }
            catch (error) {
                if (error.name !== "SecurityError" && error.name !== "NotSupportedError") throw error;
                console.warn("SQLite storage is only available for this page session.", error);
                return null;
            }
        })();
        const root = await this.root;
        if (!root) return null;
        const parent = await root.getDirectoryHandle(rootName, { create: true });
        let directory;
        let name;
        if (path === "/LightPlayer/library-v1.sqlite") {
            directory = await parent.getDirectoryHandle("library-index", { create: true });
            name = "library-v1.sqlite";
        } else {
            directory = await parent.getDirectoryHandle("sqlite-indexes", { create: true });
            const hash = new Uint8Array(await crypto.subtle.digest("SHA-256", new TextEncoder().encode(path)));
            name = Array.from(hash, value => value.toString(16).padStart(2, "0")).join("") + ".sqlite";
        }
        return { directory, name };
    }

    async remove(path) {
        if (path === "/LightPlayer/library-v1.sqlite")
            throw new Error("Source cache deletion cannot remove the primary database.");
        await this.removeFiles(path);
    }

    async resetIndexes() {
        for (const path of ["/LightPlayer/library-v1.sqlite", "/LightPlayer/photo-v1.sqlite"])
            await this.removeFiles(path);
    }

    async removeFiles(path) {
        const location = await this.location(path);
        if (!location) { this.sessionFiles.delete(path); return; }
        const { directory, name } = location;
        for (const file of [name + "-journal", name]) {
            try { await directory.removeEntry(file); }
            catch (error) { if (error.name !== "NotFoundError") throw error; }
        }
    }

    async acquire(path) {
        const location = await this.location(path);
        let files;
        if (!location) {
            files = this.sessionFiles.get(path);
            if (!files) {
                files = { main: new SessionFile(), journal: new SessionFile() };
                this.sessionFiles.set(path, files);
            }
        }
        const main = files?.main ?? await (await location.directory.getFileHandle(location.name, { create: true })).createSyncAccessHandle();
        let journal;
        try {
            if (main.getSize() > maximumDatabaseBytes) throw new Error("The SQLite database exceeds its size limit.");
            journal = files?.journal ?? await (await location.directory.getFileHandle(location.name + "-journal", { create: true })).createSyncAccessHandle();
            const nativeName = "database-" + (++this.sequence);
            const mainId = ++this.sequence;
            const journalId = ++this.sequence;
            this.handles.set(mainId, main);
            this.handles.set(journalId, journal);
            this.names.set(nativeName, mainId);
            this.names.set(nativeName + "-journal", journalId);
            return {
                nativeName,
                main,
                close: () => {
                    this.names.delete(nativeName);
                    this.names.delete(nativeName + "-journal");
                    this.handles.delete(mainId);
                    this.handles.delete(journalId);
                    try { main.close(); } finally { journal.close(); }
                },
            };
        } catch (error) {
            try { main.close(); } finally { journal?.close(); }
            throw error;
        }
    }

    open(name) { return this.names.get(name) ?? -1; }
    read(handle, pointer, length, offset) {
        try {
            const target = this.module.HEAPU8.subarray(pointer, pointer + length);
            const count = this.handles.get(handle).read(target, { at: offset });
            if (count < length) { target.fill(0, count); return 522; }
            return 0;
        } catch (error) { this.lastError = error; return 266; }
    }
    write(handle, pointer, length, offset) {
        try {
            if (offset + length > maximumDatabaseBytes) return 13;
            return this.handles.get(handle).write(this.module.HEAPU8.subarray(pointer, pointer + length), { at: offset }) === length ? 0 : 778;
        } catch (error) { this.lastError = error; return error.name === "QuotaExceededError" ? 13 : 778; }
    }
    truncate(handle, length) {
        try { this.handles.get(handle).truncate(length); return 0; }
        catch (error) { this.lastError = error; return 1546; }
    }
    sync(handle) {
        try { this.handles.get(handle).flush(); return 0; }
        catch (error) { this.lastError = error; return 1034; }
    }
    size(handle) {
        try { return this.handles.get(handle).getSize(); }
        catch (error) { this.lastError = error; return -1; }
    }
    delete(name) {
        const handle = this.open(name);
        if (handle < 0) return 0;
        const result = this.truncate(handle, 0);
        return result || this.sync(handle);
    }
    exists(name) {
        const handle = this.open(name);
        return handle < 0 ? 0 : this.size(handle) > 0 ? 1 : 0;
    }
}
