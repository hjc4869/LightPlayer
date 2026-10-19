const sources = new Map();
const requests = new Map();
let nextSourceId = 1;
let nextRequestId = 1;

export function create(content) {
    const script = new Function(`${content}\n
        return {
            getSourceInfo: () => typeof getSourceInfo === "function" ? getSourceInfo() : [],
            lookup: (title, artist, signal) => lookupLrcAsync(title, artist, signal),
            download: (candidate, signal) => downloadLrcAsync(candidate, signal)
        };
    `)();
    const sourceId = nextSourceId++;
    sources.set(sourceId, { script, pending: Promise.resolve(), requests: new Set() });
    return sourceId;
}

function getSource(sourceId) {
    const source = sources.get(sourceId);
    if (!source) {
        throw new Error("Lyric source has been disposed.");
    }
    return source;
}

export function getSourceInfo(sourceId) {
    return JSON.stringify(getSource(sourceId).script.getSourceInfo());
}

export function beginRequest(sourceId) {
    const source = getSource(sourceId);
    const requestId = nextRequestId++;
    requests.set(requestId, { source, controller: new AbortController() });
    source.requests.add(requestId);
    return requestId;
}

async function invoke(requestId, operation) {
    const { source, controller } = requests.get(requestId);
    const signal = controller.signal;
    let onAbort;
    const aborted = new Promise((resolve, reject) => {
        onAbort = () => reject(signal.reason);
        signal.addEventListener("abort", onAbort, { once: true });
        if (signal.aborted) {
            onAbort();
        }
    });
    const result = source.pending.then(async () => {
        signal.throwIfAborted();
        const timeout = setTimeout(() => controller.abort(
            new DOMException("Lyric source timed out after 120 seconds.", "TimeoutError")), 120_000);
        try {
            return JSON.stringify(await Promise.race([
                Promise.resolve().then(() => operation(source.script, signal)),
                aborted
            ]));
        } finally {
            clearTimeout(timeout);
        }
    });
    source.pending = result.then(() => undefined, () => undefined);
    try {
        return await Promise.race([result, aborted]);
    } finally {
        signal.removeEventListener("abort", onAbort);
    }
}

export function lookup(requestId, title, artist) {
    return invoke(requestId, (script, signal) => script.lookup(title, artist, signal));
}

export function download(requestId, candidateJson) {
    return invoke(requestId, (script, signal) => script.download(JSON.parse(candidateJson), signal));
}

export function cancel(requestId) {
    requests.get(requestId)?.controller.abort();
}

export function endRequest(requestId) {
    const request = requests.get(requestId);
    if (request) {
        request.source.requests.delete(requestId);
        requests.delete(requestId);
    }
}

export function dispose(sourceId) {
    const source = sources.get(sourceId);
    if (source) {
        for (const requestId of source.requests) {
            cancel(requestId);
        }
        sources.delete(sourceId);
    }
}