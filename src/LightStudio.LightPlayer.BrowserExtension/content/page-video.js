// The page's own video while a redirect is active: held paused, watched for a new source, and
// resumed at the application's position when the redirect ends.
(() => {
    function fileName(url) {
        try {
            const path = new URL(url).pathname;
            return decodeURIComponent(path.slice(path.lastIndexOf('/') + 1)) || null;
        } catch {
            return null;
        }
    }

    // The page's Media Session title, the tab title, the frame's title or the file name.
    function title(video, tabTitle) {
        const mediaTitle = navigator.mediaSession?.metadata?.title;
        return mediaTitle || tabTitle || document.title || fileName(video.currentSrc) || '';
    }

    // true for an unbounded duration, false for a finite one, null while unknown.
    function liveFlag(video) {
        const duration = video.duration;
        if (Number.isFinite(duration)) {
            return false;
        }
        return duration === Infinity ? true : null;
    }

    class Hold {
        constructor(video, source, startPaused, onSourceChanged) {
            this.video = video;
            this.source = source;
            this.startPaused = startPaused;
            this.released = false;
            this.onPlay = () => {
                if (!this.released) {
                    video.pause();
                }
            };
            this.onLoadStart = () => {
                if (!this.released && video.currentSrc !== this.source) {
                    onSourceChanged();
                }
            };
            video.addEventListener('play', this.onPlay);
            video.addEventListener('loadstart', this.onLoadStart);
            video.pause();
        }

        // Puts the video at the application's last position. It plays again when the application
        // was playing, or, without any state, when the page was playing before. A live position is
        // not carried over, because the page's player keeps its own live timeline.
        release(reason, state, live = false) {
            if (this.released) {
                return;
            }
            this.released = true;
            const video = this.video;
            video.removeEventListener('play', this.onPlay);
            video.removeEventListener('loadstart', this.onLoadStart);
            if (reason === 'sourceChanged' || !video.isConnected) {
                return;
            }
            if (!live && state && Number.isFinite(state.time) && Number.isFinite(video.duration)) {
                try {
                    video.currentTime = Math.min(state.time, video.duration);
                } catch (error) {
                    console.error('Light Media Center: restoring the position failed', error);
                }
            }
            if (reason === 'replaced') {
                return;
            }
            const play = state ? !state.paused && !state.ended : !this.startPaused;
            if (play) {
                video.play().catch((error) => console.warn('Light Media Center: the page did not resume playback', error));
            }
        }
    }

    // Pauses the video until `release`; `onSourceChanged` runs when the page loads another source.
    function hold(video, source, startPaused, onSourceChanged) {
        return new Hold(video, source, startPaused, onSourceChanged);
    }

    globalThis.LightPlayerPageVideo = Object.freeze({ hold, title, liveFlag });
})();
