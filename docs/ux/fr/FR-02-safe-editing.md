# Preserve resource drafts and make save outcomes predictable

Published issue: [#1091](https://github.com/gbaudrit/agentstration/issues/1091); parent [#1089](https://github.com/gbaudrit/agentstration/issues/1089).

## Area
Console

## Feature type
User-facing capability

## Console or Workplace UX impact
Console UX change

## Problem or need
An administrator can lose an Agent draft when following another action, and raw YAML edits can disappear when switching views. Search-only state can incorrectly mark an editor as changed. Save actions and failure feedback differ between resource forms, making it unclear what is saved or deployed.

## Expected outcome
The Agent pilot preserves drafts during in-page navigation, protects departure from unsaved changes and reports save versus deployment outcomes accurately. The same behavior can be adopted by ordinary resource editors without absorbing their domain state into a universal editor engine.

## Proposed solution
Separate the editing contract from FormActions. Reuse UnsavedChangesBar, existing confirmation and API error presentation. Track the saved baseline and actual editable values; keep lookup filters, tabs and other UI-only state outside dirty-state comparison. Domain adapters retain validation, YAML conversion and deployment actions.

## Scope
- Pilot `/agents/new` and `/agents/{Name}`, including navigation to `/agents/{Name}/run` and leaving the page.
- Preserve form and raw YAML drafts across in-page views, including invalid YAML. Define an explicit resolution when a structured edit would conflict with a raw draft; never silently discard one representation.
- Common unchanged/changed/invalid/saving/saved/error presentation and action placement. Invalid or failed submission keeps entered values and associates actionable errors with the form.
- Protect internal navigation and use the supported browser departure warning for reload/close. Cancellation returns to the draft with useful focus; confirmed discard is explicit.
- Prevent duplicate submission. If editing remains enabled during asynchronous save, a response for the submitted snapshot cannot replace newer edits or mark them saved. Disabling edits during save is an acceptable explicit alternative.
- Keep definition save and deployment/reconciliation separate: a deployment failure after successful save must not report that the definition failed to save.

## Out of scope
Durable offline drafts, a new autosave service, changing backend concurrency policy, rewriting Flow Designer state, domain deployment changes or the API/session resilience architecture owned by #215.

## Dependencies
Parent: [FR-00](FR-00-resource-administration.md). Depends on [FR-01](FR-01-clearable-fields.md). Preserve the Flow Designer correction from #988; it is related evidence, not work to repeat. Compose safe error contracts available from #215 without making its entire program a hard prerequisite.

## Alternatives considered
A persistent action bar alone cannot preserve drafts or identify actual changes. Unconditionally blocking every tab switch is unnecessary when the draft can remain in memory. Automatically saving before execution would change the user's requested operation.

## Deployment impact
None

## Acceptance criteria
- [ ] Changing a business value marks the resource dirty; reverting it to the saved value clears that state. Searching profiles or changing tabs does neither.
- [ ] Switching form/YAML views preserves drafts; invalid YAML stays editable and is never replaced by saved YAML without an explicit decision.
- [ ] Leaving a dirty editor offers a localized discard/cancel decision; cancellation preserves the full draft and returns focus appropriately.
- [ ] Required validation and expected API failures retain inputs; save actions prevent duplicate submission and offer an appropriate retry.
- [ ] A slow save cannot discard later edits or falsely mark them saved. A post-save deployment failure clearly preserves the successful definition save.
- [ ] Actions remain reachable without obscuring the last field at desktop/mobile widths and browser zoom, in light/dark themes and en-US/fr-FR.

## UX test scenario follow-up
Scenario children [#1099](https://github.com/gbaudrit/agentstration/issues/1099) are published and natively attached. Their new reusable journeys wait for explicit maintainer UX validation and merged implementation PR. Existing regression repairs, behavior tests and required smoke checks remain in the UX PR.

Routes: `/agents/new`, `/agents/{Name}`, `/agents/{Name}/run`, `/agents`. Fixtures: deterministic Agent, valid local Model Profile, controllable delayed/failed save and failed deployment responses, no live LLM. Actions: edit/revert, filter profiles, switch structured/YAML views with invalid YAML, cancel/confirm departure, retry a failed save and exercise a delayed save. Success: drafts retained, UI-only changes stay clean, successful save distinguished from failed deployment. Expected failures: validation/server rejection preserves values; duplicate save is prevented; late responses do not erase subsequent edits. Run desktop/mobile and both themes; do not repurpose the Flow Designer journey as proof of ordinary-form coverage.

## Additional context
Local planning ID: FR-02. This owns editing behavior; FR-03 owns resource page composition. Existing defects can receive separate Bug reports when they require independent tracking rather than becoming additional feature scope. Proposed labels: enhancement, triage:agent, triage:pending-review; no priority assigned.

## Attachments
[Agent observations](../etat-des-lieux-fiche-agent.md), [interactive prototype](../maquette-ecran-type.md).

## Preliminary checks
- [x] I searched for an existing issue covering this request. Open/closed review on 2026-11-09 found #988 for Flow Designer save races and #215 for API resilience; neither is reimplemented here. Recheck before publication.
