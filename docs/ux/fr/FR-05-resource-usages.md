# Browse large Model Profile usage lists with bounded filtering and paging

Published issue: [#1094](https://github.com/gbaudrit/agentstration/issues/1094); parent [#1089](https://github.com/gbaudrit/agentstration/issues/1089).

## Area
Console

## Feature type
User-facing capability

## Console or Workplace UX impact
Console UX change

## Problem or need
The list of resources using a Model Profile can be long. Administrators need to find and inspect a consumer without an unbounded list occupying the editing form. Existing cross-scope usage enumeration and change-impact warnings already exist under #898; the remaining need is scalable presentation and bounded query behavior.

## Expected outcome
Model Profile overview exposes a dedicated, searchable and paginated Used by collection. The editor shows a concise supported usage summary and a link to that section. Counts and results remain accurate for the authorized scope and current query.

## Proposed solution
Compose T-02 collection controls with the existing Model Profile usage service and client. Keep the existing Usages tab/deep links working, redirecting or composing them with the overview section where appropriate. Extend only the family-owned query contract needed for bounded filtering/paging; do not infer a generic Agent or Flow usage graph from the prototype.

## Scope
- Pilot `/modelprofiles/{Name}` with existing authorized direct Agent consumers from #898.
- Search supported consumer identity fields and page results. Offer type filtering only when the family contract exposes multiple consumer types; an Agent-only response must not imply other types are indexed.
- Display identity, owner scope and supported status, with navigation to a permitted consumer detail route. Preserve context according to existing navigation rules.
- Define filtered versus overall authorized totals, or use an explicit unknown-total/continuation presentation. Never label one page's count as a global total.
- Add bounded query support through Models-owned contracts/application/API/client where needed. Filtering must apply to the whole supported query, not just the visible page. Preserve cross-scope authorization and existing impact warnings.
- Keep editing drafts safe when opening a consumer via FR-02; do not force a save just to inspect dependencies.

## Out of scope
Reimplementing #898, transitive impact analysis, a generic dependency graph, declaring unsupported Agent/Flow usage APIs, adding consumer types without family evidence, changing access rules or altering reconciliation semantics.

## Dependencies
Parent: [FR-00](FR-00-resource-administration.md). Depends on [FR-03](FR-03-resource-pages.md) and [T-02](T-02-collection-foundations.md). Reuse #898 as the completed enumeration/impact foundation. Other resource families require a separately scoped adaptation after their usage contracts are verified.

## Alternatives considered
Rendering every usage inside the edit form would overwhelm the page. Client-only pagination of a fully loaded collection does not bound work. A shared graph service is unnecessary for this direct-consumer pilot. The existing standalone Usages route/tab can remain a compatible entry point.

## Deployment impact
None

## Acceptance criteria
- [ ] The overview provides a dedicated Used by collection; the editor shows a concise summary and navigation rather than every consumer.
- [ ] Multi-page search finds a permitted consumer outside the initial page; query changes reset paging and obsolete responses cannot replace newer results.
- [ ] Result work and payload are bounded by the requested page/query; total semantics are documented and never derived from a partial page as if complete.
- [ ] Results and any counts apply the same authorization rules; inaccessible consumers cannot be inferred from totals or filter options.
- [ ] Exact scope/namespace/name references open the intended authorized consumer, including duplicate names in different scopes.
- [ ] Existing usage enumeration, impact warnings and Usages deep links remain valid; unsupported types are not advertised.
- [ ] No-data, no-matches and expected error states are distinguishable; both themes, mobile, keyboard and en-US/fr-FR pass.

## UX test scenario follow-up
Scenario children [#1102](https://github.com/gbaudrit/agentstration/issues/1102) are published and natively attached. Their new reusable journeys wait for explicit maintainer UX validation and merged implementation PR. Existing regression repairs, behavior tests and required smoke checks remain in the UX PR.

Routes: `/modelprofiles/{Name}` and authorized `/agents/{Name}` detail links, preserving existing usage deep links. Fixtures: deterministic Instance- and Tenant-owned profiles with enough authorized direct consumers for multiple pages (the prototype illustrates 32 usages), repeated names in distinct Workspaces, a restricted user, one unused profile and controllable API failure. Actions: open overview, search for a later-page consumer, clear filters, change page, open a permitted consumer, and return with an unsaved draft where applicable. Success: correct references/count semantics, bounded requests, stable draft behavior and recoverable no-match state. Expected failures: unauthorized consumers/counts are absent, errors retain query context and allow retry, a removed result does not leave an unusable page. No remote LLM is required.

## Additional context
Local planning ID: FR-05. The audit found GetModelProfileUsagesAsync and Models-owned usage endpoints; no generic Agent usages endpoint was demonstrated. Type filters follow real API capability, not the broader fake data in the HTML prototype. Proposed labels: enhancement, triage:agent, triage:pending-review; no priority assigned.

## Attachments
[Usage presentation prototype](../maquette-ecran-type.md), [cross-screen inventory](../analyse-des-ecrans-transverses.md).

## Preliminary checks
- [x] I searched for an existing issue covering this request. Open/closed review on 2026-11-09 identified completed #898. This request preserves that capability and adds collection presentation/bounds; it does not repeat cross-scope enumeration as a new feature. Recheck before publication.
