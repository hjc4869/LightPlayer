// Foundation of the remote media classes: the LightPlayerRemote namespace, an ordered task queue,
// event dispatch, DOM exceptions and time ranges.
'use strict';
(function () {
  if (globalThis.LightPlayerRemote) return;

  // `nativeEvents`: the classes extend the realm's EventTarget and events dispatch natively, as the
  // page-world takeover shim needs; otherwise they are plain objects with their own dispatch.
  // Only the literal flag counts, since a page element's id can stand in for an unset global.
  const config = { nativeEvents: (globalThis.LightPlayerRemoteConfig || {}).nativeEvents === true };
  const R = {};
  const internal = {};
  Object.defineProperty(R, 'internal', { value: internal });
  Object.defineProperty(globalThis, 'LightPlayerRemote', { value: R, configurable: true, writable: true });

  const KEY = Symbol('LightPlayerRemote');
  internal.KEY = KEY;
  internal.now = () => performance.now();

  function report(error) {
    if (typeof reportError === 'function') reportError(error);
    else console.error(error);
  }
  internal.report = report;

  // Tasks run one per macrotask and in order, like media element and Media Source tasks. Message
  // events are not throttled in hidden tabs, unlike timers.
  const tasks = [];
  let channel = null;
  function runTask() {
    const task = tasks.shift();
    if (!task) return;
    try {
      task();
    } catch (e) {
      report(e);
    }
  }
  function queueTask(task) {
    tasks.push(task);
    if (!channel && typeof MessageChannel === 'function') {
      channel = new MessageChannel();
      channel.port1.onmessage = runTask;
    }
    if (channel) channel.port2.postMessage(null);
    else setTimeout(runTask, 0);
  }
  internal.queueTask = queueTask;

  function domError(name, message) {
    if (name === 'QuotaExceededError' && typeof QuotaExceededError === 'function') {
      try {
        return new QuotaExceededError(message);
      } catch (e) {
        if (!(e instanceof TypeError)) throw e;
      }
    }
    return new DOMException(message, name);
  }
  internal.domError = domError;

  // Event dispatch for plain objects. Events are real Event objects whose target fields point at
  // the remote object.
  const listenerLists = new WeakMap();
  const handlerEntries = new WeakMap();
  const stoppedEvents = new WeakSet();

  function listenersOf(target, type, create) {
    let byType = listenerLists.get(target);
    if (!byType) {
      if (!create) return null;
      byType = new Map();
      listenerLists.set(target, byType);
    }
    let list = byType.get(type);
    if (!list && create) {
      list = [];
      byType.set(type, list);
    }
    return list || null;
  }

  function removeEntry(target, type, entry) {
    entry.removed = true;
    const list = listenersOf(target, type, false);
    if (!list) return;
    const i = list.indexOf(entry);
    if (i >= 0) list.splice(i, 1);
  }

  function captureFlag(options) {
    return typeof options === 'boolean' ? options : !!(options && typeof options === 'object' && options.capture);
  }

  function prepareEvent(event, target) {
    for (const key of ['target', 'currentTarget', 'srcElement']) {
      Object.defineProperty(event, key, { value: target, configurable: true });
    }
    Object.defineProperty(event, 'eventPhase', { value: 2, configurable: true });
    const stop = event.stopImmediatePropagation;
    Object.defineProperty(event, 'stopImmediatePropagation', {
      configurable: true,
      value: function stopImmediatePropagation() {
        stoppedEvents.add(event);
        return stop.call(event);
      },
    });
  }

  function dispatch(target, event) {
    prepareEvent(event, target);
    const list = listenersOf(target, event.type, false);
    if (list) {
      for (const entry of list.slice()) {
        if (entry.removed) continue;
        if (entry.once) removeEntry(target, event.type, entry);
        try {
          const callback = entry.callback;
          if (typeof callback === 'function') callback.call(target, event);
          else if (callback && typeof callback.handleEvent === 'function') callback.handleEvent(event);
        } catch (e) {
          report(e);
        }
        if (stoppedEvents.has(event)) break;
      }
    }
    return !event.defaultPrevented;
  }

  const nativeDispatch = EventTarget.prototype.dispatchEvent;
  const nativeAdd = EventTarget.prototype.addEventListener;
  const nativeRemove = EventTarget.prototype.removeEventListener;

  function fire(target, type) {
    if (config.nativeEvents) return Reflect.apply(nativeDispatch, target, [new Event(type)]);
    return dispatch(target, new Event(type));
  }
  function queueFire(target, type) {
    queueTask(() => fire(target, type));
  }
  internal.fire = fire;
  internal.queueFire = queueFire;

  class RemoteEventTarget {
    addEventListener(type, callback, options) {
      if (callback === null || callback === undefined) return;
      type = String(type);
      const capture = captureFlag(options);
      const dictionary = options && typeof options === 'object' ? options : null;
      const signal = dictionary ? dictionary.signal : undefined;
      if (signal && signal.aborted) return;
      const list = listenersOf(this, type, true);
      if (list.some((e) => e.callback === callback && e.capture === capture)) return;
      const entry = { callback, capture, once: !!(dictionary && dictionary.once), removed: false };
      list.push(entry);
      if (signal) signal.addEventListener('abort', () => removeEntry(this, type, entry), { once: true });
    }

    removeEventListener(type, callback, options) {
      const list = listenersOf(this, String(type), false);
      if (!list) return;
      const capture = captureFlag(options);
      const entry = list.find((e) => e.callback === callback && e.capture === capture);
      if (entry) removeEntry(this, String(type), entry);
    }

    dispatchEvent(event) {
      if (!event || typeof event.type !== 'string') throw new TypeError("Failed to execute 'dispatchEvent': parameter 1 is not of type 'Event'.");
      return dispatch(this, event);
    }
  }
  internal.RemoteEventTarget = config.nativeEvents ? EventTarget : RemoteEventTarget;

  // Event handler attributes keep their position among listeners, as in the DOM.
  function defineEventHandlers(cls, types) {
    for (const type of types) {
      Object.defineProperty(cls.prototype, 'on' + type, {
        configurable: true,
        enumerable: true,
        get() {
          const entry = handlerEntries.get(this)?.get(type);
          return entry ? entry.handler : null;
        },
        set(value) {
          const handler = typeof value === 'function' ? value : null;
          let byType = handlerEntries.get(this);
          if (!byType) {
            byType = new Map();
            handlerEntries.set(this, byType);
          }
          const entry = byType.get(type);
          if (entry && handler) {
            entry.handler = handler;
          } else if (entry) {
            byType.delete(type);
            if (config.nativeEvents) Reflect.apply(nativeRemove, this, [type, entry.callback]);
            else removeEntry(this, type, entry);
          } else if (handler) {
            const created = { capture: false, once: false, removed: false, handler };
            created.callback = function (event) {
              const result = created.handler.call(this, event);
              if (result === false && config.nativeEvents) event.preventDefault();
              return result;
            };
            byType.set(type, created);
            if (config.nativeEvents) Reflect.apply(nativeAdd, this, [type, created.callback]);
            else listenersOf(this, type, true).push(created);
          }
        },
      });
    }
  }
  internal.defineEventHandlers = defineEventHandlers;

  function defineConstants(cls, constants) {
    for (const [name, value] of Object.entries(constants)) {
      Object.defineProperty(cls, name, { value, enumerable: true });
      Object.defineProperty(cls.prototype, name, { value, enumerable: true });
    }
  }
  internal.defineConstants = defineConstants;

  function defineTag(cls, tag) {
    Object.defineProperty(cls.prototype, Symbol.toStringTag, { value: tag, configurable: true });
  }
  internal.defineTag = defineTag;

  // Range lists are arrays of [start, end] pairs sorted by start, never mutated once created.
  function normalizeRanges(value) {
    if (!Array.isArray(value)) return [];
    const out = [];
    for (const pair of value) {
      if (!Array.isArray(pair) || pair.length < 2) continue;
      const start = Number(pair[0]);
      const end = pair[1] === null ? Infinity : Number(pair[1]);
      if (!(end > start) || Number.isNaN(start)) continue;
      out.push([start, end]);
    }
    out.sort((a, b) => a[0] - b[0]);
    const merged = [];
    for (const r of out) {
      const last = merged[merged.length - 1];
      if (last && r[0] <= last[1]) last[1] = Math.max(last[1], r[1]);
      else merged.push(r);
    }
    return merged;
  }

  function intersectRanges(a, b) {
    const out = [];
    let i = 0;
    let j = 0;
    while (i < a.length && j < b.length) {
      const start = Math.max(a[i][0], b[j][0]);
      const end = Math.min(a[i][1], b[j][1]);
      if (start < end) out.push([start, end]);
      if (a[i][1] < b[j][1]) i++;
      else j++;
    }
    return out;
  }

  function rangeAt(ranges, time, tolerance) {
    const tol = tolerance || 0;
    for (const r of ranges) {
      if (time >= r[0] - tol && time < r[1]) return r;
    }
    return null;
  }

  function highestEnd(ranges) {
    return ranges.length ? ranges[ranges.length - 1][1] : 0;
  }

  internal.normalizeRanges = normalizeRanges;
  internal.intersectRanges = intersectRanges;
  internal.rangeAt = rangeAt;
  internal.highestEnd = highestEnd;

  const rangesOf = new WeakMap();

  function rangeIndex(ranges, index, args) {
    if (args.length < 1) throw new TypeError('1 argument required, but only 0 present.');
    const i = Number(index) >>> 0;
    if (i >= ranges.length) {
      throw domError('IndexSizeError', 'The index provided (' + i + ') is greater than or equal to the maximum bound (' + ranges.length + ').');
    }
    return i;
  }

  class TimeRanges {
    constructor(key, ranges) {
      if (key !== KEY) throw new TypeError('Illegal constructor');
      rangesOf.set(this, ranges);
    }
    get length() {
      return rangesOf.get(this).length;
    }
    start(index) {
      const ranges = rangesOf.get(this);
      return ranges[rangeIndex(ranges, index, arguments)][0];
    }
    end(index) {
      const ranges = rangesOf.get(this);
      return ranges[rangeIndex(ranges, index, arguments)][1];
    }
  }
  defineTag(TimeRanges, 'TimeRanges');
  R.TimeRanges = TimeRanges;
  internal.timeRanges = (ranges) => new TimeRanges(KEY, ranges);
  internal.emptyTimeRanges = () => new TimeRanges(KEY, []);
})();
