# Source sequence refresh and in-flight Add regression

The French mobile Source Flow composition case in [Browser UX run 44260497257](https://github.com/gbaudrit/agentstration/actions/runs/44260497257) expected one added row but observed zero after refreshing the transformation catalog. The preserved job log does not establish the server cause of that exact incident. An unchanged isolated French case and an unchanged English/French pair passed locally before this correction; those passes alone did not exclude a race.

A controlled component regression holds the catalog listing on a TaskCompletionSource after an initial compatible catalog has enabled Add. It then delivers an already queued Add while refresh has cleared the options. Before correction it throws ArgumentOutOfRangeException from FlowSequenceEditor.AddAsync. A second regression shows that an already queued Add can mutate a read-only sequence. Both cases fail against the previous component ([red transcript](component-red.txt)).

The handlers now enforce the same loading, read-only, catalog and 32-item conditions as their buttons. A local load revision and aria-busy describe the component refresh; they do not change persisted business revisions. The maintained browser journey waits for the requested refresh to finish before issuing Add, keeping all row-count, version, draft preservation and navigation assertions and existing timeouts/retries.

The same two behavioral regressions pass after correction, with additional assertions for the refresh readiness marker ([raw green result](component-green.txt)). The complete Console Components suite passes all 712 cases, without skips or failures ([raw project result](component-console.txt)). TypeScript also passes. Browser follow-up and full functional-lane results are recorded in the PR validation once completed.

[Byte counts and SHA-256 hashes](observations.json) retain diagnostic provenance. The readable red transcript normalizes trailing whitespace only; [its original bytes](component-red.raw.txt.gz) are preserved unchanged in gzip form. The red result is an intentional pre-fix failure, not a current validation failure.
