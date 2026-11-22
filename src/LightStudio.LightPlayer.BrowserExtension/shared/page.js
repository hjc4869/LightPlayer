// Localizes the extension pages: an element with `data-i18n` gets that message as its text.
(() => {
    const api = globalThis.browser ?? globalThis.chrome;

    function text(key, substitutions) {
        return api.i18n.getMessage(key, substitutions);
    }

    function localize(root = document) {
        document.documentElement.lang = text('@@ui_locale').replace('_', '-');
        document.documentElement.dir = text('@@bidi_dir');
        for (const element of root.querySelectorAll('[data-i18n]')) {
            element.textContent = text(element.dataset.i18n);
        }
    }

    // Asks for the host access that content scripts and request observation need, where the
    // browser lets the user withhold it.
    const allSites = { origins: ['<all_urls>'] };

    function hasSiteAccess() {
        return api.permissions.contains(allSites);
    }

    function requestSiteAccess() {
        return api.permissions.request(allSites);
    }

    // The application's status in `line`, with the action the status calls for in `action`:
    // starting the application, trying again, or switching to the port it answers on. `progress`
    // shows the wait after a start. `options.tabId()` names the tab that opens the application's
    // URL, `options.inTab` whether this page is shown in that tab, `options.onOpenFailed` runs when
    // that tab cannot open it, and `options.onStatus` receives every status shown.
    function statusLine({ line, action, progress }, options) {
        const launch = globalThis.LightPlayerLaunch;
        let run = null;
        let busy = false;
        action.addEventListener('click', () => run?.());

        function offer(label, handler) {
            action.textContent = label;
            action.hidden = false;
            run = handler;
        }

        function show(status, started) {
            progress.hidden = true;
            action.hidden = true;
            run = null;
            switch (launch.outcome(status)) {
                case 'ready':
                    line.textContent = text('statusConnected', [status.appVersion ?? '']);
                    break;
                case 'notPaired':
                    line.textContent = text(status.revoked ? 'pairingStateRevoked' : 'statusNotPaired');
                    break;
                case 'otherPort':
                    line.textContent = text('startOtherPort', [String(status.otherPort), String(status.port)]);
                    offer(text('startUsePort', [String(status.otherPort)]), async () => {
                        await api.runtime.sendMessage({ k: 'usePort', port: status.otherPort });
                        await refresh();
                    });
                    break;
                default:
                    line.textContent = started ? text('startNoAnswer', [String(status?.port ?? '')]) : text('messageUnreachable');
                    offer(text(started ? 'startTryAgain' : 'startAction'), start);
            }
            options.onStatus?.(status);
        }

        async function refresh() {
            if (busy) {
                return;
            }
            line.textContent = text('statusChecking');
            show(await api.runtime.sendMessage({ k: 'status', alternatives: true }), false);
        }

        // Opens the application's URL once for this click and waits for the application.
        async function start() {
            if (busy) {
                return;
            }
            busy = true;
            try {
                if (!await launch.open({ tabId: options.tabId(), inTab: options.inTab })) {
                    await options.onOpenFailed();
                    return;
                }
                action.hidden = true;
                line.textContent = text('startWaiting');
                progress.value = 0;
                progress.hidden = false;
                show(await launch.wait((value) => {
                    progress.value = value;
                }), true);
            } finally {
                busy = false;
            }
        }

        return { refresh };
    }

    globalThis.LightPlayerPage = Object.freeze({ text, localize, hasSiteAccess, requestSiteAccess, statusLine });
})();
