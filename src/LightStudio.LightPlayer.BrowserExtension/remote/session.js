// One redirected session over a session link: routes application messages to the remote classes
// and carries their messages to the application.
'use strict';
(function () {
  const R = globalThis.LightPlayerRemote;
  if (!R || R.createSession) return;
  const { internal } = R;

  // `timeline` is resent when its window moves by at least this many seconds.
  const TIMELINE_STEP = 0.5;

  function positive(value, fallback) {
    const n = Number(value);
    return Number.isFinite(n) && n > 0 ? n : fallback;
  }

  class Session {
    constructor(link, options) {
      this.link = link;
      this.title = options.title !== undefined ? String(options.title) : document.title;
      this.pageUrl = options.pageUrl !== undefined ? String(options.pageUrl) : location.href;
      this.live = options.live === undefined || options.live === null ? null : !!options.live;
      this.seekableWindow = null;
      this.view = {
        width: positive(options.viewWidth, 1280),
        height: positive(options.viewHeight, 720),
        devicePixelRatio: positive(options.devicePixelRatio, 1),
      };
      this.closed = false;
      this.closeReason = null;
      this.opened = false;
      this.commandNumber = 0;
      this.bufferNumber = 0;
      this.requestNumber = 0;
      this.buffers = new Map();
      this.lastTimeline = null;
      this.timelineQueued = false;
      this.onclose = null;
      this.onviewchange = null;
      if (options.host) {
        this.element = null;
        this.state = new R.ElementState(this, options.host);
      } else {
        this.element = internal.createElement(this);
        this.state = internal.elementState(this.element);
      }
      if (link.capabilities) R.setCapabilities(link.capabilities);
      link.onmessage = (message) => this.receive(message);
      link.onclose = (reason) => this.end('link', reason === undefined ? undefined : String(reason));
    }

    send(message) {
      if (!this.closed) this.link.send(message);
    }

    append(buffer, sequence, bytes) {
      if (!this.closed) this.link.append(buffer, sequence, bytes);
    }

    // Element commands exist only once a source was opened; before that `open` carries the state.
    command(type, fields) {
      if (!this.opened || this.closed) return 0;
      const command = ++this.commandNumber;
      this.send(Object.assign({ type, command }, fields));
      return command;
    }

    nextBufferId() {
      return ++this.bufferNumber;
    }

    nextRequest() {
      return ++this.requestNumber;
    }

    registerBuffer(buffer) {
      this.buffers.set(buffer.id, buffer);
    }

    unregisterBuffer(buffer) {
      if (this.buffers.get(buffer.id) === buffer) this.buffers.delete(buffer.id);
    }

    openSource(state) {
      this.opened = true;
      this.lastTimeline = null;
      state.sourceOpened();
      this.send({
        type: 'open',
        kind: 'source',
        title: this.title,
        pageUrl: this.pageUrl,
        startTime: state.defaultStart,
        paused: state.paused,
        rate: state.playbackRate,
        volume: state.volume,
        muted: state.muted,
        live: this.live,
      });
    }

    receive(message) {
      if (this.closed || !message || typeof message !== 'object') return;
      try {
        const buffer = this.buffers.get(message.buffer);
        switch (message.type) {
          case 'appended':
            if (buffer) buffer.onAppendReply(message, false);
            break;
          case 'appendFailed':
            if (buffer) buffer.onAppendReply(message, true);
            break;
          case 'removed':
            if (buffer) buffer.onRemoved(message);
            break;
          case 'buffered':
            if (buffer) buffer.updateRanges(message.buffered, message.full);
            break;
          case 'state':
            this.updateView(message);
            this.state.onState(message);
            break;
          case 'close':
            this.end(String(message.reason || 'error'), message.message === undefined ? undefined : String(message.message));
            break;
          case 'ready':
            R.setCapabilities(message);
            break;
        }
      } catch (e) {
        internal.report(e);
      }
      if (internal.pumpTimers) internal.pumpTimers();
    }

    updateView(message) {
      const width = positive(message.viewWidth, 0);
      const height = positive(message.viewHeight, 0);
      if (!width || !height) return;
      const ratio = positive(message.devicePixelRatio, this.view.devicePixelRatio);
      const v = this.view;
      if (v.width === width && v.height === height && v.devicePixelRatio === ratio) return;
      this.view = { width, height, devicePixelRatio: ratio };
      if (this.element && internal.notifyResize) internal.notifyResize(this.element);
      if (typeof this.onviewchange === 'function') {
        try {
          this.onviewchange(this.view);
        } catch (e) {
          internal.report(e);
        }
      }
    }

    // Coalesced to the end of the current task: players clear and set the live range back to back.
    timelineChanged() {
      if (this.timelineQueued) return;
      this.timelineQueued = true;
      queueMicrotask(() => {
        this.timelineQueued = false;
        this.sendTimeline();
      });
    }

    sendTimeline() {
      if (!this.opened || this.closed) return;
      const ms = this.state.mediaSource;
      if (!ms) return;
      // Without a known flag, an unbounded duration or a live seekable range marks a live source.
      const live = this.live !== null ? this.live : ms.duration === Infinity || !!ms.liveSeekable;
      let start;
      let end;
      if (this.seekableWindow) {
        [start, end] = this.seekableWindow;
      } else {
        const ranges = this.state.seekableRanges();
        if (!ranges.length) return;
        start = ranges[0][0];
        end = ranges[ranges.length - 1][1];
      }
      const last = this.lastTimeline;
      if (last && last.live === live && Math.abs(last.seekableStart - start) < TIMELINE_STEP &&
        Math.abs(last.seekableEnd - end) < TIMELINE_STEP) return;
      this.lastTimeline = { live, seekableStart: start, seekableEnd: end };
      this.send({ type: 'timeline', live, seekableStart: start, seekableEnd: end });
    }

    setLive(live) {
      this.live = live === undefined || live === null ? null : !!live;
      this.timelineChanged();
    }

    setSeekableWindow(start, end) {
      const s = Number(start);
      const e = Number(end);
      this.seekableWindow = start === null || start === undefined || !(e >= s) ? null : [s, e];
      this.timelineChanged();
    }

    close(reason) {
      if (this.closed) return;
      const r = reason === undefined ? 'stopped' : String(reason);
      this.send({ type: 'close', reason: r });
      this.end(r, undefined, true);
    }

    end(reason, message, local) {
      if (this.closed) return;
      this.closed = true;
      this.closeReason = reason;
      this.state.sessionEnded(local ? 'stopped' : reason, message);
      if (typeof this.onclose === 'function') {
        try {
          this.onclose(reason, message);
        } catch (e) {
          internal.report(e);
        }
      }
    }
  }

  R.createSession = function createSession(link, options) {
    if (!link || typeof link.send !== 'function' || typeof link.append !== 'function') {
      throw new TypeError('The session link must have send and append methods.');
    }
    return new Session(link, options || {});
  };
})();
