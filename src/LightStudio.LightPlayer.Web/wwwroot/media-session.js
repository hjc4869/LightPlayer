// Bridges the player to navigator.mediaSession so the OS-level media keys, notification and
// lock-screen controls drive playback in the browser head.

const SEEK_OFFSET_SECONDS = 10;
const POSITION_DRIFT_TOLERANCE_MS = 1500;

const queued = [];
let pendingResolve = null;
let registered = false;

let title = '';
let artist = '';
let album = '';
let artworkUrl = null;

let hasItem = false;
let isPlaying = false;
let positionMs = 0;
let durationMs = 0;
let pushedPositionMs = -1;
let pushedDurationMs = -1;
let pushedAtMs = 0;

let silentAudio = null;

function session() {
    return globalThis.navigator?.mediaSession ?? null;
}

function enqueue(action) {
    if (pendingResolve) {
        const resolve = pendingResolve;
        pendingResolve = null;
        resolve(action);
    } else {
        // Managed code is between waits; hold the request so the next wait returns it.
        queued.push(action);
    }
}

function seekAction(seconds) {
    return `seek:${Math.max(0, seconds).toFixed(3)}`;
}

function currentPositionSeconds() {
    return positionMs / 1000;
}

function setHandler(action, handler) {
    try {
        session().setActionHandler(action, handler);
    } catch {
        // Unsupported actions throw; the rest of the transport still works.
    }
}

/**
 * A pure Web Audio graph does not make the browser consider the page a media playback source, so
 * no OS controls appear. Looping a silent element alongside the real output is the established
 * workaround. Its samples are already silence, so it is left unmuted and at full volume: the
 * actual playback state is only "paused" when no element is potentially playing and unmuted, and a
 * declared "paused" cannot override that (Media Session 4.1).
 */
function silentTrackUrl() {
    const sampleRate = 8000;
    const frames = sampleRate;
    const bytes = new Uint8Array(44 + frames);
    const view = new DataView(bytes.buffer);
    const ascii = (offset, text) => {
        for (let i = 0; i < text.length; i++) {
            view.setUint8(offset + i, text.charCodeAt(i));
        }
    };

    ascii(0, 'RIFF');
    view.setUint32(4, 36 + frames, true);
    ascii(8, 'WAVE');
    ascii(12, 'fmt ');
    view.setUint32(16, 16, true);
    view.setUint16(20, 1, true);
    view.setUint16(22, 1, true);
    view.setUint32(24, sampleRate, true);
    view.setUint32(28, sampleRate, true);
    view.setUint16(32, 1, true);
    view.setUint16(34, 8, true);
    ascii(36, 'data');
    view.setUint32(40, frames, true);
    bytes.fill(128, 44); // Unsigned 8-bit PCM silence sits at mid-scale.
    return URL.createObjectURL(new Blob([bytes], { type: 'audio/wav' }));
}

function syncSilentTrack() {
    // The element is what the browser watches to guess whether the page is playing, so it has to
    // follow the transport exactly or the OS shows the wrong button and runs the progress bar on.
    if (!hasItem || !isPlaying) {
        silentAudio?.pause();
        return;
    }

    if (!silentAudio) {
        silentAudio = new globalThis.Audio(silentTrackUrl());
        silentAudio.loop = true;
    }

    silentAudio.play().catch(() => {
        // No user gesture yet, or autoplay refused; the OS controls simply stay hidden.
    });
}

function pushMetadata() {
    if (!globalThis.MediaMetadata) {
        return;
    }

    session().metadata = hasItem
        ? new globalThis.MediaMetadata({
            title,
            artist,
            album,
            artwork: artworkUrl ? [{ src: artworkUrl }] : [],
        })
        : null;
}

function pushPositionState() {
    const ms = session();
    if (!ms.setPositionState) {
        return;
    }

    try {
        if (!hasItem || !(durationMs > 0) || !Number.isFinite(durationMs)) {
            ms.setPositionState();
            pushedPositionMs = -1;
            pushedDurationMs = -1;
            return;
        }

        const clamped = Math.min(Math.max(positionMs, 0), durationMs);
        ms.setPositionState({
            duration: durationMs / 1000,
            position: clamped / 1000,
            // A zero rate is rejected outright; playbackState already tells the OS we are paused.
            playbackRate: 1,
        });
        pushedPositionMs = clamped;
        pushedDurationMs = durationMs;
        pushedAtMs = Date.now();
    } catch {
        // Rejected for an inconsistent timeline (a stale position after a track change); the next
        // update corrects it.
    }
}

/**
 * Every push is announced to the platform as a seek (Chromium emits an MPRIS Seeked signal for
 * each one), which makes desktop shells reset their own interpolation. So compare against where
 * the OS believes playback is, not against what was last sent, and stay quiet until they diverge.
 */
function shouldPushPosition() {
    if (durationMs !== pushedDurationMs || pushedPositionMs < 0) {
        return true;
    }

    const predicted = pushedPositionMs + (isPlaying ? Date.now() - pushedAtMs : 0);
    const drift = Math.abs(positionMs - predicted);

    // Nothing extrapolates while paused, so any move there is a real seek and worth reporting.
    return isPlaying ? drift >= POSITION_DRIFT_TOLERANCE_MS : drift > 0;
}

function sniffImageType(bytes) {
    if (bytes.length > 3 && bytes[0] === 0xff && bytes[1] === 0xd8) {
        return 'image/jpeg';
    }
    if (bytes.length > 8 && bytes[0] === 0x89 && bytes[1] === 0x50) {
        return 'image/png';
    }
    if (bytes.length > 3 && bytes[0] === 0x47 && bytes[1] === 0x49 && bytes[2] === 0x46) {
        return 'image/gif';
    }
    if (bytes.length > 12 && bytes[8] === 0x57 && bytes[9] === 0x45 && bytes[10] === 0x42 && bytes[11] === 0x50) {
        return 'image/webp';
    }
    return 'application/octet-stream';
}

export function register() {
    if (registered || !session()) {
        return false;
    }

    registered = true;
    setHandler('play', () => {
        if (!isPlaying) {
            enqueue('playpause');
        }
    });
    setHandler('pause', () => {
        if (isPlaying) {
            enqueue('playpause');
        }
    });
    setHandler('nexttrack', () => enqueue('next'));
    setHandler('previoustrack', () => enqueue('previous'));
    setHandler('seekto', details => {
        if (Number.isFinite(details?.seekTime)) {
            enqueue(seekAction(details.seekTime));
        }
    });
    setHandler('seekbackward', details => {
        enqueue(seekAction(currentPositionSeconds() - (details?.seekOffset ?? SEEK_OFFSET_SECONDS)));
    });
    setHandler('seekforward', details => {
        enqueue(seekAction(currentPositionSeconds() + (details?.seekOffset ?? SEEK_OFFSET_SECONDS)));
    });
    return true;
}

// Managed code awaits this instead of being called back: a JS callback into managed code is a
// synchronous JS export, which multithreaded WebAssembly refuses, while promises marshal fine.
export function waitForAction() {
    if (queued.length > 0) {
        return Promise.resolve(queued.shift());
    }

    return new Promise(resolve => { pendingResolve = resolve; });
}

export function setNowPlaying(nextTitle, nextArtist, nextAlbum) {
    if (!registered) {
        return;
    }

    title = nextTitle ?? '';
    artist = nextArtist ?? '';
    album = nextAlbum ?? '';
    pushMetadata();
}

export function setPlaybackState(playing, itemPresent) {
    if (!registered) {
        return;
    }

    const stateChanged = isPlaying !== Boolean(playing);
    const itemChanged = hasItem !== Boolean(itemPresent);
    isPlaying = Boolean(playing);
    hasItem = Boolean(itemPresent);
    session().playbackState = hasItem ? (isPlaying ? 'playing' : 'paused') : 'none';
    syncSilentTrack();
    if (itemChanged) {
        pushMetadata();
    }
    if (stateChanged || itemChanged) {
        pushPositionState();
    }
}

export function setArtwork(bytes) {
    if (!registered) {
        return;
    }

    if (artworkUrl) {
        URL.revokeObjectURL(artworkUrl);
        artworkUrl = null;
    }

    if (bytes && bytes.length > 0) {
        artworkUrl = URL.createObjectURL(new Blob([bytes], { type: sniffImageType(bytes) }));
    }

    pushMetadata();
}

export function setPlaybackProgress(nextPositionMs, nextDurationMs) {
    if (!registered) {
        return;
    }

    positionMs = nextPositionMs;
    durationMs = nextDurationMs;
    if (shouldPushPosition()) {
        pushPositionState();
    }
}
