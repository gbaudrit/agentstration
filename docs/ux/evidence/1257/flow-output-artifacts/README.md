# Flow output artifacts Actions and Preview smoke

Four variants per fixture passed: desktop 1440 × 1000 / mobile 390 × 844, English/light and French/dark on desktop, English/dark and French/light on mobile. [observations.json](observations.json) records the exact checks and normalized source hashes. Captures use synthetic disposable fixtures and saved resource metadata.

Every case starts from a cold exact Preview URL before visiting an embedded catalog, then exercises applicable row-owned menus, keyboard Escape/focus, mobile touch, independent disclosures, hidden configuration selector, panel/document bounds, reachable close, native Open and Back, applicable authorized Edit and Back, reload and native fragment reopening. Read-only cases assert the absence of Edit. Browser page errors, actionable circuit errors and the Blazor error UI are checked. No Tool invocation, model generation, resource deletion or binary-content fetch occurs.

Artifact fixtures are actual persisted metadata, attached to a pending native Flow Run. The deliberately unopened opaque backend locator proves that Summary inspection does not resolve binary content; no completed Flow execution or real binary storage run is claimed.

Component/API tests independently verify exact owner/namespace/name reads, cancellation, stale/mismatched responses and authorization. These ad hoc smoke captures do not implement or complete the gated reusable journey in #1258; maintainer UX validation and owning UX PR merge remain required.

- [flow-output-artifact-en-US-light-1440](flow-output-artifact-en-US-light-1440.png)
- [flow-output-artifact-fr-FR-dark-1440](flow-output-artifact-fr-FR-dark-1440.png)
- [flow-output-artifact-en-US-dark-390](flow-output-artifact-en-US-dark-390.png)
- [flow-output-artifact-fr-FR-light-390](flow-output-artifact-fr-FR-light-390.png)
