import { getItem, removeItem, setItem } from './browser-storage.js';

const PLAYBACK_QUEUE_KEY = 'lightplayer.shell.playbackQueue';
const PLAYBACK_PROGRESS_KEY = 'lightplayer.shell.playbackProgress';
const PROGRESS_CHECKPOINT_INTERVAL_MS = 1000;

let latestSnapshot = null;
let playbackInProgress = false;
let registered = false;
let progressTimer = 0;
let lastProgressCheckpoint = 0;

function parseJson(json) {
    try {
        return json ? JSON.parse(json) : null;
    } catch {
        return null;
    }
}

function createProgressCheckpoint() {
    const index = latestSnapshot?.currentIndex;
    const item = Number.isInteger(index) ? latestSnapshot?.items?.[index] : null;
    if (!item) {
        return null;
    }

    return {
        currentIndex: index,
        filePath: item.filePath,
        startTimeMs: item.startTimeMs ?? null,
        positionMs: latestSnapshot.positionMs,
        durationMs: latestSnapshot.durationMs,
    };
}

function applyProgressCheckpoint(snapshot, checkpoint) {
    const item = snapshot?.items?.[checkpoint?.currentIndex];
    if (!item
        || snapshot.currentIndex !== checkpoint.currentIndex
        || item.filePath !== checkpoint.filePath
        || (item.startTimeMs ?? null) !== (checkpoint.startTimeMs ?? null)) {
        return false;
    }

    if (Number.isFinite(checkpoint.positionMs)) {
        snapshot.positionMs = checkpoint.positionMs;
    }
    if (Number.isFinite(checkpoint.durationMs)) {
        snapshot.durationMs = checkpoint.durationMs;
    }
    return true;
}

function checkpointProgress() {
    progressTimer = 0;
    lastProgressCheckpoint = Date.now();
    const checkpoint = createProgressCheckpoint();
    if (checkpoint) {
        void setItem(PLAYBACK_PROGRESS_KEY, JSON.stringify(checkpoint));
    }
}

function scheduleProgressCheckpoint(immediate = false) {
    if (!latestSnapshot) {
        return;
    }

    const delay = immediate
        ? 0
        : Math.max(0, lastProgressCheckpoint + PROGRESS_CHECKPOINT_INTERVAL_MS - Date.now());
    if (delay === 0) {
        if (progressTimer) {
            globalThis.clearTimeout(progressTimer);
        }
        checkpointProgress();
    } else if (!progressTimer) {
        progressTimer = globalThis.setTimeout(checkpointProgress, delay);
    }
}

function onBeforeUnload(event) {
    event.preventDefault();
    event.returnValue = '';
}

function setPlaybackInProgress(value) {
    const next = Boolean(value);
    if (playbackInProgress === next) {
        return;
    }

    playbackInProgress = next;
    if (!registered) {
        return;
    }

    if (playbackInProgress) {
        globalThis.addEventListener('beforeunload', onBeforeUnload);
    } else {
        globalThis.removeEventListener('beforeunload', onBeforeUnload);
    }
}

export function reloadAfterIndexDatabaseReset() {
    setPlaybackInProgress(false);
    globalThis.location.reload();
}

export async function registerBrowserLifecycle() {
    if (registered) {
        return;
    }

    const [snapshotJson, progressJson] = await Promise.all([
        getItem(PLAYBACK_QUEUE_KEY),
        getItem(PLAYBACK_PROGRESS_KEY),
    ]);
    latestSnapshot = parseJson(snapshotJson);
    if (applyProgressCheckpoint(latestSnapshot, parseJson(progressJson))) {
        await setItem(PLAYBACK_QUEUE_KEY, JSON.stringify(latestSnapshot));
    }

    registered = true;
    globalThis.addEventListener('pagehide', () => scheduleProgressCheckpoint(true));
    globalThis.document.addEventListener('visibilitychange', () => {
        if (globalThis.document.visibilityState === 'hidden') {
            scheduleProgressCheckpoint(true);
        }
    });

    if (playbackInProgress) {
        globalThis.addEventListener('beforeunload', onBeforeUnload);
    }
}

export async function updateSnapshot(snapshotJson, isPlaying) {
    setPlaybackInProgress(isPlaying);

    if (!snapshotJson) {
        latestSnapshot = null;
        await Promise.all([
            removeItem(PLAYBACK_QUEUE_KEY),
            removeItem(PLAYBACK_PROGRESS_KEY),
        ]);
        return;
    }

    try {
        latestSnapshot = JSON.parse(snapshotJson);
        const checkpoint = createProgressCheckpoint();
        await Promise.all([
            setItem(PLAYBACK_QUEUE_KEY, snapshotJson),
            checkpoint
                ? setItem(PLAYBACK_PROGRESS_KEY, JSON.stringify(checkpoint))
                : removeItem(PLAYBACK_PROGRESS_KEY),
        ]);
        lastProgressCheckpoint = Date.now();
    } catch (error) {
        console.error('Could not persist playback state.', error);
    }
}

export async function updateProgress(isPlaying, positionMs, durationMs) {
    setPlaybackInProgress(isPlaying);
    if (latestSnapshot === null) {
        return;
    }

    if (Number.isFinite(positionMs)) {
        latestSnapshot.positionMs = positionMs;
    }
    if (Number.isFinite(durationMs)) {
        latestSnapshot.durationMs = durationMs;
    }

    scheduleProgressCheckpoint(!isPlaying);
}