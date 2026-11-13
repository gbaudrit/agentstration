# Consolidate collection filtering, paging and result states

## Technical category
Refactoring

Published issue: [#1097](https://github.com/gbaudrit/agentstration/issues/1097).

## Area
Cross-cutting

## Problem
Console lists, reference choices and usage views compose search, filters, result counts and pagination differently. DataGrid and mobile resource cards already exist, but a shared presentation contract does not establish whether a count represents a page, loaded data or the complete authorized query.

## Impact
Large collections can appear complete when only a subset is present. Consumers repeat keyboard, empty-state and loading behavior, and a client-side pager can hide unbounded background loading.

## Expected outcome
Existing tables and cards can share an explicit collection toolbar, pager and result-state composition. Each adapter declares its filtering mode and data bounds without moving family-owned queries into a generic component.

## Scope
- Consolidate ResourceToolbar, SearchBox and FilterBar; search clear stays inside the field.
- Support explicit immediate versus apply-to-search modes, active filters, refresh and reset-to-first-page when query criteria change.
- Present loading, refreshing, no data, no matches, expected error and access-denied states using existing feedback components.
- Define known total, unknown total and continuation-based paging presentations; loading a page must not imply an exact global count.
- Preserve mobile table/card integration and accessible controls; no live product route change in this task.

## Out of scope
New family APIs, server aggregates, advanced spreadsheet features, a universal data repository, replacing all grids, adopting a UI library, or implementing the Flow Run performance work owned by #989.

## Dependencies
Parent: local [FR-00](FR-00-resource-administration.md). Reuse conventions from [T-01](T-01-form-foundations.md) and inventory behavior from #815. Delivery consumers: FR-04, FR-05 and FR-06. API-side work, if needed, remains in those bounded family slices or #989.

## Implementation guidance
Keep paging/filter state separate from resource-edit state. Define how stale responses are ignored and cancellation works. Preserve exact owner scope, navigation context and authorization; counts must not disclose inaccessible items. Begin with current DataGrid and the Tools mobile-card pattern. Record a concrete unsupported requirement before evaluating QuickGrid, MudBlazor or Radzen; do not add a package as a prerequisite.

## Alternatives considered
A new grid library may eventually help advanced grid behavior but cannot supply authorization, correct totals or bounded endpoints. Per-screen implementations would retain the duplication. A generic query engine exceeds this presentation task.

## Acceptance criteria
- [ ] Fixtures demonstrate table and card consumers with query changes, page changes and refresh without marking a business form dirty.
- [ ] Exact totals, loaded counts and continuation-only results are distinguishable and localized.
- [ ] No-data and no-match states have appropriate actions; clearing search restores the unfiltered query.
- [ ] A late response cannot replace the results of a newer query; page correction after result removal is defined.
- [ ] Keyboard, accessible names, light/dark styles and mobile controls work without horizontal document overflow.
- [ ] Documentation identifies which bounded query guarantees each consumer must provide; there is no implicit fetch-all loop.

## Tests and validation
Component tests exercise state transitions, query/page events, stale responses and result-state wording with deterministic fixtures. Verify desktop/mobile and both themes. Delivery PRs add contract/isolation tests where endpoints change, browser smoke tests and the required offline lanes. New reusable UX journeys follow their delivery issue's validation gate.

## Additional context
Local planning ID: T-02. DataGrid appears in 54 Razor files in the audit baseline; this does not establish advanced feature support. Proposed labels: technical-task, triage:agent, triage:pending-review; no priority assigned.

## Attachments
[Cross-screen inventory](../analyse-des-ecrans-transverses.md), [reference prototype](../maquette-ecran-type.md).

## Preliminary checks
- [x] I searched for an existing issue covering this work. Open/closed review on 2026-11-09 identified #815 as an existing presentation example and #989 as the owner of Flow Run bounded loading. Recheck before publication.
