# Unify resource administration with shared forms and collection patterns

Published issue: [#1089](https://github.com/gbaudrit/agentstration/issues/1089).

## Area
Cross-cutting

## Feature type
User-facing capability

## Console or Workplace UX impact
Console and Workplace UX change

## Problem or need
Resource administrators encounter different field, save, navigation and collection behaviors across otherwise similar screens. The Agent example exposes these inconsistencies, but the need also applies to model profiles, parameters, tools and other administration screens. A visual restyle alone would leave draft loss, ambiguous clearing and large selections unresolved.

## Expected outcome
Users encounter predictable field clearing, editing, resource navigation and collection browsing. A small shared base supports light/dark themes, keyboard access and mobile presentation without replacing domain-specific workflows.

## Proposed solution
Deliver the bounded child slices in the [delivery plan](../plan-fr-unification.md), validating Parameter, Model Profile and Agent pilots before further adoption. Consolidate existing components rather than introduce a new forms engine or require a global UI library.

## Scope
- Foundation tasks: [T-01](T-01-form-foundations.md), [T-02](T-02-collection-foundations.md).
- User outcomes: [FR-01](FR-01-clearable-fields.md), [FR-02](FR-02-safe-editing.md), [FR-03](FR-03-resource-pages.md), [FR-04](FR-04-resource-reference.md), [FR-05](FR-05-resource-usages.md), [FR-06](FR-06-phased-adoption.md).
- Common acceptance: en-US/fr-FR localization, light/dark themes, keyboard, responsive layout, existing ownership and authorization rules, deterministic offline validation.
- Maintain an explicit adoption matrix, with deferred screens identified rather than implicitly included in closure.

## Out of scope
New resource kinds, a generic dependency graph, automatic identity renaming, a Flow Designer rewrite, execution changes, mandatory third-party UI library adoption or migration of every screen in a single release.

## Dependencies
No hard prerequisite. Reuse #539/#578 (identity), #795 (Access), #898 (Model Profile usages), #509 (contextual creation) and #815 (inventory filters). Coordinate with #1051/#1054 (typed bindings), #1026 (qualified names), #215 (API resilience) and #989 (Flow Run loading); they retain their existing responsibilities.

## Alternatives considered
A global UI library would not define draft, authorization or reference semantics. An Agent-only redesign would leave the same inconsistencies elsewhere. A one-shot migration would make behavior regressions harder to isolate.

## Deployment impact
None

## Acceptance criteria
- [ ] The pilots demonstrate the common behaviors while preserving each family's capabilities, routes, rights and immutable identifiers.
- [ ] Every child has a bounded scope and observable acceptance criteria; product rollout does not wait for an unrelated screen migration.
- [ ] The FR-06 adoption matrix records completed, adapted and deferred surfaces.
- [ ] No mandatory external service or global UI library is introduced by this program.
- [ ] Shared components accept semantic token overrides, demonstrated with a test theme without a markup rewrite; existing Light/Dark/System preferences remain compatible.
- [ ] All native Sub-issues and required scenario children are complete, or explicit maintainer exceptions are recorded before closure.

## UX test scenario follow-up
The parent introduces no separate route or implementation PR. The changed journeys and deterministic scenarios are owned by FR-01 through FR-06. Delivery issues #1090–#1097 are published and natively attached; scenario children #1098–#1106 are also attached to their FRs. Each UX delivery creates its own scenario child before its UX PR; no blanket claim of existing browser coverage is made. Keep this parent open until the delivery and scenario children are closed or explicitly waived.

## Additional context
Local planning ID: FR-00, not a GitHub issue number. Source baseline: main at 8a9fafd7432e28c7aab23109c43593cc90b35c66. Reconcile newer merged/integration work before implementation. Proposed labels on publication: enhancement, triage:agent, triage:pending-review; no priority assigned.

## Attachments
[Audit and component inventory](../unification-des-formulaires.md), [cross-screen audit](../analyse-des-ecrans-transverses.md), [prototype guide](../maquette-ecran-type.md).

## Preliminary checks
- [x] I searched for an existing issue covering this request. Open and closed issues were reviewed on 2026-11-09; reuse and overlap decisions are recorded in the delivery plan. Recheck before publication.
