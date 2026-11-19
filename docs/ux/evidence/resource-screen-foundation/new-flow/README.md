# Flow creation: wizard and command guards

Creation remains a dedicated CreationWizard, not a persisted-resource fiche. The existing standard identity fields, incomplete-step navigation, retained inputs, templates, five orchestration strategies, selected owner/default namespace and draft-only creation semantics remain intact. The wizard and its command paths now require interactive rendering and resources/write; the native fieldset locks during prerender, missing permission and submission. The common read-only permission notice is shown only for a known denied context.

Seven focused NewFlowWizardTests pass, including original strategy/raw-input/owner/revision checks and new static-render/reader guards. Component, test and actual Web/Workplace/GovernedInventoryHost Release builds completed with zero warnings/errors. No shared product component or protected Agent file changes.

The first supplementary browser probe reached the newly created draft but incorrectly compared its structured FlowId to a string. The probe was corrected to assert both namespace and value; no product behavior was changed to fit the probe. The corrected desktop EN/dark and genuine-touch FR/light probes both pass, including free incomplete-step navigation, final validation returning to Identity, long identity retention, actual draft creation with revision one and exact owner/context, no published versions and strict document-width checks. The maintained Handoff Flow/Entry smoke also passes. [Exact tested source and host hashes](source-proof.json).

Real captures:

- [new-flow-incomplete-identity-desktop-en.png](new-flow-incomplete-identity-desktop-en.png)
- [new-flow-review-desktop-en.png](new-flow-review-desktop-en.png)
- [new-flow-created-draft-desktop-en.png](new-flow-created-draft-desktop-en.png)
- [new-flow-incomplete-identity-mobile-fr.png](new-flow-incomplete-identity-mobile-fr.png)
- [new-flow-review-mobile-fr.png](new-flow-review-mobile-fr.png)
- [new-flow-created-draft-mobile-fr.png](new-flow-created-draft-mobile-fr.png)

Open dependency: the shared orchestration picker loses namespace/owner coordinates and collides on homonymous IDs. This review does not claim that problem fixed or count NewFlow as fully reconciled. No CI failure was inspected or remediated; reusable transverse coverage remains gated under open #1236.
