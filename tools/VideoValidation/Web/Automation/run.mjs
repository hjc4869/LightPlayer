import { createReadStream } from 'node:fs';
import { access, mkdir, readFile, readdir, stat, writeFile } from 'node:fs/promises';
import http from 'node:http';
import path from 'node:path';
import process from 'node:process';
import { fileURLToPath } from 'node:url';

const scriptDirectory = path.dirname(fileURLToPath(import.meta.url));
const repositoryRoot = path.resolve(scriptDirectory, '../../../..');

function parseArguments(argumentsList) {
    const options = {
        app: path.join(repositoryRoot, 'artifacts/video-validation-web'),
        corpus: path.join(repositoryRoot, 'artifacts/video-validation'),
        output: path.join(repositoryRoot, 'artifacts/video-validation-web-results'),
        browser: 'chromium',
        headed: false,
        serveOnly: false,
        timeout: 10 * 60 * 1000,
        cases: [],
    };

    for (let index = 0; index < argumentsList.length; index++) {
        const argument = argumentsList[index];
        switch (argument) {
            case '--app':
            case '--corpus':
            case '--output':
                options[argument.slice(2)] = path.resolve(requireValue(argumentsList, ++index, argument));
                break;
            case '--browser':
                options.browser = requireValue(argumentsList, ++index, argument);
                break;
            case '--timeout':
                options.timeout = Number(requireValue(argumentsList, ++index, argument)) * 1000;
                break;
            case '--case':
                options.cases.push(requireValue(argumentsList, ++index, argument));
                break;
            case '--headed':
                options.headed = true;
                break;
            case '--serve':
                options.serveOnly = true;
                break;
            default:
                throw new Error(`Unknown option '${argument}'.`);
        }
    }

    if (!['chromium', 'firefox', 'webkit'].includes(options.browser)) {
        throw new Error("--browser accepts 'chromium', 'firefox', or 'webkit'.");
    }
    if (!Number.isFinite(options.timeout) || options.timeout <= 0) {
        throw new Error('--timeout must be a positive number of seconds.');
    }
    return options;
}

function requireValue(argumentsList, index, option) {
    const value = argumentsList[index];
    if (!value) {
        throw new Error(`${option} requires a value.`);
    }
    return value;
}

const contentTypes = new Map([
    ['.css', 'text/css; charset=utf-8'],
    ['.dat', 'application/octet-stream'],
    ['.dll', 'application/octet-stream'],
    ['.html', 'text/html; charset=utf-8'],
    ['.js', 'text/javascript; charset=utf-8'],
    ['.json', 'application/json; charset=utf-8'],
    ['.mjs', 'text/javascript; charset=utf-8'],
    ['.mkv', 'video/x-matroska'],
    ['.png', 'image/png'],
    ['.raw', 'application/octet-stream'],
    ['.wasm', 'application/wasm'],
]);

async function findApplicationRoot(candidate) {
    const manifestName = (await readdir(candidate)).find(name => name.endsWith('.staticwebassets.runtime.json'));
    if (manifestName) {
        return {
            root: path.join(candidate, 'wwwroot'),
            manifest: JSON.parse(await readFile(path.join(candidate, manifestName), 'utf8')),
        };
    }
    const publishedRoot = path.join(candidate, 'wwwroot');
    try {
        await access(path.join(publishedRoot, 'index.html'));
        return { root: publishedRoot };
    } catch (error) {
        if (error.code !== 'ENOENT') throw error;
        await access(path.join(candidate, 'index.html'));
        return { root: candidate };
    }
}

async function resolveApplicationPath(application, requestPath) {
    const outputPath = resolveRequestPath(application.root, requestPath);
    if (!outputPath) return null;
    try {
        if ((await stat(outputPath)).isFile()) return outputPath;
    } catch (error) {
        if (error.code !== 'ENOENT' && error.code !== 'ENOTDIR') throw error;
    }
    let entry = application.manifest?.Root;
    for (const segment of decodeURIComponent(requestPath).split('/').filter(Boolean)) {
        entry = entry?.Children?.[segment];
    }
    return entry?.Asset
        ? path.resolve(application.manifest.ContentRoots[entry.Asset.ContentRootIndex], entry.Asset.SubPath)
        : outputPath;
}

function resolveRequestPath(root, relativePath) {
    const decoded = decodeURIComponent(relativePath);
    const resolved = path.resolve(root, `.${decoded}`);
    if (resolved !== root && !resolved.startsWith(`${root}${path.sep}`)) {
        return null;
    }
    return resolved;
}

async function serveFile(request, response, filePath) {
    let details;
    try {
        details = await stat(filePath);
    } catch {
        response.writeHead(404).end('Not found');
        return;
    }
    if (!details.isFile()) {
        response.writeHead(404).end('Not found');
        return;
    }

    const commonHeaders = {
        'Cache-Control': 'no-store',
        'Cross-Origin-Embedder-Policy': 'require-corp',
        'Cross-Origin-Opener-Policy': 'same-origin',
        'Cross-Origin-Resource-Policy': 'same-origin',
        'Content-Type': contentTypes.get(path.extname(filePath).toLowerCase()) ?? 'application/octet-stream',
    };
    const range = request.headers.range?.match(/^bytes=(\d*)-(\d*)$/);
    if (range) {
        const start = range[1] ? Number(range[1]) : 0;
        const end = range[2] ? Number(range[2]) : details.size - 1;
        if (start > end || end >= details.size) {
            response.writeHead(416, { 'Content-Range': `bytes */${details.size}`, ...commonHeaders }).end();
            return;
        }
        response.writeHead(206, {
            ...commonHeaders,
            'Accept-Ranges': 'bytes',
            'Content-Length': end - start + 1,
            'Content-Range': `bytes ${start}-${end}/${details.size}`,
        });
        createReadStream(filePath, { start, end }).pipe(response);
        return;
    }

    response.writeHead(200, { ...commonHeaders, 'Content-Length': details.size });
    createReadStream(filePath).pipe(response);
}

async function startServer(application, corpusRoot) {
    const server = http.createServer(async (request, response) => {
        try {
            const url = new URL(request.url ?? '/', 'http://localhost');
            const corpusRequest = url.pathname.startsWith('/corpus/');
            const relative = corpusRequest ? url.pathname.slice('/corpus'.length) : url.pathname;
            const requestPath = relative === '/' ? '/index.html' : relative;
            const filePath = corpusRequest
                ? resolveRequestPath(corpusRoot, requestPath)
                : await resolveApplicationPath(application, requestPath);
            if (!filePath) {
                response.writeHead(400).end('Invalid path');
                return;
            }
            await serveFile(request, response, filePath);
        } catch (error) {
            response.writeHead(500).end(error?.stack ?? String(error));
        }
    });
    await new Promise((resolve, reject) => {
        server.once('error', reject);
        server.listen(0, '127.0.0.1', resolve);
    });
    return server;
}

async function main() {
    const options = parseArguments(process.argv.slice(2));
    const appRoot = await findApplicationRoot(options.app);
    await access(path.join(options.corpus, 'assets-manifest.json'));
    await mkdir(options.output, { recursive: true });

    const browserTypes = options.serveOnly ? null : await import('playwright');
    const server = await startServer(appRoot, options.corpus);
    const address = server.address();
    const baseUrl = `http://127.0.0.1:${address.port}/`;
    if (options.serveOnly) {
        console.log(`Web validation workbench: ${baseUrl}`);
        await new Promise(resolve => {
            process.once('SIGINT', resolve);
            process.once('SIGTERM', resolve);
        });
        await new Promise(resolve => server.close(resolve));
        return;
    }

    const browserType = browserTypes[options.browser];
    const consoleLines = [];
    let browser;

    try {
        browser = await browserType.launch({
            headless: !options.headed,
            args: options.browser === 'chromium' ? ['--enable-gpu', '--ignore-gpu-blocklist'] : [],
        });
        const context = await browser.newContext({
            acceptDownloads: true,
            viewport: { width: 1440, height: 900 },
        });
        const page = await context.newPage();
        page.on('console', message => consoleLines.push(`[${message.type()}] ${message.text()}`));
        page.on('pageerror', error => consoleLines.push(`[pageerror] ${error.stack ?? error.message}`));

        const parameters = new URLSearchParams({ run: '1' });
        for (const caseId of options.cases) {
            parameters.append('case', caseId);
        }
        const url = `${baseUrl}?${parameters}`;
        const response = await page.goto(url, { waitUntil: 'domcontentloaded' });
        if (!response?.ok()) {
            throw new Error(`Validation page returned HTTP ${response?.status() ?? 'no response'}.`);
        }

        let state;
        try {
            await page.waitForFunction(
                () => ['complete', 'failed', 'canceled'].includes(globalThis.lightPlayerValidation?.status),
                undefined,
                { timeout: options.timeout });
        } finally {
            state = await page.evaluate(() => ({
                status: globalThis.lightPlayerValidation?.status ?? 'booting',
                message: globalThis.lightPlayerValidation?.message ?? 'The validation runtime did not initialize.',
                report: globalThis.lightPlayerValidation?.report ?? null,
                inventory: globalThis.lightPlayerValidation?.inventory ?? null,
                bundleFileName: globalThis.lightPlayerValidation?.bundleFileName ?? null,
            }));
            consoleLines.push(`[validation] ${state.status}: ${state.message}`);
            await writeFile(path.join(options.output, 'status.json'),
                `${JSON.stringify({ status: state.status, message: state.message }, null, 2)}\n`);
            await writeFile(path.join(options.output, 'browser-gpu.json'), `${JSON.stringify(state.inventory, null, 2)}\n`);
            await writeFile(path.join(options.output, 'report.json'), `${JSON.stringify(state.report, null, 2)}\n`);
            await page.screenshot({ path: path.join(options.output, 'workbench.png'), fullPage: true });
        }

        if (state.status === 'complete') {
            if (!state.report?.Results?.length) {
                throw new Error('Validation completed without any case results.');
            }
            const downloadPromise = page.waitForEvent('download');
            await page.evaluate(() => globalThis.lightPlayerValidation &&
                import('/validation-bridge.js').then(module => module.download()));
            const download = await downloadPromise;
            await download.saveAs(path.join(options.output, state.bundleFileName ?? 'web-validation.zip'));

            await page.waitForFunction(async () => {
                const media = await import('/media/media-worker-host.js');
                return JSON.parse(media.getDiagnostics()).resources === 0;
            }, undefined, { timeout: 10000 });
            const media = await page.evaluate(async () =>
                JSON.parse((await import('/media/media-worker-host.js')).getDiagnostics()));
            await writeFile(path.join(options.output, 'browser-media.json'), `${JSON.stringify(media, null, 2)}\n`);
            console.log(`Validation: ${state.report.Passed} passed, ${state.report.Failed} failed, ${state.report.Skipped} skipped.`);
            for (const result of state.report.Results.filter(result => result.Status !== 'Passed')) {
                console.log(`${result.Status}: ${result.CaseId}: ${result.Message}`);
            }
        }

        await context.close();
        if (state.status !== 'complete') {
            throw new Error(`Validation ${state.status}: ${state.message}`);
        }
        process.exitCode = state.report?.Failed > 0 ? 1 : 0;
    } finally {
        await writeFile(path.join(options.output, 'browser-console.log'), `${consoleLines.join('\n')}\n`);
        await browser?.close();
        await new Promise(resolve => server.close(resolve));
    }
}

main().catch(error => {
    console.error(error?.stack ?? error);
    process.exitCode = 2;
});