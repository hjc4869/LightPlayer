// The redirect control shown over a video. It lives in a closed shadow tree and is styled through
// CSSOM only, so page style sheets and a page's style-src policy do not affect it.
(() => {
    const api = globalThis.browser ?? globalThis.chrome;
    const text = (key) => api.i18n.getMessage(key);

    const hoverHideMs = 2500;
    const messageMs = 6000;
    const minVideoWidth = 160;
    const minVideoHeight = 90;
    const inset = 10;

    function css(element, properties) {
        for (const [name, value] of Object.entries(properties)) {
            element.style.setProperty(name, value, 'important');
        }
    }

    function icon() {
        const ns = 'http://www.w3.org/2000/svg';
        const svg = document.createElementNS(ns, 'svg');
        svg.setAttribute('viewBox', '0 0 24 24');
        svg.setAttribute('width', '16');
        svg.setAttribute('height', '16');
        svg.setAttribute('aria-hidden', 'true');
        const screen = document.createElementNS(ns, 'rect');
        for (const [name, value] of Object.entries({ x: '2.5', y: '4', width: '19', height: '13', rx: '2', fill: 'none', stroke: 'currentColor', 'stroke-width': '1.8' })) {
            screen.setAttribute(name, value);
        }
        const stand = document.createElementNS(ns, 'path');
        stand.setAttribute('d', 'M8 20.5h8');
        stand.setAttribute('stroke', 'currentColor');
        stand.setAttribute('stroke-width', '1.8');
        stand.setAttribute('stroke-linecap', 'round');
        const play = document.createElementNS(ns, 'path');
        play.setAttribute('d', 'M10 7.8v5.4l4.6-2.7z');
        play.setAttribute('fill', 'currentColor');
        svg.append(screen, stand, play);
        css(svg, { display: 'block', 'flex-shrink': '0' });
        return svg;
    }

    function create(handlers) {
        const host = document.createElement('div');
        const usePopover = typeof host.showPopover === 'function';
        css(host, {
            all: 'initial',
            position: 'fixed',
            inset: 'auto',
            left: '0px',
            top: '0px',
            margin: '0',
            padding: '0',
            border: '0',
            width: 'auto',
            height: 'auto',
            overflow: 'visible',
            background: 'transparent',
            'z-index': '2147483647',
            display: 'none',
        });
        if (usePopover) {
            host.popover = 'manual';
        }
        const root = host.attachShadow({ mode: 'closed' });

        const bar = document.createElement('div');
        css(bar, {
            display: 'flex',
            'align-items': 'center',
            gap: '8px',
            'box-sizing': 'border-box',
            'max-width': '360px',
            'min-height': '32px',
            padding: '6px 12px',
            'border-radius': '16px',
            background: 'rgba(24, 26, 30, 0.88)',
            color: '#ffffff',
            'font-family': 'system-ui, -apple-system, "Segoe UI", sans-serif',
            'font-size': '13px',
            'line-height': '18px',
            'font-weight': '500',
            'box-shadow': '0 2px 8px rgba(0, 0, 0, 0.35)',
            'user-select': 'none',
            'pointer-events': 'auto',
            cursor: 'default',
            position: 'relative',
        });
        const label = document.createElement('span');
        css(label, { 'white-space': 'normal', 'overflow-wrap': 'anywhere' });
        const action = document.createElement('button');
        action.type = 'button';
        css(action, {
            all: 'initial',
            'flex-shrink': '0',
            padding: '3px 10px',
            'border-radius': '12px',
            background: 'rgba(255, 255, 255, 0.18)',
            color: '#ffffff',
            'font-family': 'inherit',
            'font-size': '12px',
            'line-height': '18px',
            'font-weight': '600',
            cursor: 'pointer',
            display: 'none',
        });
        // Progress of a wait, along the bottom edge of the bar.
        const meter = document.createElement('div');
        css(meter, {
            position: 'absolute',
            left: '14px',
            right: '14px',
            bottom: '3px',
            height: '2px',
            'border-radius': '1px',
            background: 'rgba(255, 255, 255, 0.25)',
            overflow: 'hidden',
            display: 'none',
        });
        const fill = document.createElement('div');
        css(fill, { height: '100%', width: '0%', background: '#ffffff' });
        meter.append(fill);
        bar.append(icon(), label, action, meter);
        root.append(bar);

        let video = null;
        let mode = null;
        let shownClassification = null;
        let persistent = false;
        let actionHandler = null;
        let barHandler = null;
        let hideTimer = null;
        let messageTimer = null;
        let loop = null;
        let hovered = false;

        // Only the user's own clicks act; arming a tab in particular needs a user gesture.
        bar.addEventListener('click', (event) => {
            event.stopPropagation();
            if (event.isTrusted && event.target !== action && barHandler) {
                barHandler(video);
            }
        });
        action.addEventListener('click', (event) => {
            event.stopPropagation();
            if (event.isTrusted) {
                actionHandler?.(video);
            }
        });
        for (const type of ['pointerdown', 'pointerup', 'mousedown', 'mouseup', 'dblclick', 'contextmenu']) {
            // Keep the page's player from treating clicks on the control as clicks on the video.
            bar.addEventListener(type, (event) => event.stopPropagation());
        }
        bar.addEventListener('pointerenter', () => {
            hovered = true;
            if (barHandler) {
                css(bar, { background: 'rgba(48, 52, 60, 0.94)' });
            }
            clearTimeout(hideTimer);
        });
        bar.addEventListener('pointerleave', () => {
            hovered = false;
            css(bar, { background: 'rgba(24, 26, 30, 0.88)' });
            scheduleHide();
        });

        function attach() {
            if (!host.isConnected) {
                (document.documentElement ?? document).append(host);
            }
        }

        function setVisible(visible) {
            const shown = host.style.getPropertyValue('display') !== 'none';
            if (visible === shown) {
                return;
            }
            css(host, { display: visible ? 'block' : 'none' });
            if (usePopover) {
                try {
                    if (visible) {
                        host.showPopover();
                    } else if (host.matches(':popover-open')) {
                        host.hidePopover();
                    }
                } catch {
                    // A popover cannot open while the host is disconnected; the next placement retries.
                }
            }
        }

        function place() {
            if (!video) {
                return;
            }
            if (!video.isConnected) {
                hide();
                return;
            }
            const rect = video.getBoundingClientRect();
            const viewWidth = document.documentElement.clientWidth || innerWidth;
            const viewHeight = document.documentElement.clientHeight || innerHeight;
            const onScreen = rect.width >= minVideoWidth && rect.height >= minVideoHeight
                && rect.bottom > 0 && rect.right > 0 && rect.top < viewHeight && rect.left < viewWidth;
            if (!onScreen) {
                setVisible(false);
                return;
            }
            attach();
            setVisible(true);
            const width = bar.offsetWidth;
            const left = Math.max(0, Math.min(rect.right - width - inset, viewWidth - width));
            const top = Math.max(0, Math.min(rect.top + inset, viewHeight - bar.offsetHeight));
            css(host, { left: `${Math.round(left)}px`, top: `${Math.round(top)}px` });
        }

        function startLoop() {
            stopLoop();
            if (persistent) {
                loop = { interval: setInterval(place, 250) };
            } else {
                const step = () => {
                    place();
                    loop = { frame: requestAnimationFrame(step) };
                };
                step();
            }
        }

        function stopLoop() {
            if (loop?.interval) {
                clearInterval(loop.interval);
            }
            if (loop?.frame) {
                cancelAnimationFrame(loop.frame);
            }
            loop = null;
        }

        function scheduleHide() {
            clearTimeout(hideTimer);
            if (!persistent && !hovered) {
                hideTimer = setTimeout(hide, hoverHideMs);
            }
        }

        function render(nextVideo, options) {
            clearTimeout(messageTimer);
            shownClassification = null;
            video = nextVideo;
            mode = options.mode;
            persistent = !!options.persistent;
            label.textContent = options.label;
            barHandler = options.onBar ?? null;
            actionHandler = options.onAction ?? null;
            action.textContent = options.actionLabel ?? '';
            css(action, { display: options.actionLabel ? 'inline-block' : 'none' });
            css(bar, {
                cursor: barHandler ? 'pointer' : 'default',
                opacity: options.dimmed ? '0.8' : '1',
            });
            bar.setAttribute('role', barHandler ? 'button' : 'status');
            bar.title = options.title ?? '';
            setProgress(options.progress ?? null);
            startLoop();
            scheduleHide();
        }

        function setProgress(progress) {
            css(meter, { display: progress === null ? 'none' : 'block' });
            if (progress !== null) {
                css(fill, { width: `${Math.round(Math.min(Math.max(progress, 0), 1) * 100)}%` });
            }
        }

        function hide() {
            clearTimeout(hideTimer);
            clearTimeout(messageTimer);
            stopLoop();
            setVisible(false);
            video = null;
            mode = null;
            shownClassification = null;
            persistent = false;
        }

        // Shows the control over a video the pointer is on, for its classification.
        function offer(target, classification) {
            if (mode === 'session' || mode === 'message' || mode === 'busy') {
                if (video === target) {
                    scheduleHide();
                }
                return;
            }
            if (video === target && shownClassification === classification) {
                scheduleHide();
                return;
            }
            if (classification === 'file' || classification === 'manifest') {
                render(target, { mode: 'offer', label: text('controlRedirect'), onBar: handlers.onRedirect });
            } else if (classification === 'mediaSource') {
                render(target, { mode: 'offer', label: text('controlArm'), onBar: handlers.onRedirect });
            } else {
                if (video === target) {
                    hide();
                }
                return;
            }
            shownClassification = classification;
        }

        // A wait with a label, and a progress between 0 and 1 when its length is known. A repeated
        // call for the same wait only updates them.
        function showBusy(target, busyLabel = text('controlConnecting'), progress = null) {
            if (mode === 'busy' && video === target && label.textContent === busyLabel) {
                setProgress(progress);
                return;
            }
            render(target, { mode: 'busy', label: busyLabel, persistent: true, progress });
        }

        function showSession(target) {
            render(target, {
                mode: 'session',
                label: text('controlPlaying'),
                actionLabel: text('controlReturn'),
                onAction: handlers.onReturn,
                persistent: true,
            });
        }

        function showMessage(target, message, action, duration = messageMs) {
            render(target, {
                mode: 'message',
                label: message,
                actionLabel: action?.label,
                onAction: action?.run,
                persistent: true,
            });
            messageTimer = setTimeout(hide, duration);
        }

        function touch(target) {
            if (video === target) {
                scheduleHide();
            }
        }

        document.addEventListener('fullscreenchange', () => {
            // Reopening the popover puts it above the element that just entered the top layer.
            if (usePopover && video && host.matches(':popover-open')) {
                host.hidePopover();
                host.showPopover();
            }
        });

        return {
            host,
            offer,
            touch,
            showBusy,
            showSession,
            showMessage,
            hide,
            get video() { return video; },
            get mode() { return mode; },
        };
    }

    globalThis.LightPlayerControl = Object.freeze({ create });
})();
