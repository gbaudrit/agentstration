export function focusFirstInvalid(formId, contextIdentity) {
    const form = document.getElementById(formId);
    const visible = element => element instanceof HTMLElement && !element.closest('[hidden],[inert]') && element.getClientRects().length > 0;
    if (!visible(form) || form.dataset.validationContext !== contextIdentity) return false;
    const editable = element => visible(element) && !element.matches(':disabled,[readonly]');
    const focusable = 'input:not([type="hidden"]),textarea,select,button,[tabindex="0"]';
    // Native inputs and specialized editors both keep their existing inline errors.
    const errors = [...form.querySelectorAll('.validation-message,[aria-invalid="true"],.invalid')].filter(visible);
    for (const error of errors) {
        const field = error.closest('.form-field,.label-editor-row') ?? error;
        const target = error.matches(focusable) && editable(error) ? error
            : [...field.querySelectorAll(focusable)].find(editable);
        field.scrollIntoView({ block: 'center', behavior: 'auto' });
        if (target) target.focus({ preventScroll: true });
        else { error.setAttribute('tabindex', '-1'); error.focus({ preventScroll: true }); }
        return true;
    }
    form.focus({ preventScroll: true });
    return true;
}
