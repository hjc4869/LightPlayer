import { dotnet } from "./_framework/dotnet.js";
import { adoptFiles } from "./file-system-access.js";
import { getDiagnostics } from "./media/media-worker-host.js";

const error = document.querySelector("#error");
const controls = [...document.querySelectorAll("button,select,input")];
controls.forEach(control => { control.disabled = true; });
function report(failure) { error.textContent = failure.stack || String(failure); console.error(failure); }
try {
    const runtime = await dotnet.create();
    await runtime.runMain(runtime.getConfig().mainAssemblyName, []);
    const api = (await runtime.getAssemblyExports(runtime.getConfig().mainAssemblyName)).LmrAvaloniaDemo.Web.BrowserProgram;
    globalThis.lmrDemo = {
        open: async (path, decoder = "auto", photo = false) => { error.textContent = ""; document.querySelector("#name").textContent = path.split("/").at(-1); await api.OpenAsync(path, decoder, photo); },
        command: (name, value = 0) => api.CommandAsync(name, value),
        status: () => ({ ...JSON.parse(api.Status()), gpu: JSON.parse(getDiagnostics()), managedShared: getDotnetRuntime(0).localHeapViewU8().buffer instanceof SharedArrayBuffer }),
    };
    controls.forEach(control => { control.disabled = false; });
    document.querySelector("#open").onclick = () => document.querySelector("#file").click();
    document.querySelector("#file").onchange = async event => {
        try {
            const file = event.target.files[0];
            if (!file) return;
            const [root, name] = adoptFiles([file]).trim().split("\t");
            await lmrDemo.open(`fsa://${root}/${encodeURIComponent(name)}`, document.querySelector("#decoder").value,
                file.type.startsWith("image/") || /\.(heic|heif|avif)$/i.test(name));
        } catch (failure) { report(failure); }
    };
    for (const command of ["play", "pause", "step", "stop"]) document.querySelector("#" + command).onclick = () => lmrDemo.command(command).catch(report);
    document.querySelector("#seek").onchange = event => lmrDemo.command("seek", Number(event.target.value)).catch(report);
    document.querySelector("#zoom").onchange = event => lmrDemo.command("zoom", Number(event.target.value)).catch(report);
    function update() {
        try {
            const state = lmrDemo.status();
            const seek = document.querySelector("#seek");
            seek.max = Math.max(1, state.duration);
            if (document.activeElement !== seek) seek.value = state.position;
            document.querySelector("#time").textContent = `${Math.floor(state.position / 60)}:${String(Math.floor(state.position % 60)).padStart(2, "0")}`;
            document.querySelector("#stats").textContent = `${state.state} | ${state.format} ${state.delivery} | decode ${state.decodeFps.toFixed(1)} fps | render ${state.renderFps.toFixed(1)} fps | dropped ${state.dropped} | tiles ${state.tiles} | GPU uploads ${state.gpu.uploads} | managed pixel bytes ${state.gpu.managedPixelBytes}`;
            if (state.error) error.textContent = state.error;
        } catch (failure) { report(failure); }
    }
    setInterval(update, 500);
    update();
} catch (failure) { report(failure); }
