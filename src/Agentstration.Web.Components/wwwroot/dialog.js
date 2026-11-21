const frames = new WeakMap();

export function openDialog(dialog) {
    if (!(dialog instanceof HTMLDialogElement) || frames.has(dialog)) return;
    const previous = document.activeElement;
    const cancel = event => {
        event.preventDefault();
        if (dialog.getAttribute("aria-busy") !== "true") dialog.querySelector("[data-modal-close]")?.click();
    };
    const keydown = event => {
        if (event.key !== "Tab") return;
        const focusable = [...dialog.querySelectorAll('button:not(:disabled):not([hidden]),input:not(:disabled),select:not(:disabled),textarea:not(:disabled),a[href],[tabindex="0"]')]
            .filter(element => element.getClientRects().length > 0);
        const first = focusable[0], last = focusable.at(-1);
        if (!first) { event.preventDefault(); dialog.focus(); return; }
        if (event.shiftKey && (document.activeElement === first || document.activeElement === dialog)) {
            event.preventDefault(); last.focus();
        } else if (!event.shiftKey && (document.activeElement === last || document.activeElement === dialog)) {
            event.preventDefault(); first.focus();
        }
    };
    const backdrop = event => {
        if (event.target !== dialog || dialog.getAttribute("aria-busy") === "true") return;
        const bounds = dialog.getBoundingClientRect();
        if (event.clientX < bounds.left || event.clientX > bounds.right || event.clientY < bounds.top || event.clientY > bounds.bottom)
            dialog.querySelector("[data-modal-close]")?.click();
    };
    dialog.addEventListener("click", backdrop);
    dialog.addEventListener("cancel", cancel);
    dialog.addEventListener("keydown", keydown);
    frames.set(dialog, { previous, cancel, keydown, backdrop });
    dialog.showModal();
    return { dispose() { disposeDialog(dialog); } };
}

export function disposeDialog(dialog) {
    const frame = frames.get(dialog);
    if (!frame) return;
    dialog.removeEventListener("click", frame.backdrop);
    dialog.removeEventListener("cancel", frame.cancel);
    dialog.removeEventListener("keydown", frame.keydown);
    if (dialog.open) dialog.close();
    if (frame.previous instanceof HTMLElement && frame.previous.isConnected) frame.previous.focus();
    frames.delete(dialog);
}
