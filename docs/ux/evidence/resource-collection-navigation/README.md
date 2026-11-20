# Resource collection navigation — real application evidence

The PNGs come from the running application with deterministic disposable local data, not a mockup. English dark desktop uses 1440 × 1000; French light touch uses 390 × 844. Four states per mode: creation header, persisted resource header with long identity, guarded invalid-draft list return, and missing-resource list return.

Both native browser probes passed. Desktop uses keyboard Enter and Escape, touch uses actual `tap()`. Assertions retain exact logical context without adding the inspected owner to list filters, confirm final action placement, invalid parser input after cancel/Escape and focus return, explicit discard, exact list navigation, missing-resource return, and no horizontal overflow. Target size is 32 × 32 CSS px on desktop and 44 × 44 on touch. The accessible name/tooltip is the localized destination.

Mobile captures use the viewport: the installed Chromium 154 resets touch emulation after a full-page capture, reproduced independently on an empty page. This keeps the subsequent real touch checks active. Product CSS is not changed to compensate for capture tooling.

`evidence.json` records capture hashes, actual pointer/target measurements and the changed product-source hashes. Host assemblies were built before the campaign and remained unchanged throughout. Full test logs, Playwright reports and host hashes are under `/tmp/list-navigation-validation` in the session workspace.

| State | English desktop | French touch |
| --- | --- | --- |
| Creation | [Capture](creation-list-link-desktop-en.png) | [Capture](creation-list-link-mobile-fr.png) |
| Resource | [Capture](resource-list-link-desktop-en.png) | [Capture](resource-list-link-mobile-fr.png) |
| Unsaved guard | [Capture](guarded-list-return-desktop-en.png) | [Capture](guarded-list-return-mobile-fr.png) |
| Missing resource | [Capture](missing-resource-list-return-desktop-en.png) | [Capture](missing-resource-list-return-mobile-fr.png) |

The final maintained Agent journey also passes on desktop English/dark and mobile French/light. The maintained Flow/Entry publication journey passes on desktop. These three cases and the two native return-navigation probes are recorded in [validation.json](validation.json). The original combined run exposed obsolete Agent identity/panel assertions; those assertions were updated to the validated parent layout and the complete Agent journey was rerun successfully.
