export function prefersDarkTheme() {
    return window.matchMedia?.("(prefers-color-scheme: dark)").matches ?? false;
}
