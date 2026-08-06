export function registerCommandPalette(dotNetReference) {
    const keyHandler = event => {
        if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === "k") {
            event.preventDefault();
            dotNetReference.invokeMethodAsync("ToggleCommandPalette");
        }
    };

    document.addEventListener("keydown", keyHandler);

    return {
        focus(element) {
            element?.focus();
        },
        dispose() {
            document.removeEventListener("keydown", keyHandler);
        }
    };
}
