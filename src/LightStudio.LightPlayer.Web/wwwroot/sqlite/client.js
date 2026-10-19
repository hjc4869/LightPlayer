let worker;
let generation = 0;
let sequence = 0;
const pending = new Map();
const connections = new Map();
let pendingBytes = 0;
let ordinalCasing;

export function configureOrdinalCasing(pairs) {
    if (worker) throw new Error("Cannot configure a running SQLite worker.");
    ordinalCasing = Array.from(pairs);
}

export function nextTurn() {
    return new Promise(resolve => {
        const channel = new MessageChannel();
        channel.port1.onmessage = () => {
            channel.port1.close();
            channel.port2.close();
            resolve();
        };
        channel.port2.postMessage(0);
    });
}

function fail(error) {
    worker?.terminate();
    worker = null;
    connections.clear();
    for (const operation of pending.values()) {
        clearTimeout(operation.timer);
        operation.reject(error);
    }
    pending.clear();
    pendingBytes = 0;
}

function getWorker() {
    if (worker) return worker;
    if (!ordinalCasing) throw new Error("The managed SQLite casing map is not initialized.");
    const current = new Worker(new URL("./worker.js", import.meta.url), { type: "module", name: "lightplayer-sqlite" });
    const epoch = ++generation;
    current.onmessage = event => {
        if (worker !== current) return;
        const message = event.data;
        if (message?.version !== 1) { fail(new Error("Invalid SQLite worker protocol version.")); return; }
        const operation = pending.get(message.id);
        if (!operation) return;
        pending.delete(message.id);
        pendingBytes -= operation.bytes;
        clearTimeout(operation.timer);
        if (message.error) {
            const error = new Error(message.error.message);
            error.name = message.error.name;
            error.stack = message.error.stack ?? error.stack;
            error.code = message.error.code;
            operation.reject(error);
        } else {
            const result = message.result ?? {};
            if (operation.kind === "open") {
                const identity = `${epoch}:${result.connection}`;
                connections.set(identity, { epoch, native: result.connection });
                result.connection = identity;
            }
            if (operation.kind === "close") connections.delete(operation.connection);
            operation.resolve(result);
        }
    };
    current.onerror = event => {
        if (worker === current) fail(new Error(`SQLite worker failed: ${event.message} (${event.filename}:${event.lineno})`));
    };
    current.onmessageerror = () => {
        if (worker === current) fail(new Error("SQLite worker message deserialization failed."));
    };
    worker = current;
    current.postMessage({ version: 1, id: 0, operation: "configure", casing: ordinalCasing });
    return current;
}

export function request(requestId, json) {
    if (!Number.isSafeInteger(requestId) || requestId <= 0) return Promise.reject(new Error("Invalid SQLite request identity."));
    const bytes = json.length * 2;
    if (bytes > 4 * 1024 * 1024) return Promise.reject(new Error("The SQLite command exceeds its byte budget."));
    const request = JSON.parse(json);
    const control = ["close", "commit", "rollback", "finish", "exportEnd", "delete", "resetIndexes"].includes(request.operation);
    if (pending.size >= (control ? 48 : 32)) return Promise.reject(new Error("The SQLite request budget was exceeded."));
    if (pendingBytes + bytes > (control ? 17 : 16) * 1024 * 1024) return Promise.reject(new Error("The SQLite queued byte budget was exceeded."));
    if (pending.has(requestId)) return Promise.reject(new Error("Duplicate SQLite request identity."));
    const connection = request.connection;
    if (!["open", "delete", "resetIndexes"].includes(request.operation)) {
        const identity = connections.get(connection);
        if (!identity || identity.epoch !== generation || !worker) {
            if (["close", "finish", "rollback", "exportEnd"].includes(request.operation)) return Promise.resolve("{}");
            return Promise.reject(new Error("The SQLite connection belongs to a closed worker."));
        }
        request.connection = identity.native;
    }
    const target = getWorker();
    return new Promise((resolve, reject) => {
        const timer = setTimeout(() => fail(new Error("SQLite worker request timed out; open new connections to recover.")), 45000);
        pending.set(requestId, { resolve, reject, timer, kind: request.operation, connection, bytes });
        pendingBytes += bytes;
        try { target.postMessage({ ...request, version: 1, id: requestId }); }
        catch (error) { fail(error); }
    }).then(result => JSON.stringify(result));
}

export function cancel(requestId) {
    if (pending.has(requestId)) worker?.postMessage({ version: 1, id: ++sequence, operation: "cancel", target: requestId });
}

export function shutdown() {
    fail(new Error("The SQLite worker was shut down."));
}

export function isConnectionAlive(identity) {
    return worker !== null && worker !== undefined && connections.get(identity)?.epoch === generation;
}
