# ADR-0080: Browser campaigns use dedicated Workspaces

## Context

Browser journeys serve UX validation and communication captures. Both need repeatable data without depending on resource labels translated for the current UI culture. A campaign may replay the same behavior across locales, themes, viewports, and product revisions. Encoding those dimensions into selectors or treating a localized Workspace as the campaign itself would couple product navigation, test data, and editorial output.

## Decision

A browser campaign uses a dedicated Agentstration Workspace as its data-isolation boundary. Workspace technical names remain stable and locale-neutral. Locale, theme, viewport, product revision, scenario input, selected checkpoints, and generated evidence remain explicit dimensions outside the Workspace.

Agentstration owns a reusable `create-workspace` Playwright journey. It creates and selects a new Workspace and fails when its technical name already exists. Other journeys may select a prepared Workspace by technical name, but they do not implicitly create, replace, reuse, or delete one. A caller prepares required profiles, Packs, and other resources explicitly inside that Workspace.

Playwright automation vocabulary is maintained through three independent TypeScript contracts: Test IDs in `src/contracts/test-ids.ts`, checkpoint names in `src/contracts/checkpoints.ts`, and expected localized text in `src/locales/expected-text.ts`. Localization expectations are intentionally not generated from product RESX resources, so assertions can detect missing or incorrect translations.

## Consequences

- The same journey and clean technical identifiers can be reused across locales.
- Parallel or repeated campaigns can isolate product data by choosing distinct Workspace names.
- Capture plans remain reproducible because non-data dimensions are visible in the plan instead of hidden in Workspace state.
- Journeys fail early when campaign setup is missing or collides with existing state.
- Persistent multi-step campaigns require a shared target instance; independently started local capture commands do not share state.
- Callers remain responsible for explicit campaign setup and lifecycle cleanup.
