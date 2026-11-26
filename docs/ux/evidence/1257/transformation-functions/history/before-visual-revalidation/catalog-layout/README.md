# Transformation function catalogue column regression

The new Actions column exposed an existing family rule that assigned 51% of the table to `th:last-child`. That selector now matched Actions, leaving the Description column approximately **0.03px** wide. Text wrapped one character per line and headers overlapped.

The correction keeps the original first three column proportions, leaves the fourth (Description) flexible and gives the fifth (Actions) its own **8rem** budget. The full description remains visible and wraps normally. Explicit narrow-screen tables retain their contained horizontal scrolling; cards remain available.

## Reproduction and verification

- The new existing-collection regression case failed before the CSS change on the French/light 1659×1000 viewport: Description was **0.03125px**, below its 240px readability bound. [Before](before-desktop-fr-FR-light.png).
- Release Web build: passed, zero warnings/errors. TypeScript: passed.
- Four browser variants cover English/dark and French/light on desktop and mobile. They inspect both table and cards, description width and native row height, header bounds, the Actions menu with keyboard Escape and mobile touch, and document overflow.
- [Observations and source hashes](observations.json) retain the failing measurement and the passing row geometry. The French desktop description is approximately **527px** after correction.
- [French desktop table](desktop-fr-FR-light-table.png), [French mobile cards](mobile-fr-FR-light-cards.png), [English desktop table](desktop-en-US-dark-table.png), [English mobile cards](mobile-en-US-dark-cards.png).

These checks maintain collection-layout regressions. The separate all-family Actions/Preview/history scenario remains gated in #1258. The earlier Summary/Preview screenshots are separate evidence and did not establish the missing catalogue-column width check.
