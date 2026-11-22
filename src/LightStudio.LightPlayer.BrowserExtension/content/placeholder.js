// The placeholder of a taken-over video: a panel over the element that names the application and
// offers its actions. It is inserted right after the video, which gives it the video's place in the
// page's stacking order: what the page draws over its video shows over the placeholder as well. A
// video that is fullscreen by itself, and a page where the panel does not stay or render beside the
// video, get it in the top layer instead. Like the control, it lives in a closed shadow tree styled
// through CSSOM only. The panel lets pointer events through to the page's player; only its card
// takes them.
(() => {
    const tagName = 'lightplayer-placeholder';
    const minWidth = 120;
    const minHeight = 68;
    const compactWidth = 280;
    const compactHeight = 150;
    const topLayerIndex = '2147483646';
    // A page that removes the panel this many times in quick succession gets it in the top layer.
    const maxQuickInserts = 4;
    const quickInsertMs = 1000;
    const fitPasses = 3;
    const fitTolerance = 0.5;
    // A page element over an action counts as a control when it is smaller than this share of the
    // video, and as a layer otherwise.
    const layerShare = 0.25;
    const minOpacity = 0.1;
    const minAlpha = 0.05;
    const pictureTags = new Set(['img', 'canvas', 'video', 'svg', 'iframe', 'object', 'embed']);

    const applied = new WeakMap();

    // Sets style properties, skipping values that are already set.
    function css(element, properties) {
        let known = applied.get(element);
        if (!known) {
            known = new Map();
            applied.set(element, known);
        }
        for (const [name, value] of Object.entries(properties)) {
            if (known.get(name) !== value) {
                known.set(name, value);
                element.style.setProperty(name, value, 'important');
            }
        }
    }

    const px = (value) => `${Math.round(value * 100) / 100}px`;

    function icon(size) {
        const ns = 'http://www.w3.org/2000/svg';
        const svg = document.createElementNS(ns, 'svg');
        svg.setAttribute('viewBox', '0 0 24 24');
        svg.setAttribute('width', String(size));
        svg.setAttribute('height', String(size));
        svg.setAttribute('aria-hidden', 'true');
        const screen = document.createElementNS(ns, 'rect');
        for (const [name, value] of Object.entries({ x: '2.5', y: '4', width: '19', height: '13', rx: '2', fill: 'none', stroke: 'currentColor', 'stroke-width': '1.6' })) {
            screen.setAttribute(name, value);
        }
        const stand = document.createElementNS(ns, 'path');
        stand.setAttribute('d', 'M8 20.5h8');
        stand.setAttribute('stroke', 'currentColor');
        stand.setAttribute('stroke-width', '1.6');
        stand.setAttribute('stroke-linecap', 'round');
        const play = document.createElementNS(ns, 'path');
        play.setAttribute('d', 'M10 7.8v5.4l4.6-2.7z');
        play.setAttribute('fill', 'currentColor');
        svg.append(screen, stand, play);
        css(svg, { display: 'block', 'flex-shrink': '0' });
        return svg;
    }

    function alphaOf(color) {
        const match = /^rgba\(.*,\s*([\d.]+)\s*\)$/.exec(color) ?? /\/\s*([\d.]+)\s*\)$/.exec(color);
        return match ? parseFloat(match[1]) : 1;
    }

    // The element's opacity with that of its ancestors, across shadow boundaries.
    function opacityOf(element) {
        let opacity = 1;
        for (let node = element; node instanceof Element && opacity >= minOpacity; node = node.parentElement ?? node.getRootNode().host) {
            opacity *= parseFloat(getComputedStyle(node).opacity);
        }
        return opacity;
    }

    // Whether a page element that lies over an action shows nothing there: an invisible element, or
    // a layer over the video without a background or a picture of its own.
    function seeThrough(element, videoArea) {
        if (opacityOf(element) < minOpacity) {
            return true;
        }
        const rect = element.getBoundingClientRect();
        if (rect.width * rect.height < videoArea * layerShare || pictureTags.has(element.localName)) {
            return false;
        }
        const style = getComputedStyle(element);
        return style.backgroundImage === 'none' && alphaOf(style.backgroundColor) < minAlpha;
    }

    // Placeholders on screen, each as a function that names the action a pointer event belongs to.
    const shown = new Set();

    // A see-through layer of the page's player over the placeholder takes the pointer events meant
    // for an action. Those events stop here, and the click runs the action.
    function onPointerEvent(event) {
        if (!event.isTrusted) {
            return;
        }
        for (const actionAt of shown) {
            const run = actionAt(event.clientX, event.clientY);
            if (run) {
                event.stopImmediatePropagation();
                if (event.type === 'click') {
                    event.preventDefault();
                    run();
                }
                return;
            }
        }
    }

    for (const type of ['pointerdown', 'pointerup', 'mousedown', 'mouseup', 'click', 'dblclick']) {
        window.addEventListener(type, onPointerEvent, true);
    }

    function create() {
        const host = document.createElement(tagName);
        const usePopover = typeof host.showPopover === 'function';
        css(host, {
            all: 'initial',
            position: 'absolute',
            inset: 'auto',
            left: '0px',
            top: '0px',
            width: '0px',
            height: '0px',
            margin: '0',
            padding: '0',
            border: '0',
            overflow: 'hidden',
            background: 'transparent',
            'pointer-events': 'none',
            isolation: 'isolate',
            'z-index': 'auto',
            display: 'none',
        });
        const root = host.attachShadow({ mode: 'closed' });

        const panel = document.createElement('div');
        css(panel, {
            position: 'absolute',
            inset: '0',
            background: 'rgba(10, 11, 14, 0.62)',
            'pointer-events': 'none',
        });
        // The part of the element inside the viewport, where the card is centered.
        const view = document.createElement('div');
        css(view, {
            position: 'absolute',
            display: 'flex',
            'align-items': 'center',
            'justify-content': 'center',
            'box-sizing': 'border-box',
            padding: '12px',
            'pointer-events': 'none',
        });
        const card = document.createElement('div');
        css(card, {
            display: 'flex',
            'flex-direction': 'column',
            'align-items': 'center',
            gap: '10px',
            'max-width': '420px',
            padding: '16px 20px',
            'border-radius': '14px',
            background: 'rgba(24, 26, 30, 0.92)',
            color: '#ffffff',
            'font-family': 'system-ui, -apple-system, "Segoe UI", sans-serif',
            'font-size': '14px',
            'line-height': '20px',
            'font-weight': '500',
            'text-align': 'center',
            'box-shadow': '0 4px 16px rgba(0, 0, 0, 0.4)',
            'pointer-events': 'auto',
            'user-select': 'none',
            cursor: 'default',
        });
        const mark = icon(30);
        const label = document.createElement('span');
        css(label, { 'white-space': 'normal', 'overflow-wrap': 'anywhere' });
        const buttonStyle = {
            all: 'initial',
            padding: '5px 14px',
            'border-radius': '14px',
            background: 'rgba(255, 255, 255, 0.2)',
            color: '#ffffff',
            'font-family': 'inherit',
            'font-size': '13px',
            'line-height': '18px',
            'font-weight': '600',
            cursor: 'pointer',
            display: 'none',
        };
        const action = document.createElement('button');
        action.type = 'button';
        css(action, buttonStyle);
        const secondary = document.createElement('button');
        secondary.type = 'button';
        css(secondary, { ...buttonStyle, background: 'transparent', 'text-decoration': 'underline' });
        const actions = document.createElement('div');
        css(actions, { display: 'flex', 'flex-wrap': 'wrap', 'justify-content': 'center', gap: '8px' });
        actions.append(action, secondary);
        // Progress of a wait, shown below the text.
        const meter = document.createElement('div');
        css(meter, {
            width: '160px',
            height: '3px',
            'border-radius': '2px',
            background: 'rgba(255, 255, 255, 0.25)',
            overflow: 'hidden',
            display: 'none',
        });
        const fill = document.createElement('div');
        css(fill, { height: '100%', width: '0%', background: '#ffffff' });
        meter.append(fill);
        card.append(mark, label, meter, actions);
        view.append(card);
        root.append(panel, view);

        let video = null;
        let onAction = null;
        let onSecondary = null;
        let frame = 0;
        // null while the host is out of the document, `beside` the video, or in the `top` layer.
        let place = null;
        let topOnly = false;
        let quickInserts = 0;
        let insertedAt = -Infinity;
        // The host's box beside the video as set, in the coordinates of its containing block.
        let box = null;

        action.addEventListener('click', (event) => {
            event.stopPropagation();
            if (event.isTrusted) {
                onAction?.();
            }
        });
        secondary.addEventListener('click', (event) => {
            event.stopPropagation();
            if (event.isTrusted) {
                onSecondary?.();
            }
        });
        for (const type of ['pointerdown', 'pointerup', 'mousedown', 'mouseup', 'click', 'dblclick', 'contextmenu']) {
            // Clicks on the card do not reach the page's player.
            card.addEventListener(type, (event) => event.stopPropagation());
        }

        // The action at a point that page elements over the placeholder keep from being hit, when
        // all of them are see-through there.
        function actionAt(x, y) {
            if (place !== 'beside') {
                return null;
            }
            const run = [[action, onAction], [secondary, onSecondary]].find(([button, handler]) => {
                const rect = button.getBoundingClientRect();
                return handler && x >= rect.left && x < rect.right && y >= rect.top && y < rect.bottom;
            })?.[1];
            if (!run) {
                return null;
            }
            const stack = host.getRootNode().elementsFromPoint(x, y);
            const index = stack.indexOf(host);
            if (index <= 0) {
                return null;
            }
            const own = host.getBoundingClientRect();
            return stack.slice(0, index).every((element) => seeThrough(element, own.width * own.height)) ? run : null;
        }

        function openPopover() {
            if (!usePopover || host.matches(':popover-open')) {
                return;
            }
            try {
                host.showPopover();
            } catch (error) {
                // A popover cannot open in a document that is not fully active; the next frame retries.
                if (error?.name !== 'InvalidStateError') {
                    throw error;
                }
            }
        }

        function setPlace(next) {
            if (next === place) {
                return;
            }
            if (place === 'top' && usePopover && host.matches(':popover-open')) {
                host.hidePopover();
            }
            place = next;
            box = null;
            if (next === 'top') {
                host.removeAttribute('slot');
                if (usePopover) {
                    host.popover = 'manual';
                }
                (document.documentElement ?? document).append(host);
            } else {
                host.removeAttribute('popover');
                if (next === null) {
                    host.remove();
                }
            }
        }

        // Keeps the host right after the video.
        function insertBeside() {
            if (host.parentNode === video.parentNode && host.previousSibling === video) {
                return true;
            }
            const now = performance.now();
            quickInserts = now - insertedAt < quickInsertMs ? quickInserts + 1 : 0;
            insertedAt = now;
            if (quickInserts >= maxQuickInserts) {
                return false;
            }
            // A slotted video's sibling shows only in the same slot.
            if (video.slot) {
                host.slot = video.slot;
            } else {
                host.removeAttribute('slot');
            }
            video.after(host);
            box = null;
            return true;
        }

        function layoutCard(width, height) {
            const compact = width < compactWidth || height < compactHeight;
            css(mark, { display: compact ? 'none' : 'block' });
            css(card, { padding: compact ? '8px 12px' : '16px 20px', gap: compact ? '6px' : '10px', 'font-size': compact ? '12px' : '14px' });
        }

        // Fits the host to the video's box by measuring where it lands, which holds in any
        // containing block and under scaled ancestors. Returns false when the host does not render
        // beside this video.
        function fitBeside(rect, visible) {
            const style = getComputedStyle(video);
            css(host, { position: style.position === 'fixed' ? 'fixed' : 'absolute', 'z-index': style.zIndex, display: 'block' });
            box ??= {
                left: video.offsetLeft,
                top: video.offsetTop,
                width: video.offsetWidth || rect.width,
                height: video.offsetHeight || rect.height,
            };
            let own;
            let scaleX;
            let scaleY;
            for (let pass = 0; pass < fitPasses; pass++) {
                css(host, { left: px(box.left), top: px(box.top), width: px(box.width), height: px(box.height) });
                own = host.getBoundingClientRect();
                if (!(own.width > 0 && own.height > 0)) {
                    return false;
                }
                scaleX = own.width / box.width;
                scaleY = own.height / box.height;
                if (Math.abs(own.left - rect.left) < fitTolerance && Math.abs(own.top - rect.top) < fitTolerance
                    && Math.abs(own.width - rect.width) < fitTolerance && Math.abs(own.height - rect.height) < fitTolerance) {
                    break;
                }
                box = {
                    left: box.left + (rect.left - own.left) / scaleX,
                    top: box.top + (rect.top - own.top) / scaleY,
                    width: rect.width / scaleX,
                    height: rect.height / scaleY,
                };
            }
            if (!visible) {
                css(view, { display: 'none' });
                return true;
            }
            const width = (visible.right - visible.left) / scaleX;
            const height = (visible.bottom - visible.top) / scaleY;
            css(view, {
                display: 'flex',
                left: px((visible.left - own.left) / scaleX),
                top: px((visible.top - own.top) / scaleY),
                width: px(width),
                height: px(height),
            });
            layoutCard(width, height);
            return true;
        }

        function fitTop(visible) {
            const width = visible.right - visible.left;
            const height = visible.bottom - visible.top;
            css(host, {
                position: 'fixed',
                'z-index': topLayerIndex,
                display: 'block',
                left: px(Math.round(visible.left)),
                top: px(Math.round(visible.top)),
                width: px(Math.round(width)),
                height: px(Math.round(height)),
            });
            css(view, { display: 'flex', left: '0px', top: '0px', width: '100%', height: '100%' });
            layoutCard(width, height);
            openPopover();
        }

        function update() {
            frame = 0;
            if (!video) {
                return;
            }
            const rect = video.isConnected ? video.getBoundingClientRect() : null;
            if (!rect || rect.width < minWidth || rect.height < minHeight) {
                setPlace(null);
            } else {
                const viewWidth = document.documentElement.clientWidth || innerWidth;
                const viewHeight = document.documentElement.clientHeight || innerHeight;
                const visible = {
                    left: Math.max(rect.left, 0),
                    top: Math.max(rect.top, 0),
                    right: Math.min(rect.right, viewWidth),
                    bottom: Math.min(rect.bottom, viewHeight),
                };
                const onScreen = visible.right - visible.left >= minWidth && visible.bottom - visible.top >= minHeight;
                // A fullscreen video shows nothing of its siblings.
                if (!topOnly && !video.matches(':fullscreen')) {
                    setPlace('beside');
                    topOnly = !insertBeside() || !fitBeside(rect, onScreen ? visible : null);
                }
                if (topOnly || video.matches(':fullscreen')) {
                    setPlace(onScreen ? 'top' : null);
                    if (onScreen) {
                        fitTop(visible);
                    }
                }
            }
            frame = requestAnimationFrame(update);
        }

        // Shows the placeholder over `target` with a text, an optional progress between 0 and 1, an
        // optional action and an optional second one.
        function show(target, options) {
            if (video !== target) {
                topOnly = false;
                quickInserts = 0;
            }
            video = target;
            label.textContent = options.text;
            const progress = options.progress ?? null;
            css(meter, { display: progress === null ? 'none' : 'block' });
            if (progress !== null) {
                css(fill, { width: `${Math.round(Math.min(Math.max(progress, 0), 1) * 100)}%` });
            }
            onAction = options.onAction ?? null;
            action.textContent = options.actionLabel ?? '';
            css(action, { display: options.actionLabel ? 'inline-block' : 'none' });
            onSecondary = options.onSecondary ?? null;
            secondary.textContent = options.secondaryLabel ?? '';
            css(secondary, { display: options.secondaryLabel ? 'inline-block' : 'none' });
            card.setAttribute('role', 'status');
            shown.add(actionAt);
            if (!frame) {
                update();
            }
        }

        function hide() {
            video = null;
            onAction = null;
            onSecondary = null;
            cancelAnimationFrame(frame);
            frame = 0;
            shown.delete(actionAt);
            setPlace(null);
        }

        document.addEventListener('fullscreenchange', () => {
            // Reopening the popover puts it above the element that just entered the top layer.
            if (place === 'top' && usePopover && host.matches(':popover-open')) {
                host.hidePopover();
                host.showPopover();
            }
        });

        return {
            show,
            hide,
            get video() { return video; },
        };
    }

    globalThis.LightPlayerPlaceholder = Object.freeze({ create });
})();
