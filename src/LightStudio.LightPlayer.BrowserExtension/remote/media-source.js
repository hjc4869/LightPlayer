// MediaSource, SourceBuffer and SourceBufferList over the browser link. They hold no media: appends
// go to the application, and buffered ranges and the full flag come back in its replies.
'use strict';
(function () {
  const R = globalThis.LightPlayerRemote;
  if (!R || R.MediaSource) return;
  const { internal } = R;
  const { KEY, domError, queueFire, RemoteEventTarget, defineEventHandlers, defineTag } = internal;

  const mediaSources = new WeakMap();
  const sourceBuffers = new WeakMap();
  const listItems = new WeakMap();

  // A duration below the highest buffered end by more than this is refused, since the highest
  // presentation time lies up to one frame before that end.
  const DURATION_TOLERANCE = 0.1;

  function finiteDouble(value, what) {
    const n = Number(value);
    if (!Number.isFinite(n)) throw new TypeError(what + ': The provided double value is non-finite.');
    return n;
  }

  function copyBytes(data) {
    const tag = Object.prototype.toString.call(data);
    let bytes;
    if (tag === '[object ArrayBuffer]') bytes = new Uint8Array(data);
    else if (ArrayBuffer.isView(data)) bytes = new Uint8Array(data.buffer, data.byteOffset, data.byteLength);
    else throw new TypeError("Failed to execute 'appendBuffer' on 'SourceBuffer': The provided value is not of type '(ArrayBuffer or ArrayBufferView)'.");
    const copy = new Uint8Array(bytes.byteLength);
    copy.set(bytes);
    return copy.buffer;
  }

  class SourceBufferList extends RemoteEventTarget {
    constructor(key) {
      super();
      if (key !== KEY) throw new TypeError('Illegal constructor');
      listItems.set(this, []);
    }
    get length() {
      return listItems.get(this).length;
    }
    [Symbol.iterator]() {
      return listItems.get(this).slice()[Symbol.iterator]();
    }
  }
  defineEventHandlers(SourceBufferList, ['addsourcebuffer', 'removesourcebuffer']);
  defineTag(SourceBufferList, 'SourceBufferList');

  function setListItems(list, items) {
    const old = listItems.get(list);
    for (let i = 0; i < old.length; i++) delete list[i];
    listItems.set(list, items.slice());
    items.forEach((item, i) => Object.defineProperty(list, i, { value: item, configurable: true, enumerable: true }));
  }

  class MediaSourceCore {
    constructor(surface) {
      this.surface = surface;
      this.readyState = 'closed';
      this.duration = NaN;
      this.buffers = [];
      this.sourceBuffers = new SourceBufferList(KEY);
      this.activeSourceBuffers = new SourceBufferList(KEY);
      this.element = null;
      this.liveSeekable = null;
    }

    get session() {
      return this.element ? this.element.session : null;
    }

    send(message) {
      const session = this.session;
      if (session) session.send(message);
    }

    anyUpdating() {
      return this.buffers.some((b) => b.updating);
    }

    highestBufferedEnd() {
      let end = 0;
      for (const b of this.buffers) end = Math.max(end, internal.highestEnd(b.ranges));
      return end;
    }

    reopenIfEnded() {
      if (this.readyState !== 'ended') return;
      this.readyState = 'open';
      if (this.element) this.element.mediaSourceReopened();
      queueFire(this.surface, 'sourceopen');
    }

    setDuration(value) {
      if (Object.is(this.duration, value)) return;
      this.duration = value;
      this.send({ type: 'duration', value: value === Infinity ? null : value });
      if (this.element) this.element.durationChanged();
    }

    addSourceBuffer(type) {
      if (type === '') throw new TypeError("Failed to execute 'addSourceBuffer' on 'MediaSource': The type provided is empty.");
      if (!internal.isTypeSupported(type)) {
        throw domError('NotSupportedError', "Failed to execute 'addSourceBuffer' on 'MediaSource': The type provided ('" + type + "') is unsupported.");
      }
      if (this.buffers.length >= 4 || (this.element && this.element.readyState >= 1)) {
        throw domError('QuotaExceededError', "Failed to execute 'addSourceBuffer' on 'MediaSource': This MediaSource has reached the limit of SourceBuffer objects it can handle. No additional SourceBuffer objects may be added.");
      }
      if (this.readyState !== 'open') {
        throw domError('InvalidStateError', "Failed to execute 'addSourceBuffer' on 'MediaSource': The MediaSource's readyState is not 'open'.");
      }
      const session = this.session;
      const surface = new SourceBuffer(KEY);
      const core = new SourceBufferCore(surface, this, type, session.nextBufferId());
      sourceBuffers.set(surface, core);
      session.registerBuffer(core);
      this.buffers.push(core);
      setListItems(this.sourceBuffers, this.buffers.map((b) => b.surface));
      this.send({ type: 'addSourceBuffer', buffer: core.id, mimeType: type });
      queueFire(this.sourceBuffers, 'addsourcebuffer');
      return surface;
    }

    removeSourceBuffer(surface) {
      const core = sourceBuffers.get(surface);
      if (!core || !this.buffers.includes(core)) {
        throw domError('NotFoundError', "Failed to execute 'removeSourceBuffer' on 'MediaSource': The SourceBuffer provided is not contained in this MediaSource.");
      }
      if (core.updating) core.abortOperation();
      const active = this.buffers.filter((b) => b.active && b !== core);
      if (core.active) {
        setListItems(this.activeSourceBuffers, active.map((b) => b.surface));
        queueFire(this.activeSourceBuffers, 'removesourcebuffer');
      }
      this.buffers = this.buffers.filter((b) => b !== core);
      setListItems(this.sourceBuffers, this.buffers.map((b) => b.surface));
      queueFire(this.sourceBuffers, 'removesourcebuffer');
      core.release();
      this.send({ type: 'removeSourceBuffer', buffer: core.id });
      if (this.element) this.element.bufferedChanged();
    }

    activate() {
      setListItems(this.activeSourceBuffers, this.buffers.filter((b) => b.active).map((b) => b.surface));
      queueFire(this.activeSourceBuffers, 'addsourcebuffer');
      if (Number.isNaN(this.duration)) this.setDuration(Infinity);
    }

    endOfStream(error) {
      if (this.readyState !== 'open') {
        throw domError('InvalidStateError', "Failed to execute 'endOfStream' on 'MediaSource': The MediaSource's readyState is not 'open'.");
      }
      if (this.anyUpdating()) {
        throw domError('InvalidStateError', "Failed to execute 'endOfStream' on 'MediaSource': The 'updating' attribute is true on one or more of this MediaSource's SourceBuffers.");
      }
      this.runEndOfStream(error);
    }

    runEndOfStream(error) {
      this.readyState = 'ended';
      queueFire(this.surface, 'sourceended');
      if (!error) this.setDuration(this.highestBufferedEnd());
      this.send({ type: 'endOfStream', error: error || null });
      if (this.element) this.element.mediaSourceEnded(error || null);
    }

    setLiveSeekableRange(start, end) {
      if (this.readyState !== 'open') {
        throw domError('InvalidStateError', "Failed to execute 'setLiveSeekableRange' on 'MediaSource': The MediaSource's readyState is not 'open'.");
      }
      if (start < 0 || start > end) {
        throw new TypeError("Failed to execute 'setLiveSeekableRange' on 'MediaSource': The start provided (" + start + ') is outside the range (0, ' + end + ').');
      }
      this.liveSeekable = [start, end];
      if (this.element) this.element.timelineChanged();
    }

    clearLiveSeekableRange() {
      if (this.readyState !== 'open') {
        throw domError('InvalidStateError', "Failed to execute 'clearLiveSeekableRange' on 'MediaSource': The MediaSource's readyState is not 'open'.");
      }
      if (!this.liveSeekable) return;
      this.liveSeekable = null;
      if (this.element) this.element.timelineChanged();
    }

    attach(element) {
      if (this.readyState !== 'closed') return false;
      this.element = element;
      this.readyState = 'open';
      queueFire(this.surface, 'sourceopen');
      return true;
    }

    detach() {
      if (this.readyState === 'closed') return;
      this.readyState = 'closed';
      this.duration = NaN;
      this.liveSeekable = null;
      const hadActive = this.buffers.some((b) => b.active);
      const hadBuffers = this.buffers.length > 0;
      for (const b of this.buffers) b.release();
      this.buffers = [];
      setListItems(this.activeSourceBuffers, []);
      setListItems(this.sourceBuffers, []);
      if (hadActive) queueFire(this.activeSourceBuffers, 'removesourcebuffer');
      if (hadBuffers) queueFire(this.sourceBuffers, 'removesourcebuffer');
      this.element = null;
      queueFire(this.surface, 'sourceclose');
    }
  }

  class MediaSource extends RemoteEventTarget {
    constructor() {
      super();
      mediaSources.set(this, new MediaSourceCore(this));
    }
    static isTypeSupported(type) {
      if (arguments.length < 1) throw new TypeError("Failed to execute 'isTypeSupported' on 'MediaSource': 1 argument required, but only 0 present.");
      return internal.isTypeSupported(String(type));
    }
    static get canConstructInDedicatedWorker() {
      return false;
    }
    get sourceBuffers() {
      return mediaSources.get(this).sourceBuffers;
    }
    get activeSourceBuffers() {
      return mediaSources.get(this).activeSourceBuffers;
    }
    get readyState() {
      return mediaSources.get(this).readyState;
    }
    get duration() {
      const core = mediaSources.get(this);
      return core.readyState === 'closed' ? NaN : core.duration;
    }
    set duration(value) {
      const core = mediaSources.get(this);
      const n = Number(value);
      if (n < 0 || Number.isNaN(n)) {
        throw new TypeError("Failed to set the 'duration' property on 'MediaSource': The value provided (" + value + ') is negative or NaN.');
      }
      if (core.readyState !== 'open') {
        throw domError('InvalidStateError', "Failed to set the 'duration' property on 'MediaSource': The MediaSource's readyState is not 'open'.");
      }
      if (core.anyUpdating()) {
        throw domError('InvalidStateError', "Failed to set the 'duration' property on 'MediaSource': The 'updating' attribute is true on one or more of this MediaSource's SourceBuffers.");
      }
      if (n < core.highestBufferedEnd() - DURATION_TOLERANCE) {
        throw domError('InvalidStateError', "Failed to set the 'duration' property on 'MediaSource': Setting duration below highest presentation timestamp of any buffered coded frames is disallowed.");
      }
      core.setDuration(n);
    }
    addSourceBuffer(type) {
      if (arguments.length < 1) throw new TypeError("Failed to execute 'addSourceBuffer' on 'MediaSource': 1 argument required, but only 0 present.");
      return mediaSources.get(this).addSourceBuffer(String(type));
    }
    removeSourceBuffer(sourceBuffer) {
      mediaSources.get(this).removeSourceBuffer(sourceBuffer);
    }
    endOfStream(error) {
      if (error !== undefined && error !== 'network' && error !== 'decode') {
        throw new TypeError("Failed to execute 'endOfStream' on 'MediaSource': The provided value '" + error + "' is not a valid enum value of type EndOfStreamError.");
      }
      mediaSources.get(this).endOfStream(error);
    }
    setLiveSeekableRange(start, end) {
      const s = finiteDouble(start, "Failed to execute 'setLiveSeekableRange' on 'MediaSource'");
      const e = finiteDouble(end, "Failed to execute 'setLiveSeekableRange' on 'MediaSource'");
      mediaSources.get(this).setLiveSeekableRange(s, e);
    }
    clearLiveSeekableRange() {
      mediaSources.get(this).clearLiveSeekableRange();
    }
  }
  defineEventHandlers(MediaSource, ['sourceopen', 'sourceended', 'sourceclose']);
  defineTag(MediaSource, 'MediaSource');

  // One append is on the wire per buffer. An append made after `abort` waits for the reply to the
  // aborted one, and `sequence` counts only appends that were sent.
  class SourceBufferCore {
    constructor(surface, mediaSource, mimeType, id) {
      this.surface = surface;
      this.mediaSource = mediaSource;
      this.mimeType = mimeType;
      this.id = id;
      this.updating = false;
      this.pending = null;
      this.inFlight = 0;
      this.sequence = 0;
      this.timestampOffset = 0;
      this.appendWindowStart = 0;
      this.appendWindowEnd = Infinity;
      this.ranges = [];
      this.rangesObject = null;
      this.full = false;
      this.tracks = null;
      this.active = false;
      this.removed = false;
    }

    get session() {
      return this.mediaSource.session;
    }

    send(message) {
      message.buffer = this.id;
      this.mediaSource.send(message);
    }

    checkUsable(operation) {
      if (this.removed) {
        throw domError('InvalidStateError', 'Failed to ' + operation + ": This SourceBuffer has been removed from the parent media source.");
      }
      if (this.updating) {
        throw domError('InvalidStateError', 'Failed to ' + operation + ": This SourceBuffer is still processing an 'appendBuffer' or 'remove' operation.");
      }
    }

    appendBuffer(data) {
      const bytes = copyBytes(data);
      const op = "execute 'appendBuffer' on 'SourceBuffer'";
      this.checkUsable(op);
      const element = this.mediaSource.element;
      if (element && element.error) {
        throw domError('InvalidStateError', 'Failed to ' + op + ': The HTMLMediaElement.error attribute is not null.');
      }
      this.mediaSource.reopenIfEnded();
      if (this.full) {
        throw domError('QuotaExceededError', 'Failed to ' + op + ': The SourceBuffer is full, and cannot free space to append additional buffers.');
      }
      this.updating = true;
      this.pending = { kind: 'append', bytes, sequence: 0 };
      queueFire(this.surface, 'updatestart');
      this.sendPendingAppend();
    }

    sendPendingAppend() {
      const pending = this.pending;
      const session = this.session;
      if (!pending || pending.kind !== 'append' || pending.sequence || this.inFlight || !session) return;
      pending.sequence = ++this.sequence;
      this.inFlight = pending.sequence;
      const bytes = pending.bytes;
      pending.bytes = null;
      session.append(this.id, pending.sequence, bytes);
    }

    remove(start, end) {
      const op = "execute 'remove' on 'SourceBuffer'";
      this.checkUsable(op);
      const duration = this.mediaSource.duration;
      if (Number.isNaN(duration)) throw new TypeError('Failed to ' + op + ": The MediaSource's duration is NaN.");
      if (start < 0 || start > duration) {
        throw new TypeError('Failed to ' + op + ': The start provided (' + start + ') is outside the range (0, ' + duration + ').');
      }
      if (!(end > start)) {
        throw new TypeError('Failed to ' + op + ': The end value provided (' + end + ') must be greater than the start value provided (' + start + ').');
      }
      this.mediaSource.reopenIfEnded();
      this.updating = true;
      const request = this.session.nextRequest();
      this.pending = { kind: 'remove', request };
      queueFire(this.surface, 'updatestart');
      this.send({ type: 'remove', request, start, end: end === Infinity ? null : end });
    }

    abort() {
      const op = "execute 'abort' on 'SourceBuffer'";
      if (this.removed) {
        throw domError('InvalidStateError', 'Failed to ' + op + ': This SourceBuffer has been removed from the parent media source.');
      }
      if (this.mediaSource.readyState !== 'open') {
        throw domError('InvalidStateError', 'Failed to ' + op + ": The parent media source's readyState is not 'open'.");
      }
      if (this.pending && this.pending.kind === 'remove') {
        throw domError('InvalidStateError', 'Failed to ' + op + ": The SourceBuffer is still processing a 'remove' operation.");
      }
      if (this.updating) this.abortOperation();
      const windowChanged = this.appendWindowStart !== 0 || this.appendWindowEnd !== Infinity;
      this.appendWindowStart = 0;
      this.appendWindowEnd = Infinity;
      this.send({ type: 'abort' });
      if (windowChanged) this.send({ type: 'configure', appendWindowStart: 0, appendWindowEnd: null });
    }

    // Ends the pending operation at once. An append that was not sent is dropped; one that was sent
    // completes in the application, and its reply only updates the ranges.
    abortOperation() {
      this.pending = null;
      this.updating = false;
      queueFire(this.surface, 'abort');
      queueFire(this.surface, 'updateend');
    }

    changeType(type) {
      const op = "execute 'changeType' on 'SourceBuffer'";
      if (type === '') throw new TypeError('Failed to ' + op + ': The type provided is empty.');
      this.checkUsable(op);
      if (!internal.isTypeSupported(type)) {
        throw domError('NotSupportedError', 'Failed to ' + op + ": The type provided ('" + type + "') is not supported for SourceBuffer.");
      }
      this.mediaSource.reopenIfEnded();
      this.mimeType = type;
      this.send({ type: 'changeType', mimeType: type });
    }

    setTimestampOffset(value) {
      this.checkUsable("set the 'timestampOffset' property on 'SourceBuffer'");
      this.mediaSource.reopenIfEnded();
      if (value === this.timestampOffset) return;
      this.timestampOffset = value;
      this.send({ type: 'configure', timestampOffset: value });
    }

    setAppendWindowStart(value) {
      const op = "set the 'appendWindowStart' property on 'SourceBuffer'";
      this.checkUsable(op);
      if (value < 0 || value >= this.appendWindowEnd) {
        throw new TypeError('Failed to ' + op + ': The value provided (' + value + ') is outside the range [0, ' + this.appendWindowEnd + ').');
      }
      if (value === this.appendWindowStart) return;
      this.appendWindowStart = value;
      this.send({ type: 'configure', appendWindowStart: value });
    }

    setAppendWindowEnd(value) {
      const op = "set the 'appendWindowEnd' property on 'SourceBuffer'";
      this.checkUsable(op);
      if (Number.isNaN(value)) throw new TypeError('Failed to ' + op + ': The value provided is NaN.');
      if (value <= this.appendWindowStart) {
        throw new TypeError('Failed to ' + op + ': The value provided (' + value + ') is less than or equal to the start of the append window.');
      }
      if (value === this.appendWindowEnd) return;
      this.appendWindowEnd = value;
      this.send({ type: 'configure', appendWindowEnd: value === Infinity ? null : value });
    }

    setMode(value) {
      const op = "set the 'mode' property on 'SourceBuffer'";
      this.checkUsable(op);
      if (value === 'sequence') throw domError('NotSupportedError', 'Failed to ' + op + ": The 'sequence' mode is not supported.");
      this.mediaSource.reopenIfEnded();
    }

    get buffered() {
      if (this.removed) {
        throw domError('InvalidStateError', "Failed to read the 'buffered' property from 'SourceBuffer': This SourceBuffer has been removed from the parent media source.");
      }
      if (!this.rangesObject) this.rangesObject = internal.timeRanges(this.ranges);
      return this.rangesObject;
    }

    updateRanges(ranges, full) {
      if (ranges !== undefined) {
        this.ranges = internal.normalizeRanges(ranges);
        this.rangesObject = null;
      }
      if (typeof full === 'boolean') this.full = full;
      if (this.mediaSource.element) this.mediaSource.element.bufferedChanged();
    }

    onAppendReply(message, failed) {
      const sequence = message.sequence;
      if (sequence === this.inFlight) this.inFlight = 0;
      if (!failed) {
        if (Array.isArray(message.tracks)) this.onTracks(message.tracks);
        this.updateRanges(message.buffered, message.full);
      }
      const pending = this.pending;
      if (pending && pending.kind === 'append' && pending.sequence === sequence) {
        this.pending = null;
        this.updating = false;
        if (failed) {
          queueFire(this.surface, 'error');
          queueFire(this.surface, 'updateend');
          this.mediaSource.runEndOfStream('decode');
        } else {
          queueFire(this.surface, 'update');
          queueFire(this.surface, 'updateend');
          if (this.mediaSource.element) this.mediaSource.element.dataAppended();
        }
      }
      this.sendPendingAppend();
    }

    onRemoved(message) {
      this.updateRanges(message.buffered, message.full);
      const pending = this.pending;
      if (pending && pending.kind === 'remove' && pending.request === message.request) {
        this.pending = null;
        this.updating = false;
        queueFire(this.surface, 'update');
        queueFire(this.surface, 'updateend');
      }
    }

    onTracks(tracks) {
      this.tracks = tracks;
      if (!this.active) {
        this.active = true;
        this.mediaSource.activate();
      }
      if (this.mediaSource.element) this.mediaSource.element.tracksChanged();
    }

    release() {
      this.removed = true;
      this.updating = false;
      this.pending = null;
      const session = this.session;
      if (session) session.unregisterBuffer(this);
    }
  }

  class SourceBuffer extends RemoteEventTarget {
    constructor(key) {
      super();
      if (key !== KEY) throw new TypeError('Illegal constructor');
    }
    get mode() {
      return 'segments';
    }
    set mode(value) {
      const v = String(value);
      if (v !== 'segments' && v !== 'sequence') return;
      sourceBuffers.get(this).setMode(v);
    }
    get updating() {
      return sourceBuffers.get(this).updating;
    }
    get buffered() {
      return sourceBuffers.get(this).buffered;
    }
    get timestampOffset() {
      return sourceBuffers.get(this).timestampOffset;
    }
    set timestampOffset(value) {
      sourceBuffers.get(this).setTimestampOffset(finiteDouble(value, "Failed to set the 'timestampOffset' property on 'SourceBuffer'"));
    }
    get appendWindowStart() {
      return sourceBuffers.get(this).appendWindowStart;
    }
    set appendWindowStart(value) {
      sourceBuffers.get(this).setAppendWindowStart(finiteDouble(value, "Failed to set the 'appendWindowStart' property on 'SourceBuffer'"));
    }
    get appendWindowEnd() {
      return sourceBuffers.get(this).appendWindowEnd;
    }
    set appendWindowEnd(value) {
      sourceBuffers.get(this).setAppendWindowEnd(Number(value));
    }
    appendBuffer(data) {
      sourceBuffers.get(this).appendBuffer(data);
    }
    abort() {
      sourceBuffers.get(this).abort();
    }
    changeType(type) {
      if (arguments.length < 1) throw new TypeError("Failed to execute 'changeType' on 'SourceBuffer': 1 argument required, but only 0 present.");
      sourceBuffers.get(this).changeType(String(type));
    }
    remove(start, end) {
      const op = "Failed to execute 'remove' on 'SourceBuffer'";
      if (arguments.length < 2) throw new TypeError(op + ': 2 arguments required, but only ' + arguments.length + ' present.');
      const s = finiteDouble(start, op);
      const e = Number(end);
      if (Number.isNaN(e)) throw new TypeError(op + ': The end value provided is NaN.');
      sourceBuffers.get(this).remove(s, e);
    }
  }
  defineEventHandlers(SourceBuffer, ['updatestart', 'update', 'updateend', 'error', 'abort']);
  defineTag(SourceBuffer, 'SourceBuffer');

  // Object URLs for remote sources; anything else goes to the browser.
  const sourcesByUrl = new Map();

  function newObjectUrl() {
    const bytes = crypto.getRandomValues(new Uint8Array(16));
    bytes[6] = (bytes[6] & 0x0f) | 0x40;
    bytes[8] = (bytes[8] & 0x3f) | 0x80;
    const hex = Array.from(bytes, (b) => b.toString(16).padStart(2, '0')).join('');
    const uuid = hex.slice(0, 8) + '-' + hex.slice(8, 12) + '-' + hex.slice(12, 16) + '-' + hex.slice(16, 20) + '-' + hex.slice(20);
    return 'blob:' + location.origin + '/' + uuid;
  }

  internal.createObjectUrl = function (object) {
    const core = mediaSources.get(object);
    if (!core) return null;
    const url = newObjectUrl();
    sourcesByUrl.set(url, core);
    return url;
  };
  internal.revokeObjectUrl = (url) => sourcesByUrl.delete(String(url));
  internal.mediaSourceForUrl = (url) => sourcesByUrl.get(String(url)) || null;
  internal.mediaSourceCore = (surface) => mediaSources.get(surface) || null;

  R.MediaSource = MediaSource;
  R.SourceBuffer = SourceBuffer;
  R.SourceBufferList = SourceBufferList;
})();
