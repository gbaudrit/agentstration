# Storage Type creation and immutable contract publication

The StorageTypeEditor route component now composes ResourceDefinitionForm and the standard ResourceIdentityEditor. Creation retains its freely navigable Identity / Contract / Review CreationWizard. Publication retains its separate Contract / Review wizard and starts with an empty explicit version. It does not expose metadata edits or an activation switch for publication. The current owner context remains the sole eligible creation scope, and creation keeps its existing editable namespace and empty-namespace/default semantics. Properties-only tags are read-only inside Identity. Binary capabilities and the publication preference reuse the standard switch styling and label target, with family-scoped alignment; native probes verify the rendered switch width/appearance and an actual saved capability.

StorageTypeEditorModel owns the previous identity rules, without new restrictions. StorageTypeVersionDraft reports the actual invalid nested JSON/version/limit field while its existing Build, bounds, digests, capabilities, requirements, contribution prohibition and immutable publication semantics are preserved. Form section declarations map the real root fields, including the Version aggregate; inline errors target the actual nested controls and the common focus mechanism. Large positive limits accepted previously remain accepted; no invented maximum or future version is introduced.

The delegated metadata form accepts the owning layout's exact ResourceSectionState and presentation dictionary. Its parent StorageTypeDetails migration remains a separate pending screen increment; this editor/creation commit does not claim the parent has adopted the common Console layout. The legacy embedded host stays compatible until that migration. Snapshot save/review callbacks, activation ETag reconciliation and explicit discard are family-owned operations. A dirty/invalid form retains its context, raw values and ETag when a new snapshot arrives; a pristine form can adopt it. Returning from publication to creation resets the previous identity/model/ETag after the ordinary leave confirmation, including a reused route parameter.

The existing explicit Review current command remains available after a write conflict. It retains the draft while showing the reviewed saved metadata and explicitly accepting that resource's latest ETag, preserving the existing metadata/publication workflow. Shared refresh does not invoke this command. The owning detail's automatic refresh will retain the original ETag until explicit discard; this distinction is tested and is an intentional family reconciliation action, not an automatic conflict overwrite.

All 24 targeted component cases, TypeScript and the three actual Release hosts pass with zero warnings/errors. Final English/dark desktop and French/light true-touch probes verify free incomplete navigation, first-error focus for identity/JSON/numeric limits, exact namespace and owner, actual creation with large accepted limits, explicit immutable version publication, unchanged first contract and preferred version, no provider contribution, keyboard/touch navigation, Escape/focus return and document bounds. Maintained Storage/Connected Service desktop/mobile checks pass against the same recorded source/host hashes: four browser cases pass. The 22 PNGs below are unmodified captures of the running application; [verification.json](verification.json) records the tested base, source and host hashes. An initial native probe referenced a missing selector constant after publication; the probe and central selector declaration were corrected without reducing an assertion or extending a timeout. No shared component or protected Agent source/style/translation changes. Existing Storage/Connected Service browser journeys remain maintained; the new reusable transverse scenario #1236 is still deferred until maintainer UX validation and merge. CI failures are outside this work by maintainer request.


- [storage-type-editor-invalid-identity-desktop-en.png](storage-type-editor-invalid-identity-desktop-en.png)
- [storage-type-editor-identity-desktop-en.png](storage-type-editor-identity-desktop-en.png)
- [storage-type-editor-invalid-contract-desktop-en.png](storage-type-editor-invalid-contract-desktop-en.png)
- [storage-type-editor-invalid-limit-desktop-en.png](storage-type-editor-invalid-limit-desktop-en.png)
- [storage-type-editor-contract-desktop-en.png](storage-type-editor-contract-desktop-en.png)
- [storage-type-editor-review-desktop-en.png](storage-type-editor-review-desktop-en.png)
- [storage-type-editor-created-desktop-en.png](storage-type-editor-created-desktop-en.png)
- [storage-type-editor-publication-invalid-version-desktop-en.png](storage-type-editor-publication-invalid-version-desktop-en.png)
- [storage-type-editor-publication-invalid-schema-desktop-en.png](storage-type-editor-publication-invalid-schema-desktop-en.png)
- [storage-type-editor-publication-review-desktop-en.png](storage-type-editor-publication-review-desktop-en.png)
- [storage-type-editor-published-desktop-en.png](storage-type-editor-published-desktop-en.png)
- [storage-type-editor-invalid-identity-mobile-fr.png](storage-type-editor-invalid-identity-mobile-fr.png)
- [storage-type-editor-identity-mobile-fr.png](storage-type-editor-identity-mobile-fr.png)
- [storage-type-editor-invalid-contract-mobile-fr.png](storage-type-editor-invalid-contract-mobile-fr.png)
- [storage-type-editor-invalid-limit-mobile-fr.png](storage-type-editor-invalid-limit-mobile-fr.png)
- [storage-type-editor-contract-mobile-fr.png](storage-type-editor-contract-mobile-fr.png)
- [storage-type-editor-review-mobile-fr.png](storage-type-editor-review-mobile-fr.png)
- [storage-type-editor-created-mobile-fr.png](storage-type-editor-created-mobile-fr.png)
- [storage-type-editor-publication-invalid-version-mobile-fr.png](storage-type-editor-publication-invalid-version-mobile-fr.png)
- [storage-type-editor-publication-invalid-schema-mobile-fr.png](storage-type-editor-publication-invalid-schema-mobile-fr.png)
- [storage-type-editor-publication-review-mobile-fr.png](storage-type-editor-publication-review-mobile-fr.png)
- [storage-type-editor-published-mobile-fr.png](storage-type-editor-published-mobile-fr.png)

Before committing, the checkout was synchronized with parallel Extension inventory/list documentation through `c93a05291a45c6d90cecb7a7de1d5d2f3c360898`. The recorded editor and protected Agent hashes remained identical. Validation above refers to the recorded tested base; it does not claim validation of those parallel changes.
