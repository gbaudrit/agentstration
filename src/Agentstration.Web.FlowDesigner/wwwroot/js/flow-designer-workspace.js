export function registerKeyboardShortcuts(dotNetReference) {
    const onKeyDown = event => {
        if (event.key !== "Escape" || event.defaultPrevented) return;
        if (event.target instanceof Element && event.target.closest("[role='dialog']")) return;

        dotNetReference.invokeMethodAsync("ExitFullScreenAsync");
    };

    document.addEventListener("keydown", onKeyDown);

    return {
        dispose() {
            document.removeEventListener("keydown", onKeyDown);
        }
    };
}
