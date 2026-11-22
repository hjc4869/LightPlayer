// Page-world takeover shim, last part of the takeover bundle. In an armed frame, every MediaSource is
// a remote one; an element given its object URL is taken over: HTMLMediaElement accessors serve the
// remote element state for that element only, and its events come from that state. The native
// element loads an empty native MediaSource meanwhile, so it neither plays nor reports errors.
// Messages to and from the application pass through the content script over a MessagePort.
'use strict';
(function () {
  const R = globalThis.LightPlayerRemote;
  const armed = globalThis.LightPlayerTakeover;
  if (!R || !armed) return;
  const { internal } = R;

  // ---- natives, captured before page scripts run ----

  const W = window;
  const getOwn = Object.getOwnPropertyDescriptor;
  const define = Object.defineProperty;
  const apply = Reflect.apply;
  const NativeMediaSource = W.MediaSource;
  const NativeEvent = W.Event;
  const NativeMessageChannel = W.MessageChannel;
  const windowPost = W.postMessage;
  const portPost = W.MessagePort.prototype.postMessage;
  const nativeDispatch = W.EventTarget.prototype.dispatchEvent;
  const nativeAdd = W.EventTarget.prototype.addEventListener;
  const nativeRemove = W.EventTarget.prototype.removeEventListener;
  const MediaProto = W.HTMLMediaElement.prototype;
  const VideoProto = W.HTMLVideoElement.prototype;
  const SourceProto = W.HTMLSourceElement.prototype;
  const ElementProto = W.Element.prototype;
  const nativeHasAttribute = ElementProto.hasAttribute;
  const nativeGetAttribute = ElementProto.getAttribute;
  const getter = (proto, name) => (getOwn(proto, name) || {}).get;
  const localNameOf = getter(ElementProto, 'localName');
  const firstChildOf = getter(ElementProto, 'firstElementChild');
  const nextSiblingOf = getter(ElementProto, 'nextElementSibling');
  const parentOf = getter(W.Node.prototype, 'parentElement');
  const nativeSrcGet = getter(MediaProto, 'src');
  const nativeSrcObjectGet = getter(MediaProto, 'srcObject');
  const nativeCurrentSrc = getter(MediaProto, 'currentSrc');
  const nativeNetworkState = getter(MediaProto, 'networkState');
  const nativeMediaKeys = getter(MediaProto, 'mediaKeys');
  const nativeLevels = ['volume', 'muted', 'defaultPlaybackRate', 'playbackRate'].map((name) => [name, getter(MediaProto, name)]);
  const nativeSourceSrc = getter(SourceProto, 'src');
  const nativeHidden = getter(W.Document.prototype, 'hidden');
  const nativeGetElements = W.Document.prototype.getElementsByTagName;
  const MEDIA_EVENTS = R.ELEMENT_EVENTS;

  // Descriptors replaced here, restored when the frame stands down.
  const replaced = [];
  let inert = false;

  // Wrappers are proxies of the native function they replace, so name, length, descriptor and the
  // native-code source text stay those of the native member.
  function proxyOf(native, impl) {
    return new Proxy(native, {
      apply(target, self, args) {
        return impl(self, args);
      },
    });
  }

  function redefine(target, key, descriptor) {
    replaced.push([target, key, getOwn(target, key)]);
    define(target, key, descriptor);
  }

  function wrapMethod(proto, name, impl) {
    const d = getOwn(proto, name);
    if (!d || typeof d.value !== 'function') return null;
    const native = d.value;
    redefine(proto, name, {
      value: proxyOf(native, (self, args) => (inert ? apply(native, self, args) : impl(self, args, native))),
      writable: d.writable,
      enumerable: d.enumerable,
      configurable: d.configurable,
    });
    return native;
  }

  function wrapAccessor(proto, name, getImpl, setImpl) {
    const d = getOwn(proto, name);
    if (!d || !d.get) return null;
    const get = d.get;
    const set = d.set;
    redefine(proto, name, {
      get: getImpl ? proxyOf(get, (self, args) => (inert ? apply(get, self, args) : getImpl(self, get))) : get,
      set: set && setImpl ? proxyOf(set, (self, args) => (inert ? apply(set, self, args) : setImpl(self, args, set))) : set,
      enumerable: d.enumerable,
      configurable: d.configurable,
    });
    return d;
  }

  // ---- channel to the content script ----

  const channel = new NativeMessageChannel();
  const port = channel.port1;

  function post(message, transfer) {
    apply(portPost, port, [message, transfer || []]);
  }

  let capabilities = armed.flag.capabilities && typeof armed.flag.capabilities === 'object' ? armed.flag.capabilities : null;
  R.setCapabilities(capabilities);

  // ---- remote sources and their native stand-ins ----

  // Native object URL of a remote MediaSource -> { ms, url, fake, dummy, elements, sources }. `fake`
  // is the remote module's URL for the same source; `dummy` is the empty native MediaSource the
  // native element loads instead.
  const remoteByUrl = new Map();
  const remoteByDummy = new WeakMap();

  function isRemoteSource(value) {
    return value !== null && typeof value === 'object' && !!internal.mediaSourceCore(value);
  }

  function standIn(ms, createUrl) {
    const dummy = new NativeMediaSource();
    const record = { ms, url: null, fake: internal.createObjectUrl(ms), dummy, elements: new Set(), sources: new Set() };
    if (createUrl) {
      record.url = apply(createUrl, W.URL, [dummy]);
      remoteByUrl.set(record.url, record);
    }
    remoteByDummy.set(dummy, record);
    apply(nativeAdd, dummy, ['sourceopen', () => onDummyOpen(record)]);
    return record;
  }

  // The candidate the native resource selection takes: srcObject, the src attribute, or the first
  // <source> child with a src attribute.
  function selectedRecord(element) {
    const object = apply(nativeSrcObjectGet, element, []);
    if (object) return remoteByDummy.get(object) || null;
    if (apply(nativeHasAttribute, element, ['src'])) return remoteByUrl.get(apply(nativeSrcGet, element, [])) || null;
    for (let child = apply(firstChildOf, element, []); child; child = apply(nextSiblingOf, child, [])) {
      if (apply(localNameOf, child, []) === 'source' && apply(nativeHasAttribute, child, ['src'])) {
        return remoteByUrl.get(apply(nativeSourceSrc, child, [])) || null;
      }
    }
    return null;
  }

  function isMediaElement(value) {
    if (value === null || typeof value !== 'object') return false;
    try {
      apply(nativeNetworkState, value, []);
      return true;
    } catch (e) {
      if (e instanceof TypeError) return false;
      throw e;
    }
  }

  // The native element attached a stand-in: the page attached the source in a way no wrapper saw,
  // such as a <source> child. The native element already fired loadstart.
  function onDummyOpen(record) {
    if (inert) return;
    let element = null;
    const matches = (e) => isMediaElement(e) && apply(nativeCurrentSrc, e, []) === record.url;
    for (const e of record.elements) if (matches(e)) element = e;
    for (const s of record.sources) {
      const parent = apply(parentOf, s, []);
      if (!element && matches(parent)) element = parent;
    }
    if (!element) {
      for (const tag of ['video', 'audio']) {
        for (const e of apply(nativeGetElements, document, [tag])) if (!element && matches(e)) element = e;
      }
    }
    if (!element) {
      console.warn('Light Media Center: a taken-over source was attached to an element that cannot be found');
      return;
    }
    record.elements.add(element);
    const entry = entryOf(element);
    if (entry.active) {
      // The native element's events are stopped, so the remote state reloads with its own events.
      if (entry.candidate === record) return;
      entry.candidate = record;
      entry.state.load();
    } else {
      entry.candidate = record;
      activate(entry, null);
      entry.state.networkState = 2;
      if (!entry.state.tryAttach(record.fake)) entry.state.sourceFailure();
    }
    refreshSoon(entry);
  }

  // ---- taken-over elements ----

  const entries = new WeakMap();
  const sessions = new Map();
  const keyed = new WeakSet();
  let nextSid = 0;

  function entryOf(element) {
    let entry = entries.get(element);
    if (!entry) {
      entry = { element, sid: 0, session: null, state: null, active: false, candidate: null, attached: false, stopping: false, protected: false };
      entries.set(element, entry);
    }
    return entry;
  }

  function activeEntry(element) {
    if (element === null || typeof element !== 'object') return null;
    const entry = entries.get(element);
    return entry && entry.active ? entry : null;
  }

  function makeLink(sid) {
    return {
      capabilities,
      onmessage: null,
      onclose: null,
      send(message) {
        post({ k: 'send', sid, message });
      },
      append(buffer, sequence, bytes) {
        post({ k: 'append', sid, buffer, sequence, data: bytes }, [bytes]);
      },
    };
  }

  // `owner` is the session the host serves; events of an earlier session do not reach the element.
  function makeHost(entry) {
    return {
      owner: null,
      fire(type) {
        if (entry.active && entry.session === this.owner) apply(nativeDispatch, entry.element, [new NativeEvent(type)]);
      },
      resourceCandidates() {
        return entry.candidate ? { mode: 'attribute', url: entry.candidate.fake } : null;
      },
      get autoplay() {
        return apply(nativeHasAttribute, entry.element, ['autoplay']);
      },
      get loop() {
        return apply(nativeHasAttribute, entry.element, ['loop']);
      },
    };
  }

  // A new session per activation after the previous one ended; volume and rate carry over, since
  // the native element keeps them in step.
  function startSession(entry) {
    const sid = ++nextSid;
    const link = makeLink(sid);
    const host = makeHost(entry);
    const session = R.createSession(link, { host, title: '', pageUrl: '' });
    host.owner = session;
    const state = session.state;
    for (const [name, get] of nativeLevels) state[name] = apply(get, entry.element, []);
    session.onclose = () => refreshSoon(entry);
    entry.sid = sid;
    entry.session = session;
    entry.state = state;
    entry.protected = false;
    sessions.set(sid, { entry, link, session });
    post({ k: 'session', sid });
  }

  function installStopper(element) {
    if (entries.get(element).stopper) return;
    entries.get(element).stopper = true;
    for (const type of MEDIA_EVENTS) apply(nativeAdd, element, [type, stopNative, true]);
  }

  // Events of the native element do not reach the page while the element is taken over.
  function stopNative(event) {
    if (event.isTrusted && activeEntry(event.target)) event.stopImmediatePropagation();
  }

  // `networkStateBefore` is the native element's state before the load that took it over, so the
  // remote state fires abort and emptied as the native element would have.
  function activate(entry, networkStateBefore) {
    if (!entry.session || entry.session.closed) startSession(entry);
    const fresh = !entry.active;
    entry.active = true;
    installStopper(entry.element);
    if (fresh && networkStateBefore !== null && entry.state.networkState === 0) entry.state.networkState = networkStateBefore;
    if (keyed.has(entry.element) || apply(nativeMediaKeys, entry.element, [])) protect(entry);
    if (entry.candidate && entry.candidate.url) post({ k: 'element', sid: entry.sid, url: entry.candidate.url });
  }

  // The element loads a source that is not remote: the remote state detaches without events and
  // the native element is in charge again. The session stays, so a later remote source continues it.
  function deactivate(entry) {
    entry.candidate = null;
    entry.active = false;
    if (entry.state) entry.state.load();
    refreshSoon(entry);
  }

  // Runs after the native element started a load: src, srcObject, the src attribute or load().
  function sourceAssigned(element, networkStateBefore) {
    const record = selectedRecord(element);
    const entry = entries.get(element);
    if (record) {
      const e = entry || entryOf(element);
      record.elements.add(element);
      e.candidate = record;
      activate(e, networkStateBefore);
      e.state.load();
      refreshSoon(e);
    } else if (entry && entry.active) {
      deactivate(entry);
    }
  }

  function refreshSoon(entry) {
    queueMicrotask(() => queueMicrotask(() => refresh(entry)));
  }

  function refresh(entry) {
    const attached = !!(entry.active && entry.state && entry.state.mediaSource && entry.session && !entry.session.closed);
    if (attached !== entry.attached) {
      entry.attached = attached;
      if (entry.sid) post({ k: 'attached', sid: entry.sid, value: attached });
    }
    updateMask();
  }

  // Media keys on a taken-over element end its session with `encrypted` before anything else goes
  // out; the content script then returns the page to the browser's own playback.
  function protect(entry) {
    if (entry.protected || !entry.session) return;
    entry.protected = true;
    post({ k: 'protected', sid: entry.sid });
    entry.session.close('encrypted');
  }

  // ---- visibility: a page with an active session sees itself as visible ----

  let masked = false;

  function computeMask() {
    for (const { entry, session } of sessions.values()) {
      if (entry.active && entry.session === session && !session.closed && entry.state.mediaSource) return true;
    }
    return false;
  }

  function pageHidden() {
    return masked ? false : apply(nativeHidden, document, []);
  }

  function updateMask() {
    const next = computeMask();
    if (next === masked) return;
    const before = pageHidden();
    masked = next;
    if (pageHidden() !== before) apply(nativeDispatch, document, [new NativeEvent('visibilitychange', { bubbles: true })]);
  }

  // ---- installation ----

  function installGlobals() {
    const globals = [['MediaSource', R.MediaSource], ['SourceBuffer', R.SourceBuffer], ['SourceBufferList', R.SourceBufferList]];
    for (const [name, remote] of globals) {
      const d = getOwn(W, name);
      if (!d || typeof d.value !== 'function') continue;
      redefine(W, name, { value: dress(remote, d.value), writable: d.writable, enumerable: d.enumerable, configurable: d.configurable });
    }
    for (const name of ['ManagedMediaSource', 'ManagedSourceBuffer']) {
      if (getOwn(W, name)) {
        replaced.push([W, name, getOwn(W, name)]);
        delete W[name];
      }
    }
  }

  // Gives a remote class the shape of the native interface: its members become proxies of the
  // native members, with the native descriptors, and the global is a proxy of the class.
  function dress(remote, native) {
    const rp = remote.prototype;
    const np = native.prototype;
    for (const key of Reflect.ownKeys(rp)) {
      if (key === 'constructor' || key === Symbol.toStringTag) continue;
      const rd = getOwn(rp, key);
      const nd = getOwn(np, key);
      define(rp, key, nd ? shaped(rd, nd) : Object.assign(rd, { enumerable: true }));
    }
    for (const key of Reflect.ownKeys(remote)) {
      if (key === 'prototype' || key === 'name' || key === 'length') continue;
      const nd = getOwn(native, key);
      if (nd) define(remote, key, shaped(getOwn(remote, key), nd));
      else delete remote[key];
    }
    define(remote, 'length', getOwn(native, 'length'));
    const global = new Proxy(remote, {});
    define(rp, 'constructor', { value: global, writable: true, enumerable: false, configurable: true });
    return global;
  }

  function shaped(rd, nd) {
    const common = { enumerable: nd.enumerable, configurable: nd.configurable };
    if ('value' in nd) {
      const value = typeof nd.value === 'function' && typeof rd.value === 'function'
        ? proxyOf(nd.value, (self, args) => apply(rd.value, self, args))
        : rd.value;
      return Object.assign(common, { value, writable: nd.writable });
    }
    return Object.assign(common, {
      get: nd.get && rd.get ? proxyOf(nd.get, (self) => apply(rd.get, self, [])) : rd.get,
      set: nd.set && rd.set ? proxyOf(nd.set, (self, args) => apply(rd.set, self, args)) : rd.set,
    });
  }

  function installObjectUrls() {
    const URLClass = W.URL;
    const create = wrapMethod(URLClass, 'createObjectURL', (self, args, native) => {
      if (args.length && isRemoteSource(args[0])) return standIn(args[0], native).url;
      return apply(native, self, args);
    });
    wrapMethod(URLClass, 'revokeObjectURL', (self, args, native) => {
      const result = apply(native, self, args);
      const record = remoteByUrl.get(String(args[0]));
      if (record) {
        remoteByUrl.delete(record.url);
        // A source assigned just before stays resolvable for the load the assignment started.
        setTimeout(() => internal.revokeObjectUrl(record.fake), 0);
      }
      return result;
    });
    return create;
  }

  // Load triggers: the native operation runs first and starts the native load; the remote state
  // follows the source the native element selects.
  function installSourceWrappers() {
    wrapAccessor(MediaProto, 'src', null, (self, args, set) => {
      const before = isMediaElement(self) ? apply(nativeNetworkState, self, []) : 0;
      apply(set, self, args);
      sourceAssigned(self, before);
    });
    wrapAccessor(MediaProto, 'srcObject', (self, get) => {
      const value = apply(get, self, []);
      const record = value ? remoteByDummy.get(value) : null;
      return record ? record.ms : value;
    }, (self, args, set) => {
      const before = isMediaElement(self) ? apply(nativeNetworkState, self, []) : 0;
      if (isRemoteSource(args[0])) {
        const record = standIn(args[0], null);
        record.elements.add(self);
        apply(set, self, [record.dummy]);
      } else {
        apply(set, self, args);
      }
      sourceAssigned(self, before);
    });
    wrapMethod(MediaProto, 'load', (self, args, native) => {
      const before = isMediaElement(self) ? apply(nativeNetworkState, self, []) : 0;
      const result = apply(native, self, args);
      sourceAssigned(self, before);
      return result;
    });
    wrapMethod(ElementProto, 'setAttribute', (self, args, native) => {
      const name = args[0];
      if (typeof name !== 'string' || name.length !== 3 || name.toLowerCase() !== 'src') return apply(native, self, args);
      const tag = apply(localNameOf, self, []);
      if (tag === 'source') {
        const result = apply(native, self, args);
        const record = remoteByUrl.get(apply(nativeSourceSrc, self, []));
        if (record) record.sources.add(self);
        return result;
      }
      if (!isMediaElement(self)) return apply(native, self, args);
      const before = apply(nativeNetworkState, self, []);
      const result = apply(native, self, args);
      sourceAssigned(self, before);
      return result;
    });
    wrapAccessor(SourceProto, 'src', null, (self, args, set) => {
      apply(set, self, args);
      const record = remoteByUrl.get(apply(nativeSourceSrc, self, []));
      if (record) record.sources.add(self);
    });
  }

  // Element members answer from the remote state for a taken-over element and natively otherwise.
  function installElementWrappers() {
    const state = (self) => {
      const entry = activeEntry(self);
      return entry ? entry.state : null;
    };
    const read = (proto, name, value) => wrapAccessor(proto, name, (self, get) => {
      const s = state(self);
      return s ? value(s) : apply(get, self, []);
    }, null);
    read(MediaProto, 'duration', (s) => s.duration);
    read(MediaProto, 'paused', (s) => s.paused);
    read(MediaProto, 'ended', (s) => s.endedPlayback);
    read(MediaProto, 'seeking', (s) => s.seekingAny);
    read(MediaProto, 'readyState', (s) => s.readyState);
    read(MediaProto, 'networkState', (s) => s.networkState);
    read(MediaProto, 'buffered', (s) => s.buffered);
    read(MediaProto, 'seekable', (s) => s.seekable);
    read(MediaProto, 'played', (s) => s.played);
    read(MediaProto, 'error', (s) => s.error);
    read(VideoProto, 'videoWidth', (s) => s.videoWidth);
    read(VideoProto, 'videoHeight', (s) => s.videoHeight);
    read(VideoProto, 'webkitDecodedFrameCount', (s) => Math.max(0, (s.st.totalFrames | 0) - (s.st.droppedFrames | 0)));
    read(VideoProto, 'webkitDroppedFrameCount', (s) => s.st.droppedFrames | 0);

    wrapAccessor(MediaProto, 'currentTime', (self, get) => {
      const s = state(self);
      return s ? s.currentTime : apply(get, self, []);
    }, (self, args, set) => {
      const s = state(self);
      if (s) s.currentTime = args[0];
      else apply(set, self, args);
    });
    // The native element follows these values silently (its events are stopped), so they hold
    // when the element is native again.
    const both = (name, setRemote) => wrapAccessor(MediaProto, name, (self, get) => {
      const s = state(self);
      return s ? s[name] : apply(get, self, []);
    }, (self, args, set) => {
      const s = state(self);
      if (s) setRemote(s, args[0]);
      apply(set, self, args);
    });
    both('playbackRate', (s, v) => s.setPlaybackRate(v));
    both('defaultPlaybackRate', (s, v) => s.setDefaultPlaybackRate(v));
    both('volume', (s, v) => s.setVolume(v));
    both('muted', (s, v) => s.setMuted(v));

    const method = (proto, name, call) => wrapMethod(proto, name, (self, args, native) => {
      const s = state(self);
      return s ? call(s, args) : apply(native, self, args);
    });
    method(MediaProto, 'play', (s) => s.play());
    method(MediaProto, 'pause', (s) => s.pause());
    method(MediaProto, 'fastSeek', (s, args) => {
      s.currentTime = args[0];
    });
    method(VideoProto, 'getVideoPlaybackQuality', (s) => s.getVideoPlaybackQuality());
    method(VideoProto, 'requestVideoFrameCallback', (s, args) => s.requestVideoFrameCallback(args[0]));
    method(VideoProto, 'cancelVideoFrameCallback', (s, args) => s.cancelVideoFrameCallback(args[0]));

    // Key systems stay native: the call goes to the browser as made, and keys on a taken-over
    // element end its session.
    wrapMethod(MediaProto, 'setMediaKeys', (self, args, native) => {
      const result = apply(native, self, args);
      if (args[0] !== null && args[0] !== undefined && isMediaElement(self)) {
        keyed.add(self);
        const entry = activeEntry(self);
        if (entry) protect(entry);
      }
      return result;
    });
  }

  // ---- type support from the capability list ----

  const MSE_TYPES = new Set(['video/mp4', 'audio/mp4', 'video/webm', 'audio/webm']);
  const MANIFEST_TYPES = new Set(['application/vnd.apple.mpegurl', 'application/x-mpegurl', 'audio/mpegurl',
    'audio/x-mpegurl', 'application/dash+xml']);

  function essenceOf(type) {
    const parsed = R.typeSupport.parseMimeType(String(type));
    return parsed ? parsed.essence : '';
  }

  function installTypeSupport() {
    // Types the application could take through Media Source, and manifests, answer from the list;
    // other progressive types stay native.
    wrapMethod(MediaProto, 'canPlayType', (self, args, native) => {
      if (args.length < 1) return apply(native, self, args);
      const essence = essenceOf(args[0]);
      if (!MSE_TYPES.has(essence) && !MANIFEST_TYPES.has(essence)) return apply(native, self, args);
      apply(native, self, args);
      return internal.canPlayType(String(args[0]));
    });
    const Capabilities = W.MediaCapabilities;
    if (Capabilities) {
      wrapMethod(Capabilities.prototype, 'decodingInfo', (self, args, native) => {
        const config = args[0];
        if (!config || typeof config !== 'object' || config.keySystemConfiguration || config.type === 'webrtc') return apply(native, self, args);
        if (config.type === 'file') {
          const types = [config.video, config.audio].filter(Boolean).map((c) => essenceOf(c && c.contentType));
          if (types.some((t) => !MSE_TYPES.has(t))) return apply(native, self, args);
        }
        return R.typeSupport.decodingInfo(config);
      });
    }
    // Dynamic-range and gamut queries answer from the list: the MediaQueryList is the native one
    // for the query as given, and `matches` comes from the query with those features resolved.
    const resolved = new WeakMap();
    const matchMediaNative = wrapMethod(W, 'matchMedia', (self, args, native) => {
      const list = apply(native, self, args);
      const query = args.length ? R.typeSupport.resolveMediaQuery(String(args[0])) : null;
      if (query !== null) resolved.set(list, apply(native, self, [query]));
      return list;
    });
    if (matchMediaNative && W.MediaQueryList) {
      wrapAccessor(W.MediaQueryList.prototype, 'matches', (self, get) => {
        const other = self !== null && typeof self === 'object' ? resolved.get(self) : undefined;
        return apply(get, other || self, []);
      }, null);
    }
  }

  function installVisibility() {
    const DocProto = W.Document.prototype;
    for (const name of ['hidden', 'webkitHidden']) {
      wrapAccessor(DocProto, name, (self, get) => (masked && self === document ? false : apply(get, self, [])), null);
    }
    for (const name of ['visibilityState', 'webkitVisibilityState']) {
      wrapAccessor(DocProto, name, (self, get) => (masked && self === document ? 'visible' : apply(get, self, [])), null);
    }
  }

  // Native media events of taken-over elements, and visibility changes while masked, stop at the
  // window before page listeners see them; elements outside the document stop them themselves.
  function onWindowEvent(event) {
    if (inert || !event.isTrusted) return;
    if (event.type === 'visibilitychange' ? masked : activeEntry(event.target)) event.stopImmediatePropagation();
  }

  function installListeners() {
    for (const type of MEDIA_EVENTS.concat(['visibilitychange'])) apply(nativeAdd, W, [type, onWindowEvent, true]);
  }

  // ---- messages from the content script ----

  function onPortMessage(event) {
    const m = event.data;
    if (!m || typeof m !== 'object') return;
    const s = typeof m.sid === 'number' ? sessions.get(m.sid) : null;
    switch (m.k) {
      case 'config':
        if (m.capabilities && typeof m.capabilities === 'object') {
          capabilities = m.capabilities;
          R.setCapabilities(capabilities);
          for (const { link } of sessions.values()) link.capabilities = capabilities;
        }
        break;
      case 'msg':
        if (s && typeof s.link.onmessage === 'function') s.link.onmessage(m.message);
        break;
      case 'closed': {
        // Another session took the link, or the application stopped: the element pauses. Any
        // other end is a failure of the link.
        const reason = String(m.reason || 'disconnected');
        if (!s) break;
        if (reason === 'stopped' || reason === 'replaced' || reason === 'sourceChanged') {
          if (typeof s.link.onmessage === 'function') s.link.onmessage({ type: 'close', reason: reason === 'stopped' ? 'stopped' : 'replaced' });
        } else if (typeof s.link.onclose === 'function') {
          s.link.onclose(reason);
        }
        break;
      }
      case 'standDown':
        standDown();
        break;
    }
  }

  // The tab turned out not to be armed and nothing was taken over: the native members return.
  function standDown() {
    if (inert) return;
    for (const { entry } of sessions.values()) if (entry.active) return;
    inert = true;
    for (const [target, key, d] of replaced.reverse()) {
      if (d) define(target, key, d);
      else delete target[key];
    }
    for (const type of MEDIA_EVENTS.concat(['visibilitychange'])) apply(nativeRemove, W, [type, onWindowEvent, true]);
    masked = false;
  }

  installGlobals();
  installObjectUrls();
  installSourceWrappers();
  installElementWrappers();
  installTypeSupport();
  installVisibility();
  installListeners();
  port.onmessage = onPortMessage;
  apply(windowPost, W, [{ lightplayerTakeover: 'hello', v: 1 }, '*', [channel.port2]]);
})();
