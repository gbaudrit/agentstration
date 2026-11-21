# Installed packs Actions and Preview smoke

Four variants per fixture passed: desktop 1440 × 1000 / mobile 390 × 844, English/light and French/dark on desktop, English/dark and French/light on mobile. [observations.json](observations.json) records the exact checks and normalized source hashes. Captures use synthetic disposable fixtures and saved resource metadata.

Every case starts from a cold exact Preview URL before visiting an embedded catalog, then exercises applicable row-owned menus, keyboard Escape/focus, mobile touch, independent disclosures, hidden configuration selector, panel/document bounds, reachable close, native Open and Back, applicable authorized Edit and Back, reload and native fragment reopening. Read-only cases assert the absence of Edit. Browser page errors, actionable circuit errors and the Blazor error UI are checked. No Tool invocation, model generation, resource deletion or binary-content fetch occurs.

Component/API tests independently verify exact owner/namespace/name reads, cancellation, stale/mismatched responses and authorization. These ad hoc smoke captures do not implement or complete the gated reusable journey in #1258; maintainer UX validation and owning UX PR merge remain required.

- [pack-en-US-light-1440](pack-en-US-light-1440.png)
- [pack-fr-FR-dark-1440](pack-fr-FR-dark-1440.png)
- [pack-en-US-dark-390](pack-en-US-dark-390.png)
- [pack-fr-FR-light-390](pack-fr-FR-light-390.png)
