export async function selectWorkspace(workspaceId) {
    const response = await fetch('/api/identity/context/workspace', {
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
