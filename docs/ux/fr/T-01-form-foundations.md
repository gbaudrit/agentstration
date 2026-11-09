# Consolidate shared form structure and field presentation

## Technical category
Refactoring

Published issue: [#1096](https://github.com/gbaudrit/agentstration/issues/1096).

## Area
Cross-cutting

## Problem
Shared CSS already provides form-section, form-grid and form-actions, but screens duplicate markup for labels, help, validation, identity and actions. This allows spacing, accessible associations and theme behavior to diverge. Existing state and dialog components should remain the common implementation points.

## Impact
Each subsequent UX fix requires repeated edits across consumers, with inconsistent keyboard, validation and localization results. A replacement forms framework would duplicate domain editors already present.

## Expected outcome
A documented, tested composition for fields, sections and responsive form layouts is available in the existing shared component boundary. Product adoption remains in the delivery FRs.

## Scope
- Define or consolidate FormField, FormSection and FormGrid responsibilities, creating wrappers only where they remove real duplication.
- Associate label, optional help and validation with stable field IDs; support required, optional, invalid, disabled, read-only and loading presentation.
- Define styling slots for integrated clear actions and the existing identity, scope and specialized input components.
- Reuse current theme tokens and feedback/dialog components; document contrast, focus, stacking and mobile layout expectations for both themes.
- Establish semantic component tokens for surfaces, text, borders, focus, validation, spacing and radii. Keep theme identity separate from the existing Light/Dark/System appearance preference so future named themes can override tokens without changing component markup or value semantics. No theme editor or new persisted preference is required in this slice.
- Provide test fixtures or existing component-test examples; no new public demo route is required.

## Out of scope
Product screen migration, save-state orchestration, a schema-driven forms engine, new identity normalization rules, a new UI library or a second set of EmptyState/ErrorState/LoadingState components.

## Dependencies
Parent: local [FR-00](FR-00-resource-administration.md); attach natively after publication. Existing ResourceIdentityFields and the naming policies delivered by #539 are inputs, not new work. Typed editors remain owned by #1051/#1054.

## Implementation guidance
Start in Agentstration.Web.Components and current CSS ownership. Respect Blazor EditContext and existing validation mechanisms; composition must not require replacing all domain model types. Treat names above as responsibilities, not a mandate for an inheritance hierarchy. Reuse en-US/fr-FR catalogs and ensure theme tokens work in both hosts. No live product journey changes in this foundation-only task; any such change belongs to an associated UX delivery with its scenario follow-up.

## Alternatives considered
CSS-only conventions leave accessible wiring repeated. A universal form generator or mandatory external library would exceed the demonstrated need. Extract only the repeated structure that the pilots can consume.

## Acceptance criteria
- [ ] Fixtures demonstrate all supported field states with correctly associated labels, help and errors, and no duplicate IDs when several instances render.
- [ ] Existing identity/scope and schema-based editors can be composed without changing their value semantics.
- [ ] Light/dark themes and 390 px layouts keep field content, focus and actions usable.
- [ ] A documented test-only token override changes a representative form and collection consistently without replacing markup, duplicating selectors or changing the existing appearance-preference API. New shared components contain no hard-coded palette colors.
- [ ] en-US and fr-FR catalogs remain valid and key-symmetric; no new product text is hard-coded.
- [ ] The component contract and pilot usage are documented without adding a new public route or migrating product screens in this task.

## Tests and validation
Use focused component rendering/behavior tests for associations, states and repeated instances; verify representative fixtures at desktop/mobile sizes and in both themes. Run applicable project/build checks. Product adoption PRs own browser smoke tests and the required offline functional lanes; defer only new reusable UX journeys under the repository workflow.

## Additional context
Local planning ID: T-01. Audit evidence includes 35 files using form-section, 33 using form-grid and 28 consumers of ResourceIdentityFields; these are static file counts, not coverage measurements. Proposed labels: technical-task, triage:agent, triage:pending-review; no priority assigned.

## Attachments
[Component inventory](../unification-des-formulaires.md), [reference prototype](../maquette-ecran-type.md).

## Preliminary checks
- [x] I searched for an existing issue covering this work. Reviewed open and closed issues on 2026-11-09, including #539/#578 and #1051/#1054; their naming and typed-value responsibilities remain unchanged. Recheck before publication.
