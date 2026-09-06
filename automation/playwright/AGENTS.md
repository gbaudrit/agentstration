# Playwright automation instructions

These instructions apply to `automation/playwright`.

## Stable contracts

- Declare every Playwright-owned `data-testid` value in `src/contracts/test-ids.ts` and use those constants from page objects. Razor markup remains the contract provider and must use the matching literal because TypeScript constants are not a runtime product dependency.
- Prefer accessible roles and labels for locale-specific semantic tests. Add a Test ID only for cross-locale journeys, ambiguous controls, stable capture targets, or non-visible readiness state.
- Declare journey checkpoint names in `src/contracts/checkpoints.ts`. Checkpoint values are external capture contracts: do not rename or remove one without updating example plans, documentation, and consumers. Add a new semantic state instead of encoding step numbers or layout positions.
- Keep independent localization expectations in `src/locales/expected-text.ts`. Never derive these expectations from product `.resx` files; doing so would make localization assertions circular. Add every supported locale value when extending the contract.

## Page objects and journeys

- Page objects own all locators and expose semantic operations or stable capture targets. Journeys must not contain raw Test ID strings, CSS selectors, or localized product text.
- A checkpoint represents a stable user-visible or domain state after the relevant wait condition succeeds. Emit checkpoints after meaningful blocks, not after arbitrary sleeps or individual DOM operations.
- Workspace creation and selection belong to the workspace page object and journey. Other journeys may select a prepared campaign workspace, but must not silently create, delete, or replace one.
- Treat a campaign Workspace as test-data isolation, not as localization state. Locale, theme, viewport, product revision, and output evidence remain separate campaign dimensions.

## Validation

- Add or update a Playwright test with every journey behavior change.
- Run `npm exec tsc -- --noEmit` and the affected Playwright test. Run `npm run test:smoke` before handoff when product hosts or shared page objects change.
