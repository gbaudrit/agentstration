window.agentstrationEnrollment = {
    copyAndOpen: async function (code, uri) {
        let copied = false;
        try {
            await navigator.clipboard.writeText(code);
            copied = true;
        } catch (_) {
            copied = false;
        }
        window.open(uri, "_blank", "noopener,noreferrer");
        return copied;
    },
    copy: async function (code) {
        await navigator.clipboard.writeText(code);
    },
    open: function (uri) {
        window.open(uri, "_blank", "noopener,noreferrer");
    }
};
