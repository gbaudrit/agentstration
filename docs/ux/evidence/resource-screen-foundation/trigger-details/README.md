# Persisted Trigger details

TriggerDetails uses ConsoleResourceDetailsTemplate with the standard Overview, Definition/Identity then Configuration, independently meaningful History and Execution identity, Access and final read-only YAML. Actual state/next/last/generation MetricCards precede saved Identity, including actual tags; configuration wraps the complete schedule, exact namespaced Flow reference/pin, target definition scope, missed/concurrent execution policies, last scheduled/error and static input. The prepared TriggerEditor supplies ResourceDefinitionForm with the layout's exact ResourceSectionState and presentation dictionary, real root fields and common Identity. Invalid/unsubmitted edits remain mounted. The dedicated `/edit` route resolves to the same canonical persisted frame and `#definition/identity`, retaining namespace and logical context; creation keeps its separate wizard.

ADR-0186's actual physical operational Workspace owner is preserved, including Instance/Tenant administration. The logical authorization context and the target Flow's definition scope are displayed separately, without assigning either to the Trigger owner. Optional ConsoleResourceDetailsTemplate.DefinitionContextScope uses the family's already authenticated physical scope only for inherited-definition presentation; it changes neither saved ownership nor command/policy authorization. The default adapter remains unchanged. Optional AccessContent composes this family's existing target-Flow access explanation/link with AllowAccess=false; no generic Trigger policy endpoint is fabricated. Its link uses `#access`. Protected Agent source/styles/translations remain unchanged.

The header switch keeps explicit enable confirmation, immediate disable and exact original ETag. Acknowledged activation updates the editor's saved envelope/ETag while retaining unrelated invalid/raw edits. Managed and insufficient-write definitions stay locked, while independently authorized execution and target access remain available. Definition save keeps the full saved pinned Flow target unless explicitly changed. Specialized parser errors stay inline once and use the common owning-section/first-error focus. Native validation exposed a deferred-preview focus request occurring before ResourceDefinitionForm had received its unlocked Busy state; the adapter now retains that request until the actual form is unlocked, without a timer or an alternate focus implementation. A focused deferred-preview component case and the unchanged native field-focus assertion verify that path. Delete clears resource/editor/guard before the post-render return navigation; a rejected/unconfirmed command retains inputs and does not automatically retry.

Same-identity unchanged refresh preserves the model/EditContext/raw input and original ETag. Changed saved snapshots are buffered until explicit discard; Overview and YAML continue presenting the accepted saved definition. Recoverable read failures retain drafts and offer common Retry. Read denial, disappearance, foreign physical owner or replacement UID clear private content before auxiliary history reads. Cancellation/request/context guards reject stale completions. History remains bounded to 100, validates Workspace and Trigger UID and distinguishes unknown/failed reads from a confirmed empty history. Task links retain their actual operational execution scope. Execution principal inspection preserves its complete authorized identity, rather than relying on truncated text. YAML serializes the saved definition envelope, without inventing unsupported writes.

43 focused Trigger detail/editor/localization/schedule/DST and Console adapter cases pass, as do TypeScript and the three Release hosts without warnings/errors. Native English/dark desktop and French/light true-touch probes verify complete identity/tags/context, all shared sections, invalid JSON and first-error focus, unchanged/changed refresh, explicit discard, enable confirmation and Escape/focus restoration, activation retaining an unrelated draft, exact pinned-target metadata writes, a real concurrent 412 conflict and canonical dedicated-edit navigation. Maintained operational Trigger and protected Agent desktop/mobile journeys pass with identical recorded product-source/host hashes. The 26 PNGs are unmodified real application captures. Exact tested base/source/host hashes and browser phases are recorded in verification.json.

The maintained Trigger journey visits the real shared sections and preserves its target authorization, operational execution and cross-workspace isolation assertions. Native probes use real API-created local Flow/Trigger resources with public fixture data; no production demonstration resources or generated images. Initial native selectors were corrected to distinguish the actual title-row owner from the target-scope badge and to assert saved label/description control values. The exact ownership/value assertions remain. No assertions or timeouts were weakened. New reusable #1236 coverage remains deferred until explicit UX validation and merge. Full solution suites are omitted at maintainer request, and no CI errors were inspected or handled.

The existing fixed global mobile navigation/platform panel remains visible over scrolled content in full-page captures. This pass keeps the prescribed shared mobile insets and does not change the global application layout. The touched resource controls and drawer are exercised with true touch, and horizontal bounds are checked at each capture; those checks do not claim a global-layout repair.


- [trigger-details-overview-desktop-en.png](trigger-details-overview-desktop-en.png)
- [trigger-details-identity-desktop-en.png](trigger-details-identity-desktop-en.png)
- [trigger-details-configuration-desktop-en.png](trigger-details-configuration-desktop-en.png)
- [trigger-details-yaml-desktop-en.png](trigger-details-yaml-desktop-en.png)
- [trigger-details-access-desktop-en.png](trigger-details-access-desktop-en.png)
- [trigger-details-history-desktop-en.png](trigger-details-history-desktop-en.png)
- [trigger-details-invalid-retained-desktop-en.png](trigger-details-invalid-retained-desktop-en.png)
- [trigger-details-enabled-with-draft-desktop-en.png](trigger-details-enabled-with-draft-desktop-en.png)
- [trigger-details-execution-desktop-en.png](trigger-details-execution-desktop-en.png)
- [trigger-details-saved-desktop-en.png](trigger-details-saved-desktop-en.png)
- [trigger-details-changed-refresh-desktop-en.png](trigger-details-changed-refresh-desktop-en.png)
- [trigger-details-discarded-desktop-en.png](trigger-details-discarded-desktop-en.png)
- [trigger-details-edit-route-desktop-en.png](trigger-details-edit-route-desktop-en.png)
- [trigger-details-overview-mobile-fr.png](trigger-details-overview-mobile-fr.png)
- [trigger-details-identity-mobile-fr.png](trigger-details-identity-mobile-fr.png)
- [trigger-details-configuration-mobile-fr.png](trigger-details-configuration-mobile-fr.png)
- [trigger-details-yaml-mobile-fr.png](trigger-details-yaml-mobile-fr.png)
- [trigger-details-access-mobile-fr.png](trigger-details-access-mobile-fr.png)
- [trigger-details-history-mobile-fr.png](trigger-details-history-mobile-fr.png)
- [trigger-details-invalid-retained-mobile-fr.png](trigger-details-invalid-retained-mobile-fr.png)
- [trigger-details-enabled-with-draft-mobile-fr.png](trigger-details-enabled-with-draft-mobile-fr.png)
- [trigger-details-execution-mobile-fr.png](trigger-details-execution-mobile-fr.png)
- [trigger-details-saved-mobile-fr.png](trigger-details-saved-mobile-fr.png)
- [trigger-details-changed-refresh-mobile-fr.png](trigger-details-changed-refresh-mobile-fr.png)
- [trigger-details-discarded-mobile-fr.png](trigger-details-discarded-mobile-fr.png)
- [trigger-details-edit-route-mobile-fr.png](trigger-details-edit-route-mobile-fr.png)
