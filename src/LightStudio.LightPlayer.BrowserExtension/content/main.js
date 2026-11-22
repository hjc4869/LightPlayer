// Wires video discovery, the redirect control and the redirect paths in one frame.
(() => {
    const api = globalThis.browser ?? globalThis.chrome;
    const videos = globalThis.LightPlayerVideos;
    const files = globalThis.LightPlayerFileSession;
    const manifests = globalThis.LightPlayerManifestSession;
    const takeover = globalThis.LightPlayerTakeover;
    const launch = globalThis.LightPlayerLaunch;
    const text = (key, substitutions) => api.i18n.getMessage(key, substitutions);

    const classificationMaxAgeMs = 1000;
    const classifications = new WeakMap();
    // Messages about starting the application stay up longer than other messages.
    const startMessageMs = 20000;
    // How long a redirect that found the application unreachable continues when it is started.
    const pendingMs = 120000;

    // The video whose redirect waits for the application to start.
    let pending = null;
    let starting = false;

    const control = globalThis.LightPlayerControl.create({
        onRedirect: (video) => redirect(video),
        onReturn: () => activeSession()?.stop('stopped'),
    });

    const endMessages = {
        notPaired: 'messageNotPaired',
        unauthorized: 'messageUnauthorized',
        unsupported: 'messageUnsupported',
        encrypted: 'messageEncrypted',
        protected: 'messageEncrypted',
        error: 'messageError',
        protocol: 'messageProtocol',
        disconnected: 'messageDisconnected',
    };

    function activeSession() {
        return files.active() ?? manifests.active();
    }

    function messageFor(reason) {
        const key = endMessages[reason];
        return key ? text(key) : null;
    }

    function actionFor(reason) {
        if (reason !== 'notPaired') {
            return null;
        }
        return {
            label: text('messagePairAction'),
            run: () => {
                api.runtime.sendMessage({ k: 'openOptions' }).catch((error) => console.error('Light Media Center: opening the options failed', error));
                control.hide();
            },
        };
    }

    // Offers to start the application for a redirect or arming that found it unreachable. Starting
    // it here or from the popup continues the redirect. An application that answers on the default
    // port instead of the extension's needs the port changed, not a start.
    function offerStart(video) {
        pending = { video, until: Date.now() + pendingMs };
        api.runtime.sendMessage({ k: 'status', alternatives: true }).then((status) => {
            if (launch.outcome(status) === 'otherPort') {
                return afterStart(video, status);
            }
            control.showMessage(video, text('startOffer'), { label: text('startAction'), run: () => startApp(video, true) }, startMessageMs);
            return null;
        }).catch((error) => {
            console.error('Light Media Center: checking the application failed', error);
            control.showMessage(video, text('messageError'));
        });
    }

    // Opens the application's URL when `open` is set, waits for its browser link, and continues
    // the pending redirect. The URL is opened only after a click and never again by itself.
    async function startApp(video, open) {
        if (starting) {
            return;
        }
        starting = true;
        try {
            if (open && !await launch.open()) {
                control.showMessage(video, text('messageError'));
                return;
            }
            control.showBusy(video, text('startWaiting'), 0);
            const status = await launch.wait((progress) => {
                if (control.video === video && control.mode === 'busy') {
                    control.showBusy(video, text('startWaiting'), progress);
                }
            });
            await afterStart(video, status);
        } catch (error) {
            console.error('Light Media Center: starting the application failed', error);
            control.showMessage(video, text('messageError'));
        } finally {
            starting = false;
        }
    }

    async function afterStart(video, status) {
        switch (launch.outcome(status)) {
            case 'ready':
                pending = null;
                if (video.isConnected) {
                    await redirect(video);
                } else {
                    control.hide();
                }
                break;
            case 'notPaired':
                pending = null;
                control.showMessage(video, text('messageNotPaired'), actionFor('notPaired'));
                break;
            case 'otherPort':
                control.showMessage(video, text('startOtherPort', [String(status.otherPort), String(status.port)]), {
                    label: text('startUsePort', [String(status.otherPort)]),
                    run: () => api.runtime.sendMessage({ k: 'usePort', port: status.otherPort }).then(() => startApp(video, false)),
                }, startMessageMs);
                break;
            default:
                control.showMessage(video, text('startNoAnswer', [String(status.port ?? '')]), {
                    label: text('startTryAgain'),
                    run: () => startApp(video, true),
                }, startMessageMs);
        }
    }

    // The application is being started for this tab; a pending redirect continues once it answers.
    function onAppStarting() {
        if (pending && pending.until > Date.now() && pending.video.isConnected) {
            startApp(pending.video, false);
        }
        takeover.onAppStarting();
    }

    // `takeover` for a video the page-world shim took over. In an armed frame, a Media Source video
    // the shim did not take over (one built in a worker or another realm) stays with the browser.
    async function classify(video, fresh = false) {
        if (takeover.statusOf(video)) {
            return 'takeover';
        }
        const cached = classifications.get(video);
        if (!fresh && cached && performance.now() - cached.time < classificationMaxAgeMs && cached.source === video.currentSrc) {
            return cached.value;
        }
        let value = await videos.classify(video);
        if (value === 'mediaSource' && takeover.armed) {
            value = 'unsupported';
        }
        classifications.set(video, { value, time: performance.now(), source: video.currentSrc });
        return value;
    }

    function onSessionEnded(video, reason) {
        const message = messageFor(reason);
        if (message) {
            control.showMessage(video, message, actionFor(reason));
        } else if (control.video === video) {
            control.hide();
        }
    }

    // Arms the tab for takeover, which reloads it.
    async function arm(video) {
        control.showBusy(video);
        let reason;
        try {
            reason = await takeover.arm();
        } catch (error) {
            console.error('Light Media Center: arming the tab failed', error);
            reason = 'error';
        }
        if (reason === 'unreachable') {
            offerStart(video);
            return false;
        }
        if (reason) {
            control.showMessage(video, messageFor(reason) ?? text('messageError'), actionFor(reason));
            return false;
        }
        return true;
    }

    async function redirect(video) {
        if (activeSession()?.video === video) {
            return true;
        }
        videos.invalidateRecords();
        const classification = await classify(video, true);
        if (classification === 'mediaSource') {
            return arm(video);
        }
        if (classification === 'takeover') {
            return takeover.play(video);
        }
        const path = { file: files, manifest: manifests }[classification];
        if (!path) {
            control.showMessage(video, text(classification === 'protected' ? 'messageEncrypted' : 'controlUnavailable'));
            return false;
        }
        // One redirected video per frame.
        (path === files ? manifests : files).active()?.stop('replaced');
        control.showBusy(video);
        try {
            await path.start(video, onSessionEnded);
        } catch (error) {
            const reason = error?.reason;
            if (!reason) {
                console.error('Light Media Center: starting the redirect failed', error);
            }
            if (reason === 'takeover') {
                // Only the page's own player can play this source; the control offers arming.
                videos.markTakeover(video);
                control.hide();
                control.offer(video, 'mediaSource');
            } else if (reason === 'sourceChanged' || reason === 'replaced' || reason === 'stopped') {
                if (control.video === video) {
                    control.hide();
                }
            } else if (reason === 'unreachable') {
                offerStart(video);
            } else {
                control.showMessage(video, messageFor(reason) ?? text('messageError'), actionFor(reason));
            }
            return false;
        }
        control.showSession(video);
        return true;
    }

    function videoAt(x, y) {
        let best = null;
        let bestArea = Infinity;
        for (const video of videos.all()) {
            const rect = video.getBoundingClientRect();
            if (x >= rect.left && x <= rect.right && y >= rect.top && y <= rect.bottom) {
                const area = rect.width * rect.height;
                if (area > 0 && area < bestArea) {
                    best = video;
                    bestArea = area;
                }
            }
        }
        return best;
    }

    let pointer = null;
    let pointerFrame = 0;
    let lastTarget = null;

    function onPointerFrame() {
        pointerFrame = 0;
        const { x, y, overControl } = pointer;
        if (overControl) {
            control.touch(control.video);
            return;
        }
        const video = videoAt(x, y);
        if (!video) {
            return;
        }
        if (control.video === video && control.mode !== 'offer') {
            control.touch(video);
            return;
        }
        classify(video).then((classification) => control.offer(video, classification), (error) => {
            console.error('Light Media Center: classifying a video failed', error);
        });
    }

    document.addEventListener('pointermove', (event) => {
        if (event.pointerType === 'touch') {
            return;
        }
        if (event.target !== lastTarget) {
            lastTarget = event.target;
            videos.collectFromPath(event.composedPath());
        }
        pointer = { x: event.clientX, y: event.clientY, overControl: event.target === control.host };
        pointerFrame ||= requestAnimationFrame(onPointerFrame);
    }, { capture: true, passive: true });

    function describeSource(url) {
        try {
            const parsed = new URL(url);
            if (parsed.protocol === 'blob:') {
                return new URL(parsed.pathname).host;
            }
            const name = parsed.pathname.slice(parsed.pathname.lastIndexOf('/') + 1);
            return name ? `${parsed.host}/${decodeURIComponent(name)}` : parsed.host;
        } catch {
            return '';
        }
    }

    // The frame's videos for the toolbar popup.
    async function list() {
        const result = [];
        for (const video of videos.all(true)) {
            if (!video.isConnected) {
                continue;
            }
            const classification = await classify(video, true);
            if (classification === 'none') {
                continue;
            }
            const taken = takeover.statusOf(video);
            result.push({
                id: videos.idOf(video),
                classification,
                active: taken ? taken.active : activeSession()?.video === video,
                waiting: taken?.status === 'waiting',
                width: video.videoWidth,
                height: video.videoHeight,
                duration: Number.isFinite(video.duration) ? video.duration : null,
                live: video.duration === Infinity,
                source: describeSource(video.currentSrc),
                title: document.title,
            });
        }
        return result;
    }

    api.runtime.onMessage.addListener((message, sender, sendResponse) => {
        switch (message?.k) {
            case 'redirect': {
                const video = videos.findById(message.id);
                if (!video) {
                    sendResponse(false);
                    return false;
                }
                redirect(video).then(sendResponse, (error) => {
                    console.error('Light Media Center: redirect from the popup failed', error);
                    sendResponse(false);
                });
                return true;
            }
            case 'openApp':
                // Firefox: the popup asked this tab's top frame to open the application's URL.
                launch.openInFrame();
                sendResponse(true);
                return false;
            case 'appStarting':
                onAppStarting();
                return false;
            case 'return': {
                const session = activeSession();
                const video = videos.findById(message.id);
                if (session && session.video === video) {
                    session.stop('stopped');
                } else if (video && takeover.statusOf(video)) {
                    takeover.returnToBrowser();
                }
                sendResponse(true);
                return false;
            }
            default:
                return false;
        }
    });

    globalThis.LightPlayerContent = Object.freeze({ list });
})();
