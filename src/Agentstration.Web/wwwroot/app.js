window.agentstrationEnrollment = {
    themedUri: function (uri) {
        const target = new URL(uri);
        const shell = document.querySelector(".app-shell");
        if (shell?.classList.contains("theme-dark")) {
            target.searchParams.set("theme", "dark");
        } else if (shell?.classList.contains("theme-light")) {
            target.searchParams.set("theme", "light");
        }
        return target.toString();
    },
    copyAndOpen: async function (code, uri) {
        let copied = false;
        try {
            await navigator.clipboard.writeText(code);
            copied = true;
        } catch (_) {
            copied = false;
        }
        window.open(window.agentstrationEnrollment.themedUri(uri), "_blank", "noopener,noreferrer");
        return copied;
    },
    copy: async function (code) {
        await navigator.clipboard.writeText(code);
    },
    open: function (uri) {
        window.open(window.agentstrationEnrollment.themedUri(uri), "_blank", "noopener,noreferrer");
    }
};
