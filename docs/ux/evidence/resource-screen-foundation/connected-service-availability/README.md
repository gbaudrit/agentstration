# Connected Service availability diagnostics

The saved overview now places Diagnostics inside the Availability MetricCard, below its status. The separate diagnostics panel is removed. Observed time, code, message, absent-value placeholders and the unobserved state retain their existing localization and data. MetricCard accepts optional child content; existing cards without it retain their rendering.

## Validation

- 63 targeted component cases passed: ConnectedServiceComponentTests, ConnectedServiceLocalizationTests and protected AgentResourceTemplateTests. Existing EN/FR overview cases also check that the diagnostic timestamp and missing code/message appear exactly once inside a metric card.
- Release Web and Workplace host builds passed with zero warnings or errors.
- Two temporary smoke probes passed against the actual deterministic local executable, using authenticated API-created synthetic service types and services: English/dark desktop (1440 × 1000), French/light touch mobile (390 × 844). Both check three metric cards, exactly one diagnostic block inside Availability, all three observed fields, the expected theme and no document/card horizontal overflow. Captures below were inspected visually.
- This is a layout adjustment within the existing overview; a new reusable user journey is not applicable. Full solution suites and CI were not run for this focused follow-up.

## Captures

- [English desktop](availability-desktop-en.png)
- [French mobile](availability-mobile-fr.png)
