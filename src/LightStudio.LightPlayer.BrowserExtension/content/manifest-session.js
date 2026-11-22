// Manifest redirect: a bundled player library plays the manifest the frame loaded against the
// remote media classes, and the segments it appends travel to the application over the session link.
(() => {
    const api = globalThis.browser ?? globalThis.chrome;
    const pageVideo = globalThis.LightPlayerPageVideo;

    // The link opens once the library appended its first segment from manifests that passed the
    // protection checks; a start that gets no further fails after this long.
    const startTimeoutMs = 20000;
    const hiddenTickMs = 250;
    const maxManifestProbes = 8;
    const defaultBudgetBytes = 256 * 1024 * 1024;
    // Shares of the application's byte budget for media ahead of and behind the position.
    const forwardShare = 0.5;
    const backShare = 0.25;
    // dash.js errors that end playback: manifest, download, parsing and Media Source failures.
    const dashFatalErrors = new Set([10, 11, 12, 23, 25, 26, 27, 28, 31, 32, 34]);

    // Content-script requests skip the page's service worker in Chromium only.
    const skipsServiceWorker = !api.runtime.getURL('').startsWith('moz-extension:');
    const loadedParts = new Set();
    let current = null;

    function failure(reason) {
        const error = new Error(reason);
        error.reason = reason;
        return error;
    }

    function clamp(value, min, max) {
        return Math.min(Math.max(value, min), max);
    }

    function originOf(url) {
        try {
            return new URL(url).origin;
        } catch {
            return null;
        }
    }

    // Attribute values of the tags with the given name in a playlist.
    function playlistTags(text, name) {
        const pattern = new RegExp(`^#${name}:([^\\r\\n]*)`, 'gm');
        return [...text.matchAll(pattern)].map((match) => {
            const attributes = {};
            for (const [, key, quoted, plain] of match[1].matchAll(/([A-Z0-9-]+)=(?:"([^"]*)"|([^,]*))/g)) {
                attributes[key] = quoted ?? plain;
            }
            return attributes;
        });
    }

    // A key with a SAMPLE-AES method, or a key format other than the clear-key identity format.
    function hlsProtected(text) {
        return [...playlistTags(text, 'EXT-X-KEY'), ...playlistTags(text, 'EXT-X-SESSION-KEY')].some((key) => {
            const method = key.METHOD ?? '';
            if (method === 'NONE') {
                return false;
            }
            return method.startsWith('SAMPLE-AES') || (key.KEYFORMAT !== undefined && key.KEYFORMAT !== 'identity');
        });
    }

    function children(element, name) {
        return [...element.children].filter((child) => child.localName === name);
    }

    function setType(set) {
        const declared = set.getAttribute('contentType') || set.getAttribute('mimeType')
            || children(set, 'Representation')[0]?.getAttribute('mimeType') || '';
        return declared.split('/')[0];
    }

    function setProtected(set) {
        if (children(set, 'ContentProtection').length) {
            return true;
        }
        const representations = children(set, 'Representation');
        return representations.length > 0 && representations.every((r) => children(r, 'ContentProtection').length > 0);
    }

    function parseMpd(text) {
        const document = new DOMParser().parseFromString(text, 'application/xml');
        const root = document.documentElement;
        return root?.localName === 'MPD' ? root : null;
    }

    // A period in which every video or every audio adaptation set declares content protection, so
    // the selected one does too.
    function dashProtected(text) {
        const mpd = parseMpd(text);
        if (!mpd) {
            return false;
        }
        for (const period of children(mpd, 'Period')) {
            const byType = { video: [], audio: [] };
            for (const set of children(period, 'AdaptationSet')) {
                byType[setType(set)]?.push(set);
            }
            if (Object.values(byType).some((sets) => sets.length && sets.every(setProtected))) {
                return true;
            }
        }
        return false;
    }

    function kindOf(text) {
        if (text.trimStart().startsWith('#EXTM3U')) {
            return 'hls';
        }
        return /<MPD[\s>]/.test(text.slice(0, 8192)) ? 'dash' : null;
    }

    async function fetchManifest(url, credentials) {
        try {
            const response = await fetch(url, { credentials: credentials(url) });
            if (!response.ok) {
                console.warn('Light Media Center: loading a manifest failed with HTTP', response.status, url);
                return null;
            }
            const text = await response.text();
            const kind = kindOf(text);
            return kind ? { url, responseUrl: response.url || url, text, kind } : null;
        } catch (error) {
            console.warn('Light Media Center: loading a manifest failed', url, error);
            return null;
        }
    }

    // The URL of the first variant of a multivariant playlist.
    function firstVariant(manifest) {
        const match = /^#EXT-X-STREAM-INF:[^\r\n]*[\r\n]+(?:#[^\r\n]*[\r\n]+)*([^#\r\n][^\r\n]*)/m.exec(manifest.text);
        try {
            return match ? new URL(match[1].trim(), manifest.responseUrl).href : null;
        } catch {
            return null;
        }
    }

    function isLivePlaylist(text) {
        return !/^#EXT-X-ENDLIST/m.test(text) && !/^#EXT-X-PLAYLIST-TYPE:VOD/m.test(text);
    }

    function pathDepth(url) {
        try {
            return new URL(url).pathname.split('/').length;
        } catch {
            return Infinity;
        }
    }

    // Media playlist URLs a multivariant playlist names.
    function playlistUris(manifest) {
        const uris = [];
        for (const line of manifest.text.split(/\r?\n/)) {
            const uri = line.startsWith('#') ? /URI="([^"]*)"/.exec(line)?.[1] : line.trim();
            if (uri) {
                try {
                    uris.push(new URL(uri, manifest.responseUrl).href);
                } catch {
                    // A malformed line names no playlist.
                }
            }
        }
        return uris;
    }

    // The manifest the frame's player started from: the element's own source when it is a
    // manifest, or else the newest DASH manifest or HLS multivariant playlist the frame loaded, or
    // the newest HLS media playlist when it loaded no multivariant one. Candidates are tried by
    // path depth and then from the newest, since a multivariant playlist usually sits above its
    // media playlists; one that names a media playlist the frame loaded last is taken at once.
    // Checks the choice for protection and tells whether it is live. Null when none loads.
    async function chooseManifest(video, records, credentials) {
        const source = video.currentSrc;
        const newest = [...new Set(records.filter((r) => r.kind === 'hls' || r.kind === 'dash').reverse().map((r) => r.url))];
        const recent = new Set(newest.slice(0, 3));
        const candidates = /^https?:/.test(source)
            ? [source]
            : newest.map((url, order) => ({ url, order, depth: pathDepth(url) }))
                .sort((a, b) => a.depth - b.depth || a.order - b.order)
                .map((c) => c.url);
        let chosen = null;
        let multivariant = null;
        for (const url of candidates.slice(0, maxManifestProbes)) {
            const manifest = await fetchManifest(url, credentials);
            if (!manifest) {
                continue;
            }
            if (manifest.kind === 'dash') {
                chosen = manifest;
                multivariant = null;
                break;
            }
            if (/^#EXT-X-STREAM-INF:/m.test(manifest.text)) {
                if (playlistUris(manifest).some((uri) => recent.has(uri))) {
                    multivariant = manifest;
                    break;
                }
                multivariant ??= manifest;
            } else {
                chosen ??= manifest;
            }
        }
        chosen = multivariant ?? chosen;
        if (!chosen) {
            return null;
        }
        if (chosen.kind === 'dash') {
            const mpd = parseMpd(chosen.text);
            return { ...chosen, protected: dashProtected(chosen.text), live: mpd?.getAttribute('type') === 'dynamic' };
        }
        let media = chosen;
        const variant = firstVariant(chosen);
        if (variant) {
            media = await fetchManifest(variant, credentials);
            if (!media) {
                return null;
            }
        }
        return { ...chosen, protected: hlsProtected(chosen.text) || hlsProtected(media.text), live: isLivePlaylist(media.text) };
    }

    // The smallest rendition that covers the view in device pixels; renditions up to its size are
    // worth fetching. Null when the renditions carry no picture size.
    function coverFor(renditions, view) {
        const width = view.width * view.devicePixelRatio;
        const height = view.height * view.devicePixelRatio;
        const sized = renditions.filter((r) => r.width > 0 && r.height > 0).sort((a, b) => a.height - b.height || a.width - b.width);
        if (!sized.length) {
            return null;
        }
        return sized.find((r) => r.width >= width || r.height >= height) ?? sized[sized.length - 1];
    }

    function fits(rendition, cover) {
        return !cover || !(rendition.width > 0 && rendition.height > 0) || (rendition.width <= cover.width && rendition.height <= cover.height);
    }

    async function loadPart(part) {
        if (loadedParts.has(part)) {
            return;
        }
        if (!await api.runtime.sendMessage({ k: 'loadPlayer', part })) {
            throw new Error(`Loading the player part ${part} failed`);
        }
        loadedParts.add(part);
    }

    async function capabilities() {
        let ready = await globalThis.LightPlayerLink.cachedCapabilities();
        if (!ready) {
            const status = await api.runtime.sendMessage({ k: 'status' });
            if (!status?.reachable) {
                throw failure('unreachable');
            }
            if (!status.paired) {
                throw failure('notPaired');
            }
            ready = await globalThis.LightPlayerLink.cachedCapabilities();
        }
        if (!ready) {
            throw failure('unreachable');
        }
        return ready;
    }

    // Loads the remote media classes and the library into this frame. The realm is prepared before
    // the library is evaluated, because libraries capture media globals when they load.
    async function loadPlayer(kind, ready) {
        await loadPart('remote');
        globalThis.LightPlayerRemote.prepareRealm({ capabilities: ready });
        await loadPart(kind);
    }

    // Stands in for the session link until the redirect is confirmed: holds what the session sends,
    // then forwards both ways once `bind` connects it to the application.
    class PendingLink {
        constructor(capabilities, onFirstAppend) {
            this.capabilities = capabilities;
            this.onmessage = null;
            this.onclose = null;
            this.link = null;
            this.queue = [];
            this.onFirstAppend = onFirstAppend;
            this.lastState = null;
            this.encrypted = false;
        }

        send(message) {
            if (this.link) {
                this.link.send(message);
            } else {
                this.queue.push(() => this.link.send(message));
            }
        }

        append(buffer, sequence, bytes) {
            if (this.link) {
                this.link.append(buffer, sequence, bytes);
                return;
            }
            this.queue.push(() => this.link.append(buffer, sequence, bytes));
            const first = this.onFirstAppend;
            this.onFirstAppend = null;
            first?.();
        }

        bind(link) {
            this.link = link;
            this.capabilities = link.capabilities;
            link.onmessage = (message) => {
                // After `encrypted` a library may reopen the source before `close` arrives; the page
                // resumes at the position from before.
                if (message?.type === 'state' && !this.encrypted) {
                    this.lastState = message;
                } else if (message?.type === 'appendFailed' && message.reason === 'encrypted') {
                    this.encrypted = true;
                }
                this.onmessage?.(message);
            };
            link.onclose = (reason, detail) => this.onclose?.(reason, detail);
            for (const send of this.queue.splice(0)) {
                send();
            }
        }
    }

    // hls.js on the remote element. Playlists pass the protection check before the library parses them.
    function hlsPlayer(owner, plan) {
        const Hls = globalThis.Hls;
        const session = owner.session;
        const budget = plan.budgetBytes;
        const PlaylistLoader = class extends Hls.DefaultConfig.loader {
            load(context, config, callbacks) {
                super.load(context, config, {
                    ...callbacks,
                    onSuccess: (response, stats, loaderContext, details) => {
                        if (typeof response.data === 'string' && hlsProtected(response.data)) {
                            owner.fail('protected');
                            return;
                        }
                        callbacks.onSuccess(response, stats, loaderContext, details);
                    },
                });
            }
        };
        const hls = new Hls({
            enableWorker: false,
            startPosition: plan.live ? -1 : plan.startTime,
            capLevelToPlayerSize: false,
            // The library asks the browser's display otherwise, which is not where the video shows.
            videoPreference: { preferHDR: plan.hdr },
            maxBufferSize: Math.round(budget * forwardShare),
            maxBufferLength: 10,
            maxMaxBufferLength: 120,
            backBufferLength: 30,
            emeEnabled: false,
            emeController: null,
            enableWebVTT: false,
            enableIMSC1: false,
            enableCEA708Captions: false,
            subtitleTrackController: null,
            subtitleStreamController: null,
            timelineController: null,
            enableDateRangeMetadataCues: false,
            enableEmsgMetadataCues: false,
            enableID3MetadataCues: false,
            enableInterstitialPlayback: false,
            pLoader: PlaylistLoader,
            xhrSetup: (xhr, url) => {
                xhr.withCredentials = plan.credentials(url) === 'include';
            },
        });
        const Events = Hls.Events;

        function applyView() {
            const levels = hls.levels ?? [];
            if (!levels.length) {
                return;
            }
            const cover = coverFor(levels, session.view);
            let cap = -1;
            levels.forEach((level, index) => {
                if (fits(level, cover)) {
                    cap = index;
                }
            });
            hls.autoLevelCapping = cap === levels.length - 1 ? -1 : cap;
            const bitrate = levels[cap >= 0 ? cap : levels.length - 1].bitrate;
            if (bitrate > 0) {
                hls.config.backBufferLength = clamp(budget * backShare * 8 / bitrate, 10, 120);
            }
        }

        function onDetails(event, data) {
            const details = data.details;
            if (!details) {
                return;
            }
            owner.live = details.live;
            session.setLive(details.live);
            if (details.live) {
                session.setSeekableWindow(details.fragmentStart, details.edge);
            } else {
                session.setSeekableWindow(null);
            }
        }

        hls.on(Events.MEDIA_ATTACHED, () => {
            if (!plan.startPaused) {
                // A play() made before attaching is dropped when the library loads the element.
                session.element.play().catch(() => {});
            }
        });
        hls.on(Events.MANIFEST_PARSED, applyView);
        hls.on(Events.LEVELS_UPDATED, applyView);
        hls.on(Events.LEVEL_LOADED, onDetails);
        hls.on(Events.LEVEL_UPDATED, onDetails);
        hls.on(Events.ERROR, (event, data) => {
            if (data.fatal) {
                owner.playerFailed(`${data.type} ${data.details}`, data.type === Hls.ErrorTypes.NETWORK_ERROR);
            }
        });
        hls.attachMedia(session.element);
        hls.loadSource(plan.url);
        return {
            applyView,
            destroy: () => hls.destroy(),
        };
    }

    // dash.js on the remote element. Manifests pass the protection check before the library parses them.
    function dashPlayer(owner, plan) {
        const dashjs = globalThis.dashjs;
        const session = owner.session;
        const budget = plan.budgetBytes;
        const player = dashjs.MediaPlayer().create();
        const Events = dashjs.MediaPlayer.events;
        player.updateSettings({
            debug: { logLevel: dashjs.Debug.LOG_LEVEL_WARNING },
            streaming: {
                text: { defaultEnabled: false },
                abr: { limitBitrateByPortal: false },
            },
        });
        player.addRequestInterceptor((request) => {
            request.credentials = plan.credentials(request.url);
            return Promise.resolve(request);
        });
        player.addResponseInterceptor((response) => {
            if (typeof response?.data === 'string' && kindOf(response.data) === 'dash' && dashProtected(response.data)) {
                owner.fail('protected');
            }
            return Promise.resolve(response);
        });

        let started = false;

        function applyView() {
            if (!started) {
                return;
            }
            const video = player.getRepresentationsByType('video') ?? [];
            const audio = player.getRepresentationsByType('audio') ?? [];
            const cover = coverFor(video, session.view);
            const allowed = video.filter((r) => fits(r, cover));
            const top = Math.max(0, ...allowed.map((r) => r.bandwidth || 0));
            const capped = allowed.length > 0 && allowed.length < video.length;
            const bitrate = (top || Math.max(0, ...video.map((r) => r.bandwidth || 0))) + Math.max(0, ...audio.map((r) => r.bandwidth || 0));
            const settings = { streaming: { abr: { maxBitrate: { video: capped ? Math.ceil(top / 1000) : -1 } } } };
            if (bitrate > 0) {
                const forward = clamp(budget * forwardShare * 8 / bitrate, 8, 60);
                settings.streaming.buffer = {
                    bufferTimeDefault: Math.min(forward, 18),
                    bufferTimeAtTopQuality: forward,
                    bufferTimeAtTopQualityLongForm: forward,
                    bufferToKeep: clamp(budget * backShare * 8 / bitrate, 8, 60),
                };
            }
            player.updateSettings(settings);
        }

        function checkTracks() {
            for (const type of ['video', 'audio']) {
                if (player.getCurrentTrackFor(type)?.contentProtection?.length) {
                    owner.fail('protected');
                    return;
                }
            }
        }

        function updateTimeline() {
            if (!started) {
                return;
            }
            const live = player.isDynamic();
            owner.live = live;
            session.setLive(live);
            const dvr = live ? player.getDvrWindow() : null;
            if (dvr && Number.isFinite(dvr.start) && Number.isFinite(dvr.end)) {
                session.setSeekableWindow(dvr.start, dvr.end);
            } else {
                session.setSeekableWindow(null);
            }
        }

        player.on(Events.STREAM_INITIALIZED, () => {
            started = true;
            checkTracks();
            applyView();
            updateTimeline();
        });
        player.on(Events.PERIOD_SWITCH_COMPLETED, () => {
            checkTracks();
            applyView();
        });
        player.on(Events.TRACK_CHANGE_RENDERED, checkTracks);
        player.on(Events.MANIFEST_LOADED, updateTimeline);
        player.on(Events.PLAYBACK_TIME_UPDATED, updateTimeline);
        player.on(Events.ERROR, (event) => {
            const code = event?.error?.code;
            if (dashFatalErrors.has(code)) {
                owner.playerFailed(`dash.js error ${code} ${event.error.message ?? ''}`, code !== 23 && code !== 31 && code !== 32 && code !== 34);
            }
        });
        player.on(Events.PLAYBACK_ERROR, (event) => owner.playerFailed(`playback error ${event?.error?.message ?? ''}`, false));
        player.initialize(session.element, plan.url, !plan.startPaused, plan.live ? undefined : plan.startTime);
        return {
            applyView,
            destroy: () => player.destroy(),
        };
    }

    class ManifestSession {
        constructor(video, plan, ready, onEnded) {
            this.video = video;
            this.plan = plan;
            this.onEnded = onEnded;
            this.live = plan.live;
            this.ended = false;
            this.link = null;
            this.hold = null;
            this.player = null;
            this.failReason = null;
            this.started = new Promise((resolve, reject) => {
                this.resolveStart = resolve;
                this.rejectStart = reject;
            });
            this.onVisibility = () => this.updateTicks();
            this.pending = new PendingLink(ready, () => this.connect());
            this.session = globalThis.LightPlayerRemote.createSession(this.pending, {
                title: plan.title,
                pageUrl: plan.pageUrl,
                live: plan.live,
                viewWidth: screen.width,
                viewHeight: screen.height,
                devicePixelRatio: devicePixelRatio,
            });
            this.session.onclose = (reason, detail) => this.finish(reason, detail);
            this.session.onviewchange = () => this.player?.applyView();
            this.startTimer = setTimeout(() => this.fail('takeover'), startTimeoutMs);
        }

        begin() {
            const element = this.session.element;
            element.playbackRate = this.plan.rate;
            element.volume = this.plan.volume;
            element.muted = this.plan.muted;
            if (this.plan.kind === 'hls') {
                this.player = hlsPlayer(this, this.plan);
            } else {
                this.player = dashPlayer(this, this.plan);
            }
            return this.started;
        }

        // Opens the session link: the library has appended its first segment.
        async connect() {
            let link;
            try {
                link = await globalThis.LightPlayerLink.connect();
            } catch (error) {
                this.fail(error?.reason ?? 'error');
                return;
            }
            if (this.ended || this.video.currentSrc !== this.plan.source) {
                link.close(this.ended ? 'stopped' : 'sourceChanged');
                this.fail('sourceChanged');
                return;
            }
            clearTimeout(this.startTimer);
            this.link = link;
            this.hold = pageVideo.hold(this.video, this.plan.source, this.plan.startPaused, () => this.stop('sourceChanged'));
            link.ontick = () => globalThis.LightPlayerRemote.runDueTimers();
            this.pending.bind(link);
            document.addEventListener('visibilitychange', this.onVisibility);
            this.updateTicks();
            this.resolveStart(this);
        }

        // A hidden tab's timers are throttled; ticks from the background run the library's due timers.
        updateTicks() {
            if (this.link && !this.ended) {
                this.link.requestTicks(document.hidden ? hiddenTickMs : 0);
            }
        }

        stop(reason) {
            if (this.link) {
                this.session.close(reason);
            } else {
                this.fail(reason);
            }
        }

        // Ends the redirect. Before the link opened, the start fails with the reason; afterwards the
        // session closes with `encrypted` for protected media and `error` otherwise.
        fail(reason) {
            if (this.ended) {
                return;
            }
            this.failReason = reason;
            if (!this.link) {
                this.session.close('stopped');
            } else {
                this.session.close(reason === 'protected' ? 'encrypted' : 'error');
            }
        }

        playerFailed(detail, network) {
            console.warn('Light Media Center: the player stopped:', detail);
            if (this.pending.encrypted) {
                // The application closes the session with `encrypted` after its `appendFailed`.
                return;
            }
            if (this.link) {
                this.fail('error');
            } else {
                this.fail(network ? 'takeover' : 'unsupported');
            }
        }

        finish(reason, detail) {
            if (this.ended) {
                return;
            }
            if (reason === 'link') {
                // The link ended without a `close` message; its own reason is the detail.
                reason = detail ?? 'disconnected';
                detail = undefined;
            }
            this.ended = true;
            clearTimeout(this.startTimer);
            document.removeEventListener('visibilitychange', this.onVisibility);
            if (current === this) {
                current = null;
            }
            try {
                this.player?.destroy();
            } catch (error) {
                console.error('Light Media Center: stopping the player failed', error);
            }
            if (!this.link) {
                this.rejectStart(failure(this.failReason ?? reason));
                return;
            }
            this.hold.release(reason, this.pending.lastState, this.live);
            this.onEnded?.(this.video, reason, detail);
        }
    }

    // Credentials mode of the page's player per origin: cookies on a cross-origin request mean it
    // sent credentials there, which a same-origin request mode would not. Players often send them
    // to the manifest host only, and a media host that allows any origin refuses credentials.
    function credentialsFor(records) {
        const origins = new Set(records.filter((r) => r.cookies && originOf(r.url) !== location.origin).map((r) => originOf(r.url)));
        return (url) => (origins.has(originOf(url)) ? 'include' : 'same-origin');
    }

    // Starts a redirect of the video's manifest. Rejects with an Error whose `reason` is a link
    // failure, `protected`, `takeover` (the manifest does not load from the extension),
    // `unsupported` (the library found nothing it can play), `sourceChanged`, or the reason given
    // to `stop`.
    async function start(video, onEnded) {
        if (current?.video === video && !current.ended) {
            return current.started;
        }
        current?.stop('replaced');
        const source = video.currentSrc;
        const startTime = video.currentTime;
        const startPaused = video.paused;
        const info = await api.runtime.sendMessage({ k: 'manifestRequest' });
        const records = info?.requests ?? [];
        if (!skipsServiceWorker) {
            // Content-script requests go through the page's service worker here, so its manifests count.
            records.push(...(info?.workerRequests ?? []));
            records.sort((a, b) => a.time - b.time);
        }
        const credentials = credentialsFor(records);
        const manifest = await chooseManifest(video, records, credentials);
        if (!manifest) {
            throw failure('takeover');
        }
        if (manifest.protected) {
            throw failure('protected');
        }
        const ready = await capabilities();
        await loadPlayer(manifest.kind, ready);
        if (video.currentSrc !== source) {
            throw failure('sourceChanged');
        }
        const plan = {
            source,
            url: manifest.url,
            kind: manifest.kind,
            live: manifest.live,
            credentials,
            startTime,
            startPaused,
            rate: video.playbackRate,
            volume: video.volume,
            muted: video.muted,
            title: pageVideo.title(video, info?.tabTitle),
            pageUrl: info?.tabUrl ?? location.href,
            budgetBytes: ready.budgetBytes > 0 ? ready.budgetBytes : defaultBudgetBytes,
            hdr: ready.hdr === true,
        };
        const session = new ManifestSession(video, plan, ready, onEnded);
        current = session;
        return session.begin();
    }

    function active() {
        return current && !current.ended ? current : null;
    }

    globalThis.LightPlayerManifestSession = Object.freeze({ start, active });
})();
