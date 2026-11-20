# Shared list-screen layout — FR #1241

Actual deterministic local executable captures, taken from `codex/ux-corrections` with uncommitted layout changes above baseline `7e4a53f2`. [Validation provenance](validation.json) records capture time, exact source-file SHA-256 hashes after LF normalization, fixture identity and executed variants. These captures do not claim a committed implementation or maintainer UX acceptance.

| Pilot | Desktop English/dark, 1440×1000 | Mobile French/light/touch, 390×844 |
| --- | --- | --- |
| Connected Services | [Filtered table](connected-services-desktop-en-dark.png) | [Filtered cards](connected-services-mobile-fr-light.png) |
| Flows | [Second server page](flows-desktop-en-dark.png) | [Second server page as cards](flows-mobile-fr-light.png) |

Desktop captures include the complete page; mobile captures show the actual viewport, including global navigation. Screenshots are unedited. No custom image generation or image post-processing is used.

## Observable checks

- Connected Services: 26 temporary services across two actual user-authored types; global search, a column-associated Type filter, the additional namespace filter, active chips, local page two, no matches/reset and retained card mode. Native creation currently assigns the default namespace; the browser smoke selects that real namespace. Mixed-namespace filtering is additionally covered by component fixtures.
- Flows: 22 temporary drafts plus existing offline built-ins; compare the actual second 20-row page and the 50-row first page against the authorized server API. No global total, page-number jumps, page-local filters, Definitions/Runs/Deployments tabs or redundant Actions/Open menu are displayed.
- Both variants: responsive document bounds, actual More filters keyboard/touch controls, readable page-size selection and zero unhandled browser errors. Native fixed navigation remains separate from collection controls.
- All smoke-owned Service/type/Flow fixtures are removed through their exact API identities and ETags; the fixture account's appearance preferences are restored. No generated database is versioned.

## Focused validation

- Web Release build: success, zero warnings/errors.
- Console pilot regressions: **69 passed**; shared collection/template regressions: **37 passed**. They include same-query refresh retention, expected failures, access denial, stale responses, context replacement, filtered snapshot shrink, known totals, continuation windows and explicit filter application.
- TypeScript and all **3** collection source/catalog checks pass. The inventory continues to count each concrete page adapter as one shared collection frame; generic form/template loops and navigation controls are explicitly classified.
- Final format verification for all changed/new C# and Razor files and `git diff --check` pass. Changed English/French catalogs are valid, duplicate-free and key-symmetric. Capture hashes match all **19** recorded final product source files.
- The two ad hoc desktop/mobile smoke probes above pass. All **4** selected maintained browser variants also pass: `flow-collection.spec.ts` in English/dark desktop and French/light touch, plus `connected-storage-collection.spec.ts` in the same two variants. These cover actual publication/version presentation, bounded Flow paging, exact links, inherited read-only inspection, keyboard/touch and the existing storage/service journeys. This is maintained regression and smoke evidence rather than implementation of the new reusable scenario.
- Full Fast/Integration lanes, repository-wide browser smoke and live-provider integrations were not rerun for this focused iteration, following the maintainer's request to limit testing to the necessary scope.

The new reusable UX scenario [#1242](https://github.com/gbaudrit/agentstration/issues/1242) remains blocked until explicit maintainer validation and merge of the implementation. Parent [#1241](https://github.com/gbaudrit/agentstration/issues/1241) remains open. The captures were made before commit; the maintainer subsequently authorized committing and pushing this increment. That authorization does not constitute UX acceptance or waive the scenario gate.
