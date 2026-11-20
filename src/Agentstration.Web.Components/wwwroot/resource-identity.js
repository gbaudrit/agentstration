export function bind(root, panel) {
    const trigger = root.querySelector('[data-identity-trigger]');
    let restoreFocus = false;
    function position(opening = false) {
        if (opening !== true && !panel.matches(':popover-open')) return;
        const viewport = window.visualViewport;
        const width = viewport?.width ?? document.documentElement.clientWidth;
        const height = viewport?.height ?? window.innerHeight;
        const offsetLeft = viewport?.offsetLeft ?? 0;
        const offsetTop = viewport?.offsetTop ?? 0;
        const anchor = trigger.getBoundingClientRect();
        const panelWidth = Math.min(544, width - 16);
        const left = Math.max(offsetLeft + 8, Math.min(anchor.right - panelWidth, offsetLeft + width - panelWidth - 8));
        const top = Math.max(offsetTop + 8, Math.min(anchor.bottom + 8, offsetTop + height - 120));
        panel.style.width = `${panelWidth}px`;
        panel.style.left = `${left}px`;
        panel.style.top = `${top}px`;
        panel.style.setProperty('--identity-max-height', `${Math.max(80, offsetTop + height - top - 8)}px`);
        panel.style.setProperty('--identity-arrow-left', `${Math.max(12, Math.min(anchor.left + anchor.width / 2 - left, panelWidth - 12))}px`);
    }
    function toggled(event) {
        trigger.setAttribute('aria-expanded', String(event.newState === 'open'));
        if (event.newState === 'open') {
            position();
            panel.querySelector('[data-identity-close]')?.focus({ preventScroll: true });
        } else if (restoreFocus) trigger.focus({ preventScroll: true });
    }
    function starting(event) {
        if (event.newState === 'open') position(true);
        else restoreFocus = panel.contains(document.activeElement);
    }
    panel.addEventListener('beforetoggle', starting);
    panel.addEventListener('toggle', toggled);
    window.addEventListener('resize', position);
    window.addEventListener('scroll', position, true);
    window.visualViewport?.addEventListener('resize', position);
    return { toggle() { panel.togglePopover(); }, dispose() {
        panel.removeEventListener('toggle', toggled);
        panel.removeEventListener('beforetoggle', starting);
        window.removeEventListener('resize', position);
        window.removeEventListener('scroll', position, true);
        window.visualViewport?.removeEventListener('resize', position);
        if (panel.matches(':popover-open')) panel.hidePopover();
    } };
}
