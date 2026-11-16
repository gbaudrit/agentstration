// Neutral semantic export is independent of any renderer.
export function download(json) {
    const url = URL.createObjectURL(new Blob([json], { type: 'application/json' }));
    const link = document.createElement('a');
    link.href = url;
    link.download = 'system-graph.json';
    link.click();
    setTimeout(() => URL.revokeObjectURL(url), 0);
}
