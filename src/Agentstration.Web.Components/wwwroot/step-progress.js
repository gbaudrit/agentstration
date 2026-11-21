const listeners = new WeakMap();
const navigationKeys = new Set(["ArrowDown", "ArrowUp", "Home", "End"]);

function reveal(container, node) {
    if (!container || !node) return;
    const box = container.getBoundingClientRect(), item = node.getBoundingClientRect();
    if (item.top < box.top) container.scrollTop += item.top - box.top - 8;
    else if (item.bottom > box.bottom) container.scrollTop += Math.min(item.top - box.top - 8, item.bottom - box.bottom + 8);
}

export function attach(root, reference) {
    detach(root);
    let frame = 0, selected, paused = false;
    const observed = new Set([root]);
    const sync = () => {
        frame = 0;
        const layout = root.querySelector(".step-progress-layout");
        if (!layout || !root.classList.contains("fit-viewport")) return;
        const available = Math.max(240, (window.visualViewport?.height ?? window.innerHeight) - Math.max(0, layout.getBoundingClientRect().top) - 20);
        const height = `${Math.floor(available)}px`;
        if (root.style.getPropertyValue("--step-progress-available") !== height) root.style.setProperty("--step-progress-available", height);
        if (root.dataset.followActive !== "true") { paused = false; return; }
        if (paused) return;
        const panel = root.querySelector("[data-selected-step]");
        const current = panel?.querySelector('[data-progress-current="true"]');
        const tabs = root.querySelector("[data-step-progress-tabs]");
        const targets = new Set([root, panel, tabs, current].filter(Boolean));
        for (const node of observed) if (!targets.has(node)) { resize.unobserve(node); observed.delete(node); }
        for (const node of targets) if (!observed.has(node)) { resize.observe(node); observed.add(node); }
        reveal(tabs, tabs?.querySelector('[aria-selected="true"]'));
        if (selected !== panel?.dataset.selectedStep && panel) panel.scrollTop = 0;
        selected = panel?.dataset.selectedStep;
        reveal(panel, current);
    };
    const schedule = () => { if (!frame) frame = requestAnimationFrame(sync); };
    const pause = event => {
        if (!root.classList.contains("fit-viewport") || root.dataset.followActive !== "true" || paused) return;
        if (!event.target.closest?.(".step-progress-panel, [data-step-progress-tabs]")) return;
        paused = true;
        reference?.invokeMethodAsync("PauseFollowingAsync").catch(() => {});
    };
    const interaction = event => {
        const container = event.target.closest?.(".step-progress-panel, [data-step-progress-tabs]");
        if (!container) return;
        const box = container.getBoundingClientRect();
        const scrollbar = event.target === container && (event.clientX >= box.left + container.clientLeft + container.clientWidth
            || event.clientY >= box.top + container.clientTop + container.clientHeight);
        if (scrollbar || event.target.closest?.("button, a, input, select, textarea, summary, [contenteditable=true]")) pause(event);
    };
    const listener = event => {
        if (event.key !== "Tab" && event.target.closest?.(".step-progress-panel")) pause(event);
        const list = event.target.closest?.("[data-step-progress-tabs]");
        if (list?.closest("[data-step-progress]") !== root
            || !event.target.matches("button[role=tab]")
            || event.altKey || event.ctrlKey || event.metaKey || event.shiftKey) return;
        if (navigationKeys.has(event.key)) event.preventDefault();
    };
    root.addEventListener("keydown", listener);
    root.addEventListener("wheel", pause, { passive: true });
    root.addEventListener("touchmove", pause, { passive: true });
    root.addEventListener("pointerdown", interaction);
    const mutation = new MutationObserver(records => {
        // A pause and explicit resume can be rendered before the next animation frame.
        if (records.some(record => record.target === root && record.attributeName === "data-follow-active")) paused = false;
        schedule();
    });
    mutation.observe(root, { attributes: true, childList: true, characterData: true, subtree: true, attributeFilter: ["data-follow-active", "data-active-step", "data-progress-key", "data-selected-step", "data-progress-current", "data-progress-resource", "aria-selected"] });
    const resize = new ResizeObserver(schedule);
    resize.observe(root);
    window.addEventListener("resize", schedule);
    window.addEventListener("scroll", schedule, { passive: true });
    window.visualViewport?.addEventListener("resize", schedule);
    schedule();
    listeners.set(root, () => {
        root.removeEventListener("keydown", listener);
        root.removeEventListener("wheel", pause);
        root.removeEventListener("touchmove", pause);
        root.removeEventListener("pointerdown", interaction);
        mutation.disconnect(); resize.disconnect();
        window.removeEventListener("resize", schedule);
        window.removeEventListener("scroll", schedule);
        window.visualViewport?.removeEventListener("resize", schedule);
        if (frame) cancelAnimationFrame(frame);
    });
}

export function detach(root) {
    listeners.get(root)?.();
    listeners.delete(root);
}
