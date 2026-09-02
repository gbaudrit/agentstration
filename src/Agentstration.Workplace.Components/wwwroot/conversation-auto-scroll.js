const states = new WeakMap();
const bottomThreshold = 32;

function findScrollTarget(container) {
    let candidate = container.parentElement;
    while (candidate && candidate !== document.body) {
        const overflowY = getComputedStyle(candidate).overflowY;
        if ((overflowY === 'auto' || overflowY === 'scroll') && candidate.scrollHeight > candidate.clientHeight) {
            return { element: candidate, eventTarget: candidate };
        }
        candidate = candidate.parentElement;
    }

    return {
        element: document.scrollingElement || document.documentElement,
        eventTarget: window
    };
}

function distanceFromBottom(element) {
    return Math.max(0, element.scrollHeight - element.scrollTop - element.clientHeight);
}

function queueScroll(state) {
    if (!state.following || state.frame !== 0) return;
    state.frame = requestAnimationFrame(() => {
        state.frame = 0;
        if (!state.following) return;
        state.target.scrollTop = state.target.scrollHeight;
        state.lastScrollTop = state.target.scrollTop;
    });
}

export function initialize(container) {
    dispose(container);

    const target = findScrollTarget(container);
    const state = {
        target: target.element,
        eventTarget: target.eventTarget,
        following: true,
        lastScrollTop: target.element.scrollTop,
        frame: 0,
        mutationObserver: null,
        resizeObserver: null,
        onScroll: null
    };

    state.onScroll = () => {
        const currentScrollTop = state.target.scrollTop;
        if (currentScrollTop < state.lastScrollTop - 2) {
            state.following = false;
        } else if (distanceFromBottom(state.target) <= bottomThreshold) {
            state.following = true;
        }
        state.lastScrollTop = currentScrollTop;
    };

    state.eventTarget.addEventListener('scroll', state.onScroll, { passive: true });
    state.mutationObserver = new MutationObserver(() => queueScroll(state));
    state.mutationObserver.observe(container, { childList: true, subtree: true, characterData: true });

    if ('ResizeObserver' in window) {
        state.resizeObserver = new ResizeObserver(() => queueScroll(state));
        state.resizeObserver.observe(container);
    }

    states.set(container, state);
    queueScroll(state);
}

export function dispose(container) {
    const state = states.get(container);
    if (!state) return;

    state.eventTarget.removeEventListener('scroll', state.onScroll);
    state.mutationObserver.disconnect();
    state.resizeObserver?.disconnect();
    if (state.frame !== 0) cancelAnimationFrame(state.frame);
    states.delete(container);
}
