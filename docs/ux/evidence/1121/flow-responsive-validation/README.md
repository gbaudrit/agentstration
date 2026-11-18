# Responsive Flow validation regression

[CI failure](https://github.com/gbaudrit/agentstration/actions/runs/44253959261)
and a local reproduction report `transition_event_invalid`: Input does not emit
`error`. The fixture predated this synchronization. The old assertion could read
the previous count while validation was still pending.

The corrected fixture keeps success/error outcomes through a real Condition's
`true`/`false` events and first verifies the server returns no issues. The UI check
waits for the completed result panel, then preserves the original zero/one issue
assertions, including the intentionally unreachable step. Three local repetitions
pass without changing timeouts or retries.

[Failure text](failure.txt) and [result/source hashes](observations.json) retain the
red/green provenance. No production Flow validation rule is weakened.

## Flow Inspector consumers — eight maintained variants

The responsive fixture is also used by the maintained form workflow. Its old
`input —completed→ completed` selection and `input-completed-completed` API lookup
needed to follow the valid graph. They now select `input —completed→ decision`
and inspect `input-completed-decision`. The Input source, `completed` event,
priority editing/clearing checks and transition-preservation assertions remain.
The Condition continues to expose the success and error outcomes through its
real `true` and `false` ports.

All **eight** English/French, light/dark, desktop/mobile Flow Inspector variants
passed, with zero failures, skips, flaky cases or retries. TypeScript also passed.
The browser campaign used the existing Release host build 10 and headless system
Chromium, without rebuilding product assemblies during execution. Test and
expectation timeouts remain 120 seconds and 15 seconds.

[The complete test log](flow-inspector-log.txt) and
[compact per-case results with source/raw-result hashes](flow-inspector-results.json)
record the evidence. The campaign ran on `5c5159d8` with the two consumer changes
in the working tree; those exact changes were subsequently committed as
[`d8ea04a2`](https://github.com/gbaudrit/agentstration/commit/d8ea04a2ccf8f9c0b72a9362f8533371a52f1005).
The JSON distinguishes that chronology and records identical tested source files.

This evidence concerns Flow validation fixtures and their Inspector consumers.
It does not validate the separate Source composition catalog-reload correction.
All corrections belong to #1174; #1132 was merged separately by the maintainer.
