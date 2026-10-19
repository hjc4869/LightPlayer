export function registerPageSearchShortcuts() {
    document.getElementById('out')?.addEventListener('keydown', event => {
        const key = event.key.toLowerCase();
        const command = event.ctrlKey !== event.metaKey && !event.altKey;
        if (command && (key === 'f' && !event.shiftKey || key === 'g') ||
            key === 'f3' && !event.ctrlKey && !event.metaKey && !event.altKey) {
            event.preventDefault();
        }
    }, { capture: true });
}