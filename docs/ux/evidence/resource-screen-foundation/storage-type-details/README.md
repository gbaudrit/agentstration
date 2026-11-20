# Storage Type persisted resource screen

StorageTypeDetails now uses ConsoleResourceDetailsTemplate with Overview, Definition/Identity and Configuration, immutable Version contracts, independently authorized Access and final read-only YAML. Relevant generation/preferred-contract MetricCards precede the saved Identity panel and its actual tags. Saved settings use the common wrapping CollectionView; discoverable usages remain in Overview with their existing bounded provider contract and exact authorized identities. An unavailable initial usage read is explicitly unknown rather than an invented empty collection or total. Immutable version selection and the business `version` query survive fragment navigation and refresh. The previous UID/origin diagnostics remain available in the complete YAML without displacing the common Identity fields.

The delegated ResourceDefinitionForm receives the exact same ResourceSectionState and presentation dictionary as its owning template. Real root fields route invalid metadata to Identity and focus its inline error. Read-only managed/inherited definitions retain inspection of identity/configuration/contracts; independent access authorization is preserved. Header activation retains confirmation, unrelated metadata drafts, immutable version payloads and the returned ETag. Create/publication keep their separate previously migrated wizards. Properties-only tags remain read-only in the editor; no unsupported metadata write is added.

Typed initial loading/retry/error and non-blocking refreshing retain visited forms, invalid fields, context, baseline, original ETag and pending access policy. A changed response is buffered and the saved overview remains stable until explicit definition discard; an unchanged refresh does not report a false conflict. Edits made while the read is pending are evaluated at response time. Lost access, deletion, replaced UID/address/owner and stale/disposed responses cannot restore private state. Definition saves use their returned snapshot without an extra GET. The family's existing explicit Review current remains a separate, intentional acceptance of the reviewed ETag while retaining its metadata draft. Automatic Refresh does not invoke it.

Successful deletion clears private state and releases the Console template's combined policy leave guard before returning to the list. A backend rejection retains current data/drafts. Exact scope, namespace, permissions, ETags, disable/delete confirmations and immutable publication contracts are unchanged.

38 targeted component/localization cases, TypeScript and all three actual Release hosts pass with zero warnings/errors. English/dark desktop and French/light true-touch probes pass: first-error routing/focus, invalid field and original ETag retention through a real concurrent API update, rejected stale save with unchanged server content, explicit discard and subsequent valid save, stable immutable version selection, retained pending access policy, activation confirmation cancellation, Escape/focus return, document bounds and actual successful deletion with a pending policy draft. Maintained Storage/Connected Service desktop/mobile smoke passes on the identical source/host hashes: four browser cases pass. The first French native probe expected the wrong synonym in the existing stale-revision translation; its expected text was corrected to the actual maintained “révision périmée” wording. No product change, assertion removal or timeout extension was used for that correction. The final native probes pass. The 24 PNGs are unmodified captures of the running application; [verification.json](verification.json) records the actual tested base, source/host hashes and matching maintained-regression campaign.

No shared component or protected Agent source/style/translation changes. The new reusable transverse #1236 journey remains deferred until explicit maintainer UX validation and implementation merge; the existing Storage/Connected Service journey is maintained and extended for the standard Type sections. Full solution suites are omitted in favor of targeted checks and affected desktop/mobile smoke as requested. CI errors were neither inspected nor handled.


- [storage-type-details-overview-desktop-en.png](storage-type-details-overview-desktop-en.png)
- [storage-type-details-identity-desktop-en.png](storage-type-details-identity-desktop-en.png)
- [storage-type-details-configuration-desktop-en.png](storage-type-details-configuration-desktop-en.png)
- [storage-type-details-invalid-identity-desktop-en.png](storage-type-details-invalid-identity-desktop-en.png)
- [storage-type-details-retained-refresh-desktop-en.png](storage-type-details-retained-refresh-desktop-en.png)
- [storage-type-details-yaml-desktop-en.png](storage-type-details-yaml-desktop-en.png)
- [storage-type-details-versions-desktop-en.png](storage-type-details-versions-desktop-en.png)
- [storage-type-details-retained-policy-desktop-en.png](storage-type-details-retained-policy-desktop-en.png)
- [storage-type-details-etag-conflict-desktop-en.png](storage-type-details-etag-conflict-desktop-en.png)
- [storage-type-details-saved-desktop-en.png](storage-type-details-saved-desktop-en.png)
- [storage-type-details-activation-cancel-desktop-en.png](storage-type-details-activation-cancel-desktop-en.png)
- [storage-type-details-deleted-with-pending-policy-desktop-en.png](storage-type-details-deleted-with-pending-policy-desktop-en.png)
- [storage-type-details-overview-mobile-fr.png](storage-type-details-overview-mobile-fr.png)
- [storage-type-details-identity-mobile-fr.png](storage-type-details-identity-mobile-fr.png)
- [storage-type-details-configuration-mobile-fr.png](storage-type-details-configuration-mobile-fr.png)
- [storage-type-details-invalid-identity-mobile-fr.png](storage-type-details-invalid-identity-mobile-fr.png)
- [storage-type-details-retained-refresh-mobile-fr.png](storage-type-details-retained-refresh-mobile-fr.png)
- [storage-type-details-yaml-mobile-fr.png](storage-type-details-yaml-mobile-fr.png)
- [storage-type-details-versions-mobile-fr.png](storage-type-details-versions-mobile-fr.png)
- [storage-type-details-retained-policy-mobile-fr.png](storage-type-details-retained-policy-mobile-fr.png)
- [storage-type-details-etag-conflict-mobile-fr.png](storage-type-details-etag-conflict-mobile-fr.png)
- [storage-type-details-saved-mobile-fr.png](storage-type-details-saved-mobile-fr.png)
- [storage-type-details-activation-cancel-mobile-fr.png](storage-type-details-activation-cancel-mobile-fr.png)
- [storage-type-details-deleted-with-pending-policy-mobile-fr.png](storage-type-details-deleted-with-pending-policy-mobile-fr.png)
