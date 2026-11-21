# Bootstrap catalog and ordered-step refinement

Implementation smoke on 2026-11-21, for PR #1260 and issues #1250 / #871.
These captures are review material, not maintainer UX approval or delivery of
reusable scenarios #1251 / #1217. Both follow-ups remain deferred until explicit
UX validation and implementation merge.

The real Release Web executable and actual Ollama extension used an isolated
SQLite directory, SharedKeyFile enrollment and a controllable offline upstream.
The public Development administrator fixture was used; no credentials, manifest
bodies or data stores are included here. The browser scripts remain temporary
implementation checks outside the repository.

## Startup

[Desktop FR, 1440px](startup-fr-desktop.png) shows only declared resource groups,
separate ordered access-policy passages, completed topology/extensions folded,
summary cards before outcomes, and the standard provider/model selects. Completed
groups were reopened and folded again. The visual search box and state selector
were measured at matching heights and bottom edges. Selecting the authorized
backup provider and cached replacement model resumed the failed model dependency
and reached ordinary Console readiness.

## Manual Bootstrap

[Catalog and preview](catalog-preview-fr-desktop.png) show the shared list layout,
metrics above its search/filter/table, explicit Tenant selection, and the retained
preview. The final catalog capture previews the already-completed fixture again,
so its expected dispositions are Skip, without another application. Preview
rendering is bounded to 25 rows; confirmation still submits the
preview digest. The temporary `ux-manual` fixture copies the committed offline
512-Parameter profile with distinct names so all resources are created.

[Running application](manual-running-fr-desktop.png) uses StepProgress within the
Bootstrap section. [Completed application](manual-result-fr-desktop.png) shows
512 created resources, searchable outcomes and reopened resource groups. The
persisted plan contains 512 safe identities and the result contains 512 outcomes.
Reload retained the same application ID. The authorized history contains exactly
one application for this fixture with one request ID before and after reload.
This is the submission proof: Blazor Server executes the client API call on the
server, so browser HTTP request counting is not used.

Mobile EN at 390x844 was checked in [light](manual-steps-mobile-light.png) and
[dark](manual-steps-mobile-dark.png) themes. No JavaScript errors or page-wide
horizontal overflow occurred. Existing shared mobile shell controls are retained;
this change does not redesign MainLayout or its navigation.

## Verification scope

Component/state tests cover ordered groups, exact group filtering, auto-collapse
and reopening across updates, native selects, preview-before-confirmation,
selected-application cancellation and invalid preparation. The persisted
512-resource integration scenario verifies the safe plan survives observation.
The existing registered distribution Bootstrap catalog smoke also passed.

No generic shell, routing, worker-observability or unrelated UX files were changed
for this refinement. See the PR validation section for the final complete-lane
results; these images alone do not certify authentication/security behavior.
