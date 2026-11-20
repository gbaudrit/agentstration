# Storage persisted resource screen

StorageDetails now composes ConsoleResourceDetailsTemplate with Overview, Definition/Identity and Configuration, the family descendant-use policy, Diagnostics and Dependents, independently authorized Access and final saved read-only YAML. Real availability/generation MetricCards precede saved Identity and tags. Missing observation stays neutral and explicitly “Not observed”; existing Ready/Stale/Unavailable/Disabled meanings are preserved. Saved configuration uses the common wrapping setting/value CollectionView and includes the actual type/version, enabled state, configuration, envelope, structured collections and use policy. Pinned type/service references keep exact namespace/owner/context destinations. The connection and export mappings remain useful Overview content; requirement names are plain settings rather than fictional resources. Full contract UID/digests/provenance remain inspectable in Diagnostics and complete YAML. No connected-service options or resolved secret contents are exposed.

The owning template and delegated ResourceDefinitionForm share one ResourceSectionState and the exact presentation dictionary, including actual root fields for specialized JSON/collection validation. The common form retains invalid identity/numeric/raw schema fields and their EditContext through section changes and refresh. Incoming changed snapshots are buffered; drafts retain their original ETag and saved overview until explicit discard. An unchanged refresh does not announce a conflict, and edits typed while a read is pending are evaluated at response time. Definition saves apply the returned snapshot without an additional GET. Lost read access, deletion/replaced UID/address/owner, superseded and disposed completions cannot restore private content. Recoverable initial/refresh failures use typed Retry; unknown dependent data is distinguished from a known empty bounded collection.

Header activation and explicit observation reconciliation retain their family authorization, confirmations, exact commands and ETags. A binding/schema change still requires the explicit Reconcile confirmation rather than an ordinary metadata PUT. Activation writes the saved definition, preserving unrelated pending fields and applying its returned ETag to the retained editor. Managed/inherited definitions stay read-only while independently authorized owner policy inspection/administration uses the common Access panel. Creation remains the separate migrated CreationWizard. No generic loader or new business contract is introduced.

65 focused component/localization cases, TypeScript and all three actual Release hosts pass with zero warnings/errors. Native English/dark desktop and French/light true-touch probes pass: identity and numeric/raw-schema first-error routing/focus, retained form and raw draft/original ETag through a real concurrent API update, rejected stale save with unchanged server metadata, explicit discard preserving a pending access policy, cancelled and confirmed binding reconciliation, and cancelled/confirmed activation preserving unrelated fields and exact type/version/digests. They also check exact namespace/UID, YAML content, policy retention through generation changes, keyboard/touch, Escape/focus return and document bounds. Maintained Storage/Connected Service desktop/mobile smoke passes against identical source/host hashes: four browser cases pass. Component checks also cover recoverable versus forbidden/deleted refreshes, replaced coordinates before auxiliary reads, in-flight edits, unknown dependent data, neutral unknown observation and independent managed-owner access. The 30 PNGs are unmodified captures of the running application; [verification.json](verification.json) records the actual tested base and exact hashes.

No shared-foundation or protected Agent source/style/translation changes. Existing Storage/Connected Service browser smoke is maintained for Identity’s standard destination. New reusable #1236 coverage stays deferred until explicit maintainer UX validation and implementation merge; full solution suites are omitted in favor of focused checks and affected desktop/mobile smoke as requested. CI errors were neither inspected nor handled.


- [storage-details-overview-desktop-en.png](storage-details-overview-desktop-en.png)
- [storage-details-invalid-identity-desktop-en.png](storage-details-invalid-identity-desktop-en.png)
- [storage-details-invalid-configuration-desktop-en.png](storage-details-invalid-configuration-desktop-en.png)
- [storage-details-invalid-json-desktop-en.png](storage-details-invalid-json-desktop-en.png)
- [storage-details-retained-refresh-desktop-en.png](storage-details-retained-refresh-desktop-en.png)
- [storage-details-yaml-desktop-en.png](storage-details-yaml-desktop-en.png)
- [storage-details-retained-policy-desktop-en.png](storage-details-retained-policy-desktop-en.png)
- [storage-details-etag-conflict-desktop-en.png](storage-details-etag-conflict-desktop-en.png)
- [storage-details-rebind-cancel-desktop-en.png](storage-details-rebind-cancel-desktop-en.png)
- [storage-details-rebound-desktop-en.png](storage-details-rebound-desktop-en.png)
- [storage-details-activation-cancel-desktop-en.png](storage-details-activation-cancel-desktop-en.png)
- [storage-details-activation-saved-desktop-en.png](storage-details-activation-saved-desktop-en.png)
- [storage-details-use-policy-desktop-en.png](storage-details-use-policy-desktop-en.png)
- [storage-details-diagnostics-desktop-en.png](storage-details-diagnostics-desktop-en.png)
- [storage-details-dependents-desktop-en.png](storage-details-dependents-desktop-en.png)
- [storage-details-overview-mobile-fr.png](storage-details-overview-mobile-fr.png)
- [storage-details-invalid-identity-mobile-fr.png](storage-details-invalid-identity-mobile-fr.png)
- [storage-details-invalid-configuration-mobile-fr.png](storage-details-invalid-configuration-mobile-fr.png)
- [storage-details-invalid-json-mobile-fr.png](storage-details-invalid-json-mobile-fr.png)
- [storage-details-retained-refresh-mobile-fr.png](storage-details-retained-refresh-mobile-fr.png)
- [storage-details-yaml-mobile-fr.png](storage-details-yaml-mobile-fr.png)
- [storage-details-retained-policy-mobile-fr.png](storage-details-retained-policy-mobile-fr.png)
- [storage-details-etag-conflict-mobile-fr.png](storage-details-etag-conflict-mobile-fr.png)
- [storage-details-rebind-cancel-mobile-fr.png](storage-details-rebind-cancel-mobile-fr.png)
- [storage-details-rebound-mobile-fr.png](storage-details-rebound-mobile-fr.png)
- [storage-details-activation-cancel-mobile-fr.png](storage-details-activation-cancel-mobile-fr.png)
- [storage-details-activation-saved-mobile-fr.png](storage-details-activation-saved-mobile-fr.png)
- [storage-details-use-policy-mobile-fr.png](storage-details-use-policy-mobile-fr.png)
- [storage-details-diagnostics-mobile-fr.png](storage-details-diagnostics-mobile-fr.png)
- [storage-details-dependents-mobile-fr.png](storage-details-dependents-mobile-fr.png)
