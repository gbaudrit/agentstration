# Storage creation and delegated definition form

StorageEditor now composes ResourceDefinitionForm and the standard ResourceIdentityEditor. Its retained owning-host contract accepts one ResourceSectionState and the exact section presentation dictionary, with actual root model fields for Identity, Configuration and descendant-use policy. Creation keeps the freely navigable Identity / Configuration / descendant-use policy / Review CreationWizard. The existing final eligibility guards stay in place: a missing/unavailable type, incomplete exact bindings, invalid schemas or invalid document collections still prevent Finish. Identity validation returns to its step and common first-error focus. The fixed creation namespace from the route/query and sole current owner remain explicit, including the existing default-namespace semantics. Properties-only metadata shows its actual saved labels read-only inside Identity without inventing a label write.

Pinned type and service selections show their readable resource name, exact qualified identity, preserved navigation context and version/generation. Contract UID/digests and provenance stay available in an expandable contract disclosure; the existing permission explanation uses the field help tooltip. Manual-mapping guidance is shown with the service configuration to which it applies.

Previous name/identity rules, type/service eligibility, exact export compatibility and binding-change checks now live in the family StorageEditorModel. The adapter retains catalog loading, permission checks, schema/collection validators, ETags, confirmations and Create/Update/Reconcile commands. Existing type UID/version/schema/contract digests, relative/scoped references, service UID/generation/type, export mappings, collection IDs, schema bindings and policies are preserved. An unavailable saved exact type remains inspectable and metadata-editable without silently choosing another contract. Service catalog failure still permits local creation. Rebind and disable confirmations retain their previous semantics; no business counter or limit changes.

Bounded schema and collection editors retain invalid raw input. Their existing validity reports map to the real Configuration/Envelope/Collections fields without duplicate error summaries or weakening their validation. The form maps identity errors to their actual controls; structured schema errors keep their own inline feedback. The six collection-policy options reuse the existing binary bindings with true switch styling and label targets, family-scoped alignment and read-only fieldset behavior. This family-only component is consumed by StorageEditor, rather than the common Agent foundation.

The delegated form retains its EditContext, invalid values, baseline and original ETag when a newer snapshot arrives. Explicit discard adopts a verified same-UID/address/owner snapshot; a clean form can accept an incoming snapshot. Typed loading/retry/forbidden/not-found states and stale/disposed checks protect accepted identity. Existing operational snapshot reconciliation keeps unrelated drafts while updating enablement and ETag. Save callbacks expose their actual returned snapshot to the upcoming owning-host migration without an extra GET. StorageDetails remains a separate pending screen increment; this commit does not count its legacy host as migrated.

55 targeted component/localization cases, TypeScript and all three actual Release hosts pass with zero warnings/errors. Final English/dark desktop and French/light true-touch probes pass: incomplete navigation, identity first-error focus, retained invalid raw configuration, exact namespace/current owner/type UID/version/digests, six actual styled collection-policy switches and toggle persistence, subsequent valid creation and server-assigned collection UID. Existing Storage/Connected Service desktop/mobile smoke passes on the identical source/host hashes: four browser cases pass. The initial native probe assumed implicit Enter submission in a wizard whose Finish is button-driven; the probe now uses actual step navigation and retains the disabled-Finish, raw-retention, keyboard-focus and real-save assertions. No product change or timeout extension was used for that probe correction. The 14 PNGs are unmodified captures of the running application; [verification.json](verification.json) records the actual tested base, source/host hashes and matching maintained-regression campaign.

No shared-foundation component or protected Agent source/style/translation change. Existing Storage/Connected Service browser smoke stays maintained. New reusable #1236 coverage remains deferred until explicit UX validation and merge. Full solution suites are omitted in favor of focused checks and affected desktop/mobile smoke at maintainer request. CI errors were neither inspected nor handled.


- [storage-editor-invalid-identity-desktop-en.png](storage-editor-invalid-identity-desktop-en.png)
- [storage-editor-identity-desktop-en.png](storage-editor-identity-desktop-en.png)
- [storage-editor-invalid-configuration-desktop-en.png](storage-editor-invalid-configuration-desktop-en.png)
- [storage-editor-collections-desktop-en.png](storage-editor-collections-desktop-en.png)
- [storage-editor-use-policy-desktop-en.png](storage-editor-use-policy-desktop-en.png)
- [storage-editor-review-desktop-en.png](storage-editor-review-desktop-en.png)
- [storage-editor-created-desktop-en.png](storage-editor-created-desktop-en.png)
- [storage-editor-invalid-identity-mobile-fr.png](storage-editor-invalid-identity-mobile-fr.png)
- [storage-editor-identity-mobile-fr.png](storage-editor-identity-mobile-fr.png)
- [storage-editor-invalid-configuration-mobile-fr.png](storage-editor-invalid-configuration-mobile-fr.png)
- [storage-editor-collections-mobile-fr.png](storage-editor-collections-mobile-fr.png)
- [storage-editor-use-policy-mobile-fr.png](storage-editor-use-policy-mobile-fr.png)
- [storage-editor-review-mobile-fr.png](storage-editor-review-mobile-fr.png)
- [storage-editor-created-mobile-fr.png](storage-editor-created-mobile-fr.png)
