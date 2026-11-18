# Source sequence refresh and in-flight Add regression

The French mobile Source Flow composition case in [Browser UX run 44260497257](https://github.com/gbaudrit/agentstration/actions/runs/44260497257) expected one added row but observed zero after refreshing the transformation catalog. The preserved job log does not establish the server cause of that exact incident. An unchanged isolated French case and an unchanged English/French pair passed locally before this correction; those passes alone did not exclude a race.

A controlled component regression holds the catalog listing on a TaskCompletionSource after an initial compatible catalog has enabled Add. It then delivers an already queued Add while refresh has cleared the options. Before correction it throws ArgumentOutOfRangeException from FlowSequenceEditor.AddAsync. A second regression shows that an already queued Add can mutate a read-only sequence. Both cases fail against the previous component ([red transcript](component-red.txt)).

The handlers now enforce the same loading, read-only, catalog and 32-item conditions as their buttons. A local load revision and aria-busy describe the component refresh; they do not change persisted business revisions. The maintained browser journey waits for the requested refresh to finish before issuing Add, keeping all row-count, version, draft preservation and navigation assertions and existing timeouts/retries.

The same two behavioral regressions pass after correction, with additional assertions for the refresh readiness marker ([raw green result](component-green.txt)). The complete Console Components suite passes all 712 cases, without skips or failures ([raw project result](component-console.txt)). TypeScript also passes. Full functional-lane and CI results are recorded separately in the PR validation.

## Browser follow-up on the corrected source

All **four maintained Source Flow Composition cases pass**, with **zero failures, skips or flaky cases** and no retries. Total campaign duration is 357.6 seconds. English runs before French on the same retained host within each project, so the French case includes the already published English transformation that was absent from an isolated French run.

| Project | Locale | Result | Case duration |
| --- | --- | --- | --- |
| Desktop, 1440 × 1000 | en-US | Passed | 69.221 s |
| Desktop, 1440 × 1000 | fr-FR | Passed | 73.027 s |
| Mobile, 390 × 844 | en-US | Passed | 98.122 s |
| Mobile, 390 × 844 | fr-FR | Passed | 63.594 s |

The campaign used the unchanged Release build11 outputs with `AGENTSTRATION_PLAYWRIGHT_NO_BUILD=true`, Playwright 1.63.0 and system Chromium 154.0.8037.57. It ran before publication of [source commit 2650622978d74f3852691aef2a1825f0b06cb3ba](https://github.com/gbaudrit/agentstration/commit/2650622978d74f3852691aef2a1825f0b06cb3ba); the recorded product component and maintained journey files match that commit exactly. No product assemblies were rebuilt during the campaign. Assertions, timeouts and retry settings are unchanged.

[Compact results and exact source blob IDs](browser-results.json), the [original Playwright JSON report](browser-report.raw.json) and [unaltered test log](browser-log.txt) retain the completed results. These successful checks validate the correction and refresh readiness contract; they do not establish the cause of the exact earlier CI failure.

[Byte counts and SHA-256 hashes](observations.json) retain diagnostic provenance. The readable red transcript normalizes trailing whitespace only; [its original bytes](component-red.raw.txt.gz) are preserved unchanged in gzip form. The red result is an intentional pre-fix failure, not a current validation failure.
