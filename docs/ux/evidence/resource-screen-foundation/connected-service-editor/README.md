# Connected Service editor: common form and retained creation wizard

The creation route `/connected-services/new` retains its own page and CreationWizard, now inside ResourceDefinitionForm with localized annotation validation and actual root Fields. Identity uses ResourceIdentityEditor, with description and read-only saved labels; the pinned type selector enriches Identity through AdditionalPanels. Configuration retains existing ValueBindingRequirementEditor controls followed by JsonSchemaInputEditor. The separate descendant-use policy and final review retain their meanings. Incomplete stage navigation remains available; only final submission validates and focuses the first visible error. Draft activation stays in the header switch.

The editor accepts the owning detail's ResourceSectionState and presentation dictionary for the retained persisted form. Its existing edit alias redirects to the canonical detail/configuration section. A successful save, explicit snapshot adoption/discard and activation ETag reconciliation remain family-owned. Invalid schema/raw controls and their EditContext survive refused snapshot adoption; changed UID/address/owner cannot replace the active editor, and activation from a replaced UID is ignored. Contextual binding creation reports its own draft without becoming a second form/leave guard.

Binding eligibility, protections, exact parameter/secret coordinates, schema validation, descendant-use grants, confirmations and original ETag commands remain intact. Existing pinned type references are preserved when unchanged. Late/disposed callbacks cannot overwrite a replacement context. Standard fields keep the API's actual capabilities: description is editable; labels are read-only because the API updates properties only. Creation has no namespace parameter in its DTO/endpoint, so its standard namespace field honestly displays the immutable `default`; existing resources retain their exact namespace.

The common first-error integration now recognizes the schema editor's existing errors; raw invalidity is also exposed on its FormField wrapper, avoiding InputBase attribute replacement. No parser/schema rule changes. The shared change requires protected Agent regression verification; its six source/style/catalog files remain unchanged.

The persisted ConnectedServiceDetails host is deliberately tracked separately for the next screen commit. This editor commit does not claim the old host already uses the new Console template.

Validation:

- **70 focused cases pass**: 50 Console cases (Connected Service existing/new cases plus protected Agent resource-template regressions), and 20 Web cases (schema editor and resource/header composition). They retain binding/source/type eligibility, activation confirmations/drafts/ETags, exact route aliases, incomplete wizard navigation and final creation; add real-root validation, localized field labels, invalid JSON/context/old-version retention until explicit discard, replaced-UID rejection, raw invalidity semantics and caller-supplied activation selectors.
- TypeScript and JavaScript syntax checks pass. All three actual Release hosts build with zero warnings/errors. Full solution suites are omitted under the requested focused-validation scope.
- **Four final browser cases pass**: actual desktop EN/dark and touch FR/light creation probes, plus both maintained create/deploy Agent journeys. The creation probes inspect all four stages, incomplete navigation and first-error focus for Identity, required bindings, generated numeric constraints and raw JSON; retain invalid JSON through stage changes; check long identities/labels and document bounds; toggle draft activation through the real header; then verify exact owner/default namespace, pinned type UID/version/digest, options and literal in the actual API result.
- **16 actual application captures** are linked below. Initial probes found raw-schema invalidity invisible to the common focus selector, schema error focus choosing adjacent field help, and PageHeader forwarding a literal activation selector. These are repaired without changing business validation or the wizard's behavior. An intermediate diagnostic schema required raw fallback; the fixture was corrected to explicitly support generated fields. No timeout/assertion is weakened. Both final protected Agent journeys pass afterwards; its six source/style/catalog files remain unchanged.
- [Exact tested base, source/host hashes and validation scope](source-proof.json). The persisted host remains pending and is not claimed by these creation proofs. New reusable transverse UX coverage remains deferred under open/blocked #1236. CI failures are not inspected or repaired.

Real application captures:

| State | Desktop EN/dark | Touch FR/light |
| --- | --- | --- |
| Incomplete Identity and error focus | [Desktop](connected-editor-invalid-identity-desktop-en.png) | [Touch](connected-editor-invalid-identity-mobile-fr.png) |
| Standard Identity, labels and pinned type | [Desktop](connected-editor-identity-desktop-en.png) | [Touch](connected-editor-identity-mobile-fr.png) |
| Generated schema error and focus | [Desktop](connected-editor-invalid-schema-field-desktop-en.png) | [Touch](connected-editor-invalid-schema-field-mobile-fr.png) |
| Bindings and schema configuration | [Desktop](connected-editor-configuration-desktop-en.png) | [Touch](connected-editor-configuration-mobile-fr.png) |
| Retained invalid raw JSON | [Desktop](connected-editor-invalid-options-desktop-en.png) | [Touch](connected-editor-invalid-options-mobile-fr.png) |
| Descendant-use stage | [Desktop](connected-editor-descendant-use-desktop-en.png) | [Touch](connected-editor-descendant-use-mobile-fr.png) |
| Review before actual create | [Desktop](connected-editor-review-desktop-en.png) | [Touch](connected-editor-review-mobile-fr.png) |
| Created API-verified resource in the existing host | [Desktop](connected-editor-created-desktop-en.png) | [Touch](connected-editor-created-mobile-fr.png) |
