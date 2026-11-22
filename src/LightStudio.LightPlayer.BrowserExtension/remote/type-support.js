// Type support: MediaSource.isTypeSupported, canPlayType, MediaCapabilities.decodingInfo and the
// media features for dynamic range and gamut, all answered from the capability list of the
// application's `ready` message.
'use strict';
(function () {
  const R = globalThis.LightPlayerRemote;
  if (!R || R.typeSupport) return;
  const { internal } = R;

  let capabilities = null;

  R.setCapabilities = function setCapabilities(ready) {
    capabilities = ready && typeof ready === 'object' ? ready : null;
  };
  Object.defineProperty(R, 'capabilities', { get: () => capabilities, enumerable: true });

  const CONTAINERS = {
    'video/mp4': { container: 'mp4', audioOnly: false },
    'audio/mp4': { container: 'mp4', audioOnly: true },
    'video/webm': { container: 'webm', audioOnly: false },
    'audio/webm': { container: 'webm', audioOnly: true },
  };

  const CONTAINER_CODECS = {
    mp4: new Set(['avc1', 'hvc1', 'av01', 'vp09', 'mp4a', 'opus', 'flac', 'ac-3', 'ec-3']),
    webm: new Set(['vp08', 'vp09', 'av01', 'opus', 'vorbis']),
  };

  const MANIFEST_TYPES = new Set(['application/vnd.apple.mpegurl', 'application/x-mpegurl', 'audio/mpegurl',
    'audio/x-mpegurl', 'application/dash+xml']);

  // Parses "type/subtype; name=value; codecs="a, b"" into lower-case essence, parameters and codecs.
  function parseMimeType(text) {
    if (typeof text !== 'string') return null;
    const parts = [];
    let current = '';
    let quoted = false;
    for (const ch of text) {
      if (ch === '"') quoted = !quoted;
      if (ch === ';' && !quoted) {
        parts.push(current);
        current = '';
      } else {
        current += ch;
      }
    }
    parts.push(current);
    const essence = parts[0].trim().toLowerCase();
    if (!/^[a-z0-9!#$&^_.+-]+\/[a-z0-9!#$&^_.+-]+$/.test(essence)) return null;
    const params = new Map();
    for (const part of parts.slice(1)) {
      const eq = part.indexOf('=');
      if (eq <= 0) continue;
      const name = part.slice(0, eq).trim().toLowerCase();
      let value = part.slice(eq + 1).trim();
      if (value.startsWith('"') && value.endsWith('"') && value.length >= 2) value = value.slice(1, -1);
      if (!params.has(name)) params.set(name, value);
    }
    const codecs = params.has('codecs') ? params.get('codecs').split(',').map((c) => c.trim()).filter(Boolean) : null;
    return { essence, params, codecs };
  }

  // Code points of ISO/IEC 23091-2 that name color primaries, a transfer or a matrix.
  const COLOR_PRIMARIES = new Set([1, 2, 4, 5, 6, 7, 8, 9, 10, 11, 12, 22]);
  const TRANSFERS = new Set([1, 2, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18]);
  const MATRICES = new Set([0, 1, 2, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14]);
  const VP9_LEVELS = new Set([10, 11, 20, 21, 30, 31, 40, 41, 50, 51, 52, 60, 61, 62]);
  const BIT_DEPTHS = new Set([8, 10, 12]);
  const DOLBY_HEVC_PROFILES = new Set([4, 5, 7, 8]);

  const twoDigits = (field) => /^\d\d$/.test(field);

  function colorValid(primaries, transfer, matrix) {
    return (primaries === undefined || (twoDigits(primaries) && COLOR_PRIMARIES.has(Number(primaries))))
      && (transfer === undefined || (twoDigits(transfer) && TRANSFERS.has(Number(transfer))))
      && (matrix === undefined || (twoDigits(matrix) && MATRICES.has(Number(matrix))));
  }

  // vp09.profile.level.depth[.chroma.primaries.transfer.matrix.range], two digits each.
  function describeVp9(v) {
    if (v.length < 3 || v.length > 8 || !v.every(twoDigits)) return null;
    const depth = Number(v[2]);
    if (Number(v[0]) > 3 || !VP9_LEVELS.has(Number(v[1])) || !BIT_DEPTHS.has(depth)) return null;
    if ((v.length > 3 && Number(v[3]) > 3) || !colorValid(v[4], v[5], v[6]) || (v.length > 7 && Number(v[7]) > 1)) return null;
    return { family: 'vp09', kind: 'video', bitDepth: depth };
  }

  // av01.profile.levelTier.depth[.monochrome.chroma.primaries.transfer.matrix.range]
  function describeAv1(v) {
    if (v.length < 3 || v.length > 9 || !/^[0-2]$/.test(v[0]) || !/^\d\d[mh]$/.test(v[1]) || !twoDigits(v[2])) return null;
    const level = parseInt(v[1], 10);
    const depth = Number(v[2]);
    if ((level > 23 && level !== 31) || !BIT_DEPTHS.has(depth)) return null;
    if ((v.length > 3 && !/^[01]$/.test(v[3])) || (v.length > 4 && !/^[01][01][0-3]$/.test(v[4]))) return null;
    if (!colorValid(v[5], v[6], v[7]) || (v.length > 8 && !/^[01]$/.test(v[8]))) return null;
    return { family: 'av01', kind: 'video', bitDepth: depth };
  }

  // Maps a codec string to its family in the capability list, its kind and, where the string
  // carries it, the bit depth. A string of a family that is not well-formed has no description:
  // players probe malformed strings to learn whether an answer can be trusted.
  function describeCodec(codec) {
    const lower = codec.toLowerCase();
    const fields = lower.split('.');
    const head = fields[0];
    switch (head) {
      case 'avc1':
      case 'avc3': {
        let profile = NaN;
        if (fields.length === 2 && /^[0-9a-f]{6}$/.test(fields[1])) profile = parseInt(fields[1].slice(0, 2), 16);
        else if (fields.length >= 2) profile = parseInt(fields[1], 10);
        const bitDepth = profile === 110 || profile === 122 ? 10 : profile === 244 ? 14 : 8;
        return { family: 'avc1', kind: 'video', bitDepth };
      }
      case 'hvc1':
      case 'hev1': {
        const profile = fields.length >= 2 ? parseInt(fields[1].replace(/^[abc]/, ''), 10) : 1;
        const bitDepth = profile === 1 || profile === 3 ? 8 : profile === 2 ? 10 : null;
        return bitDepth === null ? null : { family: 'hvc1', kind: 'video', bitDepth };
      }
      case 'dvh1':
      case 'dvhe': {
        // Dolby Vision on HEVC, dvh1.profile.level: the HEVC layer with its metadata applied.
        if (fields.length !== 3 || !twoDigits(fields[1]) || !twoDigits(fields[2]) || !DOLBY_HEVC_PROFILES.has(Number(fields[1]))) return null;
        return { family: 'hvc1', kind: 'video', bitDepth: 10, hdr: true };
      }
      case 'av01':
        return describeAv1(fields.slice(1));
      case 'vp09':
        return describeVp9(fields.slice(1));
      case 'vp9':
        return { family: 'vp09', kind: 'video', bitDepth: fields[1] === '2' || fields[1] === '3' ? 10 : 8 };
      case 'vp8':
      case 'vp08':
        return { family: 'vp08', kind: 'video', bitDepth: 8 };
      case 'mp4a': {
        const oti = fields[1] || '40';
        if (oti === '40' || oti === '66' || oti === '67' || oti === '68') return { family: 'mp4a', kind: 'audio' };
        if (oti === 'a5') return { family: 'ac-3', kind: 'audio' };
        if (oti === 'a6') return { family: 'ec-3', kind: 'audio' };
        return null;
      }
      case 'ac-3':
      case 'ec-3':
      case 'opus':
      case 'flac':
      case 'vorbis':
        return fields.length === 1 ? { family: head, kind: 'audio' } : null;
      default:
        return null;
    }
  }

  function videoCapability(family) {
    const list = capabilities && Array.isArray(capabilities.video) ? capabilities.video : [];
    return list.find((v) => v && v.codec === family) || null;
  }

  function audioCapability(family) {
    const list = capabilities && Array.isArray(capabilities.audio) ? capabilities.audio : [];
    return list.find((a) => a && a.codec === family) || null;
  }

  function playsHdr() {
    return !!capabilities && capabilities.hdr === true;
  }

  function containerSupported(container) {
    return !!capabilities && Array.isArray(capabilities.containers) && capabilities.containers.includes(container);
  }

  function sizeFits(cap, width, height) {
    if (!(width > 0) || !(height > 0)) return true;
    const maxW = Number(cap.maxWidth) || Infinity;
    const maxH = Number(cap.maxHeight) || Infinity;
    return (width <= maxW && height <= maxH) || (height <= maxW && width <= maxH);
  }

  // Checks one codec of a type: its family must be in the container and in the capability list,
  // with a supported bit depth for video.
  function codecSupported(container, audioOnly, codec, width, height) {
    const info = describeCodec(codec);
    if (!info || !CONTAINER_CODECS[container].has(info.family)) return null;
    if (info.kind === 'video') {
      if (audioOnly || (info.hdr && !playsHdr())) return null;
      const cap = videoCapability(info.family);
      if (!cap) return null;
      const depths = Array.isArray(cap.bitDepths) && cap.bitDepths.length ? cap.bitDepths : [8];
      if (!depths.includes(info.bitDepth)) return null;
      if (!sizeFits(cap, width, height)) return null;
      return { info, cap };
    }
    const cap = audioCapability(info.family);
    return cap ? { info, cap } : null;
  }

  function typeSupported(parsed) {
    const entry = CONTAINERS[parsed.essence];
    if (!entry || !containerSupported(entry.container)) return false;
    if (!parsed.codecs) return true;
    const width = Number(parsed.params.get('width'));
    const height = Number(parsed.params.get('height'));
    return parsed.codecs.every((c) => !!codecSupported(entry.container, entry.audioOnly, c, width, height));
  }

  function isTypeSupported(type) {
    const parsed = parseMimeType(String(type));
    return !!parsed && typeSupported(parsed);
  }

  function canPlayType(type) {
    const parsed = parseMimeType(String(type));
    if (!parsed || MANIFEST_TYPES.has(parsed.essence)) return '';
    const entry = CONTAINERS[parsed.essence];
    if (!entry || !containerSupported(entry.container)) return '';
    if (!parsed.codecs) return 'maybe';
    return typeSupported(parsed) ? 'probably' : '';
  }

  const HDR_TRANSFERS = new Set(['pq', 'hlg']);
  const HDR_METADATA = new Set(['smpteSt2086', 'smpteSt2094-10', 'smpteSt2094-40']);

  // Answers one audio or video configuration; null means unsupported.
  function configurationSupport(config, kind) {
    if (!config || typeof config !== 'object' || typeof config.contentType !== 'string') {
      throw new TypeError('The ' + kind + ' configuration has no valid contentType.');
    }
    const parsed = parseMimeType(config.contentType);
    if (!parsed) throw new TypeError("The provided contentType '" + config.contentType + "' is not a valid MIME type.");
    if (parsed.codecs && parsed.codecs.length > 1) {
      throw new TypeError("The provided contentType '" + config.contentType + "' contains more than one codec.");
    }
    const entry = CONTAINERS[parsed.essence];
    if (!entry || !containerSupported(entry.container) || !parsed.codecs) return null;
    const width = kind === 'video' ? Number(config.width) : NaN;
    const height = kind === 'video' ? Number(config.height) : NaN;
    const found = codecSupported(entry.container, entry.audioOnly, parsed.codecs[0], width, height);
    if (!found || found.info.kind !== kind) return null;
    if (kind === 'video') {
      const transfer = config.transferFunction;
      const metadata = config.hdrMetadataType;
      if ((transfer && HDR_TRANSFERS.has(transfer)) || metadata) {
        if (!playsHdr() || found.info.bitDepth < 10) return null;
        if (metadata && !HDR_METADATA.has(metadata)) return null;
      }
      return { smooth: true, powerEfficient: found.cap.hardware === true };
    }
    return { smooth: true, powerEfficient: true };
  }

  function decodingInfo(configuration) {
    return new Promise((resolve) => {
      if (!configuration || typeof configuration !== 'object') {
        throw new TypeError("Failed to execute 'decodingInfo': parameter 1 is not an object.");
      }
      if (typeof configuration.type !== 'string') {
        throw new TypeError("Failed to execute 'decodingInfo': required member type is undefined.");
      }
      if (!configuration.audio && !configuration.video) {
        throw new TypeError("Failed to execute 'decodingInfo': The configuration dictionary has neither 'video' nor 'audio' specified.");
      }
      const video = configuration.video ? configurationSupport(configuration.video, 'video') : { smooth: true, powerEfficient: true };
      const audio = configuration.audio ? configurationSupport(configuration.audio, 'audio') : { smooth: true, powerEfficient: true };
      const supported = !!video && !!audio && configuration.type !== 'webrtc' && !configuration.keySystemConfiguration;
      resolve({
        supported,
        smooth: supported && video.smooth && audio.smooth,
        powerEfficient: supported && video.powerEfficient && audio.powerEfficient,
        keySystemAccess: null,
        configuration,
      });
    });
  }

  const HDR_FEATURE = /\(\s*(?:(?:video-)?dynamic-range\s*:\s*high|(?:video-)?color-gamut\s*:\s*(?:p3|rec2020))\s*\)/gi;

  // A media query with its high dynamic range and wide gamut features resolved from the capability
  // list, or null when it has none. What the result matches is the answer for the query as given.
  function resolveMediaQuery(query) {
    HDR_FEATURE.lastIndex = 0;
    if (!HDR_FEATURE.test(query)) return null;
    return query.replace(HDR_FEATURE, playsHdr() ? '(min-width: 0px)' : '(max-width: -1px)');
  }

  R.typeSupport = { isTypeSupported, canPlayType, decodingInfo, parseMimeType, describeCodec, resolveMediaQuery };
  internal.isTypeSupported = isTypeSupported;
  internal.canPlayType = canPlayType;
})();
