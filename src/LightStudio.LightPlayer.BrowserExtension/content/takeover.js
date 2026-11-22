// Takeover relay: the content-script end of the page-world shim of this frame. Everything that
// arrives from the page world is treated as hostile: messages are rebuilt from validated fields,
// only `source` session traffic of a tab the background reports armed reaches the application, and
// buffer ids, sequence and command numbers on the link are this script's own. It also shows the
// placeholder over each taken-over video.
(() => {
    const api = globalThis.browser ?? globalThis.chrome;
    const pageVideo = globalThis.LightPlayerPageVideo;
    const launch = globalThis.LightPlayerLaunch;
    const text = (key, substitutions) => api.i18n.getMessage(key, substitutions);

    const flagKey = 'lightplayer.takeover';
    const maxAppendBytes = 64 * 1024 * 1024;
    const maxQueued = 4096;
    const maxBuffers = 8;
    const maxMimeType = 256;
    const maxUrl = 2048;
    // Messages from the page world per second, with bursts up to the capacity.
    const messageRate = 5000;
    const messageBurst = 10000;
    const protectedReloadMs = 3000;
    const renderRetryMs = 300;
    const closeReasons = new Set(['stopped', 'sourceChanged', 'replaced', 'disconnected', 'encrypted', 'error']);
    const commandTypes = new Set(['play', 'pause', 'seek', 'rate', 'volume']);
    const bufferTypes = new Set(['removeSourceBuffer', 'changeType', 'configure', 'remove', 'abort']);

    const endMessages = {
        stopped: 'placeholderClosed',
        replaced: 'placeholderReplaced',
        sourceChanged: 'placeholderReplaced',
        notPaired: 'messageNotPaired',
        unreachable: 'messageUnreachable',
        // A taken-over session fetches nothing, so `unauthorized` means a revoked pairing.
        unauthorized: 'pairingStateRevoked',
        unsupported: 'messageUnsupported',
        encrypted: 'placeholderProtected',
        disconnected: 'messageDisconnected',
        protocol: 'messageProtocol',
    };

    let port = null;
    // null until the background answered, then whether this tab is armed.
    let armed = null;
    let tabInfo = { title: null, url: null };
    let protectedMode = false;
    let reloading = false;
    // Auto-connect until a link ended; afterwards a waiting video connects on the user's request.
    let autoConnect = true;
    let lastEnd = null;
    // Starting the application after the frame's link found it unreachable: null, or the phase
    // (`waiting`, `notPaired`, `otherPort`, `noAnswer`, `failed`) with its status and progress.
    let start = null;
    const sessions = new Map();
    let link = null;
    let tokens = messageBurst;
    let tokensAt = performance.now();

    // ---- validation of page-world messages ----

    const isFinite = (value) => typeof value === 'number' && Number.isFinite(value);
    const isId = (value) => Number.isInteger(value) && value > 0 && value < 0x80000000;
    const isMime = (value) => typeof value === 'string' && value.length > 0 && value.length <= maxMimeType;

    function violation(detail) {
        const error = new Error(detail);
        error.violation = true;
        return error;
    }

    function check(condition, detail) {
        if (!condition) {
            throw violation(detail);
        }
    }

    // A fresh protocol message with only the members of its type, each of the right type and range.
    function sanitize(m) {
        check(m !== null && typeof m === 'object', 'message is not an object');
        const type = m.type;
        const out = { type };
        switch (type) {
            case 'open':
                check(m.kind === 'source', 'open is not of kind source');
                return {
                    type,
                    kind: 'source',
                    title: '',
                    pageUrl: '',
                    startTime: isFinite(m.startTime) && m.startTime >= 0 ? m.startTime : 0,
                    paused: m.paused === true,
                    rate: isFinite(m.rate) && m.rate >= 0 && m.rate <= 16 ? m.rate : 1,
                    volume: isFinite(m.volume) && m.volume >= 0 && m.volume <= 1 ? m.volume : 1,
                    muted: m.muted === true,
                    live: typeof m.live === 'boolean' ? m.live : null,
                };
            case 'addSourceBuffer':
            case 'changeType':
                check(isId(m.buffer) && isMime(m.mimeType), `${type} is malformed`);
                return { type, buffer: m.buffer, mimeType: m.mimeType };
            case 'removeSourceBuffer':
            case 'abort':
                check(isId(m.buffer), `${type} is malformed`);
                return { type, buffer: m.buffer };
            case 'configure':
                check(isId(m.buffer), 'configure is malformed');
                out.buffer = m.buffer;
                for (const key of ['timestampOffset', 'appendWindowStart', 'appendWindowEnd']) {
                    if (Object.hasOwn(m, key)) {
                        check(m[key] === null || isFinite(m[key]), 'configure has a malformed value');
                        out[key] = m[key];
                    }
                }
                return out;
            case 'remove':
                check(isId(m.buffer) && isId(m.request) && isFinite(m.start) && m.start >= 0
                    && (m.end === null || (isFinite(m.end) && m.end > m.start)), 'remove is malformed');
                return { type, buffer: m.buffer, request: m.request, start: m.start, end: m.end };
            case 'duration':
                check(m.value === null || (isFinite(m.value) && m.value >= 0), 'duration is malformed');
                return { type, value: m.value };
            case 'endOfStream':
                check(m.error === null || m.error === undefined || m.error === 'network' || m.error === 'decode', 'endOfStream is malformed');
                return { type, error: m.error ?? null };
            case 'timeline':
                check(typeof m.live === 'boolean' && isFinite(m.seekableStart) && isFinite(m.seekableEnd), 'timeline is malformed');
                return { type, live: m.live, seekableStart: m.seekableStart, seekableEnd: m.seekableEnd };
            case 'play':
            case 'pause':
                check(isId(m.command), `${type} is malformed`);
                return { type, command: m.command };
            case 'seek':
                check(isId(m.command) && isFinite(m.time), 'seek is malformed');
                return { type, command: m.command, time: m.time };
            case 'rate':
                check(isId(m.command) && isFinite(m.value) && m.value >= 0 && m.value <= 16, 'rate is malformed');
                return { type, command: m.command, value: m.value };
            case 'volume':
                check(isId(m.command) && isFinite(m.value) && m.value >= 0 && m.value <= 1 && typeof m.muted === 'boolean', 'volume is malformed');
                return { type, command: m.command, value: m.value, muted: m.muted };
            case 'close':
                check(closeReasons.has(m.reason), 'close has an unknown reason');
                return { type, reason: m.reason };
            default:
                throw violation(`message type ${String(type).slice(0, 40)} is not accepted`);
        }
    }

    function spendToken() {
        const now = performance.now();
        tokens = Math.min(messageBurst, tokens + ((now - tokensAt) / 1000) * messageRate);
        tokensAt = now;
        check(tokens >= 1, 'the page world sends too many messages');
        tokens -= 1;
    }

    // ---- sessions ----

    class Relay {
        constructor(sid) {
            this.sid = sid;
            this.url = null;
            this.status = 'pending';
            this.reason = null;
            this.closed = false;
            this.protected = false;
            this.attached = false;
            this.queue = [];
            this.opened = false;
            this.lastCommand = 0;
            // Page-world view: buffers declared since the last `open`, their last sequence and the
            // sequence of the append on the wire.
            this.declared = new Set();
            this.everDeclared = new Set();
            this.sequences = new Map();
            this.inFlight = new Map();
            // Link view: page-world buffer id <-> link buffer id, and link -> page command numbers.
            this.toLink = new Map();
            this.fromLink = new Map();
            this.commands = [];
            this.placeholder = null;
            this.videoRef = null;
            this.retry = 0;
        }

        // Validates one page-world message against the session's state and returns what to queue.
        accept(m) {
            const message = sanitize(m);
            if (message.type !== 'open' && message.type !== 'close') {
                check(this.opened, `${message.type} before open`);
            }
            switch (message.type) {
                case 'open':
                    this.opened = true;
                    this.declared.clear();
                    this.inFlight.clear();
                    break;
                case 'addSourceBuffer':
                    check(!this.everDeclared.has(message.buffer) && this.declared.size < maxBuffers, 'addSourceBuffer is not allowed');
                    this.declared.add(message.buffer);
                    this.everDeclared.add(message.buffer);
                    break;
                case 'close':
                    break;
                default:
                    if (bufferTypes.has(message.type)) {
                        check(this.declared.has(message.buffer), `${message.type} names an undeclared buffer`);
                        if (message.type === 'removeSourceBuffer') {
                            this.declared.delete(message.buffer);
                            this.inFlight.delete(message.buffer);
                        }
                    } else if (commandTypes.has(message.type)) {
                        check(message.command > this.lastCommand, 'command numbers do not increase');
                        this.lastCommand = message.command;
                    }
            }
            return { message };
        }

        acceptAppend(m) {
            check(isId(m.buffer) && isId(m.sequence), 'append is malformed');
            check(Object.prototype.toString.call(m.data) === '[object ArrayBuffer]', 'append carries no buffer');
            check(m.data.byteLength <= maxAppendBytes, 'append is too large');
            check(this.opened && this.declared.has(m.buffer), 'append names an undeclared buffer');
            check(!this.inFlight.has(m.buffer), 'a second append is in flight');
            check(m.sequence === (this.sequences.get(m.buffer) ?? 0) + 1, 'append sequence does not follow');
            this.sequences.set(m.buffer, m.sequence);
            this.inFlight.set(m.buffer, m.sequence);
            return { append: true, buffer: m.buffer, data: m.data };
        }

        video() {
            const known = this.videoRef?.deref();
            if (known?.isConnected && known.currentSrc === this.url) {
                return known;
            }
            if (!this.url) {
                return null;
            }
            const found = globalThis.LightPlayerVideos.all(true).find((v) => v.currentSrc === this.url) ?? null;
            this.videoRef = found ? new WeakRef(found) : null;
            return found;
        }
    }

    function post(message) {
        try {
            port.postMessage(message);
        } catch (error) {
            console.error('Light Media Center: the page-world shim cannot be reached', error);
        }
    }

    function toShim(relay, message) {
        post({ k: 'msg', sid: relay.sid, message });
    }

    function endRelay(relay, reason) {
        if (relay.closed) {
            return;
        }
        relay.closed = true;
        relay.reason = reason;
        relay.queue = [];
        relay.status = reason === 'encrypted' ? 'protected' : 'ended';
        post({ k: 'closed', sid: relay.sid, reason });
        render(relay);
    }

    // A page-world violation ends that session; the application hears `error` if it was connected.
    function fail(relay, error) {
        console.warn('Light Media Center: the page world broke the takeover protocol:', error.message);
        if (link?.owner === relay) {
            closeLink('error');
        }
        endRelay(relay, 'error');
    }

    // ---- the link to the application ----

    function closeLink(reason) {
        const current = link;
        if (!current) {
            return;
        }
        link = null;
        if (current.link && !current.link.closed) {
            current.link.close(reason);
        }
        current.closing = true;
    }

    function idle(relay) {
        return relay.closed || !relay.attached || !relay.video();
    }

    // Gives the link to a session that wants to append. An idle owner hands it over, and the
    // newcomer's queued `open` resets the application's source.
    function requestLink(relay, force) {
        if (relay.closed || relay.protected || protectedMode || armed !== true) {
            return;
        }
        if (link) {
            if (link.owner === relay) {
                return;
            }
            const owner = link.owner;
            if (force || idle(owner)) {
                link.owner = relay;
                endRelay(owner, force ? 'replaced' : 'sourceChanged');
                if (link.link) {
                    flush(relay);
                } else {
                    relay.status = 'connecting';
                    render(relay);
                }
            } else {
                relay.status = 'waiting';
                render(relay);
            }
            return;
        }
        if (!autoConnect && !force) {
            relay.status = 'waiting';
            render(relay);
            return;
        }
        autoConnect = true;
        connect(relay);
    }

    async function connect(relay) {
        const current = { link: null, owner: relay, nextBuffer: 1, command: 0, sequences: new Map(), closing: false };
        link = current;
        relay.status = 'connecting';
        render(relay);
        let opened;
        try {
            opened = await globalThis.LightPlayerLink.connect({ takeover: true });
        } catch (error) {
            if (link === current) {
                link = null;
            }
            // A link that cannot open is not retried for every new source the page tries.
            autoConnect = false;
            lastEnd = error?.reason ?? 'error';
            if (start?.phase !== 'waiting') {
                start = null;
            }
            endRelay(current.owner, error?.reason ?? 'error');
            if (error?.reason === 'unreachable') {
                offerPort().catch((failure) => console.error('Light Media Center: checking the application\'s port failed', failure));
            }
            if (!error?.reason) {
                console.error('Light Media Center: connecting a taken-over video failed', error);
            }
            return;
        }
        if (link !== current || current.owner.closed) {
            opened.close(current.owner.protected ? 'encrypted' : 'stopped');
            if (link === current) {
                link = null;
            }
            return;
        }
        current.link = opened;
        opened.onmessage = (message) => onAppMessage(current, message);
        opened.onclose = (reason, detail) => onLinkClosed(current, reason, detail);
        flush(current.owner);
    }

    function flush(relay) {
        relay.status = 'playing';
        render(relay);
        const queued = relay.queue.splice(0);
        for (const item of queued) {
            forward(relay, item);
        }
    }

    // Puts one validated item on the link with the link's own buffer ids and numbers.
    function forward(relay, item) {
        const current = link;
        if (!current?.link || current.owner !== relay || relay.closed) {
            return;
        }
        if (item.append) {
            const id = relay.toLink.get(item.buffer);
            if (!id) {
                return;
            }
            const sequence = (current.sequences.get(id) ?? 0) + 1;
            current.sequences.set(id, sequence);
            current.link.append(id, sequence, item.data);
            return;
        }
        const m = Object.assign({}, item.message);
        switch (m.type) {
            case 'open': {
                relay.toLink.clear();
                const video = relay.video();
                m.title = (video && pageVideo.title(video, tabInfo.title)) || tabInfo.title || document.title || '';
                m.pageUrl = tabInfo.url ?? location.href;
                break;
            }
            case 'addSourceBuffer': {
                const id = current.nextBuffer++;
                relay.toLink.set(m.buffer, id);
                relay.fromLink.set(id, m.buffer);
                m.buffer = id;
                break;
            }
            default:
                if (bufferTypes.has(m.type)) {
                    const id = relay.toLink.get(m.buffer);
                    if (!id) {
                        return;
                    }
                    if (m.type === 'removeSourceBuffer') {
                        relay.toLink.delete(m.buffer);
                    }
                    m.buffer = id;
                } else if (commandTypes.has(m.type)) {
                    const number = ++current.command;
                    relay.commands.push([number, m.command]);
                    m.command = number;
                }
        }
        current.link.send(m);
    }

    // Application messages for the owner, translated back to the page world's ids and numbers.
    function onAppMessage(current, m) {
        const relay = current.owner;
        if (link !== current || relay.closed || !m || typeof m !== 'object') {
            return;
        }
        const out = Object.assign({}, m);
        switch (m.type) {
            case 'appended':
            case 'appendFailed': {
                const buffer = relay.fromLink.get(m.buffer);
                if (buffer === undefined || !relay.inFlight.has(buffer)) {
                    return;
                }
                out.buffer = buffer;
                out.sequence = relay.inFlight.get(buffer);
                relay.inFlight.delete(buffer);
                toShim(relay, out);
                if (m.type === 'appendFailed' && m.reason === 'encrypted') {
                    onProtected(relay);
                }
                return;
            }
            case 'removed':
            case 'buffered': {
                const buffer = relay.fromLink.get(m.buffer);
                if (buffer === undefined) {
                    return;
                }
                out.buffer = buffer;
                toShim(relay, out);
                return;
            }
            case 'state': {
                let ack = 0;
                let keep = 0;
                relay.commands.forEach(([number, own], index) => {
                    if (number <= m.ack) {
                        ack = own;
                        keep = index;
                    }
                });
                relay.commands.splice(0, keep);
                out.ack = ack;
                toShim(relay, out);
                return;
            }
            case 'close':
                toShim(relay, out);
                return;
            default:
                // `ready` and anything newer stay in this script.
        }
    }

    function onLinkClosed(current, reason, detail) {
        if (detail) {
            console.warn('Light Media Center: the taken-over session ended:', reason, detail);
        }
        if (link === current) {
            link = null;
        }
        autoConnect = false;
        lastEnd = reason;
        const relay = current.owner;
        if (!relay.closed) {
            post({ k: 'closed', sid: relay.sid, reason });
            relay.closed = true;
            relay.reason = reason;
            relay.queue = [];
            relay.status = reason === 'encrypted' ? 'protected' : 'ended';
        }
        if (reason === 'encrypted') {
            onProtected(relay);
        }
        render(relay);
    }

    // ---- protection ----

    // Protected media ends the takeover: nothing further reaches the application, and the tab
    // returns to the browser's own playback after the placeholder said why.
    function onProtected(relay) {
        relay.protected = true;
        if (!protectedMode) {
            protectedMode = true;
            for (const other of sessions.values()) {
                other.queue = [];
            }
            if (link) {
                closeLink('encrypted');
            }
            setTimeout(() => returnToBrowser(), protectedReloadMs);
        }
        endRelay(relay, 'encrypted');
        relay.status = 'protected';
        render(relay);
    }

    // ---- messages from the page world ----

    function onShimMessage(event) {
        const m = event.data;
        if (!m || typeof m !== 'object' || !isId(m.sid)) {
            return;
        }
        let relay = sessions.get(m.sid);
        try {
            spendToken();
            if (m.k === 'session') {
                check(!relay, 'session id reused');
                relay = new Relay(m.sid);
                sessions.set(m.sid, relay);
                return;
            }
            if (!relay) {
                return;
            }
            switch (m.k) {
                case 'element':
                    check(typeof m.url === 'string' && m.url.startsWith('blob:') && m.url.length <= maxUrl, 'element is malformed');
                    relay.url = m.url;
                    relay.videoRef = null;
                    render(relay);
                    return;
                case 'attached':
                    relay.attached = m.value === true;
                    render(relay);
                    return;
                case 'protected':
                    onProtected(relay);
                    return;
                case 'send':
                case 'append':
                    break;
                default:
                    return;
            }
            if (relay.closed || relay.protected || protectedMode) {
                return;
            }
            const item = m.k === 'append' ? relay.acceptAppend(m) : relay.accept(m.message);
            if (item.message?.type === 'close') {
                if (link?.owner === relay) {
                    closeLink(item.message.reason);
                }
                endRelay(relay, item.message.reason);
                return;
            }
            if (link?.owner === relay && link.link) {
                forward(relay, item);
                return;
            }
            check(relay.queue.length < maxQueued, 'too many messages wait for the link');
            relay.queue.push(item);
            if (item.append) {
                requestLink(relay, false);
            }
        } catch (error) {
            if (!error.violation) {
                throw error;
            }
            if (relay) {
                fail(relay, error);
            } else {
                console.warn('Light Media Center: the page world broke the takeover protocol:', error.message);
            }
        }
    }

    // The background decides whether this tab is armed; a frame that carries the flag without
    // being armed (a duplicated or restored tab) drops it and loads again natively.
    async function verify() {
        let result = null;
        try {
            result = await api.runtime.sendMessage({ k: 'takeoverState' });
        } catch (error) {
            console.error('Light Media Center: reading the takeover state failed', error);
        }
        armed = result?.armed === true;
        if (armed) {
            tabInfo = { title: result.tabTitle ?? null, url: result.tabUrl ?? null };
            if (result.capabilities) {
                post({ k: 'config', capabilities: result.capabilities });
            }
            for (const relay of sessions.values()) {
                if (relay.queue.some((item) => item.append)) {
                    requestLink(relay, false);
                }
            }
            return;
        }
        try {
            sessionStorage.removeItem(flagKey);
        } catch (error) {
            if (error?.name !== 'SecurityError') {
                throw error;
            }
        }
        for (const relay of sessions.values()) {
            endRelay(relay, 'error');
        }
        if (sessions.size && !reloading) {
            reloading = true;
            location.reload();
        } else {
            post({ k: 'standDown' });
        }
    }

    // The shim's greeting carries the port of this frame; it is the first window message of the
    // document, and later greetings are ignored. Page listeners do not see it.
    window.addEventListener('message', (event) => {
        if (event.source !== window) {
            return;
        }
        const data = event.data;
        if (!data || typeof data !== 'object' || data.lightplayerTakeover !== 'hello') {
            return;
        }
        event.stopImmediatePropagation();
        if (port || event.ports?.length !== 1) {
            return;
        }
        port = event.ports[0];
        port.onmessage = onShimMessage;
        verify().catch((error) => console.error('Light Media Center: starting the takeover relay failed', error));
    }, true);

    // ---- placeholder ----

    function render(relay) {
        const video = relay.video();
        const visible = video && (relay.attached || relay.closed);
        if (!visible) {
            relay.placeholder?.hide();
            // The native element names the source once its resource selection ran.
            if (!video && relay.url && (relay.attached || relay.closed) && !relay.retry) {
                relay.retry = setTimeout(() => {
                    relay.retry = 0;
                    render(relay);
                }, renderRetryMs);
            }
            return;
        }
        relay.placeholder ??= globalThis.LightPlayerPlaceholder.create();
        const watchHere = { actionLabel: text('controlReturn'), onAction: () => returnToBrowser() };
        let options;
        if (startable(relay)) {
            relay.placeholder.show(video, startOptions());
            return;
        }
        switch (relay.status) {
            case 'playing':
                options = { text: text('controlPlaying'), ...watchHere };
                break;
            case 'waiting':
                // Another session has the link, or the last link ended and this one waits for the user.
                options = {
                    text: text(link ? 'placeholderWaiting' : endMessages[lastEnd] ?? 'messageError'),
                    actionLabel: text('controlRedirect'),
                    onAction: () => requestLink(relay, true),
                    secondaryLabel: text('controlReturn'),
                    onSecondary: () => returnToBrowser(),
                };
                break;
            case 'protected':
                options = { text: text('placeholderProtected') };
                break;
            case 'ended':
                options = { text: text(endMessages[relay.reason] ?? 'messageError'), ...watchHere };
                break;
            default:
                options = { text: text('controlConnecting'), ...watchHere };
        }
        relay.placeholder.show(video, options);
    }

    // A session that ended because the application did not answer, or that waits after that.
    function startable(relay) {
        return !link && lastEnd === 'unreachable'
            && (relay.status === 'waiting' || (relay.status === 'ended' && relay.reason === 'unreachable'));
    }

    function startOptions() {
        const watchHere = { secondaryLabel: text('controlReturn'), onSecondary: () => returnToBrowser() };
        const tryAgain = { actionLabel: text('startTryAgain'), onAction: () => startApp(true) };
        switch (start?.phase) {
            case 'waiting':
                return { text: text('startWaiting'), progress: start.progress, ...watchHere };
            case 'notPaired':
                return {
                    text: text('messageNotPaired'),
                    actionLabel: text('messagePairAction'),
                    onAction: () => api.runtime.sendMessage({ k: 'openOptions' }),
                    ...watchHere,
                };
            case 'otherPort': {
                const port = start.status.otherPort;
                return {
                    text: text('startOtherPort', [String(port), String(start.status.port)]),
                    actionLabel: text('startUsePort', [String(port)]),
                    onAction: () => api.runtime.sendMessage({ k: 'usePort', port }).then(() => startApp(false)),
                    ...watchHere,
                };
            }
            case 'noAnswer':
                return { text: text('startNoAnswer', [String(start.status.port ?? '')]), ...tryAgain, ...watchHere };
            case 'failed':
                return { text: text('messageError'), ...tryAgain, ...watchHere };
            default:
                return { text: text('startOffer'), actionLabel: text('startAction'), onAction: () => startApp(true), ...watchHere };
        }
    }

    // An application that answers on the default port instead of the extension's needs the port
    // changed, not a start.
    async function offerPort() {
        const status = await api.runtime.sendMessage({ k: 'status', alternatives: true });
        if (start === null && launch.outcome(status) === 'otherPort') {
            start = { phase: 'otherPort', status };
            renderAll();
        }
    }

    function renderAll() {
        for (const relay of sessions.values()) {
            render(relay);
        }
    }

    // Opens the application's URL when `open` is set and waits for it. Once it answers, the frame
    // reloads: the tab stays armed, and the page's player starts again and connects.
    async function startApp(open) {
        if (start?.phase === 'waiting' || reloading) {
            return;
        }
        start = { phase: 'waiting', progress: 0 };
        renderAll();
        try {
            if (open && !await launch.open()) {
                start = { phase: 'failed' };
            } else {
                const status = await launch.wait((progress) => {
                    start.progress = progress;
                    renderAll();
                });
                const outcome = launch.outcome(status);
                if (outcome === 'ready') {
                    reloading = true;
                    location.reload();
                    return;
                }
                start = { phase: outcome === 'unreachable' ? 'noAnswer' : outcome, status };
            }
        } catch (error) {
            console.error('Light Media Center: starting the application failed', error);
            start = { phase: 'failed' };
        }
        renderAll();
    }

    // The application is being started for this tab from elsewhere, such as the popup.
    function onAppStarting() {
        if (armed === true && [...sessions.values()].some(startable)) {
            startApp(false);
        }
    }

    // Ends every session of the frame, disarms the tab and reloads it, so the page plays natively.
    function returnToBrowser() {
        if (reloading) {
            return;
        }
        reloading = true;
        closeLink('stopped');
        api.runtime.sendMessage({ k: 'takeoverDisarm' }).catch((error) => {
            console.error('Light Media Center: returning the tab to the browser failed', error);
            reloading = false;
        });
    }

    // Asks the background to arm this tab, which reloads it. Resolves with the failure reason, or
    // null once the reload is underway.
    async function arm() {
        const result = await api.runtime.sendMessage({ k: 'takeoverArm' });
        return result?.ok ? null : (result?.reason ?? 'error');
    }

    function relayFor(video) {
        if (!video?.currentSrc) {
            return null;
        }
        let found = null;
        for (const relay of sessions.values()) {
            if (relay.url === video.currentSrc) {
                found = relay;
            }
        }
        return found;
    }

    // The session state of a video for the control and the popup, or null when it is not taken over.
    function statusOf(video) {
        const relay = relayFor(video);
        return relay ? { status: relay.status, reason: relay.reason, active: relay.status === 'playing' } : null;
    }

    function play(video) {
        const relay = relayFor(video);
        if (relay) {
            requestLink(relay, true);
        }
        return !!relay;
    }

    globalThis.LightPlayerTakeover = Object.freeze({
        get armed() { return armed === true; },
        statusOf,
        arm,
        play,
        returnToBrowser,
        onAppStarting,
    });
})();
