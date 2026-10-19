import createSqlite from "./native/sqlite.mjs";
import { SqliteEngine } from "./engine.js";
import { SqliteStorage } from "./storage.js";
import { configureOrdinalCasing } from "./functions.js";

const startupMessages = [];
globalThis.onmessage = event => startupMessages.push(event);
const module = await createSqlite();
const engine = new SqliteEngine(module);
const storage = new SqliteStorage(module);
module.storage = storage;
const databases = new Map();
const sessions = new Map();
const requests = new Map();
const openingReferences = new Map();
let openingSessions = 0;
const deletions = new Map();
let indexReset = null;
let nextSession = 0;

function reply(request, result, error) {
    requests.delete(request.id);
    postMessage({ version: 1, id: request.id, result, error: error ? {
        name: error.name, message: error.message, stack: error.stack, code: error.code ?? 0,
    } : null });
}

function canceled() { return new DOMException("The SQLite request was canceled.", "AbortError"); }

async function open(request) {
    const path = request.path;
    if (typeof path !== "string" || !path || path.length > 4096 || path.includes("\0")) throw new Error("Invalid database path.");
    if (indexReset) throw new Error("The local SQLite indexes are being reset.");
    if (deletions.has(path)) throw new Error("The SQLite source index is being deleted.");
    if (sessions.size + openingSessions >= 64) throw new Error("The SQLite session limit was reached.");
    const key = path === ":memory:" ? `memory:${++nextSession}` : path;
    let opening = databases.get(key);
    if (!opening) {
        if (databases.size >= 32) throw new Error("The SQLite database limit was reached.");
        opening = (async () => {
            const files = path === ":memory:" ? null : await storage.acquire(path);
            let database = 0;
            try {
                database = engine.open(files?.nativeName ?? ":memory:");
                const check = engine.execute(database, "PRAGMA quick_check;", [], "scalar");
                if (check.value?.text !== "ok") throw new Error("The stored SQLite database failed its integrity check.");
                return { key, database, files, sessions: new Set(), queue: [], owner: null, scheduled: false };
            } catch (error) {
                try { if (database) engine.close(database); } finally { files?.close(); }
                throw error;
            }
        })();
        databases.set(key, opening);
        opening.catch(() => { if (databases.get(key) === opening) databases.delete(key); });
    }
    openingSessions++;
    openingReferences.set(key, (openingReferences.get(key) ?? 0) + 1);
    let record;
    try {
        record = await opening;
        if (request.canceled) throw canceled();
        const id = String(++nextSession);
        const session = { id, record, transaction: null, generation: 0, cursor: null, exportOffset: null, closed: false };
        sessions.set(id, session);
        record.sessions.add(id);
        return { connection: id };
    } finally {
        openingSessions--;
        const remaining = openingReferences.get(key) - 1;
        if (remaining) openingReferences.set(key, remaining);
        else openingReferences.delete(key);
        if (record) closeUnused(record);
    }
}

function closeUnused(record) {
    if (record.sessions.size || openingReferences.has(record.key) || record.closed) return;
    record.closed = true;
    try {
        try { engine.close(record.database); }
        finally { record.files?.close(); databases.delete(record.key); }
        record.onIdle?.resolve();
    } catch (error) { record.onIdle?.reject(error); throw error; }
}

async function deleteDatabase(path) {
    if (typeof path !== "string" || !path || path.length > 4096 || path.includes("\0")) throw new Error("Invalid database path.");
    if (indexReset) throw new Error("The local SQLite indexes are being reset.");
    if (deletions.has(path)) return deletions.get(path);
    const operation = (async () => {
        const opening = databases.get(path);
        if (opening) {
            const record = await opening;
            if (!record.closed) {
                const idle = new Promise((resolve, reject) => { record.onIdle = { resolve, reject }; });
                closeUnused(record);
                await idle;
            }
        }
        await storage.remove(path);
        return {};
    })();
    deletions.set(path, operation);
    try { return await operation; }
    finally { deletions.delete(path); }
}

async function resetIndexes() {
    if (indexReset) return indexReset;
    if (databases.size || openingSessions || deletions.size)
        throw new Error("Close all SQLite connections before resetting the local indexes.");
    indexReset = storage.resetIndexes();
    try {
        await indexReset;
        return {};
    } finally { indexReset = null; }
}

function close(session) {
    const record = session.record;
    if (session.cursor) engine.finish(session.cursor);
    session.cursor = null;
    if (record.owner === session.id) {
        if (engine.inTransaction(record.database)) engine.execute(record.database, "ROLLBACK;");
        record.owner = null;
    }
    session.closed = true;
    sessions.delete(session.id);
    record.sessions.delete(session.id);
    closeUnused(record);
}

function execute(session, request) {
    const record = session.record;
    const database = record.database;
    if (session.closed) throw new Error("The SQLite connection is closed.");
    if (request.operation === "close") { close(session); return {}; }
    if (request.operation === "read") {
        if (!session.cursor) throw new Error("The SQLite reader is closed.");
        const result = engine.read(session.cursor);
        if (result.done) session.cursor = null;
        return result;
    }
    if (request.operation === "finish") {
        if (session.cursor) engine.finish(session.cursor);
        session.cursor = null;
        return {};
    }
    if (request.operation === "rollback") {
        if (session.cursor) engine.finish(session.cursor);
        session.cursor = null;
        if (session.transaction !== null && engine.inTransaction(database)) engine.execute(database, "ROLLBACK;");
        session.transaction = null;
        return {};
    }
    if (session.cursor) throw new Error("Finish the active SQLite reader before executing another command.");
    if (request.operation === "exportEnd") { session.exportOffset = null; return {}; }
    if (request.operation === "exportRead") {
        if (session.exportOffset === null || !record.files) throw new Error("The SQLite export is not active.");
        const buffer = new Uint8Array(65536);
        const count = record.files.main.read(buffer, { at: session.exportOffset });
        session.exportOffset += count;
        let text = "";
        for (let index = 0; index < count; index += 8192)
            text += String.fromCharCode(...buffer.subarray(index, Math.min(count, index + 8192)));
        return { data: btoa(text), done: count === 0 };
    }
    if (session.exportOffset !== null) throw new Error("Finish the SQLite export before executing another command.");
    if (request.operation === "begin") {
        if (session.transaction !== null) throw new Error("A SQLite transaction is already active.");
        engine.execute(database, "BEGIN IMMEDIATE;");
        session.transaction = String(++session.generation);
        record.owner = session.id;
        return { transaction: session.transaction };
    }
    if ((request.transaction ?? null) !== session.transaction) throw new Error("The SQLite transaction identity is obsolete or missing.");
    if (request.operation === "commit") {
        if (session.transaction === null) throw new Error("No SQLite transaction is active.");
        engine.execute(database, "COMMIT;");
        session.transaction = null;
        return {};
    }
    if (request.operation === "exportBegin") {
        if (session.transaction !== null || !record.files) throw new Error("Export requires a committed persistent database.");
        session.exportOffset = 0;
        record.owner = session.id;
        return {};
    }
    if (!["execute", "scalar", "reader"].includes(request.operation)) throw new Error("Unsupported SQLite operation.");
    const result = engine.execute(database, request.sql, request.parameters, request.operation);
    if (request.operation === "reader") {
        session.cursor = result;
        record.owner = session.id;
        const batch = engine.read(result);
        if (batch.done) session.cursor = null;
        return batch;
    }
    if (engine.inTransaction(database) !== (session.transaction !== null)) {
        if (engine.inTransaction(database)) engine.execute(database, "ROLLBACK;");
        session.transaction = null;
        throw new Error("SQL text must not change transaction ownership; use the transaction operations.");
    }
    return result;
}

function schedule(record) {
    if (record.scheduled) return;
    record.scheduled = true;
    setTimeout(() => {
        record.scheduled = false;
        const index = record.queue.findIndex(request => request.canceled || !record.owner ||
            request.connection === record.owner || request.operation === "close");
        if (index < 0) return;
        const [request] = record.queue.splice(index, 1);
        const session = sessions.get(request.connection);
        try {
            if (request.canceled) throw canceled();
            if (!session) throw new Error("Unknown SQLite connection.");
            reply(request, execute(session, request));
        } catch (error) {
            if (session?.cursor?.closed) session.cursor = null;
            if (session?.transaction && !engine.inTransaction(record.database)) session.transaction = null;
            reply(request, null, error);
        } finally {
            if (session && record.owner === session.id && !session.transaction && !session.cursor && session.exportOffset === null)
                record.owner = null;
            if (record.queue.length) schedule(record);
        }
    }, 0);
}

globalThis.onmessage = event => {
    const request = event.data;
    if (request?.version !== 1 || !Number.isSafeInteger(request.id)) return;
    if (request.operation === "configure") {
        if (databases.size || sessions.size) throw new Error("Cannot change SQLite casing while databases are open.");
        configureOrdinalCasing(request.casing);
        return;
    }
    if (request.operation === "cancel") {
        const pending = requests.get(request.target);
        if (pending) {
            pending.canceled = true;
            const session = sessions.get(pending.connection);
            if (session) schedule(session.record);
        }
        return;
    }
    if (requests.size >= 48) { reply(request, null, new Error("The SQLite worker request budget was exceeded.")); return; }
    requests.set(request.id, request);
    if (request.operation === "resetIndexes") {
        resetIndexes().then(result => reply(request, result), error => reply(request, null, error));
        return;
    }
    if (request.operation === "delete") {
        deleteDatabase(request.path).then(result => reply(request, result), error => reply(request, null, error));
        return;
    }
    if (request.operation === "open") {
        open(request).then(result => reply(request, result), error => reply(request, null, error));
        return;
    }
    const session = sessions.get(request.connection);
    if (!session) { reply(request, null, new Error("Unknown SQLite connection.")); return; }
    session.record.queue.push(request);
    schedule(session.record);
};

for (const event of startupMessages) globalThis.onmessage(event);
startupMessages.length = 0;
