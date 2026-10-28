const keys = {
    version: 'lightplayer-web.loadedVersion',
    skipped: 'lightplayer-web.skippedVersion',
    disabled: 'lightplayer-web.updateCheckDisabled',
};
const messages = {
    en: {
        checking: 'Checking for updates...', available: 'Update available',
        availableMessage: 'Version {version} is available.', latest: 'You are using the latest version.',
        failed: 'Could not check for updates. Check your connection and try again.',
        startupFailed: 'Could not load Light Media Center. Check your connection and restart with the latest version.',
        update: 'Update', skip: 'Skip this version', disable: 'Disable update check', cancel: 'Cancel', close: 'Close',
        restart: 'Restart with latest version',
    },
    de: {
        checking: 'Es wird nach Updates gesucht...', available: 'Update verfügbar',
        availableMessage: 'Version {version} ist verfügbar.', latest: 'Sie verwenden die neueste Version.',
        failed: 'Die Updateprüfung ist fehlgeschlagen. Prüfen Sie Ihre Verbindung und versuchen Sie es erneut.',
        startupFailed: 'Light Media Center konnte nicht geladen werden. Prüfen Sie Ihre Verbindung und starten Sie mit der neuesten Version neu.',
        update: 'Aktualisieren', skip: 'Diese Version überspringen', disable: 'Updateprüfung deaktivieren', cancel: 'Abbrechen', close: 'Schließen',
        restart: 'Mit der neuesten Version neu starten',
    },
    es: {
        checking: 'Buscando actualizaciones...', available: 'Actualización disponible',
        availableMessage: 'La versión {version} está disponible.', latest: 'Está usando la versión más reciente.',
        failed: 'No se pudieron buscar actualizaciones. Compruebe la conexión e inténtelo de nuevo.',
        startupFailed: 'No se pudo cargar Light Media Center. Compruebe la conexión y reinicie con la versión más reciente.',
        update: 'Actualizar', skip: 'Omitir esta versión', disable: 'Desactivar búsqueda de actualizaciones', cancel: 'Cancelar', close: 'Cerrar',
        restart: 'Reiniciar con la versión más reciente',
    },
    fr: {
        checking: 'Recherche de mises à jour...', available: 'Mise à jour disponible',
        availableMessage: 'La version {version} est disponible.', latest: 'Vous utilisez la dernière version.',
        failed: 'Impossible de rechercher des mises à jour. Vérifiez votre connexion et réessayez.',
        startupFailed: 'Impossible de charger Light Media Center. Vérifiez votre connexion et redémarrez avec la dernière version.',
        update: 'Mettre à jour', skip: 'Ignorer cette version', disable: 'Désactiver la recherche de mises à jour', cancel: 'Annuler', close: 'Fermer',
        restart: 'Redémarrer avec la dernière version',
    },
    ja: {
        checking: '更新を確認しています...', available: '更新があります',
        availableMessage: 'バージョン {version} が利用可能です。', latest: '最新バージョンを使用しています。',
        failed: '更新を確認できませんでした。接続を確認して、もう一度お試しください。',
        startupFailed: 'Light Media Center を読み込めませんでした。接続を確認して、最新バージョンで再起動してください。',
        update: '更新', skip: 'このバージョンをスキップ', disable: '更新の確認を無効にする', cancel: 'キャンセル', close: '閉じる',
        restart: '最新バージョンで再起動',
    },
    'zh-Hans': {
        checking: '正在检查更新...', available: '有可用更新',
        availableMessage: '版本 {version} 现已可用。', latest: '您正在使用最新版本。',
        failed: '无法检查更新。请检查网络连接并重试。',
        startupFailed: '无法加载 Light Media Center。请检查网络连接并使用最新版本重启。',
        update: '更新', skip: '跳过此版本', disable: '禁用更新检查', cancel: '取消', close: '关闭',
        restart: '使用最新版本重启',
    },
    'zh-Hant': {
        checking: '正在檢查更新...', available: '有可用更新',
        availableMessage: '版本 {version} 現已可用。', latest: '您正在使用最新版本。',
        failed: '無法檢查更新。請檢查網路連線並重試。',
        startupFailed: '無法載入 Light Media Center。請檢查網路連線並使用最新版本重新啟動。',
        update: '更新', skip: '略過此版本', disable: '停用更新檢查', cancel: '取消', close: '關閉',
        restart: '使用最新版本重新啟動',
    },
};
let language = navigator.language;
let currentVersion;
let pendingCheck;
let checkIsManual = false;
let bootFailed = false;
let preferencesChanged = () => {};
const dialog = document.getElementById('version-dialog');
const title = document.getElementById('version-title');
const message = document.getElementById('version-message');
const actions = document.getElementById('version-actions');

function text() {
    const locale = new Intl.Locale(language || 'en').maximize();
    return messages[locale.language === 'zh' ? `zh-${locale.script}` : locale.language] ?? messages.en;
}

function read(key) {
    try {
        return localStorage.getItem(key);
    } catch (error) {
        console.warn('Could not read browser version preferences.', error);
        return null;
    }
}

function write(key, value) {
    if (value === null) localStorage.removeItem(key);
    else localStorage.setItem(key, value);
}

function validVersion(version) {
    return typeof version === 'string' && /^[A-Za-z0-9_+-][A-Za-z0-9._+-]*$/.test(version);
}

async function latestVersion() {
    const response = await fetch(`/versions?refresh=${Date.now()}`, {
        cache: 'no-store',
        headers: { Accept: 'application/json' },
        signal: AbortSignal.timeout(15000),
    });
    if (!response.ok) throw new Error(`Version catalog returned HTTP ${response.status}.`);
    const catalog = await response.json();
    if (!Array.isArray(catalog) || catalog.length === 0 || catalog.some(entry =>
        !entry || !validVersion(entry.version) || typeof entry.buildTimestamp !== 'string'
        || !Number.isFinite(Date.parse(entry.buildTimestamp)))) {
        throw new Error('The deployment version catalog is invalid or empty.');
    }
    return catalog.reduce((latest, entry) => Date.parse(entry.buildTimestamp) > Date.parse(latest.buildTimestamp) ? entry : latest);
}

function show(titleText, messageText, buttons, checking = false) {
    title.textContent = titleText;
    message.textContent = messageText;
    dialog.classList.toggle('checking', checking);
    actions.replaceChildren();
    for (const [label, action, primary] of buttons) {
        const button = document.createElement('button');
        button.type = 'button';
        button.textContent = label;
        if (primary) button.className = 'primary';
        button.addEventListener('click', () => {
            try {
                action();
            } catch (error) {
                console.error('Could not apply the browser update preference.', error);
                show(text().failed, '', [[text().close, () => dialog.close()]]);
            }
        });
        actions.append(button);
    }
    if (!dialog.open) dialog.showModal();
}

function autoCheckEnabled() {
    return read(keys.disabled) !== 'true';
}

function setAutoCheckEnabled(enabled) {
    write(keys.disabled, enabled ? null : 'true');
    preferencesChanged();
}

function checkForUpdates(manual = false) {
    if (!manual && !autoCheckEnabled()) return Promise.resolve();
    if (pendingCheck) {
        checkIsManual ||= manual;
        if (manual) show(text().checking, '', [], true);
        return pendingCheck;
    }
    if (dialog.open && !manual) return Promise.resolve();
    checkIsManual = manual;
    if (manual) show(text().checking, '', [], true);
    pendingCheck = (async () => {
        try {
            const latest = await latestVersion();
            const labels = text();
            if (bootFailed) return;
            if (!checkIsManual && (!autoCheckEnabled() || read(keys.skipped) === latest.version)) return;
            if (latest.version === currentVersion) {
                if (checkIsManual) show(labels.latest, '', [[labels.close, () => dialog.close()]]);
                return;
            }
            show(labels.available, labels.availableMessage.replace('{version}', latest.version), [
                [labels.update, () => {
                    write(keys.version, latest.version);
                    write(keys.skipped, null);
                    location.reload();
                }, true],
                [labels.skip, () => { write(keys.skipped, latest.version); dialog.close(); }],
                [labels.disable, () => { setAutoCheckEnabled(false); dialog.close(); }],
                [labels.cancel, () => dialog.close()],
            ]);
        } catch (error) {
            console.error('Browser update check failed.', error);
            if (checkIsManual && !bootFailed) show(text().failed, '', [[text().close, () => dialog.close()]]);
        } finally {
            dialog.classList.remove('checking');
            pendingCheck = undefined;
        }
    })();
    return pendingCheck;
}

function restartLatest() {
    try {
        write(keys.version, null);
        write(keys.skipped, null);
        location.reload();
    } catch (error) {
        console.error('Could not clear the stored browser version.', error);
        globalThis.lightPlayerBoot.fail(text().startupFailed, error);
    }
}

globalThis.lightPlayerVersions = {
    checkForUpdates,
    autoCheckEnabled,
    setAutoCheckEnabled,
    restartLatest,
    configure(culture, onPreferencesChanged) {
        language = culture;
        document.documentElement.lang = culture || 'en';
        preferencesChanged = onPreferencesChanged;
        document.getElementById('boot-restart-latest').textContent = text().restart;
    },
};
window.addEventListener('storage', event => {
    if (event.key === keys.disabled || event.key === null) preferencesChanged();
});
window.addEventListener('lightplayer-boot-failed', () => {
    bootFailed = true;
    dialog.close();
});
document.getElementById('boot-restart-latest').textContent = text().restart;

async function start() {
    const stored = read(keys.version);
    const returning = validVersion(stored);
    currentVersion = returning ? stored : (await latestVersion()).version;
    const base = document.createElement('base');
    base.href = `/${encodeURIComponent(currentVersion)}/`;
    document.head.prepend(base);
    if (location.pathname === '/diag') {
        location.replace(new URL('diag.html', document.baseURI));
        return;
    }
    if (!returning) write(keys.version, currentVersion);
    for (const [rel, href, sizes] of [
        ['manifest', 'manifest.webmanifest'],
        ['icon', 'icons/icon-192.png', '192x192'],
        ['icon', 'icons/icon-512.png', '512x512'],
        ['apple-touch-icon', 'icons/apple-touch-icon.png', '180x180'],
    ]) {
        const link = document.createElement('link');
        link.rel = rel;
        link.href = href;
        if (sizes) link.sizes = sizes;
        document.head.append(link);
    }
    if ('serviceWorker' in navigator) {
        navigator.serviceWorker.register(new URL('service-worker.js', document.baseURI), { scope: '/', updateViaCache: 'none' })
            .catch(error => console.warn('Could not register the browser service worker.', error));
    }
    window.addEventListener('lightplayer-ready', () => {
        if (returning) void checkForUpdates();
    }, { once: true });
    await import(new URL('main.js', document.baseURI).href);
}

start().catch(error => {
    console.error('Browser version startup failed.', error);
    globalThis.lightPlayerBoot.fail(text().startupFailed, error);
});