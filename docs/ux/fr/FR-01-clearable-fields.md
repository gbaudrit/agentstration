# Clear field values in place and keep resource identity easy to understand

Published issue: [#1090](https://github.com/gbaudrit/agentstration/issues/1090); parent [#1089](https://github.com/gbaudrit/agentstration/issues/1089).

## Area
Console

## Feature type
User-facing capability

## Console or Workplace UX impact
Console UX change

## Problem or need
Users need to clear a value without looking for a separate action outside its field. Labels, help, validation and empty-value behavior vary across forms. Resource identity should remain understandable while users edit: the technical name belongs beside the display name and must keep the existing automatic/manual creation behavior.

## Expected outcome
Parameter and Model Profile forms demonstrate reusable fields with integrated clearing, consistent feedback and adjacent identity fields. Clearing never silently selects another value, renames a saved resource or deletes a stored secret.

## Proposed solution
Compose the T-01 field structure with clearable text, textarea, numeric and selection variants as needed by the pilots. Reuse ResourceIdentityFields and the naming policies delivered by #539. Extend existing controls rather than introduce parallel identity or schema editors.

## Scope
- Pilots: `/parameters/new`, `/parameters/{Name}`, then `/modelprofiles/new` and `/modelprofiles/{Name}`.
- A visible clear action inside a populated editable field, with a localized accessible name and keyboard activation. Return focus to the underlying control after clearing; preserve normal text-selection behavior.
- Empty optional values remain empty. Required values can be cleared and then display the existing required validation. Read-only/disabled fields expose no active clear action.
- Numbers preserve absence rather than converting it to zero; selections preserve absence rather than choosing the first option. Each existing domain adapter owns whether null, unset or an empty string is valid.
- Display name precedes the adjacent technical name. At creation, auto-generation stops on a manual technical-name edit, including intentional clear; explicit prefills are preserved. A saved technical name remains immutable when the display name changes.

## Out of scope
New naming rules, automatic uniqueness lookups, resource renaming, changing stored values or API semantics, deleting secrets, or replacing typed binding/literal editors owned by #1051/#1054. Broad adoption belongs to FR-06.

## Dependencies
Parent: [FR-00](FR-00-resource-administration.md). Depends on [T-01](T-01-form-foundations.md). Reuse #539 and #578; preserve contextual creation from #509 and typed editor behavior from #1051/#1054.

## Alternatives considered
An external reset button breaks the requested in-field interaction. Resetting the whole form is a different action. Native browser search clearing does not supply the same contract for textareas, selects and typed values.

## Deployment impact
None

## Acceptance criteria
- [ ] Populated editable fields expose an integrated, localized clear action; empty, read-only and disabled states do not expose an actionable clear control.
- [ ] Mouse and keyboard clearing update the bound value once, retain focus, trigger normal change/validation behavior and do not submit the form.
- [ ] Clearing a required value yields a field-associated error on the normal validation cycle; optional values remain empty without fallback selection.
- [ ] Numeric absence remains distinct from zero, and no generic conversion changes a specialized editor's null/unset/literal semantics.
- [ ] Display/technical names remain adjacent in desktop and mobile layouts, with existing automatic, explicit-prefill and manual-override behavior preserved.
- [ ] Reopening a saved resource and changing its display name leaves its technical identity and references unchanged.
- [ ] Light/dark rendering, focus visibility and complete en-US/fr-FR localization pass on both pilots.

## UX test scenario follow-up
Scenario children [#1098](https://github.com/gbaudrit/agentstration/issues/1098) are published and natively attached. Their new reusable journeys wait for explicit maintainer UX validation and merged implementation PR. Existing regression repairs, behavior tests and required smoke checks remain in the UX PR.

Routes: the four pilot routes above. Prerequisites: deterministic writable scope, disposable Parameter and Model Profile, a read-only resource, unique proposed technical names and no remote provider dependency. Actions: create with an accented display name, manually override and clear the proposed technical name, exercise required/optional fields, save/reopen and rename the display name. Success: focus retained, intended empty values persisted where allowed, identity stable after creation, adjacent fields usable on mobile. Expected failures: invalid/duplicate name and missing required value remain visible without losing other input; read-only values cannot be cleared. Compare existing #578 journey before adding coverage; only an evidenced overlap can be marked Not applicable.

## Additional context
Local planning ID: FR-01. For future Secret adoption, clearing a replacement input means no replacement according to the existing editor contract, never deletion of the stored secret. Proposed labels: enhancement, triage:agent, triage:pending-review; no priority assigned.

## Attachments
[Field inventory](../unification-des-formulaires.md), [prototype guide](../maquette-ecran-type.md).

## Preliminary checks
- [x] I searched for an existing issue covering this request. Open/closed review on 2026-11-09 identified #539/#578 as delivered identity behavior and #1054 as the literal-value owner; this request adds shared clearing/composition rather than recreating them. Recheck before publication.
