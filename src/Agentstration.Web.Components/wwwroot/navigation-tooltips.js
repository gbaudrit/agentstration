export function registerCollapsedNavigationTooltips(shell) {
    const tooltip = document.createElement("div");
    tooltip.className = "sidebar-nav-tooltip";
    tooltip.setAttribute("role", "tooltip");
    tooltip.hidden = true;
    document.body.appendChild(tooltip);

    let activeLink = null;

    const isCollapsedRail = () =>
        window.matchMedia("(min-width: 621px)").matches
        && (shell.classList.contains("sidebar-collapsed") || window.matchMedia("(max-width: 820px)").matches);

    const hide = () => {
        activeLink = null;
        tooltip.hidden = true;
    };

    const show = link => {
        if (!isCollapsedRail()) {
            hide();
            return;
        }

        const label = link.dataset.navTooltip;
        if (!label) return;

        activeLink = link;
        tooltip.textContent = label;
        tooltip.hidden = false;

        const linkBounds = link.getBoundingClientRect();
        tooltip.style.left = `${Math.round(linkBounds.right + 10)}px`;
        tooltip.style.top = `${Math.round(linkBounds.top + (linkBounds.height / 2))}px`;

        const tooltipBounds = tooltip.getBoundingClientRect();
        const halfHeight = tooltipBounds.height / 2;
        const safeTop = Math.min(
            window.innerHeight - halfHeight - 8,
            Math.max(halfHeight + 8, linkBounds.top + (linkBounds.height / 2)));
        tooltip.style.top = `${Math.round(safeTop)}px`;
    };

    const findLink = target => target instanceof Element ? target.closest(".side-nav a[data-nav-tooltip]") : null;
    const isInside = (link, target) => target instanceof Node && link.contains(target);
    const onPointerOver = event => {
        const link = findLink(event.target);
        if (link && !isInside(link, event.relatedTarget)) show(link);
    };
    const onPointerOut = event => {
        const link = findLink(event.target);
        if (link === activeLink && !isInside(link, event.relatedTarget)) hide();
    };
    const onFocusIn = event => {
        const link = findLink(event.target);
        if (link) show(link);
    };
    const onFocusOut = event => {
        const link = findLink(event.target);
        if (link === activeLink && !isInside(link, event.relatedTarget)) hide();
    };

    shell.addEventListener("pointerover", onPointerOver);
    shell.addEventListener("pointerout", onPointerOut);
    shell.addEventListener("focusin", onFocusIn);
    shell.addEventListener("focusout", onFocusOut);
    window.addEventListener("resize", hide);
    window.addEventListener("scroll", hide, true);

    return {
        dispose() {
            shell.removeEventListener("pointerover", onPointerOver);
            shell.removeEventListener("pointerout", onPointerOut);
            shell.removeEventListener("focusin", onFocusIn);
            shell.removeEventListener("focusout", onFocusOut);
            window.removeEventListener("resize", hide);
            window.removeEventListener("scroll", hide, true);
            tooltip.remove();
        }
    };
}
