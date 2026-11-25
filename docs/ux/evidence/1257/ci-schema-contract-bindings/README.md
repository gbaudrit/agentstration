# Schema contract CI regression after main synchronization

Corrected source: `61328e0794d0e226bb76c2287f490c32098fdb86`; UX automation fix: `8db501d3268cda1360ab67f3eda4853cf9db2976`.
The product/Razor build outputs stayed frozen throughout the browser campaign.

The [failed CI scenario](https://github.com/gbaudrit/agentstration/actions/runs/45656333320) could not click the Flow kind because an alert dialog intercepted pointer events. The unchanged local scenario passed normally. Delaying the creation page's `context-selector.js` import by 1500 ms reproduces **Discard unsaved changes?**: the form is edited before the shell adds its implicit context to the URL. The retained regression fails with the old navigation helper and passes with the corrected helper.

The page object now carries the selected context and waits for shell preferences readiness before editing, following the existing Agent editor pattern. The unsaved-navigation guard is unchanged. The test waits for the delayed shell to settle and asserts that no unexpected confirmation remains.

| Native capture | State and visual inspection |
| --- | --- |
| [before-desktop-en-US-dark.png](before-desktop-en-US-dark.png) | **Before correction**, local reproduction using the old helper; confirmation obstructs the form. |
| [after-desktop-en-US-dark.png](after-desktop-en-US-dark.png) | After correction; selected Flow and inherited output contract visible, no confirmation. |
| [after-connections-desktop-en-US-dark.png](after-connections-desktop-en-US-dark.png) | Existing connection journey still reaches its connected canvas and inspector. |
| [after-connections-mobile-en-US-dark.png](after-connections-mobile-en-US-dark.png) | Existing responsive journey remains usable at 390×844 after closing its prompt. Canvas panning remains native. |

All four retained PNGs were individually inspected at native readable size. **Nine checks pass**, with zero retries/skips: three repetitions each of the delayed binding scenario and desktop/mobile connection journeys. TypeScript, 25 JavaScript component checks, and five fixture checks pass. The full browser suite is delegated to hosted CI. This targeted campaign does not renew the earlier collection screenshots across FR/EN and light/dark variants; their original provenance is preserved.

The original CI artifact redirect was blocked by the Cloud network allowlist. The before capture is explicitly a local reproduction, not a recovered CI image. Local video was disabled; PNGs, assertions and the failure trace were retained. Full functional coverage and final hosted CI were pending at evidence commit time; consult PR #1273 Validation for completion. See [observations.json](observations.json) for hashes and limits.
