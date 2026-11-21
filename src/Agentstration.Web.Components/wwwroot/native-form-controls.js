const selector = ".native-form-control";

function refresh(wrapper) {
    const field = wrapper.querySelector("input, textarea, select");
    const clear = wrapper.querySelector("[data-native-field-clear]");
    if (!field || !clear) return;
    clear.hidden = !field.value || field.matches(":disabled") || field.readOnly;
}

document.querySelectorAll(selector).forEach(refresh);
for (const event of ["input", "change"]) {
    document.addEventListener(event, e => {
        const wrapper = e.target.closest?.(selector);
        if (wrapper) refresh(wrapper);
    });
}
document.addEventListener("click", e => {
    const clear = e.target.closest?.("[data-native-field-clear]");
    if (!clear) return;
    const wrapper = clear.closest(selector);
    const field = wrapper?.querySelector("input, textarea, select");
    if (!field || field.matches(":disabled") || field.readOnly) return;
    e.preventDefault();
    field.value = "";
    field.dispatchEvent(new Event("input", { bubbles: true }));
    field.dispatchEvent(new Event("change", { bubbles: true }));
    field.focus();
    refresh(wrapper);
});
document.addEventListener("reset", e => {
    requestAnimationFrame(() => e.target.querySelectorAll(selector).forEach(refresh));
});

// Anonymous forms follow the system. Authenticated account forms use the same
// saved preference as the interactive shells when that endpoint is available.
const media = window.matchMedia("(prefers-color-scheme: dark)");
let preference = "system";
function applyTheme() {
    const dark = preference === "dark" || preference === "system" && media.matches;
    document.documentElement.dataset.theme = dark ? "dark" : "light";
    for (const image of document.querySelectorAll("[data-theme-light-src][data-theme-dark-src]")) {
        image.src = dark ? image.dataset.themeDarkSrc : image.dataset.themeLightSrc;
    }
}
media.addEventListener("change", applyTheme);
applyTheme();
if (document.body.dataset.authenticated === "true") {
    fetch("/api/identity/preferences", { credentials: "same-origin", redirect: "error" })
        .then(response => response.ok ? response.json() : null)
        .then(value => {
            const raw = String(value?.theme ?? "system").toLowerCase();
            const candidate = ({ "0": "system", "1": "light", "2": "dark" })[raw] ?? raw;
            if (["system", "light", "dark"].includes(candidate)) preference = candidate;
            applyTheme();
        }).catch(() => { /* Keep the system appearance when offline. */ });
}
