// The remote media element: ElementState holds the media element state and its rules, and
// RemoteVideoElement is a plain object with a DOM-shaped surface over it. A takeover shim can serve
// the same ElementState through HTMLMediaElement accessors on a real element.
'use strict';
(function () {
  const R = globalThis.LightPlayerRemote;
  if (!R || R.ElementState) return;
  const { internal } = R;
  const { KEY, now, domError, queueTask, report, RemoteEventTarget, defineEventHandlers, defineConstants, defineTag } = internal;
  const { intersectRanges, normalizeRanges, rangeAt, highestEnd } = internal;

  const HAVE_NOTHING = 0;
  const HAVE_METADATA = 1;
  const HAVE_CURRENT_DATA = 2;
  const HAVE_FUTURE_DATA = 3;
  const HAVE_ENOUGH_DATA = 4;
  const NETWORK_EMPTY = 0;
  const NETWORK_IDLE = 1;
  const NETWORK_LOADING = 2;
  const NETWORK_NO_SOURCE = 3;
  const MEDIA_ERR_ABORTED = 1;
  const MEDIA_ERR_NETWORK = 2;
  const MEDIA_ERR_DECODE = 3;
  const MEDIA_ERR_SRC_NOT_SUPPORTED = 4;

  const TICK_MS = 100;
  const TIMEUPDATE_MS = 250;
  const PROGRESS_MS = 350;
  // A position change this far from what playback explains is an application seek.
  const JUMP_SECONDS = 1;
  // A backward correction up to this size is held while playing, so currentTime stays monotonic.
  const HOLD_SECONDS = 0.5;
  // The reported position may sit this far before the start of the range that holds it.
  const RANGE_TOLERANCE = 0.05;
  // A position this close to the duration of an ended source is its end.
  const END_TOLERANCE = 0.001;

  const ELEMENT_EVENTS = ['loadstart', 'progress', 'suspend', 'abort', 'error', 'emptied', 'stalled',
    'loadedmetadata', 'loadeddata', 'canplay', 'canplaythrough', 'playing', 'waiting', 'seeking', 'seeked',
    'ended', 'durationchange', 'timeupdate', 'play', 'pause', 'ratechange', 'resize', 'volumechange',
    'encrypted', 'waitingforkey', 'enterpictureinpicture', 'leavepictureinpicture'];

  const errorFields = new WeakMap();

  class MediaError {
    constructor(key, code, message) {
      if (key !== KEY) throw new TypeError('Illegal constructor');
      errorFields.set(this, { code, message });
    }
    get code() {
      return errorFields.get(this).code;
    }
    get message() {
      return errorFields.get(this).message;
    }
  }
  defineConstants(MediaError, { MEDIA_ERR_ABORTED, MEDIA_ERR_NETWORK, MEDIA_ERR_DECODE, MEDIA_ERR_SRC_NOT_SUPPORTED });
  defineTag(MediaError, 'MediaError');

  const qualityFields = new WeakMap();

  class VideoPlaybackQuality {
    constructor(key, fields) {
      if (key !== KEY) throw new TypeError('Illegal constructor');
      qualityFields.set(this, fields);
    }
    get creationTime() {
      return qualityFields.get(this).creationTime;
    }
    get totalVideoFrames() {
      return qualityFields.get(this).totalVideoFrames;
    }
    get droppedVideoFrames() {
      return qualityFields.get(this).droppedVideoFrames;
    }
    get corruptedVideoFrames() {
      return 0;
    }
  }
  defineTag(VideoPlaybackQuality, 'VideoPlaybackQuality');

  function finite(value, fallback) {
    const n = Number(value);
    return Number.isFinite(n) ? n : fallback;
  }

  function snapshot(time, paused, rate, volume, muted) {
    return { time, paused, rate, volume, muted, readyState: HAVE_NOTHING, seeking: false, ended: false,
      totalFrames: 0, droppedFrames: 0, at: now() };
  }

  // State of one media element, driven by element calls on one side and by `state` messages on the
  // other. Fields of `state` that a newer command still owns are ignored until `ack` covers it.
  class ElementState {
    constructor(session, host) {
      this.session = session;
      this.host = host;
      this.networkState = NETWORK_EMPTY;
      this.readyState = HAVE_NOTHING;
      this.error = null;
      this.paused = true;
      this.playbackRate = 1;
      this.defaultPlaybackRate = 1;
      this.volume = 1;
      this.muted = false;
      this.preservesPitch = true;
      this.canAutoplay = true;
      this.mediaSource = null;
      this.currentSrc = '';
      this.defaultStart = 0;
      this.seeking = false;
      this.seekTarget = 0;
      this.appSeeking = false;
      this.appSeekTarget = 0;
      this.ended = false;
      this.endPending = false;
      this.loadedData = false;
      this.videoWidth = 0;
      this.videoHeight = 0;
      this.st = snapshot(0, true, 1, 1, false);
      this.own = { paused: 0, seek: 0, rate: 0, volume: 0 };
      this.baseline = { paused: true, rate: 1, volume: 1, muted: false };
      this.lastAck = 0;
      this.playPromises = [];
      this.selection = 0;
      this.waitingForSource = false;
      this.timer = null;
      this.lastTimeupdate = 0;
      this.lastProgress = 0;
      this.epoch = 0;
      this.reportedEpoch = -1;
      this.reportedTime = 0;
      this.bufferedCache = null;
      this.bufferedObject = null;
      this.frameCallbacks = new Map();
      this.frameCallbackId = 0;
      this.presentedFrames = 0;
      this.playedList = [];
      this.playedSegment = null;
    }

    // ---- helpers ----

    queue(items) {
      for (const item of items) {
        if (typeof item === 'function') {
          queueTask(item);
        } else {
          if (item === 'timeupdate') this.lastTimeupdate = now();
          queueTask(() => this.host.fire(item));
        }
      }
    }

    command(type, fields) {
      return this.session ? this.session.command(type, fields) : 0;
    }

    takePlayPromises() {
      return this.playPromises.splice(0);
    }

    rejectPlayPromises(error) {
      const promises = this.takePlayPromises();
      if (promises.length) queueTask(() => promises.forEach((p) => p.reject(error)));
    }

    resolvePlayPromises() {
      const promises = this.takePlayPromises();
      if (promises.length) queueTask(() => promises.forEach((p) => p.resolve()));
    }

    // Queues `playing` and resolves the play promises pending at this point.
    playingItems() {
      const promises = this.takePlayPromises();
      return ['playing', () => promises.forEach((p) => p.resolve())];
    }

    isPotentiallyPlaying() {
      return !this.paused && !this.ended && !this.error && this.readyState >= HAVE_FUTURE_DATA;
    }

    pendingRate() {
      return this.own.rate > this.lastAck;
    }

    extrapolating() {
      const s = this.st;
      const rate = this.pendingRate() ? this.playbackRate : s.rate;
      return !this.paused && !s.paused && !s.seeking && !s.ended && !this.appSeeking && rate > 0 &&
        this.readyState >= HAVE_FUTURE_DATA;
    }

    // ---- ranges ----

    invalidateBuffered() {
      this.bufferedCache = null;
      this.bufferedObject = null;
    }

    bufferedRanges() {
      if (this.bufferedCache) return this.bufferedCache;
      const ms = this.mediaSource;
      const active = ms ? ms.buffers.filter((b) => b.active) : [];
      let ranges = [];
      if (active.length) {
        let highest = 0;
        for (const b of active) highest = Math.max(highest, highestEnd(b.ranges));
        ranges = [[0, highest]];
        for (const b of active) {
          let source = b.ranges;
          if (ms.readyState === 'ended' && source.length) {
            source = source.slice(0, -1).concat([[source[source.length - 1][0], highest]]);
          }
          ranges = intersectRanges(ranges, source);
        }
      }
      this.bufferedCache = ranges;
      return ranges;
    }

    get buffered() {
      if (!this.bufferedObject) this.bufferedObject = internal.timeRanges(this.bufferedRanges());
      return this.bufferedObject;
    }

    seekableRanges() {
      const ms = this.mediaSource;
      if (!ms || Number.isNaN(ms.duration)) return [];
      if (ms.duration !== Infinity) return [[0, ms.duration]];
      const buffered = this.bufferedRanges();
      if (ms.liveSeekable) {
        const start = Math.min(ms.liveSeekable[0], buffered.length ? buffered[0][0] : Infinity);
        const end = Math.max(ms.liveSeekable[1], buffered.length ? highestEnd(buffered) : -Infinity);
        return [[start, end]];
      }
      return buffered.length ? [[0, highestEnd(buffered)]] : [];
    }

    get seekable() {
      return internal.timeRanges(this.seekableRanges());
    }

    get played() {
      const list = this.playedSegment ? this.playedList.concat([this.playedSegment]) : this.playedList;
      return internal.timeRanges(normalizeRanges(list));
    }

    notePlayed(time) {
      if (!this.playedSegment) this.playedSegment = [time, time];
      else this.playedSegment[1] = Math.max(this.playedSegment[1], time);
    }

    closePlayed() {
      if (this.playedSegment) this.playedList.push(this.playedSegment);
      this.playedSegment = null;
    }

    // ---- attributes ----

    get duration() {
      return this.mediaSource ? this.mediaSource.duration : NaN;
    }

    get endedPlayback() {
      return this.ended && !this.seeking && !this.appSeeking;
    }

    get seekingAny() {
      return this.seeking || this.appSeeking;
    }

    atSourceEnd(time) {
      const ms = this.mediaSource;
      return !!ms && ms.readyState === 'ended' && Number.isFinite(ms.duration) && time >= ms.duration - END_TOLERANCE;
    }

    get currentTime() {
      if (!this.mediaSource || this.readyState === HAVE_NOTHING) return this.defaultStart !== 0 ? this.defaultStart : this.st.time;
      if (this.seeking) return this.seekTarget;
      if (this.endedPlayback && Number.isFinite(this.mediaSource.duration)) return this.mediaSource.duration;
      const s = this.st;
      let t = s.time;
      if (this.extrapolating()) {
        const rate = this.pendingRate() ? this.playbackRate : s.rate;
        t = s.time + ((now() - s.at) / 1000) * rate;
        const buffered = this.bufferedRanges();
        const range = rangeAt(buffered, s.time, RANGE_TOLERANCE);
        const duration = this.mediaSource.duration;
        const rangeEnd = range ? range[1] : s.time;
        const end = Number.isNaN(duration) ? rangeEnd : Math.min(rangeEnd, duration);
        t = Math.max(Math.min(t, end), s.time);
        // Playback reaching the end of an ended source ends the element at once, as on a native
        // element; the application's `state` confirms it shortly after.
        if (range && t >= end && range[1] >= highestEnd(buffered) && this.atSourceEnd(t)) {
          this.endLocally();
          if (Number.isFinite(this.mediaSource.duration)) return this.mediaSource.duration;
        }
        if (this.reportedEpoch === this.epoch && t < this.reportedTime && this.reportedTime - t < HOLD_SECONDS) {
          t = this.reportedTime;
        }
      }
      this.reportedEpoch = this.epoch;
      this.reportedTime = t;
      return t;
    }

    set currentTime(value) {
      const time = Number(value);
      if (!Number.isFinite(time)) {
        throw new TypeError("Failed to set the 'currentTime' property on 'HTMLMediaElement': The provided double value is non-finite.");
      }
      if (this.readyState === HAVE_NOTHING) {
        this.defaultStart = time;
        if (this.mediaSource) {
          const n = this.command('seek', { time });
          if (n) this.own.seek = n;
        }
        return;
      }
      this.seek(time);
    }

    endLocally() {
      this.endPending = true;
      const events = [];
      this.reachEnd(events);
      this.queue(events);
      this.updateTimer();
    }

    // Rebases the position on the local clock, so pausing or a rate change keeps what was reported.
    freezeTime() {
      if (this.seeking || !this.mediaSource) return;
      this.st.time = this.currentTime;
      this.st.at = now();
    }

    seek(time) {
      const seekable = this.seekableRanges();
      if (!seekable.length) {
        this.seeking = false;
        return;
      }
      let target = time;
      let best = null;
      for (const r of seekable) {
        const clamped = Math.min(Math.max(target, r[0]), r[1]);
        if (best === null || Math.abs(clamped - target) < Math.abs(best - target)) best = clamped;
      }
      target = best;
      const potentiallyPlaying = this.isPotentiallyPlaying();
      const buffered = rangeAt(this.bufferedRanges(), target, 0) !== null;
      this.closePlayed();
      this.seeking = true;
      this.appSeeking = false;
      this.seekTarget = target;
      this.ended = false;
      this.endPending = false;
      this.epoch++;
      const n = this.command('seek', { time: target });
      if (n) this.own.seek = n;
      if (!buffered && this.readyState > HAVE_METADATA) this.readyState = HAVE_METADATA;
      this.queue(potentiallyPlaying && !buffered ? ['seeking', 'waiting'] : ['seeking']);
      this.updateTimer();
    }

    setPlaybackRate(value) {
      const rate = Number(value);
      if (!Number.isFinite(rate)) {
        throw new TypeError("Failed to set the 'playbackRate' property on 'HTMLMediaElement': The provided double value is non-finite.");
      }
      if (rate !== 0 && (rate < 0.0625 || rate > 16)) {
        throw domError('NotSupportedError', "Failed to set the 'playbackRate' property on 'HTMLMediaElement': The provided playback rate (" + rate + ') is not in the supported playback range.');
      }
      if (rate === this.playbackRate) return;
      this.freezeTime();
      this.playbackRate = rate;
      const n = this.command('rate', { value: rate });
      if (n) this.own.rate = n;
      this.queue(['ratechange']);
      this.updateTimer();
    }

    setDefaultPlaybackRate(value) {
      const rate = Number(value);
      if (!Number.isFinite(rate)) {
        throw new TypeError("Failed to set the 'defaultPlaybackRate' property on 'HTMLMediaElement': The provided double value is non-finite.");
      }
      if (rate === this.defaultPlaybackRate) return;
      this.defaultPlaybackRate = rate;
      this.queue(['ratechange']);
    }

    setVolume(value) {
      const volume = Number(value);
      if (!Number.isFinite(volume)) {
        throw new TypeError("Failed to set the 'volume' property on 'HTMLMediaElement': The provided double value is non-finite.");
      }
      if (volume < 0 || volume > 1) {
        throw domError('IndexSizeError', "Failed to set the 'volume' property on 'HTMLMediaElement': The volume provided (" + volume + ') is outside the range [0, 1].');
      }
      if (volume === this.volume) return;
      this.volume = volume;
      this.sendVolume();
    }

    setMuted(value) {
      const muted = !!value;
      if (muted === this.muted) return;
      this.muted = muted;
      this.sendVolume();
    }

    sendVolume() {
      const n = this.command('volume', { value: this.volume, muted: this.muted });
      if (n) this.own.volume = n;
      this.queue(['volumechange']);
    }

    // ---- play and pause ----

    play() {
      return new Promise((resolve, reject) => {
        if (this.error && this.error.code === MEDIA_ERR_SRC_NOT_SUPPORTED) {
          reject(domError('NotSupportedError', 'The element has no supported sources.'));
          return;
        }
        this.playPromises.push({ resolve, reject });
        this.internalPlay();
      });
    }

    internalPlay() {
      if (this.networkState === NETWORK_EMPTY) this.selectResource();
      const restart = this.endedPlayback;
      if (restart) {
        const seekable = this.seekableRanges();
        this.seek(seekable.length ? seekable[0][0] : 0);
      }
      if (this.paused) {
        this.paused = false;
        this.freezeTime();
        const n = this.command('play');
        if (n) this.own.paused = n;
        if (this.readyState <= HAVE_CURRENT_DATA) this.queue(['play', 'waiting']);
        else this.queue(['play'].concat(this.playingItems()));
      } else {
        // The application pauses itself at the end, also when the element ended before it did.
        if (restart || (this.st.paused && this.own.paused <= this.lastAck)) {
          const n = this.command('play');
          if (n) this.own.paused = n;
        }
        if (this.readyState >= HAVE_FUTURE_DATA) this.resolvePlayPromises();
      }
      this.canAutoplay = false;
      this.updateTimer();
    }

    pause() {
      if (this.networkState === NETWORK_EMPTY) this.selectResource();
      this.internalPause();
    }

    internalPause() {
      this.canAutoplay = false;
      if (this.paused) return;
      this.freezeTime();
      this.paused = true;
      const n = this.command('pause');
      if (n) this.own.paused = n;
      const promises = this.takePlayPromises();
      const error = domError('AbortError', 'The play() request was interrupted by a call to pause().');
      this.queue(['timeupdate', 'pause', () => promises.forEach((p) => p.reject(error))]);
      this.closePlayed();
      this.updateTimer();
    }

    // ---- loading ----

    load() {
      const hadDuration = !Number.isNaN(this.duration);
      if (this.networkState === NETWORK_LOADING || this.networkState === NETWORK_IDLE) this.queue(['abort']);
      if (this.networkState !== NETWORK_EMPTY) {
        this.queue(['emptied']);
        this.detachSource();
        this.readyState = HAVE_NOTHING;
        this.loadedData = false;
        this.ended = false;
        this.endPending = false;
        this.videoWidth = 0;
        this.videoHeight = 0;
        if (!this.paused) {
          this.paused = true;
          const n = this.command('pause');
          if (n) this.own.paused = n;
          this.rejectPlayPromises(domError('AbortError', 'The play() request was interrupted by a new load request.'));
        }
        this.seeking = false;
        this.appSeeking = false;
        const hadPosition = this.st.time !== 0;
        this.st = snapshot(0, this.st.paused, this.st.rate, this.st.volume, this.st.muted);
        if (hadPosition) this.queue(['timeupdate']);
        if (hadDuration) this.queue(['durationchange']);
        this.playedList = [];
        this.playedSegment = null;
        this.epoch++;
      }
      if (this.playbackRate !== this.defaultPlaybackRate) {
        this.playbackRate = this.defaultPlaybackRate;
        const n = this.command('rate', { value: this.playbackRate });
        if (n) this.own.rate = n;
        this.queue(['ratechange']);
      }
      this.error = null;
      this.canAutoplay = true;
      this.currentSrc = '';
      this.selectResource();
      this.updateTimer();
    }

    detachSource() {
      if (this.mediaSource) {
        this.mediaSource.detach();
        this.mediaSource = null;
      }
      this.invalidateBuffered();
    }

    selectResource() {
      this.networkState = NETWORK_NO_SOURCE;
      this.waitingForSource = false;
      const generation = ++this.selection;
      queueMicrotask(() => this.continueSelection(generation));
    }

    continueSelection(generation) {
      if (generation !== this.selection) return;
      const candidates = this.host.resourceCandidates();
      if (!candidates) {
        this.networkState = NETWORK_EMPTY;
        return;
      }
      this.networkState = NETWORK_LOADING;
      this.queue(['loadstart']);
      if (candidates.mode === 'attribute') {
        if (!this.tryAttach(candidates.url)) this.sourceFailure();
        return;
      }
      for (const c of candidates.list) {
        if (c.type && internal.canPlayType(c.type) === '') continue;
        if (this.tryAttach(c.url)) return;
      }
      this.networkState = NETWORK_NO_SOURCE;
      this.waitingForSource = true;
    }

    sourceInserted(hasSrcAttribute) {
      if ((this.networkState === NETWORK_EMPTY && !hasSrcAttribute) || this.waitingForSource) this.selectResource();
    }

    tryAttach(url) {
      const ms = internal.mediaSourceForUrl(url);
      if (!ms || !this.session || this.session.closed || !ms.attach(this)) return false;
      this.mediaSource = ms;
      this.currentSrc = url;
      this.invalidateBuffered();
      this.session.openSource(this);
      return true;
    }

    // Values the application starts the source with; later `state` messages are compared to them.
    sourceOpened() {
      this.st = snapshot(this.defaultStart, this.paused, this.playbackRate, this.volume, this.muted);
      this.baseline = { paused: this.paused, rate: this.playbackRate, volume: this.volume, muted: this.muted };
    }

    sourceFailure() {
      this.error = new MediaError(KEY, MEDIA_ERR_SRC_NOT_SUPPORTED, 'MEDIA_ELEMENT_ERROR: Format error');
      this.networkState = NETWORK_NO_SOURCE;
      this.queue(['error']);
      this.rejectPlayPromises(domError('NotSupportedError', 'Failed to load because no supported source was found.'));
    }

    mediaError(code, message) {
      if (code === MEDIA_ERR_SRC_NOT_SUPPORTED) {
        this.sourceFailure();
        return;
      }
      this.error = new MediaError(KEY, code, message);
      this.networkState = NETWORK_IDLE;
      this.queue(['error']);
      this.updateTimer();
    }

    // ---- notifications from the source and the session ----

    mediaSourceEnded(error) {
      this.invalidateBuffered();
      if (!error) {
        this.networkState = NETWORK_IDLE;
        this.timelineChanged();
        return;
      }
      if (this.readyState === HAVE_NOTHING) this.sourceFailure();
      else if (error === 'network') this.mediaError(MEDIA_ERR_NETWORK, 'PIPELINE_ERROR_NETWORK: endOfStream(network)');
      else this.mediaError(MEDIA_ERR_DECODE, 'PIPELINE_ERROR_DECODE: endOfStream(decode)');
    }

    mediaSourceReopened() {
      this.networkState = NETWORK_LOADING;
      this.invalidateBuffered();
    }

    durationChanged() {
      this.queue(['durationchange']);
      this.timelineChanged();
    }

    bufferedChanged() {
      this.invalidateBuffered();
      this.timelineChanged();
    }

    tracksChanged() {
      this.invalidateBuffered();
    }

    dataAppended() {
      const t = now();
      if (t - this.lastProgress >= PROGRESS_MS) {
        this.lastProgress = t;
        this.queue(['progress']);
      }
    }

    timelineChanged() {
      if (this.session) this.session.timelineChanged();
    }

    sessionEnded(reason, message) {
      if (reason === 'stopped' || reason === 'replaced') {
        this.internalPause();
      } else if (reason === 'unauthorized' || reason === 'link') {
        this.mediaError(MEDIA_ERR_NETWORK, message || reason);
      } else if ((reason === 'unsupported' || reason === 'encrypted') && this.readyState === HAVE_NOTHING) {
        this.mediaError(MEDIA_ERR_SRC_NOT_SUPPORTED, message || reason);
      } else {
        this.mediaError(MEDIA_ERR_DECODE, message || reason);
      }
      this.stopTimer();
    }

    initPictureSize() {
      const ms = this.mediaSource;
      if (!ms) return null;
      for (const b of ms.buffers) {
        for (const t of b.tracks || []) {
          if (t && t.kind === 'video' && t.width > 0 && t.height > 0) return [t.width | 0, t.height | 0];
        }
      }
      return null;
    }

    // ---- state from the application ----

    onState(s) {
      const ack = finite(s.ack, 0);
      const prevAck = this.lastAck;
      this.lastAck = ack;
      if (!this.mediaSource) return;
      const t = now();
      const prev = this.st;
      const seekOwned = ack < this.own.seek;
      const next = {
        time: finite(s.time, prev.time),
        paused: !!s.paused,
        rate: finite(s.rate, prev.rate),
        volume: finite(s.volume, prev.volume),
        muted: !!s.muted,
        readyState: Math.min(Math.max(finite(s.readyState, 0) | 0, HAVE_NOTHING), HAVE_ENOUGH_DATA),
        seeking: !!s.seeking,
        ended: !!s.ended,
        totalFrames: finite(s.totalFrames, prev.totalFrames),
        droppedFrames: finite(s.droppedFrames, prev.droppedFrames),
        at: t,
      };
      if (seekOwned) {
        next.time = prev.time;
        next.at = prev.at;
        next.seeking = prev.seeking;
        next.ended = prev.ended;
      } else if (this.paused && ack < this.own.paused) {
        next.time = prev.time;
        next.at = prev.at;
      }

      const oldRs = this.readyState;
      const newRs = this.seeking && seekOwned ? oldRs : next.readyState;
      const events = [];

      // Picture size, metadata and first data.
      const size = (s.videoWidth | 0) > 0 && (s.videoHeight | 0) > 0 ? [s.videoWidth | 0, s.videoHeight | 0] : this.initPictureSize();
      if (newRs >= HAVE_METADATA && size && (size[0] !== this.videoWidth || size[1] !== this.videoHeight)) {
        this.videoWidth = size[0];
        this.videoHeight = size[1];
        events.push('resize');
      }
      if (oldRs === HAVE_NOTHING && newRs >= HAVE_METADATA) {
        events.push('loadedmetadata');
        // A position set before metadata went out as a seek; the element is seeking there until it is applied.
        if (this.defaultStart !== 0 && seekOwned) {
          this.seeking = true;
          this.appSeeking = false;
          this.seekTarget = this.defaultStart;
          this.epoch++;
          events.push('seeking');
        }
        this.defaultStart = 0;
      }
      if (newRs >= HAVE_CURRENT_DATA && !this.loadedData) {
        this.loadedData = true;
        events.push('loadeddata');
      }

      // Rate, volume and pause changes made with the application's own controls.
      const changed = (field, owner, value) => {
        if (ack < this.own[owner]) return false;
        if (prevAck < this.own[owner]) {
          this.baseline[field] = value;
          return false;
        }
        if (value === this.baseline[field]) return false;
        this.baseline[field] = value;
        return true;
      };
      if (changed('rate', 'rate', next.rate) && next.rate !== this.playbackRate) {
        this.playbackRate = next.rate;
        events.push('ratechange');
      }
      const volumeChanged = changed('volume', 'volume', next.volume) && next.volume !== this.volume;
      const mutedChanged = changed('muted', 'volume', next.muted) && next.muted !== this.muted;
      if (volumeChanged) this.volume = next.volume;
      if (mutedChanged) this.muted = next.muted;
      if (volumeChanged || mutedChanged) events.push('volumechange');

      // Seeks: completion of the element's own, and seeks made in the application.
      let localSeekDone = false;
      let appSeekStart = false;
      let appSeekDone = false;
      let jump = false;
      if (this.seeking && !seekOwned) {
        localSeekDone = !next.seeking;
      } else if (!this.seeking && !seekOwned) {
        if (next.seeking && (!this.appSeeking || next.time !== this.appSeekTarget)) {
          appSeekStart = true;
        } else if (!next.seeking && this.appSeeking) {
          appSeekDone = true;
        } else if (!next.seeking && !prev.seeking && oldRs >= HAVE_METADATA) {
          const prevPlaying = !prev.paused && !prev.ended && prev.readyState >= HAVE_FUTURE_DATA;
          const expected = prev.time + (prevPlaying ? ((t - prev.at) / 1000) * prev.rate : 0);
          jump = Math.abs(next.time - expected) > JUMP_SECONDS;
        }
      }
      if (localSeekDone) this.seeking = false;
      if (appSeekStart) {
        this.appSeeking = true;
        this.appSeekTarget = next.time;
      }
      if (appSeekDone) this.appSeeking = false;
      if (localSeekDone || appSeekStart || appSeekDone || jump) {
        this.epoch++;
        this.closePlayed();
      }
      // Ended playback is the end of an ended source, also when paused or reached by a seek.
      const atEnd = !seekOwned && (next.ended || this.atSourceEnd(next.time));
      if (atEnd || appSeekStart || jump) this.endPending = false;
      if (!seekOwned && !atEnd && !this.endPending) this.ended = false;
      const endedNow = atEnd && !this.ended && !this.seeking && !this.appSeeking;

      const pausedChanged = changed('paused', 'paused', next.paused) && next.paused !== this.paused;
      this.st = next;
      this.readyState = newRs;
      if (pausedChanged && next.paused && !endedNow && !this.endedPlayback) {
        this.paused = true;
        const promises = this.takePlayPromises();
        const error = domError('AbortError', 'The play() request was interrupted by a call to pause().');
        events.push('timeupdate', 'pause', () => promises.forEach((p) => p.reject(error)));
        this.closePlayed();
      } else if (pausedChanged && !next.paused) {
        this.paused = false;
        events.push('play');
        if (newRs < HAVE_FUTURE_DATA) events.push('waiting');
        else if (oldRs >= HAVE_FUTURE_DATA) events.push(...this.playingItems());
      }

      if (localSeekDone) events.push('timeupdate', 'seeked');
      if (appSeekStart) {
        events.push('seeking');
        if (!this.paused && newRs < HAVE_FUTURE_DATA) events.push('waiting');
      }
      if (appSeekDone) events.push('timeupdate', 'seeked');
      if (jump) events.push('seeking', 'timeupdate', 'seeked');

      if (oldRs < HAVE_FUTURE_DATA && newRs >= HAVE_FUTURE_DATA) {
        events.push('canplay');
        if (!this.paused) events.push(...this.playingItems());
      } else if (oldRs >= HAVE_FUTURE_DATA && newRs < HAVE_FUTURE_DATA && !this.paused && !this.seekingAny && !endedNow &&
        !appSeekStart) {
        events.push('timeupdate', 'waiting');
      }
      if (oldRs < HAVE_ENOUGH_DATA && newRs === HAVE_ENOUGH_DATA) {
        events.push('canplaythrough');
        if (this.paused && this.canAutoplay && this.host.autoplay) {
          this.paused = false;
          this.canAutoplay = false;
          const n = this.command('play');
          if (n) this.own.paused = n;
          events.push('play', ...this.playingItems());
        }
      }

      if (endedNow) this.reachEnd(events);

      if (this.isPotentiallyPlaying() && !this.seekingAny) {
        this.notePlayed(this.currentTime);
        if (!events.includes('timeupdate') && t - this.lastTimeupdate >= TIMEUPDATE_MS) events.push('timeupdate');
      }
      this.queue(events);
      if (newRs >= HAVE_CURRENT_DATA && (this.isPotentiallyPlaying() || localSeekDone || oldRs < HAVE_CURRENT_DATA)) {
        this.deliverFrameCallbacks();
      }
      this.updateTimer();
    }

    reachEnd(events) {
      if (this.host.loop) {
        const seekable = this.seekableRanges();
        events.push(() => this.seek(seekable.length ? seekable[0][0] : 0));
        return;
      }
      this.ended = true;
      this.closePlayed();
      // One task, as on a native element; its pause is skipped when a play() or seek made before
      // it already left the end.
      events.push(() => {
        this.lastTimeupdate = now();
        this.host.fire('timeupdate');
        if (this.endedPlayback && !this.paused) {
          this.paused = true;
          const promises = this.takePlayPromises();
          this.host.fire('pause');
          const error = domError('AbortError', 'The play() request was interrupted because the media ended.');
          promises.forEach((p) => p.reject(error));
          this.updateTimer();
        }
        this.host.fire('ended');
      });
    }

    // ---- periodic work ----

    updateTimer() {
      const needed = !this.paused && this.mediaSource && !this.error && (this.session && !this.session.closed);
      if (needed && !this.timer) this.timer = setInterval(() => this.tick(), TICK_MS);
      else if (!needed) this.stopTimer();
    }

    stopTimer() {
      if (this.timer) {
        clearInterval(this.timer);
        this.timer = null;
      }
    }

    tick() {
      if (!this.isPotentiallyPlaying() || this.seekingAny) return;
      this.notePlayed(this.currentTime);
      if (now() - this.lastTimeupdate >= TIMEUPDATE_MS) this.queue(['timeupdate']);
      this.deliverFrameCallbacks();
    }

    requestVideoFrameCallback(callback) {
      if (typeof callback !== 'function') {
        throw new TypeError("Failed to execute 'requestVideoFrameCallback' on 'HTMLVideoElement': The callback provided as parameter 1 is not a function.");
      }
      const id = ++this.frameCallbackId;
      this.frameCallbacks.set(id, callback);
      return id;
    }

    cancelVideoFrameCallback(id) {
      this.frameCallbacks.delete(Number(id));
    }

    deliverFrameCallbacks() {
      if (!this.frameCallbacks.size || this.readyState < HAVE_CURRENT_DATA) return;
      const callbacks = Array.from(this.frameCallbacks.values());
      this.frameCallbacks.clear();
      const t = now();
      const metadata = {
        presentationTime: t,
        expectedDisplayTime: t,
        width: this.videoWidth,
        height: this.videoHeight,
        mediaTime: this.currentTime,
        presentedFrames: ++this.presentedFrames,
        processingDuration: 0,
      };
      queueTask(() => {
        for (const cb of callbacks) {
          try {
            cb(t, metadata);
          } catch (e) {
            report(e);
          }
        }
      });
    }

    getVideoPlaybackQuality() {
      return new VideoPlaybackQuality(KEY, {
        creationTime: now(),
        totalVideoFrames: this.st.totalFrames | 0,
        droppedVideoFrames: this.st.droppedFrames | 0,
      });
    }
  }

  // ---- text tracks, kept local ----

  const trackListItems = new WeakMap();
  const trackFields = new WeakMap();
  const cueListItems = new WeakMap();

  class TextTrackCueList {
    constructor(key) {
      if (key !== KEY) throw new TypeError('Illegal constructor');
      cueListItems.set(this, []);
    }
    get length() {
      return cueListItems.get(this).length;
    }
    getCueById(id) {
      return cueListItems.get(this).find((c) => c.id === String(id)) || null;
    }
    [Symbol.iterator]() {
      return cueListItems.get(this).slice()[Symbol.iterator]();
    }
  }
  defineTag(TextTrackCueList, 'TextTrackCueList');

  function setIndexed(target, items) {
    const store = target instanceof TextTrackCueList ? cueListItems : trackListItems;
    const old = store.get(target);
    for (let i = 0; i < old.length; i++) delete target[i];
    store.set(target, items.slice());
    items.forEach((item, i) => Object.defineProperty(target, i, { value: item, configurable: true, enumerable: true }));
  }

  class TextTrackList extends RemoteEventTarget {
    constructor(key) {
      super();
      if (key !== KEY) throw new TypeError('Illegal constructor');
      trackListItems.set(this, []);
    }
    get length() {
      return trackListItems.get(this).length;
    }
    getTrackById(id) {
      return trackListItems.get(this).find((t) => t.id === String(id)) || null;
    }
    [Symbol.iterator]() {
      return trackListItems.get(this).slice()[Symbol.iterator]();
    }
  }
  defineEventHandlers(TextTrackList, ['change', 'addtrack', 'removetrack']);
  defineTag(TextTrackList, 'TextTrackList');

  function addTrack(list, track) {
    const items = trackListItems.get(list);
    if (items.includes(track)) return;
    setIndexed(list, items.concat([track]));
    queueTask(() => {
      const event = new Event('addtrack');
      Object.defineProperty(event, 'track', { value: track, configurable: true });
      list.dispatchEvent(event);
    });
  }

  function removeTrack(list, track) {
    const items = trackListItems.get(list);
    if (!items.includes(track)) return;
    setIndexed(list, items.filter((t) => t !== track));
    queueTask(() => {
      const event = new Event('removetrack');
      Object.defineProperty(event, 'track', { value: track, configurable: true });
      list.dispatchEvent(event);
    });
  }

  class TextTrack extends RemoteEventTarget {
    constructor(key, list, kind, label, language) {
      super();
      if (key !== KEY) throw new TypeError('Illegal constructor');
      trackFields.set(this, { list, kind, label, language, mode: 'hidden', cues: new TextTrackCueList(KEY) });
    }
    get id() {
      return '';
    }
    get kind() {
      return trackFields.get(this).kind;
    }
    get label() {
      return trackFields.get(this).label;
    }
    get language() {
      return trackFields.get(this).language;
    }
    get inBandMetadataTrackDispatchType() {
      return '';
    }
    get mode() {
      return trackFields.get(this).mode;
    }
    set mode(value) {
      const f = trackFields.get(this);
      const mode = String(value);
      if (mode !== 'disabled' && mode !== 'hidden' && mode !== 'showing') return;
      if (mode === f.mode) return;
      f.mode = mode;
      internal.queueFire(f.list, 'change');
    }
    get cues() {
      const f = trackFields.get(this);
      return f.mode === 'disabled' ? null : f.cues;
    }
    get activeCues() {
      const f = trackFields.get(this);
      return f.mode === 'disabled' ? null : new TextTrackCueList(KEY);
    }
    addCue(cue) {
      const cues = trackFields.get(this).cues;
      const items = cueListItems.get(cues).filter((c) => c !== cue);
      items.push(cue);
      items.sort((a, b) => a.startTime - b.startTime || b.endTime - a.endTime);
      setIndexed(cues, items);
    }
    removeCue(cue) {
      const cues = trackFields.get(this).cues;
      const items = cueListItems.get(cues);
      if (!items.includes(cue)) throw domError('NotFoundError', "Failed to execute 'removeCue' on 'TextTrack': The specified cue is not listed in the TextTrack's list of cues.");
      setIndexed(cues, items.filter((c) => c !== cue));
    }
  }
  defineEventHandlers(TextTrack, ['cuechange']);
  defineTag(TextTrack, 'TextTrack');

  // ---- the element surface ----

  const elements = new WeakMap();
  const BOOLEAN_ATTRIBUTES = { autoplay: 'autoplay', loop: 'loop', controls: 'controls', playsInline: 'playsinline',
    defaultMuted: 'muted', disableRemotePlayback: 'disableremoteplayback', disablePictureInPicture: 'disablepictureinpicture',
    hidden: 'hidden' };
  const STRING_ATTRIBUTES = { id: 'id', className: 'class', poster: 'poster', title: 'title' };

  function resolveUrl(value) {
    try {
      return new URL(value, document.baseURI).href;
    } catch (e) {
      if (e instanceof TypeError) return value;
      throw e;
    }
  }

  function nameOf(name) {
    return String(name).toLowerCase();
  }

  function childAdded(e, node) {
    if (node.localName === 'source') e.state.sourceInserted(e.attributes.has('src'));
    else if (node.localName === 'track' && node.track) addTrack(e.textTracks, node.track);
  }

  class RemoteVideoElement extends RemoteEventTarget {
    constructor(key, session) {
      super();
      if (key !== KEY) throw new TypeError('Illegal constructor');
      const attributes = new Map();
      const children = [];
      const host = {
        fire: (type) => internal.fire(this, type),
        resourceCandidates: () => {
          if (attributes.has('src')) return { mode: 'attribute', url: resolveUrl(attributes.get('src')) };
          const list = children
            .filter((c) => c && c.localName === 'source' && c.hasAttribute('src'))
            .map((c) => ({ url: c.src, type: c.type || '' }));
          return list.length ? { mode: 'children', list } : null;
        },
        get autoplay() {
          return attributes.has('autoplay');
        },
        get loop() {
          return attributes.has('loop');
        },
      };
      elements.set(this, {
        state: new ElementState(session, host),
        session,
        attributes,
        children,
        textTracks: new TextTrackList(KEY),
        style: {},
        dataset: {},
      });
    }

    // DOM-shaped members
    get nodeName() {
      return 'VIDEO';
    }
    get tagName() {
      return 'VIDEO';
    }
    get localName() {
      return 'video';
    }
    get nodeType() {
      return 1;
    }
    get namespaceURI() {
      return 'http://www.w3.org/1999/xhtml';
    }
    get ownerDocument() {
      return document;
    }
    get isConnected() {
      return true;
    }
    get parentNode() {
      return null;
    }
    get parentElement() {
      return null;
    }
    get offsetParent() {
      return null;
    }
    get style() {
      return elements.get(this).style;
    }
    get dataset() {
      return elements.get(this).dataset;
    }
    get children() {
      return elements.get(this).children.filter((c) => c && c.nodeType === 1);
    }
    get childNodes() {
      return elements.get(this).children.slice();
    }
    get firstChild() {
      return elements.get(this).children[0] || null;
    }
    get lastChild() {
      const c = elements.get(this).children;
      return c[c.length - 1] || null;
    }
    get firstElementChild() {
      return this.children[0] || null;
    }
    get lastElementChild() {
      const c = this.children;
      return c[c.length - 1] || null;
    }
    get childElementCount() {
      return this.children.length;
    }
    hasChildNodes() {
      return elements.get(this).children.length > 0;
    }
    contains(node) {
      return node === this || elements.get(this).children.includes(node);
    }

    get clientWidth() {
      return Math.round(elements.get(this).session.view.width);
    }
    get clientHeight() {
      return Math.round(elements.get(this).session.view.height);
    }
    get offsetWidth() {
      return this.clientWidth;
    }
    get offsetHeight() {
      return this.clientHeight;
    }
    get width() {
      return Number(elements.get(this).attributes.get('width')) >>> 0;
    }
    set width(value) {
      this.setAttribute('width', String(value >>> 0));
    }
    get height() {
      return Number(elements.get(this).attributes.get('height')) >>> 0;
    }
    set height(value) {
      this.setAttribute('height', String(value >>> 0));
    }
    getBoundingClientRect() {
      const view = elements.get(this).session.view;
      return new DOMRect(0, 0, view.width, view.height);
    }
    getClientRects() {
      return [this.getBoundingClientRect()];
    }

    getAttribute(name) {
      const attributes = elements.get(this).attributes;
      const n = nameOf(name);
      return attributes.has(n) ? attributes.get(n) : null;
    }
    getAttributeNames() {
      return Array.from(elements.get(this).attributes.keys());
    }
    hasAttribute(name) {
      return elements.get(this).attributes.has(nameOf(name));
    }
    hasAttributes() {
      return elements.get(this).attributes.size > 0;
    }
    setAttribute(name, value) {
      const e = elements.get(this);
      const n = nameOf(name);
      e.attributes.set(n, String(value));
      if (n === 'src') e.state.load();
    }
    removeAttribute(name) {
      elements.get(this).attributes.delete(nameOf(name));
    }
    toggleAttribute(name, force) {
      const n = nameOf(name);
      const present = this.hasAttribute(n);
      const want = force === undefined ? !present : !!force;
      if (want && !present) this.setAttribute(n, '');
      else if (!want && present) this.removeAttribute(n);
      return want;
    }

    appendChild(node) {
      return this.insertBefore(node, null);
    }
    append(...nodes) {
      for (const node of nodes) {
        if (node !== null && typeof node === 'object') this.insertBefore(node, null);
      }
    }
    insertBefore(node, reference) {
      if (!node || typeof node !== 'object') {
        throw new TypeError("Failed to execute 'insertBefore' on 'Node': parameter 1 is not of type 'Node'.");
      }
      const e = elements.get(this);
      const existing = e.children.indexOf(node);
      if (existing >= 0) e.children.splice(existing, 1);
      const at = reference ? e.children.indexOf(reference) : -1;
      if (reference && at < 0) {
        throw domError('NotFoundError', "Failed to execute 'insertBefore' on 'Node': The node before which the new node is to be inserted is not a child of this node.");
      }
      if (at < 0) e.children.push(node);
      else e.children.splice(at, 0, node);
      if (existing < 0) childAdded(e, node);
      return node;
    }
    removeChild(node) {
      const e = elements.get(this);
      const i = e.children.indexOf(node);
      if (i < 0) {
        throw domError('NotFoundError', "Failed to execute 'removeChild' on 'Node': The node to be removed is not a child of this node.");
      }
      e.children.splice(i, 1);
      if (node.localName === 'track' && node.track) removeTrack(e.textTracks, node.track);
      return node;
    }
    replaceChildren(...nodes) {
      for (const child of elements.get(this).children.slice()) this.removeChild(child);
      this.append(...nodes);
    }
    getElementsByTagName(name) {
      const n = nameOf(name);
      return this.children.filter((c) => n === '*' || c.localName === n);
    }
    querySelectorAll(selector) {
      return this.getElementsByTagName(String(selector).trim());
    }
    querySelector(selector) {
      return this.querySelectorAll(selector)[0] || null;
    }

    // Media members
    get src() {
      const value = this.getAttribute('src');
      return value === null ? '' : resolveUrl(value);
    }
    set src(value) {
      this.setAttribute('src', value);
    }
    get currentSrc() {
      return elements.get(this).state.currentSrc;
    }
    get srcObject() {
      return null;
    }
    set srcObject(value) {
      if (value !== null) throw new TypeError("Failed to set the 'srcObject' property on 'HTMLMediaElement': The provided value is not supported.");
      elements.get(this).state.load();
    }
    get crossOrigin() {
      return this.getAttribute('crossorigin');
    }
    set crossOrigin(value) {
      if (value === null) this.removeAttribute('crossorigin');
      else this.setAttribute('crossorigin', value);
    }
    get preload() {
      const value = this.getAttribute('preload');
      return value === 'none' || value === 'metadata' ? value : 'auto';
    }
    set preload(value) {
      this.setAttribute('preload', value);
    }
    get networkState() {
      return elements.get(this).state.networkState;
    }
    get readyState() {
      return elements.get(this).state.readyState;
    }
    get error() {
      return elements.get(this).state.error;
    }
    get buffered() {
      return elements.get(this).state.buffered;
    }
    get seekable() {
      return elements.get(this).state.seekable;
    }
    get played() {
      return elements.get(this).state.played;
    }
    get seeking() {
      return elements.get(this).state.seekingAny;
    }
    get currentTime() {
      return elements.get(this).state.currentTime;
    }
    set currentTime(value) {
      elements.get(this).state.currentTime = value;
    }
    fastSeek(time) {
      elements.get(this).state.currentTime = time;
    }
    get duration() {
      return elements.get(this).state.duration;
    }
    getStartDate() {
      return new Date(NaN);
    }
    get paused() {
      return elements.get(this).state.paused;
    }
    get ended() {
      return elements.get(this).state.endedPlayback;
    }
    get playbackRate() {
      return elements.get(this).state.playbackRate;
    }
    set playbackRate(value) {
      elements.get(this).state.setPlaybackRate(value);
    }
    get defaultPlaybackRate() {
      return elements.get(this).state.defaultPlaybackRate;
    }
    set defaultPlaybackRate(value) {
      elements.get(this).state.setDefaultPlaybackRate(value);
    }
    get preservesPitch() {
      return elements.get(this).state.preservesPitch;
    }
    set preservesPitch(value) {
      elements.get(this).state.preservesPitch = !!value;
    }
    get volume() {
      return elements.get(this).state.volume;
    }
    set volume(value) {
      elements.get(this).state.setVolume(value);
    }
    get muted() {
      return elements.get(this).state.muted;
    }
    set muted(value) {
      elements.get(this).state.setMuted(value);
    }
    play() {
      return elements.get(this).state.play();
    }
    pause() {
      elements.get(this).state.pause();
    }
    load() {
      elements.get(this).state.load();
    }
    canPlayType(type) {
      return internal.canPlayType(String(type));
    }
    get videoWidth() {
      return elements.get(this).state.videoWidth;
    }
    get videoHeight() {
      return elements.get(this).state.videoHeight;
    }
    getVideoPlaybackQuality() {
      return elements.get(this).state.getVideoPlaybackQuality();
    }
    get webkitDecodedFrameCount() {
      const st = elements.get(this).state.st;
      return Math.max(0, (st.totalFrames | 0) - (st.droppedFrames | 0));
    }
    get webkitDroppedFrameCount() {
      return elements.get(this).state.st.droppedFrames | 0;
    }
    requestVideoFrameCallback(callback) {
      return elements.get(this).state.requestVideoFrameCallback(callback);
    }
    cancelVideoFrameCallback(handle) {
      elements.get(this).state.cancelVideoFrameCallback(handle);
    }
    get textTracks() {
      return elements.get(this).textTracks;
    }
    addTextTrack(kind, label, language) {
      const list = elements.get(this).textTracks;
      const track = new TextTrack(KEY, list, String(kind), label === undefined ? '' : String(label), language === undefined ? '' : String(language));
      addTrack(list, track);
      return track;
    }
    get mediaKeys() {
      return null;
    }
    setMediaKeys(mediaKeys) {
      if (mediaKeys === null) return Promise.resolve();
      return Promise.reject(domError('NotSupportedError', 'Key systems are not supported.'));
    }
    requestPictureInPicture() {
      return Promise.reject(domError('NotSupportedError', 'Picture-in-picture is not available for this video.'));
    }
  }

  for (const [property, attribute] of Object.entries(BOOLEAN_ATTRIBUTES)) {
    Object.defineProperty(RemoteVideoElement.prototype, property, {
      configurable: true,
      enumerable: true,
      get() {
        return this.hasAttribute(attribute);
      },
      set(value) {
        if (value) this.setAttribute(attribute, '');
        else this.removeAttribute(attribute);
      },
    });
  }
  for (const [property, attribute] of Object.entries(STRING_ATTRIBUTES)) {
    Object.defineProperty(RemoteVideoElement.prototype, property, {
      configurable: true,
      enumerable: true,
      get() {
        const value = this.getAttribute(attribute);
        return value === null ? '' : value;
      },
      set(value) {
        this.setAttribute(attribute, value);
      },
    });
  }
  defineEventHandlers(RemoteVideoElement, ELEMENT_EVENTS);
  defineConstants(RemoteVideoElement, { HAVE_NOTHING, HAVE_METADATA, HAVE_CURRENT_DATA, HAVE_FUTURE_DATA, HAVE_ENOUGH_DATA,
    NETWORK_EMPTY, NETWORK_IDLE, NETWORK_LOADING, NETWORK_NO_SOURCE });
  defineTag(RemoteVideoElement, 'HTMLVideoElement');

  internal.createElement = (session) => new RemoteVideoElement(KEY, session);
  internal.elementState = (element) => {
    const e = elements.get(element);
    return e ? e.state : null;
  };
  internal.isRemoteElement = (value) => elements.has(value);

  R.ElementState = ElementState;
  R.RemoteVideoElement = RemoteVideoElement;
  R.MediaError = MediaError;
  R.VideoPlaybackQuality = VideoPlaybackQuality;
  R.TextTrack = TextTrack;
  R.TextTrackList = TextTrackList;
  R.TextTrackCueList = TextTrackCueList;
  R.ELEMENT_EVENTS = ELEMENT_EVENTS;
})();
