let changed = false;
let notify;

export function configure(culture) {
    globalThis.lightPlayerVersions.configure(culture, () => {
        changed = true;
        notify?.();
        notify = undefined;
    });
}

export function autoCheckEnabled() {
    return globalThis.lightPlayerVersions.autoCheckEnabled();
}

export function setAutoCheckEnabled(enabled) {
    globalThis.lightPlayerVersions.setAutoCheckEnabled(enabled);
}

export function checkForUpdates() {
    return globalThis.lightPlayerVersions.checkForUpdates(true);
}

export async function waitForChange() {
    if (!changed) await new Promise(resolve => { notify = resolve; });
    changed = false;
}