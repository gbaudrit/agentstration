# Staged Artifact operational inspection

`/artifacts/staged/{id}` exposes StagedArtifactView, a temporary-copy/content/lease projection. Its public contract has no resource UID, editable metadata, namespace/owner, policy or YAML declaration. It therefore keeps the common operational ResourceDetailsTemplate with Overview, Content and Retention. It does not invent ResourceIdentityEditor fields, a ResourceDefinitionForm, owner badges, labels, Access, YAML or a creation wizard. Durable FlowRunArtifact remains a separate persisted Resource adopter.

Overview presents actual state, size and expiration/lease indicators before Identity, a wrapping Configuration collection and provenance. Provenance links preserve the administrative context without inferring an owner from a binding namespace. Content keeps its existing explicit read actions, bounded chunk/display limits, authorization, download coordinates and integrity behavior.

One ResourceSectionState owns accepted/visited sections. Typed loading, refreshing, retry, recoverable failure and lost-access/not-found states use the common feedback. Refresh retains a pending expiration (including invalid/empty input) and previously loaded content. Changed public metadata is buffered for explicit Discard; unchanged metadata does not raise a conflict. Input during a pending refresh is retained. Changed content identity/status invalidates the prior byte cache, including purge, without silently replacing retention input. Replaced IDs, context changes, lost read access and disposal reject/clear stale private content.

The local date/baseline and existing purge eligibility rules live in StagedArtifactRetentionModel; the adapter owns commands and TimeProvider. Retention requires resources/write plus artifacts/manage-retention; purge requires resources/delete plus artifacts/purge, matching both endpoint and application authorization. Their capabilities stay independent. Monotonic extension, binding limits, leases and purge confirmations remain server-owned and unchanged. The retained local input joins a single leave guard; successful purge clears it before navigation so there is no second discard prompt.

All 45 focused component cases, TypeScript and the three actual Release hosts pass with zero warnings/errors. Final native English/dark desktop and French/light true-touch probes pass: real local metadata/content, long names, retained bytes and expiration across sections/unchanged and concurrent refresh, explicit discard, actual retention extension without content/provenance changes, exact administrative context, Escape/focus return, cancellation and confirmed purge without a second leave prompt, and no horizontal document overflow. The maintained artifact-content desktop/mobile journeys pass against the exact same source and host hashes. Four browser cases pass in total. The 14 PNGs are unmodified screenshots of the running application; [verification.json](verification.json) records the tested base and hashes. Initial probes needed canonical fixture IDs and an unambiguous primary-button selector; no product assertion was removed and no timeout was extended. No shared component or protected Agent source/style/translation is changed. Existing artifact-content desktop/mobile assertions are maintained and extended to verify that refresh keeps the selected Content section and loaded bytes. New reusable transverse scenario #1236 remains deferred until explicit maintainer UX validation and implementation merge. CI failures are outside this work by maintainer request.


- [staged-artifact-overview-desktop-en.png](staged-artifact-overview-desktop-en.png)
- [staged-artifact-retention-desktop-en.png](staged-artifact-retention-desktop-en.png)
- [staged-artifact-content-desktop-en.png](staged-artifact-content-desktop-en.png)
- [staged-artifact-retained-refresh-desktop-en.png](staged-artifact-retained-refresh-desktop-en.png)
- [staged-artifact-retention-saved-desktop-en.png](staged-artifact-retention-saved-desktop-en.png)
- [staged-artifact-purge-cancel-desktop-en.png](staged-artifact-purge-cancel-desktop-en.png)
- [staged-artifact-purged-desktop-en.png](staged-artifact-purged-desktop-en.png)
- [staged-artifact-overview-mobile-fr.png](staged-artifact-overview-mobile-fr.png)
- [staged-artifact-retention-mobile-fr.png](staged-artifact-retention-mobile-fr.png)
- [staged-artifact-content-mobile-fr.png](staged-artifact-content-mobile-fr.png)
- [staged-artifact-retained-refresh-mobile-fr.png](staged-artifact-retained-refresh-mobile-fr.png)
- [staged-artifact-retention-saved-mobile-fr.png](staged-artifact-retention-saved-mobile-fr.png)
- [staged-artifact-purge-cancel-mobile-fr.png](staged-artifact-purge-cancel-mobile-fr.png)
- [staged-artifact-purged-mobile-fr.png](staged-artifact-purged-mobile-fr.png)

Parallel function catalog collection adoption was synchronized before this commit. The verification records the actual tested base and unchanged migration sources; it does not claim coverage of that parallel collection increment.
