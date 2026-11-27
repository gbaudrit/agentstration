# CAR-1420: Nullable Cloudflare DNS metadata blocked preview reconciliation

## Status

Open — 2026-11-27

## References

- Issue: [#1420](https://github.com/gbaudrit/agentstration/issues/1420)
- Introducing change: [#1418](https://github.com/gbaudrit/agentstration/pull/1418), merged as `a378862a`
- Corrective pull request: pending creation
- Related ADRs: [ADR-0269](../decisions/0269-preview-reconciliation-enforces-owned-expiry-and-capacity.md)

## Defect

The DNS inventory classifier called `startswith` on an optional Cloudflare
`comment` value. An unrelated DNS record with `comment: null` aborted inventory
before the reconciler could evaluate owned previews. Missing comments must never
authorize orphan adoption or deletion.

## Detection

- Stage: Post-merge
- Detection mechanism: trusted-main dry run [45968475426](https://github.com/gbaudrit/agentstration/actions/runs/45968475426), job 140402803049, raised `AttributeError` in `route_inventory`.
- Why it was not detected earlier: the provider fixtures exercised absent and string comments, but not an explicitly null optional comment. The first live dry run exposed that response shape.

## Causal analysis

### Faulty approach

`record.get('comment', '')` supplies its fallback only when the key is absent.
The resulting value was used as a string without normalizing a present null.

### Contributing assumptions

The implementation treated an optional API field as either absent or a string.
The live Cloudflare response demonstrated a third legitimate representation:
an explicitly null value.

### Missed signals

The classifier scans shared DNS inventory, including records not managed by
preview tooling. Its optional metadata must therefore be normalized without
weakening the existing saved-route or explicit ownership-marker checks.

### Safeguard gap

The contract tests proved route ownership classification with hand-authored
fixtures, but their fixture shapes omitted the nullable provider value. The
credential-free suite could not discover that omission through a live API.

## Resolution

Normalize an absent, null or empty comment to an empty string before checking
the ownership marker. Keep saved hostname matching and marked unknown-route
classification unchanged. No route deletion, adoption, credentials or deployment
policy changes are introduced.

## Prevention

Add a provider-shaped regression covering null, absent, empty and ordinary
comments alongside known saved routes and explicitly marked orphan candidates.
It must classify only the known route as occupied and only the marked unknown
route as an orphan. The regression fails on the introducing implementation.

## Validation

- Reproduced the live failure offline: the new test fails with `AttributeError` on `comment=None` before the correction.
- Corrected preview suite: 325 passed, one optional Azure CLI check skipped (326 total).
- Actionlint for the reconciler workflow and `git diff --check`: passed.
- Full documentation build: running. Corrective CI: pending PR creation.
- Live trusted-main dry run has not been repeated; it requires the correction to merge and remains part of #1286 qualification.
