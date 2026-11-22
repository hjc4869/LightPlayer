// Plain-file redirect: the application fetches the element's file with the headers the browser
// sent, and the page's video stays paused until the session ends.
(() => {
    const api = globalThis.browser ?? globalThis.chrome;
    const pageVideo = globalThis.LightPlayerPageVideo;

    let current = null;

    // The Referer the default referrer policy produces, for a file whose request was not observed.
    function defaultReferer(url) {
        try {
            const page = new URL(location.href);
            const target = new URL(url);
            if (page.protocol !== 'http:' && page.protocol !== 'https:') {
                return null;
            }
            if (page.protocol === 'https:' && target.protocol === 'http:') {
                return null;
            }
            page.hash = '';
            return page.origin === target.origin ? page.href : `${page.origin}/`;
        } catch {
            return null;
        }
    }

    function headersFor(url, request) {
        const headers = {};
        if (request) {
            if (request.referer) {
                headers.referer = request.referer;
            }
            if (request.origin) {
                headers.origin = request.origin;
            }
            if (request.userAgent) {
                headers.userAgent = request.userAgent;
            }
        } else {
            const referer = defaultReferer(url);
            if (referer) {
                headers.referer = referer;
            }
        }
        headers.userAgent ??= navigator.userAgent;
        return headers;
    }

    class FileSession {
        constructor(video, link, source, startPaused, onEnded) {
            this.video = video;
            this.link = link;
            this.onEnded = onEnded;
            this.lastState = null;
            this.ended = false;
            this.hold = pageVideo.hold(video, source, startPaused, () => this.stop('sourceChanged'));
            link.onmessage = (message) => {
                if (message?.type === 'state') {
                    this.lastState = message;
                }
            };
            link.onclose = (reason, detail) => this.finish(reason, detail);
        }

        stop(reason) {
            if (!this.ended) {
                this.link.close(reason);
            }
        }

        finish(reason, detail) {
            if (this.ended) {
                return;
            }
            this.ended = true;
            if (current === this) {
                current = null;
            }
            this.hold.release(reason, this.lastState);
            this.onEnded?.(this.video, reason, detail);
        }
    }

    // Starts a session for the video's current file. Rejects with an Error whose `reason` names
    // why the application could not be reached.
    async function start(video, onEnded) {
        if (current?.video === video && !current.ended) {
            return current;
        }
        // Another redirected video in this frame keeps its page paused at the application's position.
        current?.stop('replaced');
        const source = video.currentSrc;
        const startTime = video.currentTime;
        const startPaused = video.paused;
        const info = await api.runtime.sendMessage({ k: 'fileRequest', url: source });
        const link = await globalThis.LightPlayerLink.connect();
        if (video.currentSrc !== source) {
            link.close('sourceChanged');
            const error = new Error('sourceChanged');
            error.reason = 'sourceChanged';
            throw error;
        }
        const session = new FileSession(video, link, source, startPaused, onEnded);
        current = session;
        link.send({
            type: 'open',
            kind: 'file',
            title: pageVideo.title(video, info?.tabTitle),
            pageUrl: info?.tabUrl ?? location.href,
            startTime,
            paused: startPaused,
            rate: video.playbackRate,
            volume: video.volume,
            muted: video.muted,
            live: pageVideo.liveFlag(video),
            urls: [source],
            headers: headersFor(source, info?.request),
        });
        return session;
    }

    function active() {
        return current && !current.ended ? current : null;
    }

    globalThis.LightPlayerFileSession = Object.freeze({ start, active });
})();
