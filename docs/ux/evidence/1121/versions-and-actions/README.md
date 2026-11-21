# Version placement and configurable primary actions (#1121)

These captures come from the actual executable application and its maintained distribution/governed-inventory browser journeys. They supersede the earlier version-column/menu presentation for the affected resources. Capture files are unedited Playwright screenshots.

The resource's own version metadata is aligned under its qualified identity; Data Source Profile active/draft versions retain separate labels. Agent generation is spelled out and remains a definition-change counter. Exact identity copy and navigation, scope, state and governance keep their prior meanings.

Family adapters configure multiple directly visible primary commands and optional secondary commands in Actions. Model Profile Choose is directly visible. Extension enrollment/renewal/unenrollment commands remain guarded; credential revocation stays secondary with its keyboard/Escape/focus contract.

## Capture matrix

| Resource | Desktop English dark | Desktop English light | Mobile French dark | Mobile French light |
| --- | --- | --- | --- | --- |
| Data Source Profiles | [cards](./data-source-profiles-en-US-dark.png) | [cards](./data-source-profiles-en-US-light.png) | [cards](./data-source-profiles-fr-FR-dark.png) | [cards](./data-source-profiles-fr-FR-light.png) |
| Flows | [cards](./flows-en-US-dark.png) | [cards](./flows-en-US-light.png) | [cards](./flows-fr-FR-dark.png) | [cards](./flows-fr-FR-light.png) |
| Agents | [cards](./agents-en-US-dark.png) | [cards](./agents-en-US-light.png) | [cards](./agents-fr-FR-dark.png) | [cards](./agents-fr-FR-light.png) |
| Extension catalog | [cards](./extensions-en-US-dark.png) | [cards](./extensions-en-US-light.png) | [cards](./extensions-fr-FR-dark.png) | [cards](./extensions-fr-FR-light.png) |
| Model Profile picker | [cards](./profile-primary-choice-en-US-dark.png) | [cards](./profile-primary-choice-en-US-light.png) | [cards](./profile-primary-choice-fr-FR-dark.png) | [cards](./profile-primary-choice-fr-FR-light.png) |

Desktop table captures accompany the four collections above using the `-table-en-US-dark.png` and `-table-en-US-light.png` names. Four independent distribution variants check both table and card rendering, full localized version roles, alignment and bounded viewport width; together they capture 28 images across the four variants, including direct Model Profile choice.

The local runner uses the installed `/usr/bin/chromium` and disables failure-video recording because the packaged Playwright browser/ffmpeg are absent. Assertions, test timeouts, screenshots and traces remain enabled. CI uses the unchanged pinned Playwright browser configuration.

Implemented-UX acceptance and both merges remain pending. These observations do not imply approval.

## Extension commands

| Locale / viewport | Dark | Light |
| --- | --- | --- |
| en-US / desktop | [Capture](./enrollment-primary-actions-en-US-desktop-dark.png) | [Capture](./enrollment-primary-actions-en-US-desktop-light.png) |
| en-US / mobile | [Capture](./enrollment-primary-actions-en-US-mobile-dark.png) | [Capture](./enrollment-primary-actions-en-US-mobile-light.png) |
| fr-FR / desktop | [Capture](./enrollment-primary-actions-fr-FR-desktop-dark.png) | [Capture](./enrollment-primary-actions-fr-FR-desktop-light.png) |
| fr-FR / mobile | [Capture](./enrollment-primary-actions-fr-FR-mobile-dark.png) | [Capture](./enrollment-primary-actions-fr-FR-mobile-light.png) |

All **nine maintained browser cases passed**, zero failed/skipped/flaky, producing **36 captures** (28 version/selection views and eight Extension command views). The Extension captures include a real pending announcement and two available enrollments; commands are tested without executing credential mutations. ArrowDown opens the secondary menu, Escape dismisses it and restores summary focus.

Direct navigation additionally waits for the shared permission context before loading enrollments. The delayed-permission component test failed before the correction and passes afterwards. The complete Fast lane passes **984 tests**, zero skipped; Release solution and all ten browser hosts build with zero warnings/errors. TypeScript, six JavaScript lifecycle tests, five fixture tests and the documentation production build pass. Full regression and final CI results are maintained in the issue and PR descriptions.

[Structured browser results](./results.json) preserve case names, project, duration and source captures. UX acceptance remains pending.

## Integration of merged form unification

These 36 captures were refreshed after form PR #1129 merged as `a60d76e335dce1853e2d8fbd81a6ea3ef25d9018` and its 16 conflicts were reconciled. The captured implementation snapshot is `04f7dfe`; it retains shared form sections/grids/actions, checkboxes, clearable controls and native form themes together with collection identities and command placement.

The browser regression also checks the loaded scope picker: its right-aligned help uses the existing left-opening tooltip, keeping desktop document width bounded, while mobile retains the existing panel. The test failed on the combined source before this correction and passes afterwards. Version/theme/viewport variants are independent cases with the unchanged 120-second test timeout.

Full deterministic integration lane on the combined implementation: **1,011 passed, 12 optional external integrations skipped**, zero failures. Final Browser UX/documentation CI for both drafts is recorded in the issues and PRs.

A subsequent action-menu regression makes initialization idempotent across repeated Blazor renders: one ArrowDown focuses the first command rather than advancing twice. The new lifecycle test fails before the fix and all six JavaScript tests pass afterwards. The correction changes event binding only; the captured HTML/CSS remains unchanged.
