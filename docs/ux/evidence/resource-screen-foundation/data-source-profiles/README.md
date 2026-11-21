# Data Source Profile: validated resource foundation

The historical Data Source Profile adapter now uses ConsoleResourceDetailsTemplate: actual active-publication metric above saved Identity/labels, Identity then Configuration, family Contracts/Policies sections, Revisions, independently authorized Access and final YAML. ResourceDefinitionForm shares the same section state/presentations and declares public root model fields for invalid-submit navigation/focus. The model owns the prior required-field/JSON-shape rules and exact Flow-reference construction; the screen owns loading, commands, authorization, confirmations and ETags. Namespace, owner and navigation context remain distinct. Initial/refresh reads validate exact coordinates and refreshed UID.

Creation retains CreationWizard, freely navigable incomplete steps, standard identity fields and the existing create-then-publish sequence. An unconfirmed publication returns to the created resource for retry instead of creating again. The properties-only API has no metadata-label write: labels are inside Identity and stay read-only in both modes. Namespace remains selectable on creation and immutable on persisted profiles; description retains its 2,000-character control limit. The header activation switch remains a saved-ETag draft until Save.

Exact saved acquisition/discovery references retain nullable relative coordinates, pinned versions and active-version semantics. New selections retain the authorized catalog owner/namespace. Published summary continues to use actual publication history/active version rather than the working version. Existing publish/activate commands and revision semantics remain intact; no counter or future publication is invented. A separately prepared publication-version input survives refresh.

Invalid JSON, pending YAML and access policies remain mounted across section changes and refresh. JSON fields now track the input buffer immediately so edits entered while a read is pending cannot be replaced; this does not save or invoke a business command. A changed ETag/generation buffers the saved profile/history until explicit discard, while an unchanged snapshot causes no false conflict. Form and YAML remain mutually guarded, YAML retains its existing identity checks and definition-only update payload, and API failures remain visible. Loss of read access/deletion/replaced identity clears cached content, including a read lost after publication; recoverable errors retain drafts. Successful deletion invalidates the saved identity and combined policy guard before navigation.

The only common-component capability added is an optional YAML input test selector, forwarding ConsoleResourceDetailsTemplate.YamlInputTestId to ResourceYamlEditor.InputTestId. It preserves the existing `profile-yaml` input contract; absent values add no attribute and retain Agent's default behavior. No generic loader, domain schema or parallel form/navigation framework is introduced.

Validation:

- All 27 focused component cases pass: existing profile behavior, standard identity/form/field declarations, exact pinned references, draft-only activation/ETag, incomplete creation/publication failure, invalid JSON/YAML and policy retention, in-flight JSON input, unchanged/changed snapshot handling, independent publication input, access loss/replacement, successful deletion and post-publication privacy clearing. Common adapter selector/busy/read-only checks, both managed-profile policy cases and both Agent refresh/draft cases also pass in that count.
- TypeScript passes. Actual components/tests and Web/Workplace/GovernedInventoryHost Release builds have zero warnings/errors. Data Source translations are symmetric and duplicate-free.
- Both English/dark desktop and French/light genuine-touch probes pass real user-profile creation/publication in a non-default namespace against the local governed fixture, exact acquisition coordinates, first-error focus, concurrent API update and explicit discard, retained invalid JSON/YAML/policy, header activation followed by Save, a second real immutable publication, saved YAML and managed locks. Strict document-width assertions and touch Escape/focus return remain intact.
- Both maintained governed-inventory cases pass. Their obsolete hidden-Definition assertion becomes four mandatory inspection destinations with mutation/fieldset/YAML locks preserved; ordinary profile editing now visits Identity. Both maintained Agent creation/deployment cases pass after the common-component change, and its protected screen/model/styles/translations are byte-identical. [Exact source and actual-host hashes; all six browser results](source-proof.json).
- No CI failure is inspected or remediated, no full solution suite is claimed, and no issue/PR status is changed. New reusable transverse coverage remains tracked by open/blocked #1236 pending maintainer UX validation and merge of #1238. Existing journeys are maintained; transient probes do not bypass that gate.

Real application captures:

| State | Desktop EN/dark | Touch FR/light |
| --- | --- | --- |
| Creation acquisition | [Wizard](profile-wizard-acquisition-desktop-en.png) | [Wizard](profile-wizard-acquisition-mobile-fr.png) |
| Creation review | [Review](profile-wizard-review-desktop-en.png) | [Review](profile-wizard-review-mobile-fr.png) |
| Saved overview | [Identity and publication](profile-overview-desktop-en.png) | [Identity and publication](profile-overview-mobile-fr.png) |
| Invalid JSON after concurrent update | [Retained draft](profile-json-draft-refresh-desktop-en.png) | [Retained draft](profile-json-draft-refresh-mobile-fr.png) |
| Pending YAML and policy refresh | [Retained YAML](profile-yaml-policy-refresh-desktop-en.png) | [Retained YAML](profile-yaml-policy-refresh-mobile-fr.png) |
| Actual published versions | [Publications](profile-published-versions-desktop-en.png) | [Publications](profile-published-versions-mobile-fr.png) |
| Saved YAML | [YAML](profile-saved-yaml-desktop-en.png) | [YAML](profile-saved-yaml-mobile-fr.png) |
| Managed identity | [Read-only](profile-managed-readonly-desktop-en.png) | [Read-only](profile-managed-readonly-mobile-fr.png) |
