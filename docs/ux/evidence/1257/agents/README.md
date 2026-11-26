# Agents Actions and Preview smoke

Four variants per fixture passed: desktop 1440 × 1000 / mobile 390 × 844, English/light and French/dark on desktop, English/dark and French/light on mobile. [observations.json](observations.json) records the exact checks and normalized source hashes. Captures use synthetic disposable fixtures and saved resource metadata.

Every case starts from a cold exact Preview URL before visiting an embedded catalog, then exercises applicable row-owned menus, keyboard Escape/focus, mobile touch, independent disclosures, hidden configuration selector, panel/document bounds, reachable close, native Open and Back, applicable authorized Edit and Back, reload and native fragment reopening. Read-only cases assert the absence of Edit. Browser page errors, actionable circuit errors and the Blazor error UI are checked. No Tool invocation, model generation, resource deletion or binary-content fetch occurs.

The namespaced Agent was installed through the native Pack installation API and retains its managed/read-only governance. The default-namespace Agent remains editable in its owner context.

Component/API tests independently verify exact owner/namespace/name reads, cancellation, stale/mismatched responses and authorization. These ad hoc smoke captures do not implement or complete the gated reusable journey in #1258; maintainer UX validation and owning UX PR merge remain required.

- [agent-en-US-light-1440](agent-en-US-light-1440.png)
- [agent-pack-en-US-light-1440](agent-pack-en-US-light-1440.png)
- [agent-fr-FR-dark-1440](agent-fr-FR-dark-1440.png)
- [agent-pack-fr-FR-dark-1440](agent-pack-fr-FR-dark-1440.png)
- [agent-en-US-dark-390](agent-en-US-dark-390.png)
- [agent-pack-en-US-dark-390](agent-pack-en-US-dark-390.png)
- [agent-fr-FR-light-390](agent-fr-FR-light-390.png)
- [agent-pack-fr-FR-light-390](agent-pack-fr-FR-light-390.png)
