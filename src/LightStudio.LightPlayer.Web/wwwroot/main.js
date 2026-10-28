import { dotnet } from './_framework/dotnet.js?v=cache-policy-v1';
import { registerBrowserLifecycle } from './browser-lifecycle.js';
import { registerFileHandlers } from './file-handlers.js';
import { beginLoad as beginFontLoad } from './browser-fonts.js';
import { registerPageSearchShortcuts } from './page-search.js';

const noop = () => { };
const boot = globalThis.lightPlayerBoot ?? {
    status: noop,
    notice: noop,
    progress: noop,
    suspend: noop,
    resume: noop,
    hideWhenAppReady: noop,
    ready: noop,
    fail: noop,
};

async function startApplication() {
    if (typeof screen.addEventListener !== 'function') {
        const screenEvents = new EventTarget();
        screen.addEventListener = screenEvents.addEventListener.bind(screenEvents);
        screen.removeEventListener = screenEvents.removeEventListener.bind(screenEvents);
        const notifyScreenChanged = () => screenEvents.dispatchEvent(new Event('change'));
        window.addEventListener('resize', notifyScreenChanged);
        screen.orientation?.addEventListener('change', notifyScreenChanged);
    }

    registerPageSearchShortcuts();

    await registerBrowserLifecycle();

    // Downloads the fallback fonts alongside the runtime. The app collects them once it starts, and
    // survives without them, so a failure here must not reject into the boot error handler.
    beginFontLoad().catch(() => { });

    boot.status('Loading .NET runtime…');

    const runtime = await dotnet
        // Satellite resource assemblies are published but never fetched otherwise: unlike Blazor,
        // nothing asks the loader for them, so every ResourceManager lookup fell back to English.
        .withConfig({
            loadAllSatelliteResources: true,
        })
        .withModuleConfig({
            onDownloadResourceProgress: (loaded, total) => boot.progress(loaded, total),
            onAbort: (reason) => boot.fail('The .NET runtime stopped unexpectedly.', reason),
            onExit: (code, reason) => {
                if (code !== 0) {
                    boot.fail(`The .NET runtime exited unexpectedly (exit code ${code}).`, reason);
                }
            },
        })
        .create();

    const config = runtime.getConfig();
    boot.status('Starting Light Media Center…');
    boot.hideWhenAppReady();
    await runtime.runMain(config.mainAssemblyName, []);
    boot.ready();
    await new Promise(() => { });
}

async function startInstance() {
    await navigator.locks.request('lightplayer.instance', { ifAvailable: true }, async lock => {
        if (!lock) {
            const messages = {
                en: {
                    alreadyOpen: 'Light Media Center is already open in another tab or window. Close it, then reload this page.',
                    forwarding: 'Sending files to the open Light Media Center window...',
                    forwarded: 'Files sent to the open Light Media Center window. You can close this window.',
                    failed: 'Could not confirm file delivery. Check the open Light Media Center window before opening the files again.',
                },
                de: {
                    alreadyOpen: 'Light Media Center ist bereits in einem anderen Tab oder Fenster geöffnet. Schließen Sie es und laden Sie diese Seite neu.',
                    forwarding: 'Dateien werden an das geöffnete Light Media Center-Fenster gesendet...',
                    forwarded: 'Die Dateien wurden an das geöffnete Light Media Center-Fenster gesendet. Sie können dieses Fenster schließen.',
                    failed: 'Die Dateiübertragung konnte nicht bestätigt werden. Prüfen Sie das geöffnete Light Media Center-Fenster, bevor Sie die Dateien erneut öffnen.',
                },
                es: {
                    alreadyOpen: 'Light Media Center ya está abierto en otra pestaña o ventana. Ciérrela y vuelva a cargar esta página.',
                    forwarding: 'Enviando archivos a la ventana abierta de Light Media Center...',
                    forwarded: 'Los archivos se han enviado a la ventana abierta de Light Media Center. Puede cerrar esta ventana.',
                    failed: 'No se pudo confirmar la entrega de los archivos. Compruebe la ventana abierta de Light Media Center antes de volver a abrirlos.',
                },
                fr: {
                    alreadyOpen: 'Light Media Center est déjà ouvert dans un autre onglet ou une autre fenêtre. Fermez-le, puis rechargez cette page.',
                    forwarding: 'Envoi des fichiers à la fenêtre ouverte de Light Media Center...',
                    forwarded: 'Les fichiers ont été envoyés à la fenêtre ouverte de Light Media Center. Vous pouvez fermer cette fenêtre.',
                    failed: 'La réception des fichiers n’a pas pu être confirmée. Vérifiez la fenêtre ouverte de Light Media Center avant de les ouvrir à nouveau.',
                },
                ja: {
                    alreadyOpen: 'Light Media Center は別のタブまたはウィンドウで開いています。それを閉じてから、このページを再読み込みしてください。',
                    forwarding: '開いている Light Media Center ウィンドウにファイルを送信しています...',
                    forwarded: '開いている Light Media Center ウィンドウにファイルを送信しました。このウィンドウを閉じることができます。',
                    failed: 'ファイルの受信を確認できませんでした。再度ファイルを開く前に、開いている Light Media Center ウィンドウを確認してください。',
                },
                'zh-Hans': {
                    alreadyOpen: 'Light Media Center 已在其他标签页或窗口中打开。请将其关闭，然后重新加载此页面。',
                    forwarding: '正在将文件发送到已打开的 Light Media Center 窗口...',
                    forwarded: '文件已发送到已打开的 Light Media Center 窗口。您可以关闭此窗口。',
                    failed: '无法确认文件是否已送达。请先检查已打开的 Light Media Center 窗口，再重新打开文件。',
                },
                'zh-Hant': {
                    alreadyOpen: 'Light Media Center 已在其他分頁或視窗中開啟。請將其關閉，然後重新載入此頁面。',
                    forwarding: '正在將檔案傳送到已開啟的 Light Media Center 視窗...',
                    forwarded: '檔案已傳送到已開啟的 Light Media Center 視窗。您可以關閉此視窗。',
                    failed: '無法確認檔案是否已送達。請先檢查已開啟的 Light Media Center 視窗，再重新開啟檔案。',
                },
            };
            const locale = new Intl.Locale(navigator.language).maximize();
            const language = locale.language === 'zh' ? `zh-${locale.script}` : locale.language;
            const text = messages[language] ?? messages.en;
            boot.fail(text.alreadyOpen);
            registerFileHandlers({
                forward: true,
                onForwarding: () => boot.notice(text.forwarding),
                onForwarded: () => boot.notice(text.forwarded),
                onForwardError: error => boot.fail(text.failed, error),
            });
            return;
        }

        const stopFileHandlers = registerFileHandlers();
        try {
            await startApplication();
        } finally {
            stopFileHandlers();
        }
    });
}

startInstance().catch(error => {
    boot.fail('The application failed to load. Check your network connection and try again.', error);
    throw error;
});