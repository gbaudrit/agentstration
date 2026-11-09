# Adopt shared UX patterns across a bounded set of Console and Workplace screens

## Area
Cross-cutting

## Feature type
User-facing capability

## Console or Workplace UX impact
Console and Workplace UX change

## Problem or need
The pilot screens alone would leave users facing inconsistent forms, list filters and dialogs elsewhere. Adoption needs explicit waves and a finite scope so that a shared-component program does not turn into an unreviewable rewrite of every surface.

## Expected outcome
A second set of representative forms, a populated catalog, a modal workflow and Workplace filters use the validated shared contracts. An adoption matrix states what was changed, reused without change or deferred, preserving domain-specific behavior.

## Proposed solution
Use small family-owned PRs after the pilots are accepted. Reuse existing components and the validated tokens/behavior rather than rewriting rendering technology. Restrict each wave to the relevant shared contract, not the whole feature area.

## Scope
The finite rollout below is the closure scope. Any expansion needs its own bounded follow-up.

| Wave | Routes | Shared behavior to adopt |
| --- | --- | --- |
| A — ordinary forms | `/runtimeprofiles/new`, `/runtimeprofiles/{Name}`, `/tools/definitions/new`, `/tools/definitions/{Name}`, `/triggers/new`, `/triggers/{Name}/edit` | Field/section structure, adjacent existing identity, appropriate clearing, validation and safe save actions. Preserve each family's typed/schema controls, rights and deployment semantics. |
| B — populated collection | `/tools` | Search/filter/result states using T-02; preserve current mobile cards and exact resource navigation. Keep existing data bounds explicit; no new advanced grid features. |
| C — modal composition | `/packs`, existing install dialog | Shared field/actions/feedback styling and existing dialog contract, with focus containment, Escape behavior when safe, waiting/error state and focus restoration. Preserve install/preview behavior. |
| D — Workplace filters | `/w/{WorkspaceName}/tasks` | Shared search/filter field presentation and clearing, result states and localized connection labels in this route. Preserve valid Task statuses and the page's existing filtering semantics. No forced resource tabs. |

Update the complete audit matrix with one of adopted, already compatible or deferred for every audited family. Record why Razor Pages/account screens share styling conventions where useful without forcing Blazor component reuse.

## Out of scope
Automatic migration of all 220 Razor files; a Workplace conversation/task-detail redesign; Flow Designer; a new login/account workflow; generic dependency APIs; advanced grid selection/virtualization; the Flow Run API performance work in #989; broad fixes to all issues found during the audit.

## Dependencies
Parent: [FR-00](FR-00-resource-administration.md). Depends on accepted pilot outcomes from [FR-01](FR-01-clearable-fields.md), [FR-02](FR-02-safe-editing.md), [FR-03](FR-03-resource-pages.md), [FR-04](FR-04-resource-reference.md) and [FR-05](FR-05-resource-usages.md). Reuse T-01/T-02. Coordinate with active family work before touching those routes; reconcile latest main and integration branches rather than duplicating their implementations.

## Alternatives considered
A global search-and-replace could change value semantics and keyboard behavior. A complete UI-library migration would enlarge the scope without resolving domain behavior. Leaving adoption indefinite would make completion impossible to assess.

## Deployment impact
None

## Acceptance criteria
- [ ] Each named wave uses the relevant common behaviors and records the changed components and routes in the adoption matrix.
- [ ] Existing naming policies, exact resource references, authorization, schema/value-binding controls and domain actions retain their semantics.
- [ ] Tools retains its mobile cards and clear resource navigation; clearing a filter restores the applicable collection without modifying business data.
- [ ] Pack dialog focus stays within the modal, returns to its trigger on close, and safe Escape behavior is explicit; submitted/waiting states cannot accidentally duplicate installation.
- [ ] Workplace filtering preserves its domain status options and uses localized field, result-state and connection labels; no model-profile or resource-page concepts are introduced there.
- [ ] Every wave passes keyboard, desktop/mobile, light/dark and en-US/fr-FR checks, plus relevant regression and offline validation.
- [ ] All other audited families have an explicit disposition; deferred screens are not claimed as migrated. Closure waits for required scenario children or documented maintainer waivers.

## UX test scenario follow-up
Children: pending parent publication. Before the first UX PR for a wave, create and natively attach a separate Technical task scenario child for that coherent route group, labeled technical-task and test-scenario-task. Do not create one giant journey that couples all four waves. Each new reusable journey waits for its wave's explicit maintainer UX approval and merged implementation PR. Existing tests, component behavior and browser smoke remain in each UX PR.

- Wave A routes: listed above. Fixtures: disposable writable and read-only resources using deterministic providers; valid and invalid form inputs. Actions: create, clear required/optional fields, save/reopen, change display name and cancel departure. Assert validation, stable identity, retained drafts and denied editing where applicable.
- Wave B route: `/tools`. Fixtures: local populated catalog, multiple categories and a no-match query. Actions: filter, clear, inspect details and return. Assert valid result scope/count wording, stable navigation and mobile cards; expected failures show no-match/error without implying an empty catalog.
- Wave C route: `/packs`. Fixtures: deterministic valid and invalid local Pack archives plus controlled expected failures; no online registry. Actions: open, Tab/Shift+Tab, cancel/Escape, preview, submit and retry where allowed. Assert focus containment/restoration, no duplicate install, and preserved safe error feedback. Respect existing preview/installation tests rather than duplicating their business logic.
- Wave D route: `/w/{WorkspaceName}/tasks`. Fixtures: authorized local Workspace with supported Task statuses, a no-match state and a denied Workspace. Actions: change/clear filters and open a permitted task. Assert correct results and localized labels; inaccessible Workspace data must never appear. New filter behavior, if required, is explicitly reviewed rather than silently substituted for the current semantics.

## Additional context
Local planning ID: FR-06. The broader inventory includes entries, sources, administration, operational details and account pages; their deferred disposition is required, their migration is not. Concrete audit bugs requiring an independent correction use Bug issues rather than being hidden as new capabilities. Proposed labels: enhancement, triage:agent, triage:pending-review; no priority assigned.

## Attachments
[Full audit scope](../unification-des-formulaires.md), [23 additional screens](../analyse-des-ecrans-transverses.md), [reference prototype](../maquette-ecran-type.md).

## Preliminary checks
- [x] I searched for an existing issue covering this request. Open/closed review on 2026-11-09 considered #539, #795, #815, #989, #1051 and #1054. Their existing work is retained; verify current family issues and browser journeys again before each wave and publication.
