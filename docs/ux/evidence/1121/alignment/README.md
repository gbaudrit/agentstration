# Collection column and Actions alignment (#1121)

The maintainer requested vertically aligned column contents and a centered Actions chevron. Shared collection cells now center resource identities, business values and actions vertically within each table row. The Actions summary uses one inline flex alignment for its text and chevron. The Flow compact override and the active Source Registry, Runtime Worker and worker-assignment overrides no longer force cells to the top. Resource Plan multiline before/after comparisons retain their top-aligned diff semantics.

These **10 unedited screenshots** come from the actual offline application on source `776d164cb202d2dacc0d8c5bf85fd577c31bcecb`. All four maintained distribution variants passed: English desktop (1440 × 1000), French mobile viewport (390 × 844), both themes. Local Chromium is 154.0.8037.57; CI retains its pinned Playwright browser. The source/capture hashes are recorded in [observations.json](observations.json), alongside the [raw successful results](playwright-results.json).

| Surface | Dark | Light |
| --- | --- | --- |
| Tools, desktop table | [View](tools-table-en-US-dark.png) | [View](tools-table-en-US-light.png) |
| Tools, French mobile cards | [View](tools-fr-FR-dark.png) | [View](tools-fr-FR-light.png) |
| Flows, desktop table | [View](flows-table-en-US-dark.png) | [View](flows-table-en-US-light.png) |
| Agents, desktop table | [View](agents-table-en-US-dark.png) | [View](agents-table-en-US-light.png) |
| Data Source Profiles, desktop table | [View](data-source-profiles-table-en-US-dark.png) | [View](data-source-profiles-table-en-US-light.png) |

The Tools regression measures the real center of all four desktop cells against their contents, with a maximum offset below 2px, including two-line identities. In both table and card views, the chevron center must remain within 1px of its button center. ArrowDown opens the menu and focuses its first action; Escape closes it and restores focus. Both mobile variants also verify ordered business blocks and the absence of horizontal document overflow.

The first campaign passed both desktop cases but used a table-row assertion for the existing stacked mobile renderer, producing two failures. The corrected expectation explicitly requires table rows on desktop and the native ordered grid on mobile. Desktop thresholds, chevron checks and timeouts are unchanged. All four final cases pass. Existing [icon frames](../icon-frames/README.md) and corner markers are preserved. Full functional and final-head CI outcomes are recorded in draft PRs [#1123](https://github.com/gbaudrit/agentstration/pull/1123) and [#1132](https://github.com/gbaudrit/agentstration/pull/1132).
