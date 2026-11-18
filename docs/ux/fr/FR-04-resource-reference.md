# Find and select model profiles in an inline filtered collection

Published issue: [#1093](https://github.com/gbaudrit/agentstration/issues/1093); parent [#1089](https://github.com/gbaudrit/agentstration/issues/1089).

## Area
Console

## Feature type
User-facing capability

## Console or Workplace UX impact
Console UX change

## Problem or need
An Agent can choose from many Model Profiles. A long native option list is difficult to scan, and a separate search field does not provide a clear selection workflow. A modal would interrupt the form and hide useful context. Identical display names across scopes make presentation alone insufficient to identify the intended reference.

## Expected outcome
An administrator can find an eligible Model Profile by name, provider or model, narrow the list and choose the exact resource without leaving the form. The selected reference remains readable even when it is outside the current filtered page.

## Proposed solution
Consolidate the existing ModelProfilePicker around an inline ResourceReferenceField responsibility: selected-value summary, expandable in-page searchable collection, supported filters and paging. Compose the shared field and collection contracts without replacing typed value-binding editors.

## Scope
- Pilot on `/agents/new` and `/agents/{Name}` using the existing exact reference and eligibility rules.
- Search display name, technical name, provider and model; provide relevant provider and owner-scope filters within the eligible set. Show namespace and owner scope to distinguish duplicate names.
- Keep current selection separate from lookup results. Clear the selection in the field; if required, display validation without choosing another profile automatically.
- Show availability/read-only information without hiding a selected resource that becomes unavailable. Do not substitute another resource on refresh.
- Bound result loading and rendering. Where current listing cannot support the contract, add the narrow family-owned lookup/query support in the Models boundary, preserving compatibility and authorization.
- Ignore obsolete search responses, cancel obsolete work and use honest total/continuation presentation from T-02.

## Out of scope
A modal selector, a new generic resource query API, creation of Model Profiles inside the picker, changing eligibility/access rules, a graph of dependencies or the Parameter/Secret/Literal binding editor owned by #1051/#1054.

## Dependencies
Parent: [FR-00](FR-00-resource-administration.md). Depends on [FR-01](FR-01-clearable-fields.md) and [T-02](T-02-collection-foundations.md). Preserve #1026's existing exact identity coordinates; new naming conventions remain there. FR-03 is compatible but not required before improving the field.

## Alternatives considered
A modal obscures the editing context. A huge option list does not support efficient filtering. Fetching all pages before filtering defeats bounded loading. A generic reference editor that also owns typed values would duplicate #1051.

## Deployment impact
None

## Acceptance criteria
- [ ] The chooser expands inside the form; keyboard users can search, filter, page and select without opening a modal.
- [ ] Search matches the documented fields; filters narrow the authorized eligible collection and reset paging appropriately.
- [ ] Duplicate names remain distinguishable by namespace and owner scope, and saving retains the exact selected reference.
- [ ] Filtering, paging or opening the chooser does not mark the resource dirty; changing/clearing the selected reference does.
- [ ] The selected profile remains visible outside the result page and is retained with clear feedback if unavailable; no implicit replacement occurs.
- [ ] Lookup requests/materialization are bounded to the requested page or explicit lookup bound; there is no fetch-all-pages loop. Known and unknown totals are presented accurately.
- [ ] Empty results, access denial, loading and expected service errors are distinct and recoverable without losing the Agent draft.
- [ ] The two themes, en-US/fr-FR, mobile layout and focus behavior pass; no external UI library is required.

## UX test scenario follow-up
Scenario children [#1101](https://github.com/gbaudrit/agentstration/issues/1101) are published and natively attached. Their new reusable journeys wait for explicit maintainer UX validation and merged implementation PR. Existing regression repairs, behavior tests and required smoke checks remain in the UX PR.

Routes: `/agents/new`, `/agents/{Name}`. Fixtures: a deterministic authorized catalog spanning several result pages (the prototype uses 72 profiles), repeated display/technical names in distinct scopes/namespaces, several providers/models, inaccessible profiles, a selected profile outside the filtered page and an unavailable current profile. Actions: expand inline, search by each field, narrow filters, page, select, clear, save/reopen and simulate late/failed lookup responses. Success: exact selection persists, search leaves the draft clean and bounded queries are observed. Expected failures: no matches can be cleared; unavailable selection is retained and explained; inaccessible results/counts are absent; a late response cannot replace the newest results. No live provider calls are needed.

## Additional context
Local planning ID: FR-04. The prototype validates interaction intent, not production query scalability. Any API addition stays in the Models family and uses its existing authorization/service boundary. Proposed labels: enhancement, triage:agent, triage:pending-review; no priority assigned.

## Attachments
[Inline catalog prototype](../maquette-ecran-type.md), [field inventory](../unification-des-formulaires.md).

## Preliminary checks
- [x] I searched for an existing issue covering this request. Open/closed review on 2026-11-09 considered #1051/#1054, #815 and #1026. Typed bindings, scope inventory filtering and naming remain owned by those issues; this slice is the Model Profile reference choice. Recheck before publication.
