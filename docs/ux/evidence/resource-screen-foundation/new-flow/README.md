# Flow creation: wizard and exact Agent references

Creation remains a dedicated CreationWizard, not a persisted-resource fiche. Standard ResourceIdentityFields, free incomplete-step navigation, retained parser input, templates, five strategies, exact selected owner/default namespace and draft-only creation semantics remain intact. Interactivity/resources-write guards lock the native fieldset, wizard and final command paths. Known denied users receive the common permission notice. Activation uses the existing common switch; strategy booleans use switch presentation and participant multi-selection keeps checkboxes.

The orchestration picker displays namespace/name and the authorized owner badge. New selections save both coordinates. Saved participant/manager references retain their full version, UID, revision UID, definition hash, schemas and output contract; metadata editing does not reconstruct a bare ID. The strategy's business IDs remain unique: a second homonym is disabled with a localized, wrapping explanation. Removing a selection explicitly permits choosing another homonym. Manager options, execution order and node inspection use the selected reference, rather than the first matching name. Stable option/row keys preserve the visible manager selection when participants change; the real browser probes assert that displayed value after removal/reselection and wizard navigation.

Existing relative references remain relative. The authorized catalog can identify the same owner or an unambiguous Instance ancestor without rewriting the reference. Unknown scope stays unknown. Another Tenant/Workspace is never guessed: an unresolved reference remains in the order and can be explicitly removed/reselected. No extra row request or catalog loader is introduced. The existing bounded catalog remains unchanged.

Previewing an unfinished Handoff/Magentic strategy no longer executes final submission validation. An absent manager shows existing localized guidance, without a fictional Agent. Final creation still rejects insufficient/duplicate participants, missing routes/manager and manager overlap. Publication remains a separate command; no revision/version counter or business contract changes.

Validation:

- All **25 focused cases** pass: original creation/strategy/route/read-only cases, retained invalid input and owner/draft revision, plus exact saved-reference round-trip, homonym/manager selection, inherited/unknown/ambiguous relative references, explicit replacement, incomplete preview, prerender and permission guards. Six reproductions failed before their respective corrections (two reference cases, three incomplete-preview cases, one relative inspection case).
- Component/test and actual Web, Workplace and GovernedInventoryHost Release builds completed with zero warnings/errors. TypeScript passes. The two parallel list migrations are preserved.
- Both final real application probes pass: EN/dark desktop and FR/light native touch. They create homonymous Agents through the actual API in separate namespaces; choose a Magentic manager, remove/reselect the exact participant, retain choices through wizard steps, create an unpublished Flow and save a metadata edit. Actual API reads verify unchanged participant/manager coordinates and no published version. Full-width bounds are asserted on each capture.
- Both maintained Agent desktop/mobile cases and the maintained Handoff Flow/Entry smoke pass against the same final build. Protected Agent source/styles/translations remain byte-identical. [Exact tested source, actual-host hashes and results](source-proof.json).
- Initial Workflow guard probes separately verify revision-one creation, exact structured FlowId/owner/context, no publication, incomplete Identity and long-input retention. Their six captures below are historical to the [initial guard proof](creation-guard-proof.json); they are not presented as screenshots of the later orchestration changes.

This completes the creation review. **FlowDetails and its delegated persisted FlowOrchestrationEditor still require their own common-layout/form migration**; the latter only receives exact owner/namespace context in this correction. No CI failure was inspected/remediated and no full solution suite is claimed. The reusable transverse scenario remains under open/blocked #1236 pending UX validation and merge of #1238.

Final real orchestration captures:

| State | Desktop EN/dark | Touch FR/light |
| --- | --- | --- |
| Qualified selection and manager | [Picker](orchestration-qualified-picker-desktop-en.png) | [Picker](orchestration-qualified-picker-mobile-fr.png) |
| Saved references after metadata editing | [Inspection](orchestration-qualified-saved-desktop-en.png) | [Inspection](orchestration-qualified-saved-mobile-fr.png) |

Initial Workflow creation captures:

| State | Desktop EN/dark | Touch FR/light |
| --- | --- | --- |
| Incomplete Identity | [Validation](new-flow-incomplete-identity-desktop-en.png) | [Validation](new-flow-incomplete-identity-mobile-fr.png) |
| Review | [Review](new-flow-review-desktop-en.png) | [Review](new-flow-review-mobile-fr.png) |
| Created revision-one draft | [Draft](new-flow-created-draft-desktop-en.png) | [Draft](new-flow-created-draft-mobile-fr.png) |
