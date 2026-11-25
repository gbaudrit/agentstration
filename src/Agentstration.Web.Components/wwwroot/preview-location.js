export function observePreviewLocation(receiver) {
    let disposed = false;
    const changed = () => {
        if (!disposed) receiver.invokeMethodAsync('RestoreBrowserLocation', window.location.href)
            .catch(() => { /* The circuit can end while a native navigation is in flight. */ });
    };
    // Detail section navigation routes native hash pushes through Blazor, whose
    // accepted same-document entry change need not raise hashchange/LocationChanged.
    // Traversals remain owned by Blazor's guarded LocationChanged notification.
    const entryChanged = event => { if (event.navigationType !== 'traverse') changed(); };
    const collection = new URL(window.location.href);
    const nativePreview = event => {
        if (disposed || event.defaultPrevented || !event.hashChange || !event.cancelable || event.navigationType === 'traverse') return;
        const current = new URL(window.location.href), target = new URL(event.destination.url);
        if (current.origin !== collection.origin || current.pathname !== collection.pathname
            || target.origin !== current.origin || target.pathname !== current.pathname || target.search !== current.search
            || !target.hash.startsWith('#preview/') || target.hash.length > 2048) return;
        // A native hash entry has no Blazor history index. Let its public navigation
        // path apply guards and create the entry before the observer restores it.
        event.preventDefault();
        receiver.invokeMethodAsync('NavigatePreviewLocation', target.href).catch(() => { });
    };
    window.navigation?.addEventListener('navigate', nativePreview);
    window.navigation?.addEventListener('currententrychange', entryChanged);
    const legacyChanged = () => { if (!window.navigation) changed(); };
    window.addEventListener('hashchange', legacyChanged);
    window.addEventListener('popstate', legacyChanged);
    return {
        dispose() {
            disposed = true;
            window.navigation?.removeEventListener('navigate', nativePreview);
            window.navigation?.removeEventListener('currententrychange', entryChanged);
            window.removeEventListener('hashchange', legacyChanged);
            window.removeEventListener('popstate', legacyChanged);
        }
    };
}
