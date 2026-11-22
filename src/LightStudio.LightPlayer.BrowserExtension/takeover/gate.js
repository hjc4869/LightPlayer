// First part of the page-world takeover bundle: reads the armed flag that the extension wrote into
// the frame's session storage. Without it the bundle stops before anything is installed.
'use strict';
(function () {
  let flag = null;
  try {
    flag = JSON.parse(window.sessionStorage.getItem('lightplayer.takeover'));
  } catch (e) {
    // Storage is unavailable in opaque origins and where the user blocks it; the frame stays native.
    flag = null;
  }
  if (flag && typeof flag === 'object' && flag.v === 1) globalThis.LightPlayerTakeover = { flag };
})();
