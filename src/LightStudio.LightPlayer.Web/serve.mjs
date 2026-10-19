// Serves a published browser client on this machine with the cross-origin isolation its threaded
// media worker requires. Usage: node serve.mjs <wwwroot> [port]
import http from "node:http";
import { createReadStream } from "node:fs";
import { stat } from "node:fs/promises";
import path from "node:path";

if (!process.argv[2]) throw new Error("Pass the wwwroot folder of a published client.");
const root = path.resolve(process.argv[2]);
const port = Number(process.argv[3] ?? 5000);
const types = new Map([
    [".html", "text/html; charset=utf-8"],
    [".js", "text/javascript; charset=utf-8"],
    [".mjs", "text/javascript; charset=utf-8"],
    [".json", "application/json; charset=utf-8"],
    [".webmanifest", "application/manifest+json"],
    [".css", "text/css; charset=utf-8"],
    [".wasm", "application/wasm"],
    [".png", "image/png"],
    [".svg", "image/svg+xml"],
    [".ico", "image/x-icon"],
]);
const isolation = {
    "Cross-Origin-Opener-Policy": "same-origin",
    "Cross-Origin-Embedder-Policy": "require-corp",
    "Cross-Origin-Resource-Policy": "same-origin",
    "Service-Worker-Allowed": "/",
    "Cache-Control": "no-cache",
};

http.createServer(async (request, response) => {
    let headers = isolation;
    try {
        const { pathname } = new URL(request.url ?? "/", "http://localhost");
        if (pathname === "/versions") headers = { ...isolation, "Cache-Control": "no-store" };
        if (request.method !== "GET" && request.method !== "HEAD") {
            response.writeHead(405, { ...headers, Allow: "GET, HEAD" }).end();
            return;
        }
        // Like the static Cloudflare host, this origin has no library server behind it.
        if (pathname === "/api/v1/server") {
            const body = JSON.stringify({ name: "LightPlayer", apiVersion: "1", apiImplemented: false });
            response.writeHead(200, { ...isolation, "Cache-Control": "no-store", "Content-Type": "application/json; charset=utf-8" });
            response.end(request.method === "HEAD" ? undefined : body);
            return;
        }
        const file = path.join(root, decodeURIComponent(pathname === "/" || pathname === "/diag" ? "/index.html" : pathname));
        const info = file.startsWith(root + path.sep) ? await stat(file) : null;
        if (!info?.isFile()) {
            response.writeHead(404, headers).end();
            return;
        }
        response.writeHead(200, { ...headers,
            "Content-Type": pathname === "/versions" ? "application/json; charset=utf-8" : types.get(path.extname(file)) ?? "application/octet-stream",
            "Content-Length": info.size });
        if (request.method === "HEAD") {
            response.end();
            return;
        }
        createReadStream(file).on("error", error => {
            console.error(error);
            response.destroy(error);
        }).pipe(response);
    } catch (error) {
        if (error.code === "ENOENT" || error.code === "ENOTDIR") response.writeHead(404, headers).end();
        else if (error instanceof URIError) response.writeHead(400, headers).end();
        else {
            console.error(error);
            response.writeHead(500, headers).end();
        }
    }
}).listen(port, "127.0.0.1", () => console.log(`Serving ${root} at http://127.0.0.1:${port}/ (Ctrl+C stops)`));
