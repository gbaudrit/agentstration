# Compact Flow collection prototype

This prototype applies only to Console `/flows` (Definitions) under #1121. Wider rollout awaits explicit UX validation. Other collection families keep their existing presentation and all primary/optional-secondary action behavior is retained.

The four columns are Resource, Version, Updated and Actions. The resource keeps its exact owner/context-aware display-name link, namespace/name and independent copy; the folder is omitted. Existing family artwork is retained. Scope, state and independent managed/read-only indicators sit beside the identity; Flow kind remains visible beside the name in both desktop and mobile views.

Scope uses existing `server` (Instance), `building-community` (Tenant) and `layout-dashboard` (Workspace) icons. Shared disclosure behavior exposes the complete authorized type/name on hover, keyboard focus and touch activation, with Escape dismissal and an accessible name. Names come from the authorized context snapshot, without per-row requests. Unknown states remain neutral and use a symbol as well as tone.

The Version cell is a two-column grid: numbers share the same start position and the draft pencil occupies a reserved trailing slot. The first line is the active version, or an existing legacy definition version where applicable; a dash denotes no active version for a draft-only Flow. A real Flow Draft currently has a revision, **not a saved future semantic publication version**. Its second line therefore displays `rN`; the full localized `Draft, revision N` meaning is exposed to assistive technology and through the pencil disclosure. Existence is never inferred from equal/different version numbers. A bounded batch query loads exact workspace/owner/namespace draft revisions for only the visible server page, with equivalent SQLite/PostgreSQL implementations.

Pagination requests only the selected 20/50/100 rows. Previous/Next derives continuation from the API's `nextLink`; the API exposes no total, so no total is fabricated. Changing size returns to page one; selected table/card presentation survives page changes. While loading controls are disabled. A failed request preserves current rows and page, and Retry repeats the failed request. The page-size selector reuses FormField/ClearableSelect and is included in the form adoption catalog.

## Validation

- Release solution build: zero warnings/errors; all ten browser hosts rebuilt before browser validation.
- Required Fast lane: 988 passed, zero failed/skipped.
- Focused Flow API/storage checks: 2 passed, including one-query batch bounds and workspace/owner/namespace isolation.
- Component regressions cover sparse/draft versions, authorized long-owner disclosure, exact links, 20/50 pagination, retained card view, unknown total and failed-request retry.
- TypeScript, six collection JavaScript lifecycle tests, five host fixture tests and four form/application catalog checks passed.
- Full integration, current-head browser checks and actual-application screenshots are recorded in the delivery updates on draft PR #1123 and stacked draft #1132.

## Adoption boundary

The existing CollectionView, CollectionIdentity, CollectionVersion, ResourceScopeBadge, StatusBadge, ManagedResourceBadge and ValueDisclosure gain opt-in presentation options. Family loading/commands remain in adapters. This does not introduce a column configuration engine or migrate the other collection surfaces before the Flow UX is validated. The existing #1122 follow-up remains responsible for reusable cross-family assertions; UX acceptance and both merges remain pending.
