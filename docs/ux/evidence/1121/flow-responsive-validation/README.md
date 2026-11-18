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
