# ADR-0230: HTTP diagnostics use explicit category capture policies

## Status

Accepted

## Context

The HTTP diagnostics introduced by ADR-0229 need finer operator controls: inspecting health traffic should not require capturing unrelated API or Worker payloads. The AppHost also needs a discoverable, disabled configuration block. The initial health exclusion and JSON-only capture hide the standard health responses used by Workplace and Workers.

## Decision

Classify exchanges into `Health`, `Awp`, `Aep`, `Bff`, `Api` and `Other` using bounded, segment-based path rules. `Observability:Http:Categories` selects `Off`, `Metadata`, `RequestBody`, `ResponseBody` or `Bodies` for each category. Explicit category policies override global capture flags; unlisted categories inherit them. The master switch, exclusions, sensitive-route restrictions, byte limits and Development-only body capture remain authoritative. Unknown categories or levels fail startup.

Commit the full configuration block to the AppHost with its master switch disabled and every category set to `Off`. Forward it through the existing host configuration integration. Add the selected category to diagnostic spans and structured logs; leave the standard HTTP instrumentation and non-HTTP GenAI controls independent.

Remove the default `/health` exclusion. Extend ADR-0229's body-format rule narrowly: on `/health` and its descendants, complete `text/plain` responses may emit exactly the standard `Healthy`, `Degraded` or `Unhealthy` values. Other plain text remains omitted. Requests or responses known to be empty emit an explicit `[empty body]` marker. JSON capture, credential redaction and stream preservation remain unchanged.

## Consequences

- Operators can inspect one traffic category without enabling all payloads.
- New AppHost checkouts keep every diagnostic category disabled until explicitly enabled.
- Both JSON and standard text health results are inspectable without enabling arbitrary text capture.
- The trace list still shows span names; payload contents are available in span events and correlated structured logs.
