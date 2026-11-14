# ADR-0195: HTTP category switches always enable both payload directions

## Status

Accepted

## Context

Operators require simple boolean category switches rather than capture-level presets. Enabled diagnostics must systematically include both the request and the response.

## Decision

Replace the category-level enum from ADR-0194 with boolean values. `true` enables both request and response body capture; `false` disables the category. Remove the separate general HTTP `CaptureRequestBody` and `CaptureResponseBody` options introduced by ADR-0193. `Enabled` remains the master switch; unlisted categories inherit it. The AppHost lists all categories explicitly as `false`, with the master switch also `false`.

Since enabled diagnostics always capture bodies, activation remains Development-only. Existing redaction, sensitive-route suppression, byte limits, health status handling and streaming guarantees are unchanged. Legacy GenAI configuration remains separate.

## Consequences

- Category configuration uses only `true` and `false`, without another payload-selection setting.
- The earlier string levels are rejected at startup instead of being silently reinterpreted.
- This supersedes the independent HTTP payload controls in ADR-0193 and capture-level configuration in ADR-0194; their other decisions remain applicable.
