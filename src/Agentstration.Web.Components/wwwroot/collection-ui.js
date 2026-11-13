const collections = new WeakMap();
export function observeCollection(root, receiver) {
    if (!root?.isConnected) return;
    const query = matchMedia('(max-width: 760px)');
    let active = true;
    const release = () => {
        active = false;
        query.removeEventListener('change', changed);
        window.removeEventListener('pagehide', release);
    };
    const changed = () => {
        if (!active || !root.isConnected) { release(); return; }
        receiver.invokeMethodAsync('SetNarrow', query.matches).catch(error => {
            // A resize can finish after navigation or component disposal closes the circuit.
            if (!active || !root.isConnected) return;
            throw error;
        });
    };
    query.addEventListener('change', changed);
    window.addEventListener('pagehide', release, { once: true });
    collections.set(root, release);
    changed();
}
export function releaseCollection(root) { collections.get(root)?.(); collections.delete(root); }

const actions = new WeakMap();
function positionPanel(panel, trigger) {
    const rect = trigger.getBoundingClientRect();
    panel.style.left = `${Math.max(8, Math.min(rect.left, innerWidth - panel.offsetWidth - 8))}px`;
    panel.style.top = `${Math.max(8, Math.min(rect.bottom + 4, innerHeight - panel.offsetHeight - 8))}px`;
}
export function attachActions(root) {
    if (!root?.isConnected || actions.has(root)) return;
    const trigger = root.querySelector('summary');
    const panel = root.querySelector('.collection-actions-panel');
    if (!trigger || !panel) return;
    const toggle = () => {
        trigger.setAttribute('aria-expanded', String(root.open));
        if (root.open) { panel.showPopover(); positionPanel(panel, trigger); }
        else panel.hidePopover();
    };
    const items = () => [...root.querySelectorAll('a[href], button:not(:disabled), input:not(:disabled), select:not(:disabled)')]
        .filter(element => element.getClientRects().length > 0);
    const close = (restore) => { root.open = false; toggle(); if (restore) trigger.focus(); };
    const keydown = event => {
        if (event.key === 'Escape' && root.open) { event.preventDefault(); event.stopPropagation(); close(true); }
        if (['ArrowDown', 'ArrowUp', 'Home', 'End'].includes(event.key)) {
            event.preventDefault(); root.open = true; toggle();
            const entries = items();
            const index = entries.indexOf(document.activeElement);
            const next = event.key === 'Home' ? 0 : event.key === 'End' ? entries.length - 1
                : event.key === 'ArrowDown' ? (index + 1) % entries.length : (index < 0 ? entries.length : index) - 1;
            entries[(next + entries.length) % entries.length]?.focus();
        }
    };
    const click = event => { if (event.target.closest('a, button')) close(true); };
    const outside = event => { if (!root.contains(event.target)) close(false); };
    const focusout = event => { if (!root.contains(event.relatedTarget)) close(false); };
    root.addEventListener('keydown', keydown);
    root.addEventListener('toggle', toggle);
    root.addEventListener('click', click);
    root.addEventListener('focusout', focusout);
    document.addEventListener('pointerdown', outside);
    actions.set(root, () => {
        root.removeEventListener('keydown', keydown); root.removeEventListener('toggle', toggle); root.removeEventListener('click', click);
        root.removeEventListener('focusout', focusout); document.removeEventListener('pointerdown', outside);
    });
}
export function releaseActions(root) { actions.get(root)?.(); actions.delete(root); }

const disclosures = new WeakMap();
export function attachDisclosure(root) {
    if (!root?.isConnected) return;
    const trigger = root.querySelector('button');
    const panel = root.querySelector('[popover]');
    if (!trigger || !panel) return;
    let pinned = false;
    const show = () => { panel.showPopover(); positionPanel(panel, trigger); trigger.setAttribute('aria-expanded', 'true'); };
    const hide = () => { panel.hidePopover(); trigger.setAttribute('aria-expanded', 'false'); };
    const enter = event => { if (event.pointerType === 'mouse') show(); };
    const leave = () => { if (!pinned && document.activeElement !== trigger) hide(); };
    const blur = () => { pinned = false; hide(); };
    const click = () => { pinned = !pinned; if (pinned) show(); else hide(); };
    const keydown = event => { if (event.key === 'Escape') { event.preventDefault(); event.stopPropagation(); pinned = false; hide(); } };
    const outside = event => { if (!root.contains(event.target)) { pinned = false; hide(); } };
    trigger.addEventListener('pointerenter', enter); trigger.addEventListener('pointerleave', leave);
    trigger.addEventListener('focus', show); trigger.addEventListener('blur', blur);
    trigger.addEventListener('click', click); trigger.addEventListener('keydown', keydown);
    document.addEventListener('pointerdown', outside);
    disclosures.set(root, () => {
        trigger.removeEventListener('pointerenter', enter); trigger.removeEventListener('pointerleave', leave);
        trigger.removeEventListener('focus', show); trigger.removeEventListener('blur', blur);
        trigger.removeEventListener('click', click); trigger.removeEventListener('keydown', keydown);
        document.removeEventListener('pointerdown', outside);
    });
}
export function releaseDisclosure(root) { disclosures.get(root)?.(); disclosures.delete(root); }
