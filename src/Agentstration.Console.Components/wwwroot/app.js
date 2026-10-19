window.agentstrationEnrollment = {
    theme: function () {
        const shell = document.querySelector(".app-shell");
        if (shell?.classList.contains("theme-dark")) {
            return "dark";
        }
        if (shell?.classList.contains("theme-light")) {
            return "light";
        }
        return null;
    },
    themedUri: function (uri) {
        const target = new URL(uri);
        const theme = window.agentstrationEnrollment.theme();
        if (theme) {
            target.searchParams.set("theme", theme);
        }
        return target.toString();
    },
    postAndOpen: function (code, uri) {
        const popup = window.open("about:blank", "_blank");
        if (!popup) {
            return false;
        }
        const targetName = `agentstration-enrollment-${Date.now()}-${Math.random().toString(16).slice(2)}`;
        popup.name = targetName;
        popup.opener = null;

        const form = document.createElement("form");
        form.method = "post";
        form.action = uri;
        form.target = targetName;
        form.rel = "noopener noreferrer";

        const addField = (name, value) => {
            const input = document.createElement("input");
            input.type = "hidden";
            input.name = name;
            input.value = value;
            form.appendChild(input);
        };
        addField("code", code);
        const theme = window.agentstrationEnrollment.theme();
        if (theme) {
            addField("theme", theme);
        }

        document.body.appendChild(form);
        form.submit();
        form.remove();
        return true;
    },
    copy: async function (code) {
        await navigator.clipboard.writeText(code);
    },
    open: function (uri) {
        window.open(window.agentstrationEnrollment.themedUri(uri), "_blank", "noopener,noreferrer");
    }
};
