import { createHash } from "node:crypto";
import { execFileSync } from "node:child_process";
import { existsSync, mkdirSync, readFileSync, statSync, writeFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const options = Object.fromEntries(process.argv.slice(2).reduce((pairs, value, index, args) => {
    if (index % 2 === 0) pairs.push([value.replace(/^--/, ""), args[index + 1]]);
    return pairs;
}, []));
const project = path.dirname(fileURLToPath(import.meta.url));
const sdk = options.emscripten;
if (!sdk || !options.package || !options.output) throw new Error("Pass --emscripten, --package, and --output.");
const compilerVersion = readFileSync(path.join(sdk, "emscripten/emscripten-version.txt"), "utf8").trim();
const flavor = /^"?3\./.test(compilerVersion) ? "em3" : /^"?6\./.test(compilerVersion) ? "em6" : null;
if (!flavor) throw new Error(`Unsupported Emscripten version: ${compilerVersion}`);
const cache = path.resolve(project, "../../artifacts/tools/browser-sqlite");
const output = options.output;
const version = "3500400";
const sourceHash = "1d3049dd0f830a025a53105fc79fd2ab9431aea99e137809d064d8ee8356b032";
const archive = path.join(cache, `sqlite-amalgamation-${version}.zip`);
const headers = path.join(cache, `sqlite-amalgamation-${version}`);
mkdirSync(cache, { recursive: true });
mkdirSync(output, { recursive: true });
if (!existsSync(archive)) execFileSync("curl", ["--fail", "--location", "--retry", "3", "--output", archive,
    `https://www.sqlite.org/2025/sqlite-amalgamation-${version}.zip`], { stdio: "inherit" });
if (createHash("sha256").update(readFileSync(archive)).digest("hex") !== sourceHash) throw new Error("SQLite header archive checksum mismatch.");
if (!existsSync(headers)) execFileSync("python3", ["-m", "zipfile", "-e", archive, cache], { stdio: "inherit" });
const libraries = [path.join(options.package, "static", `wasm-${flavor}`, "libsqlite3.a")];
const source = path.join(project, "native/sqlite-worker.c");
const exports = ["malloc", "free", "sqlite3_close", "sqlite3_prepare_v2", "sqlite3_step", "sqlite3_finalize",
    "sqlite3_bind_parameter_index", "sqlite3_bind_null", "sqlite3_bind_text", "sqlite3_bind_blob", "sqlite3_bind_int64", "sqlite3_bind_double",
    "sqlite3_column_count", "sqlite3_column_type", "sqlite3_column_int64", "sqlite3_column_double", "sqlite3_column_text", "sqlite3_column_blob", "sqlite3_column_bytes",
    "sqlite3_value_type", "sqlite3_value_int64", "sqlite3_value_text", "sqlite3_value_bytes", "sqlite3_result_null", "sqlite3_result_text", "sqlite3_result_int64",
    "sqlite3_result_error", "sqlite3_errmsg", "sqlite3_changes", "sqlite3_get_autocommit", "sqlite3_interrupt"];
const args = [source, "-I", headers, ...libraries, "-o", path.join(output, "sqlite.mjs"), "-O2", "-msimd128",
    "-sMODULARIZE=1", "-sEXPORT_ES6=1", "-sENVIRONMENT=web,worker,node", "-sALLOW_MEMORY_GROWTH=1",
    "-sMAXIMUM_MEMORY=536870912", "-sINITIAL_MEMORY=16777216", "-sSTACK_SIZE=1048576", "-sWASM_BIGINT=1",
    `-sEXPORTED_FUNCTIONS=${JSON.stringify(exports.map(name => "_" + name))}`,
    "-sEXPORTED_RUNTIME_METHODS=['HEAPU8','HEAPU32','UTF8ToString','stringToUTF8','lengthBytesUTF8']",
    "-sERROR_ON_UNDEFINED_SYMBOLS=1", "-sFILESYSTEM=1"];
const fingerprint = createHash("sha256").update(readFileSync(source)).update(readFileSync(fileURLToPath(import.meta.url)))
    .update(JSON.stringify([sdk, compilerVersion, args, libraries.map(file => [file, statSync(file).size, statSync(file).mtimeMs])])).digest("hex");
const stamp = path.join(output, "build.json");
if (existsSync(stamp) && JSON.parse(readFileSync(stamp, "utf8")).fingerprint === fingerprint &&
    existsSync(path.join(output, "sqlite.wasm")) && existsSync(path.join(output, "sqlite.mjs"))) {
    console.log(`SQLite worker is current (${flavor}).`);
    process.exit(0);
}
const env = { ...process.env, DOTNET_EMSCRIPTEN_LLVM_ROOT: path.join(sdk, "bin"),
    DOTNET_EMSCRIPTEN_BINARYEN_ROOT: sdk, DOTNET_EMSCRIPTEN_NODE_JS: process.execPath,
    EM_CACHE: path.join(cache, `emscripten-${flavor}`), FROZEN_CACHE: "" };
execFileSync("python3", [path.join(sdk, "emscripten/emcc.py"), ...args], { env, stdio: "inherit" });
writeFileSync(stamp, JSON.stringify({ fingerprint, sqlite: "3.50.4", flavor }) + "\n");
console.log(`Built isolated SQLite worker (${flavor}): ${output}`);
