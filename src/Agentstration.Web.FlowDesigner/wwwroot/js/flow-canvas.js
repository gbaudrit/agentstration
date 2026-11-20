export function attachCtrlWheelZoom(element, dotNetReference) {
    if (!element?.isConnected) return { dispose() {} };
    let pendingDelta = 0;
    let animationFrame = 0;

    const flush = () => {
        animationFrame = 0;
        const delta = pendingDelta;
        pendingDelta = 0;
        if (delta !== 0 && element.isConnected) {
            void dotNetReference.invokeMethodAsync('ApplyCtrlWheelZoom', delta);
        }
    };

    const onWheel = event => {
        if (!event.ctrlKey) return;

        event.preventDefault();
        event.stopPropagation();
        pendingDelta += event.deltaY;
        if (animationFrame === 0) {
            animationFrame = requestAnimationFrame(flush);
        }
    };

    element.addEventListener('wheel', onWheel, { passive: false });

    return {
        dispose() {
            element.removeEventListener('wheel', onWheel);
            if (animationFrame !== 0) cancelAnimationFrame(animationFrame);
        }
    };
}
