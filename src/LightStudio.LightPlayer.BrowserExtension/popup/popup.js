// Toolbar popup: whether the application is reachable and paired, and the videos in the current tab.
(() => {
    const api = globalThis.browser ?? globalThis.chrome;
    const page = globalThis.LightPlayerPage;

    const elements = Object.fromEntries(
        ['status', 'progress', 'start', 'pair', 'takeover', 'disarm', 'access', 'grant', 'videos', 'empty', 'options']
            .map((id) => [id, document.getElementById(id)]));

    const stateKeys = {
        protected: 'videoProtected',
        unsupported: 'videoUnsupported',
    };

    let tabId = null;

    // The application's URL opens for the current tab; in Firefox a tab without content scripts
    // cannot open it, and the options page offers the start instead.
    const status = page.statusLine({ line: elements.status, action: elements.start, progress: elements.progress }, {
        tabId: () => tabId,
        inTab: false,
        onOpenFailed: () => api.runtime.openOptionsPage().then(() => window.close()),
        onStatus: (current) => {
            elements.pair.hidden = globalThis.LightPlayerLaunch.outcome(current) !== 'notPaired';
        },
    });

    function formatDuration(seconds) {
        const total = Math.round(seconds);
        const h = Math.floor(total / 3600);
        const m = Math.floor((total % 3600) / 60);
        const s = String(total % 60).padStart(2, '0');
        return h ? `${h}:${String(m).padStart(2, '0')}:${s}` : `${m}:${s}`;
    }

    async function collect() {
        const results = await api.scripting.executeScript({
            target: { tabId, allFrames: true },
            func: () => globalThis.LightPlayerContent?.list() ?? [],
        });
        const items = [];
        for (const result of results ?? []) {
            for (const video of result.result ?? []) {
                items.push({ ...video, frameId: result.frameId });
            }
        }
        return items;
    }

    function row(video) {
        const item = document.createElement('li');
        const info = document.createElement('div');
        info.className = 'video';
        const source = document.createElement('div');
        source.className = 'source';
        source.textContent = video.source || video.title || '';
        source.title = source.textContent;
        const details = document.createElement('div');
        details.className = 'details';
        const parts = [];
        if (video.width && video.height) {
            parts.push(`${video.width}x${video.height}`);
        }
        if (video.live) {
            parts.push(page.text('videoLive'));
        } else if (video.duration) {
            parts.push(formatDuration(video.duration));
        }
        details.textContent = parts.join(', ');
        info.append(source, details);
        item.append(info);

        if (video.active || (video.classification === 'takeover' && !video.waiting)) {
            const button = document.createElement('button');
            button.type = 'button';
            button.textContent = page.text('controlReturn');
            button.title = page.text(video.active ? 'controlPlaying' : 'popupDisarm');
            button.addEventListener('click', () => act({ k: 'return', id: video.id }, video.frameId));
            item.append(button);
        } else if (video.classification === 'mediaSource') {
            // The frame arms the tab, which reloads it; when the application does not answer, the
            // frame keeps the request until the application is started.
            const button = document.createElement('button');
            button.type = 'button';
            button.className = 'primary';
            button.textContent = page.text('videoArm');
            button.addEventListener('click', () => act({ k: 'redirect', id: video.id }, video.frameId, true));
            item.append(button);
        } else if (video.classification === 'file' || video.classification === 'manifest' || video.classification === 'takeover') {
            const button = document.createElement('button');
            button.type = 'button';
            button.className = 'primary';
            button.textContent = page.text('videoRedirect');
            button.addEventListener('click', () => act({ k: 'redirect', id: video.id }, video.frameId));
            item.append(button);
        } else {
            const state = document.createElement('span');
            state.className = 'state';
            state.textContent = page.text(stateKeys[video.classification] ?? 'videoUnsupported');
            item.append(state);
        }
        return item;
    }

    async function renderTakeover() {
        const state = await api.runtime.sendMessage({ k: 'takeoverState', tabId });
        elements.takeover.hidden = !state?.armed;
    }

    // `reloads`: the action reloads the tab when it succeeds.
    async function act(message, frameId, reloads = false) {
        let done = false;
        try {
            done = await api.tabs.sendMessage(tabId, message, { frameId });
        } catch (error) {
            console.error('Light Media Center: the page did not answer', error);
        }
        if (done && reloads) {
            window.close();
            return;
        }
        await Promise.all([status.refresh(), renderVideos()]);
    }

    async function renderVideos() {
        let items;
        try {
            items = await collect();
        } catch (error) {
            // Browser pages and stores do not run content scripts.
            console.warn('Light Media Center: the page cannot be read', error);
            elements.videos.replaceChildren();
            elements.empty.textContent = page.text('popupNoAccess');
            elements.empty.hidden = false;
            return;
        }
        elements.videos.replaceChildren(...items.map(row));
        elements.empty.textContent = page.text('popupNoVideos');
        elements.empty.hidden = items.length > 0;
    }

    async function renderAccess() {
        elements.access.hidden = await page.hasSiteAccess();
    }

    async function init() {
        page.localize();
        const [tab] = await api.tabs.query({ active: true, currentWindow: true });
        tabId = tab?.id ?? null;
        elements.pair.addEventListener('click', () => api.runtime.openOptionsPage().then(() => window.close()));
        elements.options.addEventListener('click', () => api.runtime.openOptionsPage().then(() => window.close()));
        elements.grant.addEventListener('click', () => page.requestSiteAccess().then(() => Promise.all([renderAccess(), renderVideos()])));
        elements.disarm.addEventListener('click', () => api.runtime.sendMessage({ k: 'takeoverDisarm', tabId }).then(() => window.close()));
        await Promise.all([
            status.refresh(),
            renderAccess(),
            tabId === null ? Promise.resolve() : renderTakeover(),
            tabId === null ? Promise.resolve() : renderVideos(),
        ]);
    }

    init().catch((error) => console.error('Light Media Center: showing the popup failed', error));
})();
