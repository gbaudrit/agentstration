# Durable Artifact: persisted, immutable resource inspection

The route `/artifacts/durable/{Id}` consumes the actual persisted FlowRunArtifactResource, including its saved metadata, namespace, UID and Workspace owner. It now uses ConsoleResourceDetailsTemplate, one ResourceSectionState, the same section-presentation dictionary and one retained read-only ResourceDefinitionForm with real model Fields. Standard Overview/Definition Identity/Configuration precede specialized Content/Storage, independently authorized Access and final YAML. Useful saved metrics precede Identity; saved labels remain inside Identity. The common identity editor exposes its real metadata without inventing a definition-save, activation or creation command. Artifact ID is a family enrichment through AdditionalPanels.

The receipt's opaque storage locator must never be disclosed. YAML deliberately exports only reviewed public metadata, identity, media/size/digest and provenance IDs; localized help identifies this as inspection metadata, not an importable full resource definition. It excludes content, opaque locators and unreviewed receipt/provenance payloads. Existing storage/provenance, contract evidence, record reads, materialization and bounded content/download contracts are retained. Definition immutability does not disable independent content/materialization or policy authorization.

Shared typed loading/refresh/error/retry replaces custom state branches. Recoverable refresh retains visited inspection, content and policy drafts. Lost access/missing/replaced identity clears all private mounted content and leave protection. Original Artifact ID/UID/address/owner are checked; superseded/disposed reads are ignored. A pending record read cannot repopulate a replaced inspection. Execution links preserve administrative navigation context. No backend/API/business counter changes.

Validation:

- **28 focused component cases pass**, including EN/FR common form/state/dictionary/Field contracts, safe YAML, read-only controls, retained content/EditContext/independent policy drafts, refresh failure/retry, 401/403/404 clearing, replaced UID and superseded loads, alongside existing bounded content/materialization/record tests.
- Component/test and actual Web, Workplace and GovernedInventoryHost Release builds complete with zero warnings/errors. TypeScript passes. No shared or protected Agent product source changes; protected hashes are verified.
- Both final EN/dark desktop and FR/light genuine-touch probes pass. They create and persist a real artifact through ordinary APIs, inspect its exact saved owner/UID/qualified identity, locked Definition, safe YAML and Storage; request a real local materialization before inspecting its context-preserving download, refresh retained Content, inspect actual generic policy context, and verify strict document bounds and touch Escape/focus return. Date/time are split between metric value/detail to avoid an oversized mobile metric.
- Both maintained artifact-content desktop/mobile cases pass against identical source/host hashes: bounded reads, safe preview, downloads, materialization/history, denied metadata-only content and real integrity/missing-content failures. The first ad hoc probe used textarea text instead of value, then expected a download before requesting a local copy; only those probe preparations were corrected. No assertion, product permission or timeout was weakened to pass.
- Recoverable-refresh/privacy fault injection is component-tested; no browser outage or policy write is claimed. [Exact tested source/host hashes and results](source-proof.json). Parallel list work is preserved and fetched/integrated before commit, then synchronized afterward. No CI failure is inspected/remediated and no full solution suite is claimed. The reusable transverse journey remains deferred under open/blocked #1236.

Real application captures:

| State | Desktop EN/dark | Touch FR/light |
| --- | --- | --- |
| Saved overview | [Overview](durable-artifact-overview-desktop-en.png) | [Overview](durable-artifact-overview-mobile-fr.png) |
| Locked Definition | [Configuration](durable-artifact-definition-desktop-en.png) | [Configuration](durable-artifact-definition-mobile-fr.png) |
| Reviewed public metadata | [YAML](durable-artifact-yaml-desktop-en.png) | [YAML](durable-artifact-yaml-mobile-fr.png) |
| Materialized Content after refresh | [Content](durable-artifact-content-desktop-en.png) | [Content](durable-artifact-content-mobile-fr.png) |
| Independent policy inspection | [Access](durable-artifact-access-desktop-en.png) | [Access](durable-artifact-access-mobile-fr.png) |
