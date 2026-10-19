# CAR-0458: Agent Tool contract omitted execution constraints

## Status

Prevented — 2026-09-17

## References

- Issue: [#458](https://github.com/gbaudrit/agentstration/issues/458)
- Introducing change: commit `34f4213f` in [PR #274](https://github.com/gbaudrit/agentstration/pull/274)
- Corrective pull request: [#465](https://github.com/gbaudrit/agentstration/pull/465)
- Related ADRs: [ADR-0107](../decisions/0107-notification-channels-are-delivery-flows.md)

## Defect

The Agent-facing `work.notification.create` input schema described `actionUrl` only as a string of at most 2048 characters. Execution rejects non-local paths, protocol-relative paths, empty strings, and backslashes. The schema also omitted the non-blank requirement for `deliveryKey`, `title`, and `message`, while the Tool description did not explain intended usage or the delivery key's retry semantics. A model could therefore produce arguments conforming to the published contract that the application deterministically rejected.

## Detection

- Stage: Post-merge
- Detection mechanism: an OpenTelemetry Tool span recorded `notification_action_url_invalid` for an Agent invocation; issue #458 compared the exposed Tool contract with `WorkplaceService.DeliverNotificationAsync`.
- Why it was not detected earlier: existing tests covered publication and idempotent delivery but did not assert the Agent-facing description or compare the schema with downstream argument validation.

## Causal analysis

### Faulty approach

The initial implementation treated the JSON schema as a structural sketch and relied on application validation to enforce the full contract. The Tool description stated only that a notification was created, leaving the model to infer usage and restrictions from argument names.

### Contributing assumptions

- Type and maximum length were treated as sufficient schema coverage for `actionUrl` even though path shape was a deterministic rejection condition.
- The internal Tool's direct validation and the downstream Work validation were not considered together as one Agent-facing contract.

### Missed signals

- ADR-0107 explicitly defines the delivery key as the identity of a logical notification and requires internal Tools to use the same MCP schema abstraction as governed Tools.
- `WorkplaceService.DeliverNotificationAsync` already contained the local-path and non-blank checks absent from the schema.
- The sample Flow and ToolDefinition repeated the incomplete notification input contract.

### Safeguard gap

Publication tests checked that the Tool existed and executed idempotently, but not that its description and schema represented constraints that could reject model-controlled input. No contributor guidance explicitly separated capability description from invocation schema.

## Resolution

The internal Tool description now explains purpose, appropriate use, idempotent retries, and the local-action restriction. Its input schema communicates field semantics, non-blank values, length limits, and the local absolute-path pattern. The sample Flow and ToolDefinition carry an aligned contract. Existing projected Tool resources refresh their owned description and schema while preserving administrator enablement.

## Prevention

Contract tests assert the path pattern against valid and rejected inputs, compare it with downstream rejection, check semantic descriptions, verify MCP/resource propagation, and verify an existing disabled projection is refreshed without being enabled. Architecture guidance now requires authors to compare Tool-level and downstream validation with the Agent-facing description and schema.

## Validation

- Focused notification tests: 5 passed during implementation.
- Current `main` base (`015d78a8`) Release solution build: succeeded with 0 warnings and 0 errors.
- Required fast functional lane: 374 passed, 0 skipped, 0 failed.
- Required integration functional lane: 553 passed, 3 optional external-provider integrations skipped, 0 failed.
- `git diff --check`: passed.
