export function open(dialog) {
    if (dialog.open) return;
    // Native modal behavior provides focus containment, inert background and focus restoration.
    dialog.addEventListener('cancel', event => {
        event.preventDefault();
        dialog.querySelector('[data-cancel]').click();
    }, { once: true });
    dialog.showModal();
}
