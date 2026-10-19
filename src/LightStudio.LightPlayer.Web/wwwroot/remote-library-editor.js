// Browser-native twin of the Avalonia remote-library editor.
//
// Keep this file in step with:
//   - src/LightStudio.LightPlayer/Views/Pages/RemoteLibraryEditorView.axaml  (the form every other head draws)
//   - src/LightStudio.LightPlayer.Web/BrowserRemoteLibraryEditor.cs          (the managed side that drives this)
//
// Avalonia paints its text boxes into a canvas, which no password manager can see, so the browser
// head shows this HTML form instead. A field, label, or rule added on either side has to be added
// on the other. The API proxy field of the XAML form has no twin here on purpose: a page can only
// relay through the origin that served it, so it offers the relay as a checkbox
// (RemoteLibraryEditorViewModel.ShowWebProxy) instead of an address. The credentials the XAML form
// keeps behind "Advanced" are always visible here, because a password manager can neither fill nor
// offer to save a hidden field.

const css = `
.lp-remote-dialog {
    --lp-surface: #23272e;
    --lp-text: #e7eaee;
    --lp-secondary: #9aa5b0;
    --lp-border: rgba(255, 255, 255, .16);
    --lp-input: rgba(255, 255, 255, .06);
    --lp-accent: #4cc2ff;
    --lp-accent-text: #10151a;
    --lp-error: #ff6b6b;

    width: min(460px, calc(100vw - 32px));
    max-height: calc(100vh - 48px);
    padding: 0;
    border: 1px solid var(--lp-border);
    border-radius: 10px;
    background: var(--lp-surface);
    color: var(--lp-text);
    font-family: system-ui, -apple-system, "Segoe UI", Roboto, "Noto Sans", sans-serif;
    font-size: 14px;
}

.lp-remote-dialog.light {
    --lp-surface: #ffffff;
    --lp-text: #1a1d21;
    --lp-secondary: #5a6470;
    --lp-border: rgba(0, 0, 0, .16);
    --lp-input: #f3f5f7;
    --lp-accent: #0a84ff;
    --lp-accent-text: #ffffff;
    --lp-error: #c62828;
}

.lp-remote-dialog::backdrop { background: rgba(0, 0, 0, .45); }

.lp-remote-form {
    display: flex;
    flex-direction: column;
    gap: 12px;
    box-sizing: border-box;
    padding: 20px;
    max-height: calc(100vh - 48px);
    overflow: auto;
}

.lp-remote-form h2 { margin: 0; font-size: 18px; font-weight: 600; }

.lp-error { margin: 0; color: var(--lp-error); line-height: 1.4; }

.lp-field { display: flex; flex-direction: column; gap: 6px; }

.lp-field > span { font-weight: 600; }

.lp-remote-form input[type="text"],
.lp-remote-form input[type="url"],
.lp-remote-form input[type="password"],
.lp-remote-form select {
    box-sizing: border-box;
    width: 100%;
    padding: 8px 10px;
    border: 1px solid var(--lp-border);
    border-radius: 6px;
    background: var(--lp-input);
    color: var(--lp-text);
    font: inherit;
}

.lp-remote-form input:focus-visible { outline: 2px solid var(--lp-accent); outline-offset: -1px; }

.lp-checks { display: flex; flex-wrap: wrap; gap: 8px 16px; }

.lp-check { display: flex; align-items: center; gap: 8px; }

.lp-check input { accent-color: var(--lp-accent); }

.lp-hint { margin: 0; color: var(--lp-secondary); line-height: 1.4; font-size: 12px; }

.lp-busy { height: 3px; border-radius: 2px; background: var(--lp-input); overflow: hidden; }

.lp-busy[hidden] { display: none; }

.lp-busy-fill {
    width: 35%;
    height: 100%;
    background: var(--lp-accent);
    animation: lp-busy-slide 1.1s ease-in-out infinite;
}

@keyframes lp-busy-slide {
    0% { transform: translateX(-100%); }
    100% { transform: translateX(300%); }
}

.lp-buttons { display: flex; justify-content: flex-end; gap: 8px; flex-wrap: wrap; }

.lp-buttons button {
    padding: 7px 16px;
    border: 1px solid var(--lp-border);
    border-radius: 6px;
    background: var(--lp-input);
    color: var(--lp-text);
    font: inherit;
    cursor: pointer;
}

.lp-buttons button.primary { border-color: transparent; background: var(--lp-accent); color: var(--lp-accent-text); font-weight: 600; }

.lp-buttons button:disabled { opacity: .5; cursor: default; }

.lp-remote-form [hidden] { display: none; }
`;

const template = `
<form class="lp-remote-form" novalidate>
  <h2 data-role="header"></h2>
  <p class="lp-error" data-role="error" hidden></p>
  <label class="lp-field">
    <span data-role="nameLabel"></span>
    <input data-role="name" name="lp-library-name" type="text" autocomplete="off" spellcheck="false">
  </label>
    <label class="lp-field">
        <span data-role="providerLabel"></span>
        <select data-role="provider"></select>
    </label>
  <label class="lp-field">
    <span data-role="addressLabel"></span>
    <input data-role="address" name="lp-library-address" type="url" autocomplete="url" spellcheck="false" inputmode="url">
  </label>
    <div class="lp-field" data-role="lightPlayerRelay">
        <label class="lp-check"><input data-role="useWebProxy" type="checkbox"><span data-role="webProxyLabel"></span></label>
        <p class="lp-hint" data-role="webProxyHint"></p>
    </div>
  <div class="lp-checks">
    <label class="lp-check"><input data-role="loadMusic" type="checkbox"><span data-role="loadMusicLabel"></span></label>
        <label class="lp-check" data-role="loadVideoField"><input data-role="loadVideo" type="checkbox"><span data-role="loadVideoLabel"></span></label>
        <label class="lp-check" data-role="refreshAtStartupField"><input data-role="refreshAtStartup" type="checkbox"><span data-role="refreshAtStartupLabel"></span></label>
  </div>
    <div class="lp-field" data-role="lightPlayerAuthentication">
        <label class="lp-field">
            <span data-role="userNameLabel"></span>
            <input data-role="userName" name="username" type="text" autocomplete="username" spellcheck="false">
        </label>
        <label class="lp-field">
            <span data-role="passwordLabel"></span>
            <input data-role="password" name="password" type="password" autocomplete="current-password">
        </label>
        <label class="lp-check"><input data-role="rememberPassword" type="checkbox"><span data-role="rememberPasswordLabel"></span></label>
    </div>
    <div class="lp-field" data-role="nextcloudAuthentication">
        <div class="lp-buttons"><button data-role="openNextcloudMusic" type="button"></button></div>
        <label class="lp-field">
            <span data-role="apiKeyLabel"></span>
            <input data-role="apiKey" name="lp-nextcloud-music-api-key" type="password" autocomplete="off" spellcheck="false">
        </label>
        <p class="lp-hint" data-role="apiKeyHint"></p>
    </div>
  <div class="lp-busy" data-role="busy" hidden><div class="lp-busy-fill"></div></div>
  <div class="lp-buttons">
    <button data-role="cancel" type="button"></button>
    <button data-role="offline" type="button" hidden></button>
    <button data-role="save" type="submit" class="primary"></button>
  </div>
</form>`;

let dialog = null;
let parts = null;
let pending = null;
let styled = false;

function ensureStyle() {
    if (styled) {
        return;
    }

    styled = true;
    const style = document.createElement('style');
    style.textContent = css;
    document.head.appendChild(style);
}

function ensureDialog() {
    if (dialog) {
        return;
    }

    ensureStyle();
    dialog = document.createElement('dialog');
    dialog.className = 'lp-remote-dialog';
    dialog.innerHTML = template;

    parts = {};
    for (const element of dialog.querySelectorAll('[data-role]')) {
        parts[element.dataset.role] = element;
    }

    parts.cancel.addEventListener('click', dismiss);
    parts.offline.addEventListener('click', () => submit(true));
    parts.provider.addEventListener('change', updateProvider);
    parts.openNextcloudMusic.addEventListener('click', openNextcloudMusic);
    dialog.querySelector('form').addEventListener('submit', event => {
        // The page never navigates, so the default submit is suppressed; browsers still take the
        // submit itself as the sign-in that makes them offer to remember the password.
        event.preventDefault();
        submit(false);
    });

    // Esc, and a click on the backdrop, count as closing the overlay.
    dialog.addEventListener('cancel', event => {
        event.preventDefault();
        dismiss();
    });
    dialog.addEventListener('click', event => {
        if (event.target === dialog) {
            dismiss();
        }
    });

    // Avalonia listens for keys on the document, and would read typing here as player shortcuts.
    for (const type of ['keydown', 'keyup', 'keypress']) {
        dialog.addEventListener(type, event => event.stopPropagation());
    }

    document.body.appendChild(dialog);
}

function text(element, value) {
    element.textContent = value ?? '';
}

function apply(state) {
    dialog.classList.toggle('light', state.dark === false);
    text(parts.header, state.header);
    text(parts.nameLabel, state.nameLabel);
    text(parts.providerLabel, state.providerLabel);
    text(parts.addressLabel, state.addressLabel);
    text(parts.loadMusicLabel, state.loadMusicLabel);
    text(parts.loadVideoLabel, state.loadVideoLabel);
    text(parts.refreshAtStartupLabel, state.refreshAtStartupLabel);
    text(parts.webProxyLabel, state.webProxyLabel);
    text(parts.webProxyHint, state.webProxyHint);
    text(parts.userNameLabel, state.userNameLabel);
    text(parts.passwordLabel, state.passwordLabel);
    text(parts.rememberPasswordLabel, state.rememberPasswordLabel);
    text(parts.apiKeyLabel, state.apiKeyLabel);
    text(parts.apiKeyHint, state.apiKeyHint);
    text(parts.openNextcloudMusic, state.openNextcloudMusicLabel);
    text(parts.cancel, state.cancelLabel);
    text(parts.offline, state.saveOfflineLabel);
    text(parts.save, state.saveLabel);

    parts.name.placeholder = state.namePlaceholder ?? '';
    parts.address.placeholder = state.addressPlaceholder ?? '';
    parts.name.value = state.name ?? '';
    parts.address.value = state.address ?? '';
    parts.userName.value = state.userName ?? '';
    parts.password.value = state.password ?? '';
    parts.apiKey.value = state.apiKey ?? '';
    parts.provider.replaceChildren(
        new Option(state.lightPlayerLabel ?? 'Light Player', '0'),
        new Option(state.nextcloudMusicLabel ?? 'Nextcloud Music', '1'),
        new Option(state.webDavLabel ?? 'WebDAV', '2'));
    parts.provider.value = String(state.selectedProviderIndex ?? 0);
    parts.loadMusic.checked = state.loadMusic === true;
    parts.loadVideo.checked = state.loadVideo === true;
    parts.refreshAtStartup.checked = state.refreshAtStartup === true;
    parts.useWebProxy.checked = state.useWebProxy === true;
    parts.rememberPassword.checked = state.rememberPassword === true;

    text(parts.error, state.error);
    parts.error.hidden = !state.error;
    parts.offline.hidden = state.canSaveOffline !== true;
    updateProvider();
}

function updateProvider() {
    const lightPlayer = parts.provider.value === '0';
    const nextcloud = parts.provider.value === '1';
    parts.lightPlayerRelay.hidden = !lightPlayer;
    parts.lightPlayerAuthentication.hidden = nextcloud;
    parts.loadVideoField.hidden = nextcloud;
    parts.refreshAtStartupField.hidden = lightPlayer;
    parts.nextcloudAuthentication.hidden = !nextcloud;
    if (nextcloud) {
        parts.loadMusic.checked = true;
        parts.loadVideo.checked = false;
        parts.useWebProxy.checked = false;
    }
}

function openNextcloudMusic() {
    const address = parts.address.value.trim().replace(/\/+$/, '');
    if (!address) {
        parts.address.focus();
        return;
    }

    const suffix = address.toLowerCase().endsWith('/index.php')
        ? '/apps/music/'
        : '/index.php/apps/music/';
    window.open(address + suffix, '_blank', 'noopener');
}

function setBusy(busy) {
    parts.busy.hidden = !busy;
    parts.save.disabled = busy;
    parts.offline.disabled = busy;
    parts.cancel.disabled = busy;
    parts.provider.disabled = busy;
    parts.openNextcloudMusic.disabled = busy;
}

function focusFirst() {
    // The address is already filled in when the page's own host serves the library, which leaves
    // the credentials as the only thing this form still wants.
    const nextcloud = parts.provider.value === '1';
    const target = parts.address.value
        ? (nextcloud ? parts.apiKey : parts.userName)
        : parts.address;
    target.focus();
    target.select?.();
}

function answer(offline) {
    return JSON.stringify({
        name: parts.name.value,
        address: parts.address.value,
        selectedProviderIndex: Number(parts.provider.value),
        userName: parts.userName.value,
        password: parts.password.value,
        apiKey: parts.apiKey.value,
        rememberPassword: parts.rememberPassword.checked,
        loadMusic: parts.loadMusic.checked,
        loadVideo: parts.loadVideo.checked,
        refreshAtStartup: parts.refreshAtStartup.checked,
        useWebProxy: parts.useWebProxy.checked,
        offline,
    });
}

function submit(offline) {
    if (!pending) {
        return;
    }

    const resolve = pending;
    pending = null;
    setBusy(true);
    resolve(answer(offline));
}

function dismiss() {
    if (!pending) {
        return;
    }

    const resolve = pending;
    pending = null;
    setBusy(true);
    resolve('');
}

// Shows the form (or updates the one already on screen with a failed save's message) and resolves
// with the values the user submitted, or with an empty string when the overlay was closed instead.
export function show(stateJson) {
    ensureDialog();
    apply(JSON.parse(stateJson));
    setBusy(false);
    if (!dialog.open) {
        dialog.showModal();
    }

    focusFirst();
    return new Promise(resolve => { pending = resolve; });
}

// Takes the form off the screen. Removing the element rather than only hiding it is also what
// tells the browser the sign-in it saw finished, so it offers to remember the password.
// Async because managed code awaits it: a plain return marshals to a null Task, not a done one.
export async function close() {
    const resolve = pending;
    pending = null;
    if (dialog) {
        if (dialog.open) {
            dialog.close();
        }

        dialog.remove();
        dialog = null;
        parts = null;
    }

    // The modal took focus away from the canvas, which is where Avalonia reads the keyboard.
    document.querySelector('#out canvas')?.focus?.();
    if (resolve) {
        resolve('');
    }
}
