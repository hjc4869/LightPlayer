// Realm preparation: makes this realm's media globals the remote classes and answers its type
// support queries from the capability list. Runs before any player library code.
'use strict';
(function () {
  const R = globalThis.LightPlayerRemote;
  if (!R || R.prepareRealm) return;
  const { internal } = R;

  let prepared = false;

  // A content script global and its window differ in Firefox; both are prepared.
  function scopes() {
    const list = [globalThis];
    if (typeof window !== 'undefined' && window !== globalThis) list.push(window);
    return list;
  }

  function define(target, name, value) {
    Object.defineProperty(target, name, { value, writable: true, configurable: true, enumerable: false });
  }

  function uniqueTargets(list, pick) {
    const out = [];
    for (const scope of list) {
      const target = pick(scope);
      if (target && !out.includes(target)) out.push(target);
    }
    return out;
  }

  // A Firefox content script sees `window` through an Xray wrapper that hides these functions of
  // the JavaScript global; libraries that call them through `window` find them there again.
  const SCRIPT_FUNCTIONS = ['isNaN', 'isFinite', 'parseInt', 'parseFloat', 'escape', 'unescape', 'decodeURI', 'encodeURI',
    'decodeURIComponent', 'encodeURIComponent'];

  function exposeScriptFunctions(scope) {
    if (scope === globalThis) return;
    for (const name of SCRIPT_FUNCTIONS) {
      if (scope[name] === undefined && typeof globalThis[name] === 'function') define(scope, name, globalThis[name]);
    }
  }

  const REMOVED_GLOBALS = {
    ManagedMediaSource: () => R.MediaSource,
    ManagedSourceBuffer: () => R.SourceBuffer,
    WebKitMediaSource: () => R.MediaSource,
    WebKitSourceBuffer: () => R.SourceBuffer,
    WebKitSourceBufferList: () => R.SourceBufferList,
  };

  function installGlobals(scope) {
    define(scope, 'MediaSource', R.MediaSource);
    define(scope, 'SourceBuffer', R.SourceBuffer);
    define(scope, 'SourceBufferList', R.SourceBufferList);
    for (const [name, replacement] of Object.entries(REMOVED_GLOBALS)) {
      if (!(name in scope)) continue;
      try {
        delete scope[name];
      } catch (e) {
        if (!(e instanceof TypeError)) throw e;
      }
      // A variant that cannot be removed answers with the remote class.
      if (name in scope) define(scope, name, replacement());
    }
  }

  function patchObjectUrls(list) {
    for (const URLClass of uniqueTargets(list, (s) => s.URL)) {
      const create = URLClass.createObjectURL;
      const revoke = URLClass.revokeObjectURL;
      const methods = {
        createObjectURL(object) {
          const url = internal.createObjectUrl(object);
          return url !== null ? url : Reflect.apply(create, URLClass, arguments);
        },
        revokeObjectURL(url) {
          if (!internal.revokeObjectUrl(url)) Reflect.apply(revoke, URLClass, arguments);
        },
      };
      define(URLClass, 'createObjectURL', methods.createObjectURL);
      define(URLClass, 'revokeObjectURL', methods.revokeObjectURL);
    }
  }

  // ResizeObserver accepts the remote element and reports the application's view size for it.
  const observations = new Set();

  function deliverResize(observation) {
    if (!observations.has(observation) || typeof observation.callback !== 'function') return;
    const state = internal.elementState(observation.target);
    const view = state && state.session ? state.session.view : null;
    if (!view || (view.width === observation.width && view.height === observation.height)) return;
    observation.width = view.width;
    observation.height = view.height;
    const size = [{ inlineSize: view.width, blockSize: view.height }];
    const device = [{ inlineSize: Math.round(view.width * view.devicePixelRatio), blockSize: Math.round(view.height * view.devicePixelRatio) }];
    const entry = {
      target: observation.target,
      contentRect: new DOMRectReadOnly(0, 0, view.width, view.height),
      borderBoxSize: size,
      contentBoxSize: size,
      devicePixelContentBoxSize: device,
    };
    try {
      observation.callback.call(observation.observer, [entry], observation.observer);
    } catch (e) {
      internal.report(e);
    }
  }

  internal.notifyResize = function (element) {
    for (const observation of observations) {
      if (observation.target === element) internal.queueTask(() => deliverResize(observation));
    }
  };

  function patchResizeObserver(list) {
    const callbacks = new WeakMap();
    const patched = new Set();
    for (const scope of list) {
      const Native = scope.ResizeObserver;
      if (typeof Native !== 'function' || patched.has(Native)) continue;
      const proto = Native.prototype;
      const observe = proto.observe;
      const unobserve = proto.unobserve;
      const disconnect = proto.disconnect;
      const Wrapped = new Proxy(Native, {
        construct(target, args, newTarget) {
          const observer = Reflect.construct(target, args, newTarget === Wrapped ? target : newTarget);
          callbacks.set(observer, args[0]);
          return observer;
        },
      });
      patched.add(Native);
      patched.add(Wrapped);
      const methods = {
        observe(target) {
          if (!internal.isRemoteElement(target)) return Reflect.apply(observe, this, arguments);
          for (const o of observations) if (o.observer === this && o.target === target) return undefined;
          const observation = { observer: this, target, callback: callbacks.get(this), width: -1, height: -1 };
          observations.add(observation);
          internal.queueTask(() => deliverResize(observation));
          return undefined;
        },
        unobserve(target) {
          if (!internal.isRemoteElement(target)) return Reflect.apply(unobserve, this, arguments);
          for (const o of observations) if (o.observer === this && o.target === target) observations.delete(o);
          return undefined;
        },
        disconnect() {
          for (const o of observations) if (o.observer === this) observations.delete(o);
          return Reflect.apply(disconnect, this, arguments);
        },
      };
      define(proto, 'observe', methods.observe);
      define(proto, 'unobserve', methods.unobserve);
      define(proto, 'disconnect', methods.disconnect);
      define(scope, 'ResizeObserver', Wrapped);
    }
  }

  function patchTypeSupport(list) {
    const methods = {
      canPlayType(type) {
        if (arguments.length < 1) throw new TypeError("Failed to execute 'canPlayType' on 'HTMLMediaElement': 1 argument required, but only 0 present.");
        return internal.canPlayType(String(type));
      },
      decodingInfo(configuration) {
        return R.typeSupport.decodingInfo(configuration);
      },
    };
    for (const proto of uniqueTargets(list, (s) => s.HTMLMediaElement && s.HTMLMediaElement.prototype)) {
      define(proto, 'canPlayType', methods.canPlayType);
    }
    for (const proto of uniqueTargets(list, (s) => s.MediaCapabilities && s.MediaCapabilities.prototype)) {
      define(proto, 'decodingInfo', methods.decodingInfo);
    }
  }

  // Timers that also run when a link message arrives. Timers of a hidden tab are throttled, while
  // message events are not; due timers run from whichever comes first.
  function installLinkTimers(list) {
    const native = {
      setTimeout: globalThis.setTimeout,
      clearTimeout: globalThis.clearTimeout,
      setInterval: globalThis.setInterval,
      clearInterval: globalThis.clearInterval,
    };
    const timers = new Map();
    let nextId = 0x40000000;

    function arm(timer) {
      timer.handle = Reflect.apply(native.setTimeout, undefined, [() => run(timer), Math.max(0, timer.due - internal.now())]);
    }

    function run(timer) {
      if (timers.get(timer.id) !== timer) return;
      Reflect.apply(native.clearTimeout, undefined, [timer.handle]);
      if (timer.repeat) {
        timer.due = internal.now() + timer.delay;
        arm(timer);
      } else {
        timers.delete(timer.id);
      }
      try {
        Reflect.apply(timer.callback, undefined, timer.args);
      } catch (e) {
        internal.report(e);
      }
    }

    function add(callback, delay, args, repeat) {
      const d = Math.max(0, Number(delay) || 0);
      const timer = { id: nextId++, callback, args, delay: d, repeat, due: internal.now() + d, handle: 0 };
      timers.set(timer.id, timer);
      arm(timer);
      return timer.id;
    }

    function clear(id, nativeClear) {
      const timer = timers.get(id);
      if (!timer) {
        Reflect.apply(nativeClear, undefined, [id]);
        return;
      }
      Reflect.apply(native.clearTimeout, undefined, [timer.handle]);
      timers.delete(id);
    }

    const methods = {
      setTimeout(callback, delay, ...args) {
        if (typeof callback !== 'function') return Reflect.apply(native.setTimeout, undefined, arguments);
        return add(callback, delay, args, false);
      },
      setInterval(callback, delay, ...args) {
        if (typeof callback !== 'function') return Reflect.apply(native.setInterval, undefined, arguments);
        return add(callback, delay, args, true);
      },
      clearTimeout(id) {
        clear(id, native.clearTimeout);
      },
      clearInterval(id) {
        clear(id, native.clearInterval);
      },
    };
    for (const scope of list) {
      for (const name of Object.keys(methods)) define(scope, name, methods[name]);
    }

    internal.pumpTimers = function () {
      const t = internal.now();
      const due = [];
      for (const timer of timers.values()) if (timer.due <= t) due.push(timer);
      due.sort((a, b) => a.due - b.due || a.id - b.id);
      for (const timer of due) run(timer);
    };
  }

  // Runs the timers that are due; a link calls it on messages that do not reach a session.
  R.runDueTimers = function runDueTimers() {
    if (internal.pumpTimers) internal.pumpTimers();
  };

  R.prepareRealm = function prepareRealm(options) {
    const o = options || {};
    if (o.capabilities) R.setCapabilities(o.capabilities);
    if (prepared) return;
    prepared = true;
    const list = scopes();
    for (const scope of list) {
      exposeScriptFunctions(scope);
      installGlobals(scope);
    }
    patchObjectUrls(list);
    patchResizeObserver(list);
    patchTypeSupport(list);
    if (o.linkTimers !== false) installLinkTimers(list);
  };
})();
