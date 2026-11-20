const listeners = new WeakMap();
const navigationKeys = new Set(["ArrowDown", "ArrowUp", "Home", "End"]);

export function attach(root) {
    detach(root);
    const listener = event => {
        const list = event.target.closest?.("[data-step-progress-tabs]");
        if (list?.closest("[data-step-progress]") !== root
            || !event.target.matches("button[role=tab]")
            || event.altKey || event.ctrlKey || event.metaKey || event.shiftKey) return;
        // Blazor handles selection/focus; suppress only these keys' native scroll.
        // Tab, Enter and Space retain their normal browser behavior.
        if (navigationKeys.has(event.key)) event.preventDefault();
    };
    root.addEventListener("keydown", listener);
    listeners.set(root, listener);
}

export function detach(root) {
    const listener = listeners.get(root);
    if (listener) root.removeEventListener("keydown", listener);
    listeners.delete(root);
}
