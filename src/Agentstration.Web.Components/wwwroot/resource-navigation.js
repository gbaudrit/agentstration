// Only section history belongs here. Resource/context navigation remains owned by Blazor.
export function navigate(location, replace) {
    const current = new URL(window.location.href), target = new URL(location, current);
    const identity = url => {
        const query = new URLSearchParams(url.search);
        query.delete('view'); query.delete('tab');
        return url.origin + url.pathname + '?' + query.toString();
    };
    if (identity(current) !== identity(target)) return current.href;
    if (replace) {
        window.history.replaceState(window.history.state, '', target.href);
        window.dispatchEvent(new Event('hashchange'));
    } else {
        if (current.search !== target.search) {
            current.search = target.search;
            window.history.replaceState(window.history.state, '', current.href);
        }
        // Native hash navigation produces the expected history/hash events even
        // when Blazor optimizes same-document fragment navigation.
        window.location.hash = target.hash;
    }
    return window.location.href;
}

export function currentLocation() { return window.location.href; }

export function observe(root, reference) {
    if (!root?.isConnected) return;
    let active = true;
    const send = (method, ...args) => {
        if (!active || !root.isConnected) return;
        reference.invokeMethodAsync(method, ...args).catch(error => {
            if (active && root.isConnected) console.error(error);
        });
    };
    const changed = () => send("ObserveLocation", window.location.href);
    const click = event => {
        const link = event.target.closest?.('a[data-resource-section]');
        if (!link || !root.contains(link) || event.defaultPrevented || event.button !== 0 || event.ctrlKey || event.metaKey || event.altKey || event.shiftKey) return;
        event.preventDefault();
        send("NavigateSection", link.dataset.resourceSection);
    };
    const media = window.matchMedia("(min-width: 900px)");
    const resized = () => send("BreakpointChanged", media.matches, !!root.querySelector('.resource-navigation-rail')?.contains(document.activeElement));
    root.addEventListener("click", click);
    window.addEventListener("hashchange", changed);
    window.addEventListener("popstate", changed);
    media.addEventListener("change", resized);
    return {
        focusRail() {
            const rail = root.querySelector('.resource-navigation-rail');
            const selected = [...(rail?.querySelectorAll('a[aria-current=page]') ?? []), ...(rail?.querySelectorAll('.active-branch button:not(:disabled), a[href], button:not(:disabled)') ?? [])].find(element => element.getClientRects().length);
            selected?.focus();
        },
        focusTrigger() { root.querySelector('.resource-navigation-trigger')?.focus(); },
        dispose() {
            active = false;
            root.removeEventListener("click", click);
            window.removeEventListener("hashchange", changed);
            window.removeEventListener("popstate", changed);
            media.removeEventListener("change", resized);
        }
    };
}
