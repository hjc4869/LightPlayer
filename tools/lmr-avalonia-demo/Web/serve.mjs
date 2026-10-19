import http from "node:http";
import { createReadStream } from "node:fs";
import { stat } from "node:fs/promises";
import path from "node:path";

const root = path.resolve(process.argv[2] || "artifacts/lmr-browser-demo/wwwroot");
const port = Number(process.argv[3] || 5176);
const types = { ".html": "text/html", ".js": "text/javascript", ".mjs": "text/javascript", ".wasm": "application/wasm",
    ".json": "application/json", ".css": "text/css", ".png": "image/png", ".svg": "image/svg+xml" };
const headers = { "Cross-Origin-Opener-Policy": "same-origin", "Cross-Origin-Embedder-Policy": "require-corp",
    "Cross-Origin-Resource-Policy": "same-origin", "Cache-Control": "no-cache" };

http.createServer(async (request, response) => {
    try {
        if (request.method !== "GET" && request.method !== "HEAD") { response.writeHead(405, headers).end(); return; }
        const url = new URL(request.url || "/", "http://localhost");
        const file = path.resolve(root, "." + decodeURIComponent(url.pathname === "/" ? "/index.html" : url.pathname));
        if (!file.startsWith(root + path.sep)) { response.writeHead(403, headers).end(); return; }
        const details = await stat(file);
        if (!details.isFile()) { response.writeHead(404, headers).end(); return; }
        const range = /^bytes=(\d+)-(\d*)$/.exec(request.headers.range || "");
        const start = range ? Number(range[1]) : 0;
        const end = range?.[2] ? Math.min(details.size - 1, Number(range[2])) : details.size - 1;
        if (start > end || start < 0) { response.writeHead(416, { ...headers, "Content-Range": `bytes */${details.size}` }).end(); return; }
        response.writeHead(range ? 206 : 200, { ...headers, "Content-Type": types[path.extname(file)] || "application/octet-stream",
            "Accept-Ranges": "bytes", "Content-Length": end - start + 1,
            ...(range ? { "Content-Range": `bytes ${start}-${end}/${details.size}` } : {}) });
        if (request.method === "HEAD") response.end();
        else {
            const stream = createReadStream(file, { start, end });
            stream.on("error", error => { console.error(error); response.destroy(error); });
            stream.pipe(response);
        }
    } catch (error) {
        if (error.code === "ENOENT") response.writeHead(404, headers).end("Not found");
        else { console.error(error); response.writeHead(500, headers).end("Could not serve this asset."); }
    }
}).listen(port, "127.0.0.1", () => console.log(`LMR browser demo: http://127.0.0.1:${port}/`));
