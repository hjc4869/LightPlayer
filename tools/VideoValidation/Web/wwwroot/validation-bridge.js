const boot = document.getElementById('boot');
const bootStatus = document.getElementById('boot-status');
let resultBundle = null;

function collectWebGlInventory() {
    const canvas = document.createElement('canvas');
    const gl = canvas.getContext('webgl2') ?? canvas.getContext('webgl');
    if (!gl) {
        return { 'webgl.available': 'false' };
    }

    const debug = gl.getExtension('WEBGL_debug_renderer_info');
    return {
        'webgl.available': 'true',
        'webgl.version': gl.getParameter(gl.VERSION) ?? '',
        'webgl.shading-language': gl.getParameter(gl.SHADING_LANGUAGE_VERSION) ?? '',
        'webgl.vendor': gl.getParameter(gl.VENDOR) ?? '',
        'webgl.renderer': gl.getParameter(gl.RENDERER) ?? '',
        'webgl.unmasked-vendor': debug ? gl.getParameter(debug.UNMASKED_VENDOR_WEBGL) ?? '' : '',
        'webgl.unmasked-renderer': debug ? gl.getParameter(debug.UNMASKED_RENDERER_WEBGL) ?? '' : '',
    };
}

const inventory = {
    'browser.user-agent': navigator.userAgent,
    'browser.platform': navigator.userAgentData?.platform ?? navigator.platform ?? '',
    'browser.language': navigator.language ?? '',
    'browser.hardware-concurrency': String(navigator.hardwareConcurrency ?? ''),
    'browser.device-memory-gib': String(navigator.deviceMemory ?? ''),
    'browser.cross-origin-isolated': String(globalThis.crossOriginIsolated),
    'browser.webcodecs': String(typeof globalThis.VideoDecoder === 'function'),
    'browser.webgpu': String(Boolean(navigator.gpu)),
    ...collectWebGlInventory(),
};

const state = {
    status: 'booting',
    message: 'Loading .NET runtime',
    report: null,
    inventory,
    bundleFileName: null,
};
globalThis.lightPlayerValidation = state;

export function getConfiguration() {
    const parameters = new URLSearchParams(location.search);
    return JSON.stringify({
        autoRun: parameters.get('run') === '1' || parameters.get('autorun') === '1',
        corpusBaseUri: new URL(parameters.get('corpus') ?? '/corpus/', location.href).href,
        caseIds: parameters.getAll('case'),
    });
}

export function getInventory() {
    return JSON.stringify(inventory);
}

export function setStatus(status, message) {
    state.status = status;
    state.message = message;
    if (bootStatus) {
        bootStatus.textContent = message;
    }
}

export function markAppReady() {
    boot?.classList.add('hidden');
}

export function complete(reportJson, bundle, fileName) {
    state.report = JSON.parse(reportJson);
    state.bundleFileName = fileName;
    resultBundle = new Blob([bundle.slice()], { type: 'application/zip' });
    setStatus('complete', `Validation complete: ${state.report.Passed} passed, ${state.report.Failed} failed`);
    globalThis.dispatchEvent(new CustomEvent('lightplayer-validation-complete'));
}

export function fail(message) {
    state.status = 'failed';
    state.message = message;
    if (bootStatus) {
        bootStatus.textContent = message;
    }
    boot?.classList.remove('hidden');
    globalThis.dispatchEvent(new CustomEvent('lightplayer-validation-failed'));
}

export function download() {
    if (!resultBundle || !state.bundleFileName) {
        throw new Error('Validation results are not ready.');
    }

    const url = URL.createObjectURL(resultBundle);
    const anchor = document.createElement('a');
    anchor.href = url;
    anchor.download = state.bundleFileName;
    anchor.click();
    setTimeout(() => URL.revokeObjectURL(url), 0);
}