# Shared resource icon frame follow-up (#1121)

The maintainer requested a frame around every resource icon. The outer rounded frame of an Agent comes from the shared `ResourceIcon` CSS; the robot SVG contains only its own head outline. The same existing outer frame now receives a theme-aware neutral color when no family color is declared. Agent and other family colors retain their existing frame. There is one ResourceIcon per identity, with no added wrapper, double frame, image storage change or alteration to the owner/state/read-only corner markers.

These **12 unedited screenshots** come from four successful maintained distribution journeys against the actual offline application on source `033d15fb3e60ed545169e97d2e80f6df8c05d0e5`. Both themes, English desktop (1440 × 1000) and French touch mobile (390 × 844) are covered. Desktop journeys also verify table mode. Local Chromium is 154.0.8037.57; CI uses the repository's pinned Playwright browser.

| Resource | Desktop dark | Desktop light | Mobile dark | Mobile light |
| --- | --- | --- | --- | --- |
| Data Source Profiles, neutral frame | [View](data-source-profiles-en-US-dark.png) | [View](data-source-profiles-en-US-light.png) | [View](data-source-profiles-fr-FR-dark.png) | [View](data-source-profiles-fr-FR-light.png) |
| Agents, existing blue frame | [View](agents-en-US-dark.png) | [View](agents-en-US-light.png) | [View](agents-fr-FR-dark.png) | [View](agents-fr-FR-light.png) |
| Profiles table | [View](data-source-profiles-table-en-US-dark.png) | [View](data-source-profiles-table-en-US-light.png) | — | — |
| Agents table | [View](agents-table-en-US-dark.png) | [View](agents-table-en-US-light.png) | — | — |

The shared browser assertion checks exactly one icon and its visible solid border for neutral Profiles, colored Agents and neutral Extensions, in cards and tables where available. It waits for the attached interactive element: the first probe read detached prerender nodes in two dark variants and failed before this readiness correction. No test timeout was increased or assertion weakened. All four final cases passed.

[Raw successful Playwright results](playwright-results.json) and [source/capture hashes](observations.json) retain provenance. The existing [all-list gallery](../compact-all/README.md) remains historical evidence for the full collection inventory, with its original source hashes.
