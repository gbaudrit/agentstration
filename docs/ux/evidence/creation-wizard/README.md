# Data Source creation Wizard — real application evidence

These are unedited screenshots of the running Console, not generated mockups. The application was built from implementation commit `978b59d6e4d7308c060b423d802f615e241f0194`, based on main `efbf247a`. Capture-only files and this evidence were untracked during that build. Product assemblies were not rebuilt during the browser campaign.

## Capture matrix

| Stage | English, dark, desktop | French, light, touch mobile |
| --- | --- | --- |
| Identity | [Capture](en-US-dark-identity.png) | [Viewport](fr-FR-light-identity.png) · [Full document](fr-FR-light-identity-full.png) |
| Acquisition | [Capture](en-US-dark-acquisition.png) | [Viewport](fr-FR-light-acquisition.png) · [Full document](fr-FR-light-acquisition-full.png) |
| Storage and lifecycle | [Capture](en-US-dark-storage.png) | [Viewport](fr-FR-light-storage.png) · [Full document](fr-FR-light-storage-full.png) |
| Processing | [Capture](en-US-dark-treatments.png) | [Viewport](fr-FR-light-treatments.png) · [Full document](fr-FR-light-treatments-full.png) |
| Contracts | [Capture](en-US-dark-contracts.png) | [Viewport](fr-FR-light-contracts.png) · [Full document](fr-FR-light-contracts-full.png) |
| Review | [Capture](en-US-dark-review.png) | [Viewport](fr-FR-light-review.png) · [Full document](fr-FR-light-review-full.png) |

Desktop viewport: 1440 × 1000. Mobile viewport: 390 × 844, `isMobile: true`, `hasTouch: true`. The page object uses actual `Locator.tap()` on the mobile project and keyboard focus/Enter on desktop. System Chromium is used locally; CI uses the repository-pinned Playwright browser. Exact versions, dimensions and SHA-256 digests are recorded in `observations.json`.

Mobile viewport images show the actual first viewport. Additional full-document captures preserve all fields and the footer; Chromium can render the existing fixed mobile navigation at its viewport position within those tall images. No images have been cropped, composited or edited.

## Exercised behavior

The capture wrapper reuses the existing `SourcePoliciesPage.exerciseLifecycle` journey with the deterministic Source policy fixture. It does not introduce a permanent test scenario or a new production route. The fixture uses disposable local Development data; no live model or external Storage service is required.

- All six creation steps render in both matrix cells; the product theme is asserted after language preference persistence.
- Only one content pane is active. Focus moves to its region after navigation. Horizontal overflow is checked at every forward transition.
- Missing Storage and incompatible direct-input freshness/history settings keep the user on Storage, expose the localized error and leave the resource absent from the API.
- Back navigation and the review's Edit Acquisition action retain the exact operation pin and parameters.
- The source is absent before final confirmation. Explicit creation retains its configured policy; the original downstream acquisition/projection lifecycle assertions also run.
- Component tests additionally cover conditional steps, asynchronous navigation, disposal during validation, disabled/busy commands, retained child-component state, canonical namespaces and final EditContext validation.

The archived capture wrapper/configuration are suffixed `.txt` intentionally. They document this one-off evidence campaign and are not discovered as reusable Playwright tests. To reproduce, copy them to their original paths under `automation/playwright`, install the locked npm packages, build the required product/fixture hosts first, and execute the command recorded in `observations.json`. Remove the temporary files afterwards.

UX test scenario follow-up: **Not applicable** for a new child scenario. The existing Source freshness journey was maintained for the changed pilot; future resource-family adoption needs its own follow-up decision. The screenshots demonstrate implementation, not maintainer UX acceptance.
