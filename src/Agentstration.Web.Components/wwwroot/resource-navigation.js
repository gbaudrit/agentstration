// Only section history belongs here. Resource/context navigation remains owned by Blazor.
export function navigate(location, replace, historyEntryState) {
    const current = new URL(window.location.href), target = new URL(location, current);
    const identity = url => {
        // Match ASP.NET query parsing: key comparison/grouping ignores case;
        // resource, owner and context VALUES remain exact and ordered per key.
        const groups = new Map();
        for (const [name, value] of url.searchParams) {
            const key = name.toLowerCase();
            if (key === 'view' || key === 'tab') continue;
            if (!groups.has(key)) groups.set(key, []);
            groups.get(key).push(value);
        }
        const query = new URLSearchParams();
        for (const [key, values] of groups) for (const value of values) query.append(key, value);
        return url.origin + url.pathname + '?' + query.toString();
    };
    if (identity(current) !== identity(target)) return current.href;
    // Drop obsolete selectors without creating a resource navigation. Preserve
    // Blazor's history state; its public navigation API owns section entries.
    if (current.search !== target.search) {
        current.search = target.search;
        window.history.replaceState(window.history.state, '', current.href);
    }
    if (current.href !== target.href) window.Blazor.navigateTo(target.href, { replaceHistoryEntry: replace, historyEntryState });
    return window.location.href;
}

export function currentLocation() { return window.location.href; }

export function observe(root, reference, externalTriggerId) {
    if (!root?.isConnected) return;
    let active = true;
    const send = (method, ...args) => {
        if (!active || !root.isConnected) return;
        reference.invokeMethodAsync(method, ...args).catch(error => {
            if (active && root.isConnected) console.error(error);
        });
    };
    // Blazor deliberately skips LocationChanged for same-document fragment
    // pushes. Observe public entry changes; guarded traversals are reported by
    // NavigationManager only after Blazor has accepted the destination.
    const entryChanged = event => {
        if (event.navigationType !== "traverse") send("ObserveLocation", window.location.href);
    };
    // A native location.hash push otherwise creates an entry outside Blazor's
    // navigation bookkeeping. With NavigationLock active, Back can then skip
    // entries or reload the document. Cancel that push before it commits and
    // let the same public Blazor path used by menu links create the entry.
    const nativeHash = event => {
        if (!active || !root.isConnected || !event.hashChange || !event.cancelable || event.navigationType === "traverse") return;
        event.preventDefault();
        send("NavigateLocation", event.destination.url);
    };
    window.navigation?.addEventListener("currententrychange", entryChanged);
    window.navigation?.addEventListener("navigate", nativeHash);
    const click = event => {
        const link = event.target.closest?.('a[data-resource-section]');
        if (!link || !root.contains(link) || event.defaultPrevented || event.button !== 0 || event.ctrlKey || event.metaKey || event.altKey || event.shiftKey) return;
        event.preventDefault();
        send("NavigateSection", link.dataset.resourceSection);
    };
    const media = window.matchMedia("(min-width: 900px)");
    let railHadFocus = false;
    const railFocused = () => !!root.querySelector('.resource-navigation-rail')?.contains(document.activeElement);
    const focused = () => { railHadFocus = media.matches && railFocused(); };
    const resized = () => {
        const transfer = railHadFocus || railFocused();
        if (!media.matches) railHadFocus = false;
        send("BreakpointChanged", media.matches, transfer);
    };
    document.addEventListener("focusin", focused);
    root.addEventListener("click", click);
    media.addEventListener("change", resized);
    return {
        focusRail() {
            const rail = root.querySelector('.resource-navigation-rail');
            const selected = [...(rail?.querySelectorAll('a[aria-current=page]') ?? []), ...(rail?.querySelectorAll('.active-branch button:not(:disabled), a[href], button:not(:disabled)') ?? [])].find(element => element.getClientRects().length);
            selected?.focus();
        },
        focusTrigger() { (externalTriggerId ? document.getElementById(externalTriggerId) : root.querySelector('.resource-navigation-trigger'))?.focus(); },
        dispose() {
            active = false;
            root.removeEventListener("click", click);
            media.removeEventListener("change", resized);
            document.removeEventListener("focusin", focused);
            window.navigation?.removeEventListener("currententrychange", entryChanged);
            window.navigation?.removeEventListener("navigate", nativeHash);
        }
    };
}
