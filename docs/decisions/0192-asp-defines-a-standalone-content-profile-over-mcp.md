# ADR-0192: ASP defines a standalone content profile over MCP

## Status

Accepted

## Context

Issue #1038, independently startable under #1032, needs interoperable storage
semantics for consumers and extensions without binding them to Agentstration
hosts, physical backends or a universal database SDK. AEP already discovers and
maps service operations to MCP (ADR-0171). Artifacts retain their existing staged
and durable Flow lifecycle (ADR-0151). Neither MCP hints nor shared physical
placement provides logical-store authorization or retry-safe writes.

## Decision

Stage an autonomous `asp/` SDK with neutral contracts, an MCP server adapter, a
typed MCP client, canonical JSON Schemas, offline sample and conformance tests.
The first executable profile is `asp.content/1`, ASP version `1.0`, covering
bounded describe/list/stat/read/write. MCP negotiation, ASP compatibility, provider
version and package distribution version are independent dimensions. Use the
repository's selected MCP .NET SDK. No custom wire transport or product dependency.

Each Tool binding pins an exact logical `StoreRef` (store, binding, configuration
revision), provider and maximum policy. Per-call host authorization resolves trusted
subject, current permission revision and grant separately from business arguments.
Intersect the grant with fixed allowed operations, logical prefix/key interval and
payload limits; providers receive and enforce this context on every call. Physical
configuration is provider-owned. References/cursors are signed, short-lived and
bound to effective context; cursors also bind their query. Shared physical storage
never implies shared authorization.

The optional writer contract requires atomic conditional content writes and bounded
receipt retention for identical requests within an explicit deadline. Missing
expectedVersion means create-only. Cancellation or transport failure may leave an
unknown outcome; exact original retry reconciles within its validity window. The
SDK does not infer atomicity from MCP annotations or implement blind retries.

The deterministic sample uses bounded process-local memory with separate exact
logical partitions and a read-only variant through the same server adapter.
Deletion, stable historical versions and retention are explicitly unavailable.
This sample cannot back durable Snapshots. Future guarantees and query profiles
require executable provider and consumer support; no placeholder interfaces or
OData implementation are introduced.

AEP remains discovery/configuration, not storage invocation semantics. #1040 owns
the AEP storage contribution and governed product/builtin Artifact integration;
#1039 owns OpenDAL. This ADR does not migrate or replace current Artifact behavior.
The normative delivered semantics and limits are in
[the ASP specification](https://github.com/gbaudrit/agentstration/blob/main/asp/docs/protocol.md).

## Consequences

- .NET extensions implement typed provider methods without hand-writing MCP Tool
  schemas; consumers use the same result/error and compatibility checks.
- Product hosts and independent extensions can pack/build the three SDK packages
  without EF Core, OpenDAL, Runtime, Web or business-module dependencies.
- A host must authenticate the transport, reauthorize each call and limit bodies;
  providers must uphold logical isolation, limits and atomicity themselves.
- Process-local tokens and receipts do not promise durability across restarts;
  exact object versions fail explicitly when overwritten.
- Optional conservation/deletion/query features remain unavailable until their
  complete operations and compatibility rules are implemented.
