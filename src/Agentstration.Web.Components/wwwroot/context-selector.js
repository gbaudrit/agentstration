export async function selectWorkspace(workspaceId) {
    const response = await fetch('/bff/identity/context/workspace', {
        method: 'POST',
        credentials: 'same-origin',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ workspaceId })
    });
    if (!response.ok) {
        const problem = await response.json().catch(() => null);
        throw new Error(problem?.detail ?? 'The workspace could not be selected.');
    }
    window.location.reload();
}

export async function selectContext(scopeRef, confirmationMessage) {
    if (document.querySelector('.unsaved-changes-bar') && !window.confirm(confirmationMessage)) {
        return false;
    }
    const response = await fetch('/bff/identity/context/selection', {
        method: 'POST',
        credentials: 'same-origin',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ scopeRef })
    });
    if (!response.ok) {
        const problem = await response.json().catch(() => null);
        throw new Error(problem?.detail ?? 'The Console context could not be selected.');
    }
    const url = new URL(window.location.href);
    url.searchParams.set('context', scopeRef);
    window.location.assign(url.toString());
    return true;
}

export function ensureContextUrl(scopeRef) {
    const url = new URL(window.location.href);
    if (url.searchParams.get('context') === scopeRef) return;
    url.searchParams.set('context', scopeRef);
    window.history.replaceState(window.history.state, '', url.toString());
}
