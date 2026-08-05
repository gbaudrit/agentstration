function resolveCanvas(canvasId) {
    if (typeof canvasId !== 'string') return null;
    const canvas = document.getElementById(canvasId);
    return canvas instanceof HTMLElement ? canvas : null;
}

export function attach(canvasId, dotnet) {
    const canvas = resolveCanvas(canvasId);
    if (!canvas || !dotnet || typeof dotnet.invokeMethodAsync !== 'function') return;
    for (const node of canvas.querySelectorAll('[data-flow-node]')) {
        if (node.dataset.dragReady === 'true') continue;
        node.dataset.dragReady = 'true';
        node.addEventListener('pointerdown', event => {
            if (event.button !== 0) return;
            const startX = event.clientX;
            const startY = event.clientY;
            const originX = Number.parseFloat(node.style.left) || 0;
            const originY = Number.parseFloat(node.style.top) || 0;
            node.setPointerCapture(event.pointerId);
            const move = current => {
                node.style.left = `${Math.max(0, originX + current.clientX - startX)}px`;
                node.style.top = `${Math.max(0, originY + current.clientY - startY)}px`;
            };
            const up = current => {
                node.removeEventListener('pointermove', move);
                node.removeEventListener('pointerup', up);
                node.releasePointerCapture(current.pointerId);
                dotnet.invokeMethodAsync('CommitMove', node.dataset.name, Number.parseFloat(node.style.left), Number.parseFloat(node.style.top));
            };
            node.addEventListener('pointermove', move);
            node.addEventListener('pointerup', up);
        });
    }
}

export function center(canvasId) {
    const canvas = resolveCanvas(canvasId);
    if (canvas) canvas.scrollTo({ left: 0, top: 0, behavior: 'smooth' });
}
