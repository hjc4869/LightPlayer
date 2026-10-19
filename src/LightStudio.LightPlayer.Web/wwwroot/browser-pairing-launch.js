const storageKey = 'lightplayer.pairingLaunch';
let pendingLaunch = globalThis.__lightPlayerPairingLaunch || readStagedLaunch();
delete globalThis.__lightPlayerPairingLaunch;

function readStagedLaunch() {
    try {
        const launch = globalThis.sessionStorage.getItem(storageKey);
        if (!launch) {
            return null;
        }

        const parameters = new URLSearchParams(launch);
        const expiresAt = Number(parameters.get('expires'));
        if (Number.isFinite(expiresAt) && expiresAt * 1000 <= Date.now()) {
            globalThis.sessionStorage.removeItem(storageKey);
        }
        return launch;
    } catch {
        return null;
    }
}

export async function take() {
    if (!pendingLaunch) {
        return null;
    }

    const parameters = new URLSearchParams(pendingLaunch);
    pendingLaunch = null;
    const payload = JSON.stringify({
        server: parameters.get('server') || '',
        token: parameters.get('token') || '',
        expiresAtUnixSeconds: Number(parameters.get('expires')) || 0
    });
    return payload;
}

export async function complete() {
    pendingLaunch = null;
    try {
        globalThis.sessionStorage.removeItem(storageKey);
    } catch { }
}

export async function showResult(succeeded, message) {
    const previous = document.getElementById('pairing-result');
    if (previous) {
        previous.remove();
    }

    const result = document.createElement('div');
    result.id = 'pairing-result';
    result.setAttribute('role', succeeded ? 'status' : 'alert');
    result.textContent = message;
    Object.assign(result.style, {
        position: 'fixed',
        zIndex: '1200',
        top: '12px',
        left: '50%',
        width: 'max-content',
        maxWidth: 'calc(100vw - 24px)',
        boxSizing: 'border-box',
        padding: '10px 14px',
        border: `1px solid ${succeeded ? '#3e8e68' : '#b76363'}`,
        borderRadius: '6px',
        background: succeeded ? '#173d2d' : '#4c2427',
        color: '#f7f9fa',
        font: '13px system-ui, sans-serif',
        lineHeight: '1.4',
        boxShadow: '0 6px 20px rgba(0, 0, 0, .28)',
        transform: 'translateX(-50%)'
    });
    document.body.appendChild(result);
    globalThis.setTimeout(() => result.remove(), succeeded ? 8000 : 15000);
}