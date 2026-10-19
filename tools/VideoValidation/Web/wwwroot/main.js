import { dotnet } from './_framework/dotnet.js';
import { fail, markAppReady, setStatus } from './validation-bridge.js';

try {
    setStatus('booting', 'Loading .NET runtime');
    const runtime = await dotnet
        .withModuleConfig({
            onAbort: reason => fail(`The .NET runtime stopped unexpectedly: ${reason ?? 'unknown reason'}`),
            onExit: (code, reason) => {
                if (code !== 0) {
                    fail(`The .NET runtime exited with code ${code}: ${reason ?? 'unknown reason'}`);
                }
            },
        })
        .create();

    const config = runtime.getConfig();
    setStatus('booting', 'Starting validation app');
    await runtime.runMain(config.mainAssemblyName, []);
    markAppReady();
} catch (error) {
    fail(error?.stack ?? error?.message ?? String(error));
    throw error;
}