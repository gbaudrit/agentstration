# ADR-0288: Forge operations are an independent contract over MCP

## Status

Proposed — implementation for [#1379](https://github.com/gbaudrit/agentstration/issues/1379).

## Context

The existing Coding/GitHub boundary resolves native repositories, preserves exact
head/base evidence and recovers uncertain PR creation. Generic Project and delivery
services need reusable forge semantics without importing vendor or Work/Coding DTOs.
MCP supplies invocation and AEP discovery/configuration; neither establishes native
concurrency, creation recovery or review/check meaning. Provider transport identity
remains distinct from consumer/native authority (ADR-0122).

The maintainer selected independent Storage, Tracking and Forge contracts. ASC now
implements the storage naming change; ATC is the separate tracking foundation. No
released compatibility surface requires old forge aliases or an operational migration.

## Decision

- Stage an autonomous `afc/` SDK with provider-neutral abstractions, typed MCP client
  and server adapters, and optional existing-AEP Tool contribution metadata integration.
  Its exposure is `agentstration.forge.contracts.mcp.v1`, revision `1.0` (`AFC.mcp.v1`).
- Resolve exact configured provider/target/binding revisions through host-owned current
  authorization on every call. Keep native repository and change-request identities,
  draft state and exact head/base evidence; do not derive authority from caller IDs.
- Provide bounded describe/resolve/read/create/observe operations. Gate review/check
  observations, preserve native states and explicit Unknown/Stale evidence, and never
  infer functional acceptance or merge permission from a successful observation.
- Distinguish observed versus atomic native creation guarantees. Preserve stable
  operation/intent identities and explicit recovery; partial artifact evidence remains
  Uncertain when expected revisions cannot be established. Process recovery requires
  restart fencing and cannot claim durable or exactly-once remote effects.
- Declare exact mappings, compatible revision and canonical schema digests at existing
  AEP Tool contribution level, and verify actual discovered schemas. Keep contract,
  negotiated MCP, implementation and package versions separate; add no generic registry.
- Allow opaque external tracking associations without depending on ATC or acquiring
  tracker authority. Keep Git clone/fetch/commit/push, sharing, Work/Coding persistence,
  policy and orchestration outside the neutral contract. Expose no merge operation.
- Supply an authenticated real-MCP offline sample and adversarial conformance tests.
  The bounded memory fixture is process-local and cannot establish vendor durability.

## Consequences

AFC can be consumed without an agent, LLM or tracker; one extension may implement it
alone or alongside ATC. Consumers retain explicit independent connection selection.
The SDK does not add a deployable product service or replace the delivery engine.

#951 owns connection adoption; #958/#959 own vendor adapters and #1349 delivery
integration. Local development may reseed unpublished configuration; this foundation
performs no automatic data reset, historical rewrite or compatibility migration.
See the [normative specification](https://github.com/gbaudrit/agentstration/blob/main/afc/docs/contract.md).
