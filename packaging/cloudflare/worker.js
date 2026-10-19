// Assets over Cloudflare's 25 MiB per-file limit are uploaded as brotli only, so a request for the
// original name finds no asset and lands here. Rewrapping with `encodeBody: "manual"` is what stops
// the runtime from compressing the already-compressed body a second time.
const CONTENT_TYPES = {
	wasm: "application/wasm",
	js: "text/javascript; charset=utf-8",
	dat: "application/octet-stream",
};

export default {
	async fetch(request, env) {
		const url = new URL(request.url);
		if (url.pathname === "/versions") {
			const headers = new Headers({
				"Cache-Control": "no-store",
				"Content-Type": "application/json; charset=utf-8",
			});
			if (request.method !== "GET" && request.method !== "HEAD") {
				headers.set("Allow", "GET, HEAD");
				return new Response(null, { status: 405, headers });
			}
			const requestHeaders = new Headers(request.headers);
			requestHeaders.delete("If-None-Match");
			requestHeaders.delete("If-Modified-Since");
			const catalog = await env.ASSETS.fetch(new Request(request, { headers: requestHeaders }));
			if (!catalog.ok || catalog.headers.get("Content-Type")?.startsWith("text/html")) {
				await catalog.body?.cancel();
				return new Response(null, { status: 404, headers });
			}
			return new Response(request.method === "HEAD" ? null : catalog.body, { headers });
		}
		if (url.pathname === "/api/v1/server") {
			if (request.method !== "GET" && request.method !== "HEAD") {
				return new Response(null, { status: 405, headers: { Allow: "GET, HEAD" } });
			}

			const headers = {
				"Cache-Control": "no-store",
				"Content-Type": "application/json; charset=utf-8",
			};
			const body = JSON.stringify({ name: "LightPlayer", apiVersion: "1", apiImplemented: false });
			return new Response(request.method === "HEAD" ? null : body, { headers });
		}

		const assetPath = url.pathname === "/diag" ? "/" : url.pathname;
		const assetUrl = new URL(assetPath + url.search, url);
		const assetRequest = assetPath === url.pathname ? request : new Request(assetUrl, request);
		const compressed = await env.ASSETS.fetch(new URL(assetPath + ".br", url));

		if (!compressed.ok) {
			await compressed.body?.cancel();
			return env.ASSETS.fetch(assetRequest);
		}

		const headers = new Headers({
			"Content-Encoding": "br",
			"Content-Type": CONTENT_TYPES[assetPath.split(".").pop()] ?? "application/octet-stream",
			// no-transform keeps the edge from re-encoding; the name is already content-hashed.
			"Cache-Control": "no-transform, public, max-age=31536000, immutable",
			"Cross-Origin-Opener-Policy": "same-origin",
			"Cross-Origin-Embedder-Policy": "require-corp",
			"Cross-Origin-Resource-Policy": "same-origin",
			"Service-Worker-Allowed": "/",
		});

		const etag = compressed.headers.get("ETag");
		if (etag) {
			headers.set("ETag", etag);
			if (request.headers.get("If-None-Match") === etag) {
				await compressed.body?.cancel();
				return new Response(null, { status: 304, headers });
			}
		}

		if (request.method === "HEAD") {
			await compressed.body?.cancel();
			return new Response(null, { headers, encodeBody: "manual" });
		}

		return new Response(compressed.body, { headers, encodeBody: "manual" });
	},
};
