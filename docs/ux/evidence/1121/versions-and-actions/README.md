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

Desktop table captures accompany the four collections above using the `-table-en-US-dark.png` and `-table-en-US-light.png` names. The maintained distribution test checks both table and card rendering, full localized version roles, alignment and bounded viewport width; it captures 28 images across the four variants, including direct Model Profile choice.

The local runner uses the installed `/usr/bin/chromium` and disables failure-video recording because the packaged Playwright browser/ffmpeg are absent. Assertions, test timeouts, screenshots and traces remain enabled. CI uses the unchanged pinned Playwright browser configuration.

Implemented-UX acceptance and both merges remain pending. These observations do not imply approval.

## Extension commands

| Locale / viewport | Dark | Light |
| --- | --- | --- |
| en-US / desktop | [Capture](./enrollment-primary-actions-en-US-desktop-dark.png) | [Capture](./enrollment-primary-actions-en-US-desktop-light.png) |
| en-US / mobile | [Capture](./enrollment-primary-actions-en-US-mobile-dark.png) | [Capture](./enrollment-primary-actions-en-US-mobile-light.png) |
| fr-FR / desktop | [Capture](./enrollment-primary-actions-fr-FR-desktop-dark.png) | [Capture](./enrollment-primary-actions-fr-FR-desktop-light.png) |
| fr-FR / mobile | [Capture](./enrollment-primary-actions-fr-FR-mobile-dark.png) | [Capture](./enrollment-primary-actions-fr-FR-mobile-light.png) |

All **five maintained browser cases passed**, zero failed/skipped/flaky, producing **36 captures** (28 version/selection views and eight Extension command views). The Extension captures include a real pending announcement and two available enrollments; commands are tested without executing credential mutations. ArrowDown opens the secondary menu, Escape dismisses it and restores summary focus.

Direct navigation additionally waits for the shared permission context before loading enrollments. The delayed-permission component test failed before the correction and passes afterwards. The complete Fast lane passes **967 tests**, zero skipped; Release solution and all ten browser hosts build with zero warnings/errors. TypeScript, five JavaScript lifecycle tests, five fixture tests and the documentation production build pass. Full regression and final CI results are maintained in the issue and PR descriptions.

[Structured browser results](./results.json) preserve case names, project, duration and source captures. UX acceptance remains pending.
