(() => {
    function derive(displayName, maximumLength) {
        return displayName
            .normalize("NFD")
            .replace(/[\u0300-\u036f]/g, "")
            .toLowerCase()
            .replace(/[^a-z0-9]+/g, "-")
            .replace(/^-+|-+$/g, "")
            .slice(0, maximumLength)
            .replace(/-+$/g, "");
    }

    for (const displayName of document.querySelectorAll("[data-resource-display-name]")) {
        const technicalName = document.getElementById(displayName.dataset.technicalNameTarget);
        if (!technicalName) continue;

        let automatic = technicalName.value.length === 0;
        technicalName.addEventListener("input", () => automatic = false);
        displayName.addEventListener("input", () => {
            if (!automatic) return;
            technicalName.value = derive(displayName.value, Number(displayName.dataset.maximumLength));
        });
    }
})();
